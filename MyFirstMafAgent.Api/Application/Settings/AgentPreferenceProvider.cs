using MyFirstMafAgent.Api.Domain.Settings;

namespace MyFirstMafAgent.Api.Application.Settings;

/// <summary>
/// 用户级智能体开关的读取端口。工具集构建发生在每一次聊天请求里，
/// 不能每轮都查一次库，因此这里做一层短时内存缓存；设置保存后调用 <see cref="Invalidate"/> 立即失效。
/// </summary>
public sealed class AgentPreferenceProvider(ISettingsRepository repository)
{
    /// <summary>缓存有效期：既能让多实例/外部改动较快生效，又能避免逐轮查库。</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentPreferences? _cached;
    private DateTimeOffset _cachedAt;
    private long _version;

    /// <summary>读取当前用户的智能体开关；未保存过设置时返回 <see cref="AgentPreferences.Default"/>。</summary>
    public async Task<AgentPreferences> GetAsync(CancellationToken cancellationToken)
    {
        var snapshot = _cached;
        if (snapshot is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration) return snapshot;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 双重检查：等待锁期间可能已被其他请求刷新，或设置刚被保存。
            snapshot = _cached;
            if (snapshot is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration) return snapshot;

            var version = _version;
            var stored = await repository.GetAsync(cancellationToken);
            var preferences = stored?.Agent ?? AgentPreferences.Default;

            // 查询期间设置被保存过时不要写入旧值，直接把新值返回给调用方。
            if (version == _version)
            {
                _cached = preferences;
                _cachedAt = DateTimeOffset.UtcNow;
            }

            return preferences;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>设置保存后失效缓存，使下一次读取立即拿到新开关。</summary>
    public void Invalidate()
    {
        _cached = null;
        _cachedAt = default;
        Interlocked.Increment(ref _version);
    }
}
