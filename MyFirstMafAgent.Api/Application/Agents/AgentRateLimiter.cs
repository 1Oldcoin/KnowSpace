using System.Collections.Concurrent;

namespace MyFirstMafAgent.Api.Application.Agents;

/// <summary>
/// 按客户端计数的令牌桶限流。
///
/// <para>
/// 刻意不用 ASP.NET Core 的 <c>AddRateLimiter</c>：那里每个策略的额度在启动期固定，
/// 而本项目的额度存在数据库里、可以随时改。令牌桶还能给出准确的
/// <c>Retry-After</c>，比固定窗口「等下一分钟」对智能体更友好。
/// </para>
///
/// <para>
/// 计数只放内存：重启后从满额度重新开始。这是有意的取舍——
/// 限流是保护手段，不是计费依据。
/// </para>
/// </summary>
public sealed class AgentRateLimiter(TimeProvider? timeProvider = null)
{
    private const int PruneThreshold = 512;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <summary>
    /// 尝试获取一个令牌。<paramref name="requestsPerMinute"/> ≤ 0 表示不限制。
    /// 被拒时 <paramref name="retryAfterSeconds"/> 给出建议的重试间隔（至少 1 秒）。
    /// </summary>
    public bool TryAcquire(string key, int requestsPerMinute, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (requestsPerMinute <= 0) return true;

        if (_buckets.Count > PruneThreshold) Prune();

        var bucket = _buckets.GetOrAdd(key, static _ => new Bucket());
        return bucket.TryAcquire(_time.GetUtcNow(), requestsPerMinute, out retryAfterSeconds);
    }

    /// <summary>丢弃某客户端的计数（删除客户端或改额度时调用）。</summary>
    public void Forget(string key) => _buckets.TryRemove(key, out _);

    private void Prune()
    {
        var cutoff = _time.GetUtcNow().AddMinutes(-10);
        foreach (var (key, bucket) in _buckets)
        {
            if (bucket.LastAccess < cutoff) _buckets.TryRemove(key, out _);
        }
    }

    private sealed class Bucket
    {
        private readonly object _gate = new();
        private double _tokens;
        private int _capacity;
        private DateTimeOffset _lastRefill = DateTimeOffset.MinValue;
        private DateTimeOffset _lastAccess = DateTimeOffset.MinValue;

        public DateTimeOffset LastAccess
        {
            get
            {
                lock (_gate) return _lastAccess;
            }
        }

        public bool TryAcquire(DateTimeOffset now, int capacity, out int retryAfterSeconds)
        {
            lock (_gate)
            {
                _lastAccess = now;

                if (_capacity != capacity || _lastRefill == DateTimeOffset.MinValue)
                {
                    // 首次使用或额度被调整：以新额度重新装满，避免改额度后仍按旧桶计算。
                    _capacity = capacity;
                    _tokens = capacity;
                    _lastRefill = now;
                }
                else
                {
                    var elapsedSeconds = (now - _lastRefill).TotalSeconds;
                    if (elapsedSeconds > 0)
                    {
                        _tokens = Math.Min(capacity, _tokens + elapsedSeconds * capacity / 60.0);
                        _lastRefill = now;
                    }
                }

                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    retryAfterSeconds = 0;
                    return true;
                }

                var missing = 1 - _tokens;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(missing * 60.0 / capacity));
                return false;
            }
        }
    }
}
