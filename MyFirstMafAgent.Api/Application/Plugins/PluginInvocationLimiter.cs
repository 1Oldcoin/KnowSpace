using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Application.Plugins;

/// <summary>
/// 插件调用的准入控制：<b>并发上限 + 有界队列</b>，超额立刻拒绝而不是无限排队。
///
/// <para>
/// 为什么需要它：<c>PluginProcessHost</c> 原本用 <see cref="SemaphoreSlim"/> 限制并发，
/// 但 <c>WaitAsync</c> 的排队是<b>无上限</b>的——调用方会一直挂着，直到自己的 HTTP 超时或
/// 客户端断开。对一个只允许 2 路并发的插件连续发 10 个请求，表现是「2 个跑完、8 个慢慢超时」，
/// 使用者看到的是「卡住」，不是「太忙」。
/// </para>
///
/// <para>
/// 三层限流（从外到内，任一不满足都在<b>入队前</b>就拒绝）：
/// <list type="number">
///   <item>插件队列：<c>PluginOptions.MaxQueuedInvocationsPerPlugin</c>（含等待中的调用）</item>
///   <item>能力队列：<c>PluginOptions.MaxQueuedInvocationsPerCapability</c></item>
///   <item>并发：插件级取 manifest 的 <c>limits.maxConcurrentInvocations</c>；
///         能力级取 <c>PluginOptions.CapabilityConcurrencyLimits</c>（配了才有第二道闸）</item>
/// </list>
/// </para>
///
/// <para>
/// 计数是<b>进程内</b>的：多进程部署时每个进程各自限流（“每进程上限”，不是集群上限）。
/// 与 <c>JobTypeQuota</c> 同一口径。
/// </para>
/// </summary>
public sealed class PluginInvocationLimiter : IDisposable
{
    private readonly PluginOptions _options;
    private readonly ILogger<PluginInvocationLimiter> _logger;
    private readonly ConcurrentDictionary<string, PluginSlot> _slots = new(StringComparer.Ordinal);
    private int _disposed;

