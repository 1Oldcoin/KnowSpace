using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 一个插件子进程的宿主。进程隔离用于故障隔离——插件崩溃、死循环、
/// 协议错误只会影响它自己，不会拖垮 API。
///
/// <para>
/// 注意：同一 OS 用户下的子进程<b>不构成</b>恶意代码安全沙箱。
/// 权限最小化、签名校验与受限令牌属于发布阻断项（见阶段 4）。
/// </para>
/// </summary>
public sealed class PluginProcessHost : IAsyncDisposable
{
    private readonly PluginManifestLoadResult _load;
    private readonly PluginOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly string? _dataDirectory;

    private Process? _process;
    private JsonRpcPeer? _peer;
    private CancellationTokenSource? _lifetime;
    private PluginHealth? _lastHealth;
    private int _disposed;

    /// <summary>正在执行的能力调用数量。排空需要等它归零。</summary>
    private int _inFlight;

    /// <summary>排空标志：置位后拒绝新调用，只等已发出的调用结束。</summary>
    private int _draining;

    public PluginProcessHost(
        PluginManifestLoadResult load,
        PluginOptions options,
        string? dataDirectory,
        ILogger logger)
    {
        _load = load;
        _options = options;
        _dataDirectory = dataDirectory;
        _logger = logger;
        _concurrency = new SemaphoreSlim(
            Math.Max(1, load.Manifest?.Limits?.MaxConcurrentInvocations ?? 2));
    }

    public string Id => _load.Id;

    /// <summary>当前生效的包目录（版本切换后会变）。</summary>
    public string Directory => _load.Directory;

    public PluginManifest Manifest => _load.Manifest ?? throw new InvalidOperationException("manifest is missing");

    public PluginState State { get; private set; } = PluginState.Discovered;

    public string? LastError { get; private set; }

    public IReadOnlyList<PluginCapability> Capabilities { get; private set; } = [];

    public PluginHealth? LastHealth => _lastHealth;

    /// <summary>当前正在执行的调用数。</summary>
    public int InFlightInvocations => Volatile.Read(ref _inFlight);

    /// <summary>是否已经进入排空状态（不再接受新调用）。</summary>
    public bool IsDraining => Volatile.Read(ref _draining) != 0;

    /// <summary>当前生效的安装态（启用、配置、授权）。启动前由 <see cref="PluginManager"/> 注入。</summary>
    public PluginRegistration? Registration { get; private set; }

    /// <summary>按 manifest 声明的最小间隔做健康检查节流，避免调用方刷爆插件。</summary>
    private DateTimeOffset _lastHealthAt = DateTimeOffset.MinValue;

