using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Plugins.Isolation;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 一个插件子进程的宿主。两层隔离：
/// <list type="number">
///   <item><b>故障隔离</b>：插件崩溃、死循环、协议错误只影响它自己，拖不垮 API。</item>
///   <item><b>权限隔离</b>：Windows 上用 AppContainer 启动，令牌里没有 <c>network</c> 能力就
///   <b>真的没有出站网络</b>；未授权的目录也真的读不到（见 <c>Plugins/Isolation</c>）。</item>
/// </list>
/// </summary>
public sealed class PluginProcessHost : IAsyncDisposable
{
    private readonly PluginManifestLoadResult _load;
    private readonly PluginOptions _options;
    private readonly PluginInvocationLimiter _limiter;
    private readonly ILogger _logger;
    private readonly string? _dataDirectory;
    private readonly string _pluginsRoot;

    private PluginProcess? _process;
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
        PluginInvocationLimiter limiter,
        ILogger logger,
        string? pluginsRoot = null)
    {
        _load = load;
        _options = options;
        _dataDirectory = dataDirectory;
        _limiter = limiter;
        _logger = logger;
        _pluginsRoot = pluginsRoot ?? Path.GetDirectoryName(load.Directory) ?? load.Directory;

        // 并发上限仍然由 manifest 决定；具体闸门与队列由 limiter 统一持有（跨版本切换也不会漏配额）。
        _limiter.EnsurePlugin(_load.Id, ConcurrencyCapacity);
    }

    /// <summary>本插件允许的并发数（来自 manifest 的 <c>limits.maxConcurrentInvocations</c>）。</summary>
    public int ConcurrencyCapacity => Math.Max(1, _load.Manifest?.Limits?.MaxConcurrentInvocations ?? 2);

    public string Id => _load.Id;

    /// <summary>当前生效的包目录（版本切换后会变）。</summary>
    public string Directory => _load.Directory;

    public PluginManifest Manifest => _load.Manifest ?? throw new InvalidOperationException("manifest is missing");

    /// <summary>生效的对话方言：<c>plugin</c>（自有协议）或 <c>mcp</c>。</summary>
    public string Dialect => PluginDialects.IsMcp(Manifest.Dialect) ? PluginDialects.Mcp : PluginDialects.Native;

    /// <summary>是否用标准 MCP 方法名。握手、健康检查、调用三处会走不同分支。</summary>
    private bool IsMcp => PluginDialects.IsMcp(Manifest.Dialect);

    /// <summary>
    /// MCP 工具名 → 内部能力 ID。工具名允许大写与任意字符，能力 ID 不行，
    /// 所以两边不是同一个字符串，调用时必须查回来。
    /// </summary>
    private readonly Dictionary<string, string> _mcpToolNames = new(StringComparer.Ordinal);

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

    /// <summary>
    /// 本次启动的隔离实况。<see cref="PluginState.Running"/> 为 false 时是"未启动"，
    /// 而不是"已隔离"——这个区别会直接回显到接口，不允许含糊。
    /// </summary>
    public PluginIsolationReport Isolation { get; private set; } =
        PluginIsolationReport.NotStarted("unknown", "尚未启动");

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
            var spec = BuildStartSpec();
            var launch = PluginProcessFactory.Start(
                spec,
                _options.Isolation,
                PluginProcessFactory.RuntimeRoot(_pluginsRoot),
                ResolveRoots(_options.Isolation.ReadableRoots),
                ResolveRoots(_options.Isolation.WritableRoots),
                _logger);

            Isolation = launch.Isolation;

            if (!launch.Success)
                throw new InvalidOperationException(launch.Error ?? "插件进程未能启动");

            _process = launch.Process!;
            _process.Exited += OnProcessExited;

            _logger.LogInformation("Plugin {PluginId} process started (pid {Pid})", Id, _process.Id);

            _ = _process.PumpErrorAsync(
                line =>
                {
                    _logger.LogInformation("Plugin {PluginId}: {Line}", Id, line);
                    return true;
                },
                _lifetime?.Token ?? CancellationToken.None);

            _peer = new JsonRpcPeer(
                _process.Output,
                _process.Input,
                handler: null,
                log: message => _logger.LogDebug("Plugin {PluginId} protocol: {Message}", Id, message));
            _peer.NotificationReceived += OnNotification;

            if (IsMcp)
            {
                // MCP 的取消通知叫 notifications/cancelled，参数是 { requestId, reason }。
                _peer.CancelNotificationMethod = McpProtocol.MethodCancelled;
                _peer.CancelNotificationParameters = id => new JsonObject
                {
                    ["requestId"] = JsonNode.Parse(id.ToJsonString()),
                    ["reason"] = "cancelled by host"
                };
            }

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
        if (IsMcp) return await SendMcpInitializeAsync(cancellationToken).ConfigureAwait(false);

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
    /// MCP 握手：<c>initialize</c> → <c>notifications/initialized</c> → <c>tools/list</c>。
    ///
    /// <para>
    /// 与自有协议的两点关键差别：
    /// 一是 <b>能力不来自清单而来自 <c>tools/list</c></b>，所以清单里 <c>capabilities</c>
    /// 可以为空（甚至完全不写）；二是 <c>serverInfo</c> 里的 name/version 不是反向域名 ID，
    /// 也无法与清单版本对齐，因此只记录不对账——对账会把所有现成 MCP server 挡在门外。
    /// </para>
    /// </summary>
    private async Task<(string PluginId, string PluginVersion, IReadOnlyList<PluginCapability> Capabilities)> SendMcpInitializeAsync(
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(5, _options.StartupTimeoutSeconds));

        var result = await Peer.SendRequestAsync(
            McpProtocol.MethodInitialize,
            new JsonObject
            {
                ["protocolVersion"] = McpProtocol.Version,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "MyFirstMafAgent",
                    ["version"] = typeof(PluginProcessHost).Assembly.GetName().Version?.ToString()
                }
            },
            timeout,
            cancellationToken).ConfigureAwait(false)
            ?? throw new PluginRpcException(PluginProtocol.ErrorInternal, "MCP server returned an empty initialize result");

        var serverInfo = result["serverInfo"];
        _logger.LogInformation(
            "MCP server {PluginId} introduced itself as '{ServerName}' {ServerVersion} (protocol {ProtocolVersion})",
            Id,
            serverInfo?["name"]?.GetValue<string>() ?? "unknown",
            serverInfo?["version"]?.GetValue<string>() ?? "?",
            result["protocolVersion"]?.GetValue<string>() ?? "?");

        // 少发这条通知，部分 server 会把后续请求当成"未初始化"直接拒绝。
        await Peer.SendNotificationAsync(McpProtocol.MethodInitialized, null, cancellationToken).ConfigureAwait(false);

        var tools = await ListMcpToolsAsync(timeout, cancellationToken).ConfigureAwait(false);
        var capabilities = BuildMcpCapabilities(tools);

        if (capabilities.Count == 0)
            _logger.LogWarning("MCP server {PluginId} exposed no tools; the plugin will have no usable capability", Id);

        // MCP 没有"插件版本"的概念，身份以清单为准。
        return (Manifest.Id, Manifest.Version, capabilities);
    }

    /// <summary>
    /// 拉取工具列表。MCP 用 <c>nextCursor</c> 分页，页数设上限是为了防止对端给一个永不结束的游标。
    /// </summary>
    private async Task<IReadOnlyList<McpToolDescriptor>> ListMcpToolsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tools = new List<McpToolDescriptor>();
        string? cursor = null;

        for (var page = 0; page < 20; page++)
        {
            var parameters = string.IsNullOrWhiteSpace(cursor)
                ? null
                : new JsonObject { ["cursor"] = cursor };

            var result = await Peer.SendRequestAsync(McpProtocol.MethodListTools, parameters, timeout, cancellationToken)
                .ConfigureAwait(false);

            if (result?["tools"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    var name = item?["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    tools.Add(new McpToolDescriptor(
                        name.Trim(),
                        item?["description"]?.GetValue<string>(),
                        item?["inputSchema"]?.ToJsonString(),
                        item?["annotations"]));
                }
            }

            cursor = result?["nextCursor"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(cursor)) break;
        }

        return tools;
    }

    /// <summary>
    /// 把 MCP 工具映射成 <c>agent-tool</c> 能力。
    ///
    /// <para>
    /// 副作用等级优先取 MCP 自己的 <c>annotations</c>（<c>readOnlyHint</c> /
    /// <c>destructiveHint</c>），因为这是 server 唯一的自述；清单里同名的能力声明可以覆盖它，
    /// 这样"零配置能用"与"要精细控制也能配"两件事不冲突。
    /// </para>
    /// </summary>
    private IReadOnlyList<PluginCapability> BuildMcpCapabilities(IReadOnlyList<McpToolDescriptor> tools)
    {
        var declared = Manifest.Capabilities.ToDictionary(capability => capability.Id, StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var capabilities = new List<PluginCapability>(tools.Count);

        _mcpToolNames.Clear();

        foreach (var tool in tools)
        {
            var id = MakeUniqueCapabilityId(tool.Name, usedIds);
            _mcpToolNames[id] = tool.Name;

            if (declared.TryGetValue(id, out var declaredCapability)
                || declared.TryGetValue(tool.Name, out declaredCapability))
            {
                // 清单里可以只写"这个工具的副作用等级"，其余（名字/描述/schema）仍以 server 为准。
                capabilities.Add(declaredCapability with
                {
                    Id = id,
                    Parameters = string.IsNullOrWhiteSpace(declaredCapability.Parameters)
                        ? tool.InputSchema
                        : declaredCapability.Parameters
                });
                continue;
            }

            capabilities.Add(new PluginCapability
            {
                Id = id,
                Kind = PluginCapabilityKinds.AgentTool,
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.InputSchema,
                Permissions = Manifest.Permissions,
                SideEffect = InferSideEffect(tool.Annotations)
            });
        }

        return capabilities;
    }

    /// <summary>MCP 的 annotations 是"提示"而非保证；判不准时按更保守的一档处理。</summary>
    private static string InferSideEffect(JsonNode? annotations)
    {
        if (annotations is null) return PluginSideEffectLevels.Read;

        if (annotations["destructiveHint"]?.GetValue<bool>() == true)
            return PluginSideEffectLevels.Destructive;

        if (annotations["readOnlyHint"]?.GetValue<bool>() == false)
            return PluginSideEffectLevels.WriteLocal;

        return PluginSideEffectLevels.Read;
    }

    /// <summary>能力 ID 必须是小写且只含 <c>a-z0-9._-</c>，重名时加序号。</summary>
    private static string MakeUniqueCapabilityId(string toolName, HashSet<string> usedIds)
    {
        var baseId = SanitizeCapabilityId(toolName);
        var candidate = baseId;
        var suffix = 2;

        while (!usedIds.Add(candidate))
        {
            candidate = $"{baseId}-{suffix++}";
            if (candidate.Length > 64) candidate = candidate[^64..];
        }

        return candidate;
    }

    private static string SanitizeCapabilityId(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            builder.Append(character < 128 && (char.IsLetterOrDigit(character) || character is '.' or '_' or '-')
                ? character
                : '-');
        }

        var sanitized = builder.ToString().Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "tool" : sanitized;
    }

    /// <summary>MCP tools/list 里一个工具的宿主侧投影。</summary>
    private sealed record McpToolDescriptor(
        string Name,
        string? Description,
        string? InputSchema,
        JsonNode? Annotations);

    /// <summary>
    /// 把用户配置下发给插件。未配置时跳过；
    /// 配置被插件拒绝时视为启动失败——半个配置的插件比不启动更危险。
    /// </summary>
    private async Task ApplyConfigurationAsync(PluginRegistration? registration, CancellationToken cancellationToken)
    {
        var config = ParseConfig(registration?.ConfigJson);
        if (config is null) return;

        // MCP 没有 configure 方法：配置在启动时以环境变量下发（见 BuildStartInfo），
        // 改了配置会重启进程，因此这里无事可做。
        if (IsMcp)
        {
            _logger.LogInformation(
                "Plugin {PluginId} is an MCP server; configuration was passed as environment variables at launch",
                Id);
            return;
        }

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
                IsMcp ? McpProtocol.MethodPing : PluginProtocol.MethodHealth,
                null,
                timeout: TimeSpan.FromMilliseconds(Manifest.HealthCheck?.TimeoutMs ?? 5000),
                cancellationToken).ConfigureAwait(false);

            // MCP 的 ping 只回一个空对象，没有"状态"可读。
            if (IsMcp)
            {
                _lastHealth = PluginHealth.Healthy();
                _lastHealthAt = DateTimeOffset.UtcNow;
                State = PluginState.Running;
                return _lastHealth;
            }

            var health = PluginJson.ToObject<PluginHealth>(node) ?? PluginHealth.Healthy();
            _lastHealth = health;
            _lastHealthAt = DateTimeOffset.UtcNow;
            State = health.IsUsable ? PluginState.Running : PluginState.Degraded;
            return health;
        }
        catch (PluginRpcException exception) when (IsMcp && exception.Code == PluginProtocol.ErrorMethodNotFound)
        {
            // 不是所有 MCP server 都实现了 ping；"没这个方法"不等于"不健康"。
            _lastHealth = PluginHealth.Healthy();
            _lastHealthAt = DateTimeOffset.UtcNow;
            State = PluginState.Running;
            return _lastHealth;
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

        // 准入控制：并发 + 有界队列。队满/排队超时都以 ErrorOverloaded 快速失败（端点映射 429），
        // 而不是让调用方一直挂在 WaitAsync 上直到自己超时。
        using var lease = await _limiter
            .AcquireAsync(Id, capabilityId, ConcurrencyCapacity, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
            .ConfigureAwait(false);

        // 计时从拿到执行机会开始：排队时间不该算进插件的执行耗时（审计里的 elapsed 要能反映插件本身）。
        var started = Stopwatch.StartNew();

        Interlocked.Increment(ref _inFlight);
        try
        {
            if (IsMcp)
            {
                var mcpResult = await CallMcpToolAsync(capabilityId, arguments, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
                    .ConfigureAwait(false);

                started.Stop();
                _logger.LogInformation(
                    "Plugin {PluginId} MCP tool {CapabilityId} completed in {Elapsed} ms (sideEffect={SideEffect})",
                    Id, capabilityId, started.ElapsedMilliseconds, capability.SideEffect);

                return new PluginInvocationResult(Id, capabilityId, mcpResult, started.ElapsedMilliseconds);
            }

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
        }
    }

    /// <summary>
    /// 调用一个 MCP 工具，把 <c>content</c> 数组压平成一段文本。
    ///
    /// <para>
    /// 为什么要压平：模型消费的是文本，而 MCP 的返回值是"内容块"数组
    /// （text / image / resource）。把整个数组原样丢给模型只会浪费 token 且难以理解；
    /// 非文本块用一行占位符表示（我们能看图，但工具结果这条路目前只给模型文字）。
    /// </para>
    /// </summary>
    private async Task<JsonNode?> CallMcpToolAsync(
        string capabilityId,
        JsonNode? arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var toolName = _mcpToolNames.TryGetValue(capabilityId, out var mapped) ? mapped : capabilityId;

        var response = await Peer.SendRequestAsync(
            McpProtocol.MethodCallTool,
            new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments ?? new JsonObject()
            },
            timeout,
            cancellationToken).ConfigureAwait(false);

        var text = FlattenMcpContent(response);

        if (response?["isError"]?.GetValue<bool>() == true)
        {
            throw new PluginRpcException(
                PluginProtocol.ErrorInternal,
                string.IsNullOrWhiteSpace(text) ? $"MCP tool '{toolName}' reported an error" : text!);
        }

        return text is null ? null : JsonValue.Create(text);
    }

    /// <summary>把 content 数组与 structuredContent 合成一段文本；超长时截断并标注。</summary>
    private static string? FlattenMcpContent(JsonNode? response)
    {
        if (response is null) return null;

        var builder = new System.Text.StringBuilder();

        if (response["content"] is JsonArray content)
        {
            foreach (var item in content)
            {
                var type = item?["type"]?.GetValue<string>();

                switch (type)
                {
                    case "text":
                        var text = item?["text"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (builder.Length > 0) builder.AppendLine();
                            builder.Append(text);
                        }
                        break;

                    case "resource":
                        builder.AppendLine();
                        builder.Append($"[resource] {item?["resource"]?["uri"]?.GetValue<string>() ?? "unknown"}");
                        break;

                    default:
                        builder.AppendLine();
                        builder.Append($"[{type ?? "content"}]");
                        break;
                }
            }
        }

        if (response["structuredContent"] is JsonNode structured)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(structured.ToJsonString());
        }

        if (builder.Length == 0)
        {
            // 既没有 content 也没有结构化结果：退回原始 JSON，总比"（插件未返回内容）"有用。
            return response.ToJsonString();
        }

        const int limit = 32_000;
        var result = builder.ToString().Trim();
        return result.Length <= limit ? result : result[..limit] + "…（结果过长已截断）";
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

    /// <summary>
    /// 算出启动规格。这里只描述"要启动什么"，不含任何隔离决策——
    /// 走哪条路（普通子进程 / AppContainer）由 <see cref="PluginProcessFactory"/> 决定。
    /// </summary>
    private PluginProcess.StartSpec BuildStartSpec()
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

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 清单里写死的环境变量先放，核心自己的变量后放（插件不能靠同名变量覆盖身份/数据目录）。
        foreach (var pair in entry.Env)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
                environment[pair.Key] = pair.Value ?? string.Empty;
        }

        environment["MYFIRSTMAFAGENT_PLUGIN_ID"] = manifest.Id;
        environment["MYFIRSTMAFAGENT_PROTOCOL"] = PluginProtocol.ProtocolVersion;
        environment["MYFIRSTMAFAGENT_DIALECT"] = Dialect;
        if (_dataDirectory is not null)
            environment["MYFIRSTMAFAGENT_PLUGIN_DATA"] = _dataDirectory;

        // MCP server 只认环境变量：把用户填的配置逐项注入，改配置时重启进程生效。
        if (IsMcp) InjectConfigurationEnvironment(environment);

        return new PluginProcess.StartSpec(
            manifest.Id,
            executable,
            entry.Arguments,
            workingDirectory,
            environment,
            _load.Directory,
            _dataDirectory,
            GrantedPermissions());
    }

    /// <summary>
    /// 交给容器的是<b>用户已授予</b>的权限，不是清单声明的权限。
    /// 这两个混起来的话，"声明了但没授权"会悄悄变成"拥有"。
    /// </summary>
    private IReadOnlyList<string> GrantedPermissions()
    {
        var declared = Manifest.Capabilities
            .SelectMany(capability => capability.Permissions)
            .Concat(Manifest.Permissions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var granted = Registration?.GrantedPermissions ?? [];
        if (_options.AutoGrantPermissions) return declared;

        return declared
            .Where(permission => granted.Contains(permission, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// 把用户配置的标量字段注入为 <c>MYFIRSTMAFAGENT_CONFIG_&lt;KEY&gt;</c>。
    /// 嵌套对象/数组不展开：MCP 的环境变量通道本来就是平的，展开反而会造出一堆猜出来的名字。
    /// </summary>
    private void InjectConfigurationEnvironment(IDictionary<string, string> environment)
    {
        if (ParseConfig(Registration?.ConfigJson) is not JsonObject config) return;

        foreach (var pair in config)
        {
            if (pair.Value is not JsonValue value) continue;

            var text = value.TryGetValue<string>(out var asString) ? asString : value.ToJsonString();
            environment[$"MYFIRSTMAFAGENT_CONFIG_{pair.Key.ToUpperInvariant()}"] = text;
        }
    }

    /// <summary>把配置里的路径（可能是相对路径）解析成绝对路径。</summary>
    private IReadOnlyList<string> ResolveRoots(IReadOnlyList<string>? roots)
    {
        if (roots is null || roots.Count == 0) return [];

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.IsPathRooted(root) ? Path.GetFullPath(root) : Path.GetFullPath(Path.Combine(_pluginsRoot, root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnNotification(string method, JsonNode? parameters)
    {
        if (method is PluginProtocol.MethodLog or McpProtocol.MethodMessage)
        {
            // 两种方言的日志通知字段不同：plugin/log 是 {level,message}，MCP 是 {level,data}。
            var level = parameters?["level"]?.GetValue<string>() ?? "info";
            var message = method == PluginProtocol.MethodLog
                ? parameters?["message"]?.GetValue<string>()
                : parameters?["data"]?.ToString();

            if (!string.IsNullOrWhiteSpace(message))
                _logger.LogInformation("Plugin {PluginId} [{Level}] {Message}", Id, level, message);

            return;
        }

        if (method == McpProtocol.MethodToolsChanged)
        {
            // 工具表变了：不热重建能力（会在途调用看到两套 ID），让用户显式重启插件。
            _logger.LogInformation(
                "Plugin {PluginId} reported a changed tool list; restart the plugin to pick it up",
                Id);
            return;
        }

        _logger.LogDebug("Plugin {PluginId} notification {Method}", Id, method);
    }

    private void OnProcessExited(int exitCode)
    {
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
        Isolation = PluginIsolationReport.NotStarted(
            PluginIsolationPlanner.ToConfigValue(
                PluginIsolationPlanner.ResolveMode(_options.Isolation.Mode, OperatingSystem.IsWindows())),
            reason ?? state.ToString());
    }

    public async Task StopAsync()
    {
        if (State is PluginState.Stopped or PluginState.Discovered or PluginState.Disabled
            or PluginState.NeedsConfiguration or PluginState.PermissionRequired) return;

        try
        {
            if (_peer is not null && State is PluginState.Running or PluginState.Degraded)
            {
                if (IsMcp)
                {
                    // MCP 没有 shutdown 方法：关掉它的 stdin 就是约定好的退出信号。
                    _process?.CloseInput();
                }
                else
                {
                    // 先请插件自己退出，给它机会刷新缓冲。
                    await _peer.SendRequestAsync(
                        PluginProtocol.MethodShutdown,
                        null,
                        TimeSpan.FromSeconds(5),
                        CancellationToken.None).ConfigureAwait(false);
                }
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
                        _process.KillTree();
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
    }
}
