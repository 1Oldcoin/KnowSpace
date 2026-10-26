using System.Collections.Concurrent;

namespace MyFirstMafAgent.Api.Application.Agents;

/// <summary>
/// 按来源 IP 计数的鉴权失败闸门。
///
/// <para>
/// 与 <see cref="AgentRateLimiter"/> 的分工：那个是「<b>已通过鉴权</b>的客户端」的调用配额，
/// 计费口径是客户端 ID；这个是「<b>还没通过鉴权</b>的请求」的封禁，计费口径是来源 IP。
/// 两者不能合并——失败请求根本没有客户端 ID 可用，而这恰恰是最需要拦下的那一类。
/// </para>
///
/// <para>
/// 计数只放内存：重启后清零。限流是保护手段，不是计费依据；重启后重新计数是可接受的取舍。
/// </para>
/// </summary>
public sealed class AgentAuthThrottle(TimeProvider? timeProvider = null)
{
    private const int PruneThreshold = 1024;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// 该来源当前是否被封禁。被封时 <paramref name="retryAfterSeconds"/> 给出剩余秒数（至少 1）。
    /// </summary>
    public bool IsBlocked(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!_windows.TryGetValue(key, out var window)) return false;

        lock (window.Gate)
        {
            var now = _time.GetUtcNow();
            if (window.BlockedUntil is not { } until) return false;

            if (until > now)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
                return true;
            }

            // 封禁到期：清掉计数重新开始，而不是立刻又因为旧计数被封一次。
            window.BlockedUntil = null;
            window.Count = 0;
            window.WindowStart = now;
            return false;
        }
    }

    /// <summary>
    /// 记一次鉴权失败；达到上限则封禁。
    /// 返回本次失败后是否进入封禁状态，便于调用方在日志里说明原因。
    /// </summary>
    public bool RecordFailure(string key, int limitPerMinute, int blockSeconds)
    {
        if (limitPerMinute <= 0) return false;

        if (_windows.Count > PruneThreshold) Prune();

        var window = _windows.GetOrAdd(key, static _ => new Window());

        lock (window.Gate)
        {
            var now = _time.GetUtcNow();

            // 跨分钟就重新开窗。
            if (now - window.WindowStart >= TimeSpan.FromMinutes(1))
            {
                window.WindowStart = now;
                window.Count = 0;
            }

            window.Count++;
            window.LastSeen = now;

            if (window.Count < limitPerMinute) return false;

            window.BlockedUntil = now.AddSeconds(Math.Max(1, blockSeconds));
            return true;
        }
    }

    /// <summary>鉴权成功：清掉该来源的失败计数（正常客户端不会因为偶发拼错被牵连）。</summary>
    public void RecordSuccess(string key) => _windows.TryRemove(key, out _);

    private void Prune()
    {
        var cutoff = _time.GetUtcNow().AddMinutes(-10);
        foreach (var (key, window) in _windows)
        {
            if (window.LastSeen < cutoff && window.BlockedUntil is null) _windows.TryRemove(key, out _);
        }
    }

    private sealed class Window
    {
        public readonly object Gate = new();
        public DateTimeOffset WindowStart = DateTimeOffset.MinValue;
        public DateTimeOffset LastSeen = DateTimeOffset.MinValue;
        public DateTimeOffset? BlockedUntil;
        public int Count;
    }
}