    /// <summary>启动插件进程并完成握手。<paramref name="registration"/> 提供用户配置，握手后会下发一次。</summary>
    public async Task StartAsync(PluginRegistration? registration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (State is PluginState.Running or PluginState.Starting) return;
        if (!_load.IsUsable)
            throw new InvalidOperationException($"plugin '{Id}' is not usable: {string.Join("; ", _load.Validation.Errors)}");

        Registration = registration;
        State = PluginState.Starting;
        _lifetime = new CancellationTokenSource();
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.StartupTimeoutSeconds)));

        try
        {
            var startInfo = BuildStartInfo();
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;

            if (!_process.Start())
                throw new InvalidOperationException("process did not start");

            _logger.LogInformation("Plugin {PluginId} process started (pid {Pid})", Id, _process.Id);

            DrainStandardError(_process);

            _peer = new JsonRpcPeer(
                _process.StandardOutput.BaseStream,
                _process.StandardInput.BaseStream,
                handler: null,
                log: message => _logger.LogDebug("Plugin {PluginId} protocol: {Message}", Id, message));
            _peer.NotificationReceived += OnNotification;
            _peer.Start();

            var handshake = await SendInitializeAsync(startupTimeout.Token).ConfigureAwait(false);

            Capabilities = handshake.Capabilities;
            _lastHealth = PluginHealth.Healthy();
            State = PluginState.Running;
            LastError = null;

            _logger.LogInformation(
                "Plugin {PluginId} v{Version} ready with {CapabilityCount} capability(ies): {Capabilities}",
                Id,
                Manifest.Version,
                Capabilities.Count,
                string.Join(", ", Capabilities.Select(capability => capability.Id)));

            // 用户配置在握手后下发：插件可能在 configure 里做连通性检查。
            await ApplyConfigurationAsync(registration, startupTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            State = PluginState.Failed;
            _logger.LogError(exception, "Plugin {PluginId} failed to start", Id);
            await TearDownProcessAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(string PluginId, string PluginVersion, IReadOnlyList<PluginCapability> Capabilities)> SendInitializeAsync(
        CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["protocolVersion"] = PluginProtocol.ProtocolVersion,
            ["coreVersion"] = typeof(PluginProcessHost).Assembly.GetName().Version?.ToString(),
            ["dataDirectory"] = _dataDirectory,
            ["config"] = ParseConfig(Registration?.ConfigJson)
        };

        var result = await Peer.SendRequestAsync(
            PluginProtocol.MethodInitialize,
            parameters,
            timeout: TimeSpan.FromSeconds(Math.Max(5, _options.StartupTimeoutSeconds)),
            cancellationToken).ConfigureAwait(false)
            ?? throw new PluginRpcException(PluginProtocol.ErrorInternal, "plugin returned an empty initialize result");

        var pluginId = result["pluginId"]?.GetValue<string>();
        var pluginVersion = result["pluginVersion"]?.GetValue<string>();

        // 运行期自述必须与 manifest 一致，否则视为冒充。
        if (!string.Equals(pluginId, Manifest.Id, StringComparison.Ordinal))
            throw new PluginRpcException(
                PluginProtocol.ErrorIncompatibleProtocol,
                $"plugin reported id '{pluginId}' but manifest declares '{Manifest.Id}'");

        if (!string.Equals(pluginVersion, Manifest.Version, StringComparison.Ordinal))
            throw new PluginRpcException(
                PluginProtocol.ErrorIncompatibleProtocol,
                $"plugin reported version '{pluginVersion}' but manifest declares '{Manifest.Version}'");

        var reported = PluginJson.ToObject<IReadOnlyList<PluginCapability>>(result["capabilities"]) ?? [];

        // 只承认 manifest 里声明过的能力：插件不能运行期"发明"新能力。
        var declared = Manifest.Capabilities.Select(capability => capability.Id).ToHashSet(StringComparer.Ordinal);
        var unexpected = reported.Select(capability => capability.Id).Where(id => !declared.Contains(id)).ToList();
        if (unexpected.Count > 0)
            throw new PluginRpcException(
                PluginProtocol.ErrorIncompatibleProtocol,
                $"plugin reported undeclared capabilities: {string.Join(", ", unexpected)}");

        var effective = Manifest.Capabilities
            .Where(capability => reported.Count == 0 || reported.Any(item => item.Id == capability.Id))
            .ToList();

        return (pluginId!, pluginVersion!, effective);
    }

    /// <summary>
    /// 把用户配置下发给插件。未配置时跳过；
    /// 配置被插件拒绝时视为启动失败——半个配置的插件比不启动更危险。
    /// </summary>
    private async Task ApplyConfigurationAsync(PluginRegistration? registration, CancellationToken cancellationToken)
    {
        var config = ParseConfig(registration?.ConfigJson);
        if (config is null) return;

        try
        {
            await Peer.SendRequestAsync(
                PluginProtocol.MethodConfigure,
                new JsonObject { ["config"] = config },
                TimeSpan.FromSeconds(Math.Max(5, _options.StartupTimeoutSeconds)),
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Plugin {PluginId} accepted its configuration", Id);
        }
        catch (PluginRpcException exception)
        {
            throw new PluginRpcException(
                exception.Code,
                $"插件拒绝了配置：{exception.Message}",
                exception.ErrorData);
        }
    }

    private static JsonNode? ParseConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;

        try
        {
            return JsonNode.Parse(configJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (State is PluginState.Failed or PluginState.Stopped or PluginState.Discovered
            or PluginState.Disabled or PluginState.NeedsConfiguration or PluginState.PermissionRequired)
            return PluginHealth.Unhealthy(LastError ?? "plugin is not running");

        var interval = HealthInterval;
        if (DateTimeOffset.UtcNow - _lastHealthAt < interval)
            return _lastHealth ?? PluginHealth.Healthy();

        try
        {
            var node = await Peer.SendRequestAsync(
                PluginProtocol.MethodHealth,
                null,
                timeout: TimeSpan.FromMilliseconds(Manifest.HealthCheck?.TimeoutMs ?? 5000),
                cancellationToken).ConfigureAwait(false);

            var health = PluginJson.ToObject<PluginHealth>(node) ?? PluginHealth.Healthy();
            _lastHealth = health;
            _lastHealthAt = DateTimeOffset.UtcNow;
            State = health.IsUsable ? PluginState.Running : PluginState.Degraded;
            return health;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _lastHealth = PluginHealth.Unhealthy(exception.Message);
            _lastHealthAt = DateTimeOffset.UtcNow;
            State = PluginState.Degraded;
            _logger.LogWarning(exception, "Plugin {PluginId} health check failed", Id);
            return _lastHealth;
        }
    }

    private TimeSpan HealthInterval =>
        TimeSpan.FromSeconds(Math.Clamp(
            Manifest.HealthCheck?.IntervalSeconds ?? _options.HealthIntervalSeconds,
            5,
            Math.Max(5, _options.HealthIntervalSeconds)));

    /// <summary>调用一项能力。超时/取消会向插件发送取消通知。</summary>
    public async Task<PluginInvocationResult> InvokeAsync(
        string capabilityId,
        JsonNode? arguments,
        CancellationToken cancellationToken)
    {
        var capability = Capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, capabilityId, StringComparison.Ordinal));

        if (capability is null)
            throw new PluginRpcException(
                PluginProtocol.ErrorUnknownCapability,
                $"capability '{capabilityId}' is not declared by plugin '{Id}'");

        if (State is not (PluginState.Running or PluginState.Degraded))
            throw new PluginRpcException(PluginProtocol.ErrorUnhealthy, $"plugin '{Id}' is not available ({State})");

        // 排空期间新调用必须立刻失败，而不是排队等一个即将被替换的进程：
        // 否则升级时调用方会先卡住、再收到“连接已断开”。
        if (IsDraining)
            throw new PluginRpcException(
                PluginProtocol.ErrorUnhealthy,
                $"plugin '{Id}' is draining for an upgrade; retry later");

        var timeoutMs = Math.Clamp(
            Manifest.Limits?.InvocationTimeoutMs ?? 30_000,
            1000,
            Math.Max(1000, _options.MaxInvocationTimeoutMs));

        var started = Stopwatch.StartNew();
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _inFlight);
        try
        {
            var parameters = new JsonObject
            {
                ["capabilityId"] = capabilityId,
                ["arguments"] = arguments
            };

            var response = await Peer.SendRequestAsync(
                PluginProtocol.MethodInvoke,
                parameters,
                TimeSpan.FromMilliseconds(timeoutMs),
                cancellationToken).ConfigureAwait(false);

            started.Stop();
            _logger.LogInformation(
                "Plugin {PluginId} capability {CapabilityId} completed in {Elapsed} ms (sideEffect={SideEffect})",
                Id, capabilityId, started.ElapsedMilliseconds, capability.SideEffect);

            return new PluginInvocationResult(Id, capabilityId, response?["result"], started.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Plugin {PluginId} capability {CapabilityId} failed", Id, capabilityId);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _concurrency.Release();
        }
    }

    /// <summary>
    /// 进入排空状态：不再接受新调用。
    ///
    /// <para>
    /// 为什么要在升级前先排空而不是直接杀进程：升级必然要换掉进程（Windows 上运行中的 exe 删不掉），
    /// 但正在跑的一次同步/工具调用如果被硬断，对调用方就是一次无解释的失败。
    /// 排空把“中断”变成“等它做完，然后后面的请求去新版本”。
    /// </para>
    /// </summary>
    public void BeginDrain()
    {
        if (Interlocked.Exchange(ref _draining, 1) == 0 && State is PluginState.Running or PluginState.Degraded)
        {
            State = PluginState.Draining;
            _logger.LogInformation("Plugin {PluginId} entered draining ({InFlight} invocation(s) in flight)", Id, InFlightInvocations);
        }
    }

    /// <summary>
    /// 等待正在执行的调用结束；返回 false 表示超时（调用方应自行决定强制停止或保留进程）。
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        BeginDrain();

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (InFlightInvocations > 0)
        {
            if (DateTimeOffset.UtcNow >= deadline || cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Plugin {PluginId} drain timed out with {InFlight} invocation(s) still running",
                    Id,
                    InFlightInvocations);
                return false;
            }

            try
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>退出排空状态（启动新的实例之前必须清掉，否则新进程会拒绝一切调用）。</summary>
    public void EndDrain()
    {
        Interlocked.Exchange(ref _draining, 0);
        if (State is PluginState.Draining) State = PluginState.Stopped;
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var manifest = Manifest;
        var entry = manifest.Entry!;
        var workingDirectory = string.IsNullOrWhiteSpace(entry.WorkingDirectory)
            ? _load.Directory
            : Path.GetFullPath(Path.Combine(_load.Directory, entry.WorkingDirectory));

        var command = entry.Command;
        var isHostCommand = PluginEntryCommand.IsHostCommand(_load.Directory, command);
        var executable = isHostCommand ? command : Path.GetFullPath(Path.Combine(_load.Directory, command));

        // Linux/macOS 上从 zip 解出来的原生插件会丢 +x 位，不补上就直接 Permission denied。
        if (!isHostCommand) PlatformRuntime.EnsureExecutable(executable);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        foreach (var argument in entry.Arguments)
            startInfo.ArgumentList.Add(argument);

        // 插件只能看到自己的目录与授权数据目录，不继承核心的连接串等机密。
        startInfo.Environment["MYFIRSTMAFAGENT_PLUGIN_ID"] = manifest.Id;
        startInfo.Environment["MYFIRSTMAFAGENT_PROTOCOL"] = PluginProtocol.ProtocolVersion;
        if (_dataDirectory is not null)
            startInfo.Environment["MYFIRSTMAFAGENT_PLUGIN_DATA"] = _dataDirectory;

        return startInfo;
    }

    private void DrainStandardError(Process process)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 0)
                        _logger.LogInformation("Plugin {PluginId}: {Line}", Id, line);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // 进程退出时管道关闭，属正常情况。
            }
        }, CancellationToken.None);
    }

    private void OnNotification(string method, JsonNode? parameters)
    {
        if (method == PluginProtocol.MethodLog)
        {
            var level = parameters?["level"]?.GetValue<string>() ?? "info";
            var message = parameters?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
                _logger.LogInformation("Plugin {PluginId} [{Level}] {Message}", Id, level, message);

            return;
        }

        _logger.LogDebug("Plugin {PluginId} notification {Method}", Id, method);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = -1;
        try { exitCode = _process?.ExitCode ?? -1; }
        catch (InvalidOperationException) { }

        if (State is PluginState.Stopped || Volatile.Read(ref _disposed) != 0) return;

        LastError = $"plugin process exited with code {exitCode}";
        _lastHealth = PluginHealth.Unhealthy(LastError);
        State = PluginState.Failed;
        _logger.LogError("Plugin {PluginId} exited unexpectedly with code {ExitCode}", Id, exitCode);
    }

    private JsonRpcPeer Peer => _peer ?? throw new PluginRpcException(
        PluginProtocol.ErrorUnhealthy,
        $"plugin '{Id}' has no active connection");

    /// <summary>
    /// 由管理器标记“未启动”的状态（已停用、缺配置、缺权限）。
    /// 这些状态下没有进程，状态即为最终状态。
    /// </summary>
    public void MarkNotStarted(PluginState state, string? reason)
    {
        State = state;
        LastError = reason;
        Capabilities = [];
        Interlocked.Exchange(ref _draining, 0);
        _lastHealth = state is PluginState.Disabled ? null : PluginHealth.Unhealthy(reason ?? state.ToString());
    }

    public async Task StopAsync()
    {
        if (State is PluginState.Stopped or PluginState.Discovered or PluginState.Disabled
            or PluginState.NeedsConfiguration or PluginState.PermissionRequired) return;

        try
        {
            if (_peer is not null && State is PluginState.Running or PluginState.Degraded)
            {
                // 先请插件自己退出，给它机会刷新缓冲。
                await _peer.SendRequestAsync(
                    PluginProtocol.MethodShutdown,
                    null,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug("Plugin {PluginId} shutdown request failed: {Message}", Id, exception.Message);
        }

        State = PluginState.Stopped;
        await TearDownProcessAsync().ConfigureAwait(false);
        _logger.LogInformation("Plugin {PluginId} stopped", Id);
    }

    private async Task TearDownProcessAsync()
    {
        // 进程没了，排空状态也必须清掉：下一个进程重新开始计数。
        Interlocked.Exchange(ref _draining, 0);

        if (_peer is not null)
        {
            _peer.NotificationReceived -= OnNotification;
            try { await _peer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* 关闭阶段异常忽略 */ }
            _peer = null;
        }

        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            try
            {
                if (!_process.HasExited)
                {
                    if (!_process.WaitForExit(3000))
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit(3000);
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug("Plugin {PluginId} process teardown: {Message}", Id, exception.Message);
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        var lifetime = _lifetime;
        _lifetime = null;
        if (lifetime is not null)
        {
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            lifetime.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _concurrency.Dispose();
    }
}