    public PluginInvocationLimiter(IOptions<PluginOptions> options, ILogger<PluginInvocationLimiter> logger)
    {
        _options = options.Value;
        _logger = logger;

        // 启动时把生效值打出来：限流是最容易出现「配了没生效」的地方，
        // 没有这行日志就只能靠压测反推。
        _logger.LogInformation(
            "Plugin invocation limits: queue per plugin={PluginQueue}, queue per capability={CapabilityQueue}, capability limits={CapabilityLimits}",
            PluginQueueLimit,
            CapabilityQueueLimit,
            _options.CapabilityConcurrencyLimits.Count == 0
                ? "(none)"
                : string.Join(", ", _options.CapabilityConcurrencyLimits.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    /// <summary>插件级队列上限（等待中的调用数，不含正在运行的）。0 表示排队即拒绝。</summary>
    public int PluginQueueLimit => Math.Max(0, _options.MaxQueuedInvocationsPerPlugin);

    /// <summary>能力级队列上限。仅对配置了能力级并发的插件生效。</summary>
    public int CapabilityQueueLimit => Math.Max(0, _options.MaxQueuedInvocationsPerCapability);

    /// <summary>
    /// 登记（或校正）某插件的并发上限。插件升级换了 manifest 时由宿主重新登记；
    /// 若此刻仍有调用在跑，新上限会在空闲后生效，避免把一个满负荷的插件扩容成不一致的状态。
    /// </summary>
    public void EnsurePlugin(string pluginId, int concurrency)
    {
        var slot = _slots.GetOrAdd(pluginId, _ => new PluginSlot(Math.Max(1, concurrency)));
        slot.UpdateCapacity(Math.Max(1, concurrency), _logger, pluginId);
    }

    /// <summary>
    /// 获取一次调用的准入。失败时抛 <see cref="PluginProtocol.ErrorOverloaded"/>，
    /// 由端点映射成 429（可重试）。
    /// </summary>
    /// <param name="waitTimeout">
    /// 排队等待上限。它约束的是「排队」这一段，不含实际调用耗时——
    /// 所以一次调用最坏是 <c>waitTimeout + 调用超时</c>，而不是无限等待。
    /// </param>
    public async Task<PluginInvocationLease> AcquireAsync(
        string pluginId,
        string capabilityId,
        int pluginConcurrency,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        EnsurePlugin(pluginId, pluginConcurrency);
        var plugin = _slots[pluginId];

        var capabilityLimit = ResolveCapabilityLimit(pluginId, capabilityId);
        var capability = capabilityLimit is null
            ? null
            : plugin.GetCapability(capabilityId, capabilityLimit.Value);
        // ① 占队位：这一步是"快速失败"的关键——队满了就不必再等任何信号量。
        if (!plugin.TryEnqueue(PluginQueueLimit))
            throw Overloaded(pluginId, capabilityId,
                $"并发已满（{plugin.Capacity}），且排队已达上限 {PluginQueueLimit}。请稍后重试。");

        var pluginQueued = true;
        var capabilityQueued = false;
        var pluginRunning = false;
        var capabilityRunning = false;

        try
        {
            // ② 能力级队位（配了能力级并发才算）
            if (capability is not null)
            {
                if (!capability.TryEnqueue(CapabilityQueueLimit))
                    throw Overloaded(pluginId, capabilityId,
                        $"能力 '{capabilityId}' 并发已满（{capability.Capacity}），且排队已达上限 {CapabilityQueueLimit}。");

                capabilityQueued = true;
            }

            using var gate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (waitTimeout > TimeSpan.Zero && waitTimeout != Timeout.InfiniteTimeSpan)
                gate.CancelAfter(waitTimeout);

            // ③ 先拿“更窄”的能力闸门，再拿插件闸门。
            //    顺序不能反：若先占插件槽位再等能力槽位，一次调用会一边占着插件容量一边空等，
            //    插件级 running 也会虚高（看起来在跑，其实在排队），并且会挡住同插件的其它能力。
            if (capability is not null)
            {
                try
                {
                    await capability.Concurrency.WaitAsync(gate.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw Overloaded(pluginId, capabilityId,
                        $"能力 '{capabilityId}' 排队超过 {waitTimeout.TotalSeconds:0.#} 秒（并发 {capability.Capacity}）。");
                }

                capability.PromoteToRunning();
                capabilityQueued = false;
                capabilityRunning = true;
            }

            try
            {
                await plugin.Concurrency.WaitAsync(gate.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Overloaded(pluginId, capabilityId,
                    $"排队超过 {waitTimeout.TotalSeconds:0.#} 秒仍未获得执行机会（并发 {plugin.Capacity}，排队 {plugin.Queued}）。");
            }

            plugin.PromoteToRunning();
            pluginQueued = false;
            pluginRunning = true;

            return new PluginInvocationLease(this, pluginId, capabilityId, plugin, capability, pluginRunning, capabilityRunning);
        }
        catch
        {
            // 任何一步失败都要把已经占住的队位/闸门还回去，否则一次拒绝会永久吃掉一个名额。
            if (pluginRunning) plugin.RollbackRunning();
            else if (pluginQueued) plugin.RollbackQueued();

            if (capabilityRunning) capability?.RollbackRunning();
            else if (capabilityQueued) capability?.RollbackQueued();

            throw;
        }
    }

    /// <summary>释放一次准入（由 lease 调用）。</summary>
    internal void Release(PluginSlot plugin, CapabilitySlot? capability, bool pluginRunning, bool capabilityRunning)
    {
        if (capabilityRunning && capability is not null) capability.RollbackRunning();
        if (pluginRunning) plugin.RollbackRunning();
    }

    /// <summary>当前占用快照（运维/界面用）。</summary>
    public IReadOnlyList<PluginConcurrencySnapshot> Snapshot()
    {
        var items = new List<PluginConcurrencySnapshot>(_slots.Count);
        foreach (var (pluginId, slot) in _slots.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            items.Add(new PluginConcurrencySnapshot(
                pluginId,
                slot.Capacity,
                slot.Running,
                slot.Queued,
                PluginQueueLimit,
                slot.CapabilitySnapshot()));
        }

        return items;
    }

    /// <summary>卸载插件时丢弃计数，避免重复安装沿用旧状态。</summary>
    public void Forget(string pluginId)
    {
        if (_slots.TryRemove(pluginId, out var slot)) slot.Dispose();
    }

    private int? ResolveCapabilityLimit(string pluginId, string capabilityId)
    {
        var limits = _options.CapabilityConcurrencyLimits;
        if (limits is null || limits.Count == 0) return null;

        // 精确匹配 plugin/capability 优先，其次只写 capabilityId（对所有插件生效）。
        if (limits.TryGetValue($"{pluginId}/{capabilityId}", out var specific) && specific > 0)
            return specific;
        if (limits.TryGetValue(capabilityId, out var shared) && shared > 0)
            return shared;

        return null;
    }

    private PluginRpcException Overloaded(string pluginId, string capabilityId, string message)
    {
        _logger.LogWarning(
            "Plugin {PluginId} capability {CapabilityId} rejected: {Message}", pluginId, capabilityId, message);
        return new PluginRpcException(PluginProtocol.ErrorOverloaded, message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var slot in _slots.Values) slot.Dispose();
        _slots.Clear();
    }

    /// <summary>一次准入的凭据。释放时把并发闸门还回去；重复 Dispose 是安全的。</summary>
    public sealed class PluginInvocationLease : IDisposable
    {
        private readonly PluginInvocationLimiter _owner;
        private readonly PluginSlot _plugin;
        private readonly CapabilitySlot? _capability;
        private readonly bool _pluginRunning;
        private readonly bool _capabilityRunning;
        private int _disposed;

        internal PluginInvocationLease(
            PluginInvocationLimiter owner,
            string pluginId,
            string capabilityId,
            PluginSlot plugin,
            CapabilitySlot? capability,
            bool pluginRunning,
            bool capabilityRunning)
        {
            _owner = owner;
            PluginId = pluginId;
            CapabilityId = capabilityId;
            _plugin = plugin;
            _capability = capability;
            _pluginRunning = pluginRunning;
            _capabilityRunning = capabilityRunning;
        }

        public string PluginId { get; }

        public string CapabilityId { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Release(_plugin, _capability, _pluginRunning, _capabilityRunning);
        }
    }

    /// <summary>插件级计数器与闸门。回调内部用锁保护“换容量”和“改计数”的竞争。</summary>
    internal sealed class PluginSlot : IDisposable
    {
        private readonly object _sync = new();
        private readonly ConcurrentDictionary<string, CapabilitySlot> _capabilities = new(StringComparer.Ordinal);

        private SemaphoreSlim _concurrency;
        private int _capacity;
        private int _queued;
        private int _running;
        private bool _disposed;

        public PluginSlot(int capacity)
        {
            _capacity = capacity;
            _concurrency = new SemaphoreSlim(capacity, capacity);
        }

        public SemaphoreSlim Concurrency => Volatile.Read(ref _concurrency);

        public int Capacity { get { lock (_sync) return _capacity; } }

        public int Queued => Volatile.Read(ref _queued);

        public int Running => Volatile.Read(ref _running);

        public void UpdateCapacity(int capacity, ILogger logger, string pluginId)
        {
            lock (_sync)
            {
                if (_disposed || capacity == _capacity) return;

                // 只有完全空闲才换闸门：半途替换会让已持有旧闸门的调用无法按原路释放。
                if (_running != 0 || _queued != 0)
                {
                    logger.LogDebug(
                        "Plugin {PluginId} concurrency {Current} -> {Next} deferred until idle ({Running} running, {Queued} queued)",
                        pluginId, _capacity, capacity, _running, _queued);
                    return;
                }

                var previous = _concurrency;
                _concurrency = new SemaphoreSlim(capacity, capacity);
                _capacity = capacity;
                previous.Dispose();
            }
        }

        public bool TryEnqueue(int queueLimit)
        {
            lock (_sync)
            {
                if (_disposed) return false;

                // 在途 = 正在运行 + 已准入未运行。上限 = 并发 + 队列。
                // 这个写法让 queueLimit=0 变成“没有空闲槽位就拒绝”，而不是“总是拒绝”。
                if (_running + _queued >= _capacity + queueLimit) return false;
                _queued++;
                return true;
            }
        }

        public void RollbackQueued()
        {
            lock (_sync)
            {
                if (_queued > 0) _queued--;
            }
        }

        public void PromoteToRunning()
        {
            lock (_sync)
            {
                if (_queued > 0) _queued--;
                _running++;
            }
        }

        public void RollbackRunning()
        {
            lock (_sync)
            {
                if (_running > 0) _running--;
            }

            // 闸门按「进去一次、出来一次」成对释放，与计数是否下溢无关。
            Concurrency.Release();
        }

        public CapabilitySlot GetCapability(string capabilityId, int limit) =>
            _capabilities.GetOrAdd(capabilityId, id => new CapabilitySlot(id, limit));

        public IReadOnlyList<PluginCapabilityConcurrency> CapabilitySnapshot() =>
            _capabilities
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new PluginCapabilityConcurrency(
                    pair.Key, pair.Value.Capacity, pair.Value.Running, pair.Value.Queued))
                .ToList();

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }

            foreach (var capability in _capabilities.Values) capability.Dispose();
            _capabilities.Clear();
            _concurrency.Dispose();
        }
    }

    /// <summary>能力级计数器与闸门。</summary>
    internal sealed class CapabilitySlot : IDisposable
    {
        private readonly object _sync = new();
        private readonly int _capacity;
        private readonly SemaphoreSlim _concurrency;
        private int _queued;
        private int _running;

        public CapabilitySlot(string capabilityId, int capacity)
        {
            CapabilityId = capabilityId;
            _capacity = capacity;
            _concurrency = new SemaphoreSlim(capacity, capacity);
        }

        public string CapabilityId { get; }

        public SemaphoreSlim Concurrency => _concurrency;

        public int Capacity => _capacity;

        public int Queued => Volatile.Read(ref _queued);

        public int Running => Volatile.Read(ref _running);

        public bool TryEnqueue(int queueLimit)
        {
            lock (_sync)
            {
                if (_running + _queued >= _capacity + queueLimit) return false;
                _queued++;
                return true;
            }
        }

        public void RollbackQueued()
        {
            lock (_sync)
            {
                if (_queued > 0) _queued--;
            }
        }

        public void PromoteToRunning()
        {
            lock (_sync)
            {
                if (_queued > 0) _queued--;
                _running++;
            }
        }

        public void RollbackRunning()
        {
            lock (_sync)
            {
                if (_running > 0) _running--;
            }

            _concurrency.Release();
        }

        public void Dispose() => _concurrency.Dispose();
    }
}

/// <summary>一个插件的并发占用。</summary>
public sealed record PluginConcurrencySnapshot(
    string PluginId,
    int ConcurrencyLimit,
    int Running,
    int Queued,
    int QueueLimit,
    IReadOnlyList<PluginCapabilityConcurrency> Capabilities);

/// <summary>一个能力的并发占用。</summary>
public sealed record PluginCapabilityConcurrency(
    string CapabilityId,
    int ConcurrencyLimit,
    int Running,
    int Queued);
