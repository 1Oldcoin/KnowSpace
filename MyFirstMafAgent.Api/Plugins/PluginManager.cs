using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 插件生命周期管理器：发现 → 校验 → 授权 → 启动 → 健康检查 → 停用/升级/回滚/卸载。
///
/// <para>
/// 状态分两层：进程与健康是<b>运行时</b>（易失，重启后重新发现）；
/// 启用、配置、授权与审计是<b>用户意图</b>（落库，见 <see cref="IPluginRegistry"/>）。
/// 启动判定顺序固定为：启用 → 完整性 → 配置完整性 → 权限，任一不满足都不会拉起进程。
/// </para>
/// </summary>
public sealed class PluginManager : IAsyncDisposable, IPluginRuntime
{
    private readonly PluginOptions _options;
    private readonly PluginManifestLoader _loader;
    private readonly PluginPaths _paths;
    private readonly PluginInstaller _installer;
    private readonly PluginIntegrityVerifier _verifier;
    private readonly PluginAuditWriter _audit;
    private readonly IPluginRegistry _registry;
    private readonly IPluginItemMap _itemMap;
    private readonly IPluginSyncStateStore _syncStates;
    private readonly IKnowledgeBaseDirectory _knowledgeBaseDirectory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginManager> _logger;

    private readonly ConcurrentDictionary<string, PluginProcessHost> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginManifestLoadResult> _loads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginRegistration> _registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginIntegrityReport> _integrity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _restartAttempts = new(StringComparer.Ordinal);

    /// <summary>生命周期操作串行化：安装、切换版本、启停不能交叉执行。</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private int _initialized;
    private int _disposed;

    public PluginManager(
        IOptions<PluginOptions> options,
        PluginManifestLoader loader,
        PluginPaths paths,
        PluginInstaller installer,
        PluginIntegrityVerifier verifier,
        PluginAuditWriter audit,
        IPluginRegistry registry,
        IPluginItemMap itemMap,
        IPluginSyncStateStore syncStates,
        IKnowledgeBaseDirectory knowledgeBaseDirectory,
        ILoggerFactory loggerFactory,
        ILogger<PluginManager> logger)
    {
        _options = options.Value;
        _loader = loader;
        _paths = paths;
        _installer = installer;
        _verifier = verifier;
        _audit = audit;
        _registry = registry;
        _itemMap = itemMap;
        _syncStates = syncStates;
        _knowledgeBaseDirectory = knowledgeBaseDirectory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public string RootPath => _paths.Root;

    public bool IsEnabled => _options.Enabled;

    /// <summary>扫描插件目录并启动已启用且通过全部闸门的插件。任何单个插件失败都不阻止核心启动。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        if (!_options.Enabled)
        {
            _logger.LogInformation("Plugin runtime is disabled by configuration");
            return;
        }

        Directory.CreateDirectory(_paths.Root);
        await LoadRegistrationsAsync(cancellationToken).ConfigureAwait(false);

        var loads = _loader.Scan(_paths.Root);
        foreach (var load in loads)
            _loads[load.Id] = load;

        _logger.LogInformation("Discovered {Count} plugin(s) under {RootPath}", loads.Count, _paths.Root);

        foreach (var load in loads)
        {
            if (!load.IsUsable)
            {
                GetOrCreateHost(load).MarkNotStarted(
                    PluginState.Failed,
                    string.Join("; ", load.Validation.Errors));
                continue;
            }

            await StartSafelyAsync(load, PluginActors.System, silent: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task LoadRegistrationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var registration in await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
                _registrations[registration.PluginId] = registration;
        }
        catch (Exception exception)
        {
            // 数据库不可用时插件仍按 manifest 发现，只是读不到用户意图：
            // 此时按配置默认值处理（EnabledPluginIds 白名单 + 不自动授权）。
            _logger.LogError(exception, "Failed to load plugin registrations; falling back to configuration defaults");
        }
    }

    /// <summary>
    /// 启动单个插件，并按顺序执行四道闸门。返回最终状态；
    /// 失败原因同时写入 <see cref="PluginProcessHost.LastError"/> 与审计。
    /// </summary>
    private async Task<PluginState> StartSafelyAsync(
        PluginManifestLoadResult load,
        string actor,
        bool silent,
        CancellationToken cancellationToken)
    {
        var host = GetOrCreateHost(load);
        var manifest = load.Manifest!;

        try
        {
            var registration = await ResolveRegistrationAsync(load, cancellationToken).ConfigureAwait(false);

            // 闸门 1：用户是否启用（持久化状态 + 配置白名单，两者都要满足）。
            if (!registration.Enabled)
            {
                host.MarkNotStarted(PluginState.Disabled, "插件已停用");
                return PluginState.Disabled;
            }

            if (!IsAllowedByConfiguration(registration.PluginId))
            {
                host.MarkNotStarted(PluginState.Disabled, "未包含在 Plugins:EnabledPluginIds 白名单中");
                return PluginState.Disabled;
            }

            // 闸门 2：完整性（摘要/签名）。
            var integrity = await _verifier
                .VerifyAsync(load.Directory, manifest, cancellationToken)
                .ConfigureAwait(false);
            _integrity[registration.PluginId] = integrity;

            if (!integrity.IsAcceptable)
            {
                host.MarkNotStarted(
                    PluginState.Failed,
                    $"完整性校验未通过：{string.Join("; ", integrity.Errors)}");

                await _audit.RecordAsync(
                    registration.PluginId,
                    PluginAuditActions.IntegrityRejected,
                    null,
                    actor,
                    PluginAuditOutcomes.Denied,
                    0,
                    string.Join("; ", integrity.Errors),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return PluginState.Failed;
            }

            // 闸门 3：必填配置是否已填写。
            if (RequiresConfiguration(manifest) && string.IsNullOrWhiteSpace(registration.ConfigJson))
            {
                host.MarkNotStarted(
                    PluginState.NeedsConfiguration,
                    $"插件需要配置：{string.Join(", ", manifest.Config!.Required)}");
                return PluginState.NeedsConfiguration;
            }

            // 闸门 4：权限授予。
            var decision = PluginAccessPolicy.CheckStart(
                DeclaredPermissions(manifest),
                registration.GrantedPermissions,
                _options.AutoGrantPermissions);

            if (!decision.IsAllowed)
            {
                host.MarkNotStarted(PluginState.PermissionRequired, decision.Reason);
                return PluginState.PermissionRequired;
            }

            await host.StartAsync(registration, cancellationToken).ConfigureAwait(false);

            if (!silent)
            {
                await _audit.RecordAsync(
                    registration.PluginId,
                    PluginAuditActions.Start,
                    null,
                    actor,
                    PluginAuditOutcomes.Success,
                    0,
                    $"v{manifest.Version}",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await PersistStateAsync(registration, host, cancellationToken).ConfigureAwait(false);
            return host.State;
        }
        catch (Exception exception)
        {
            // 单个插件失败不得阻止核心启动。
            _logger.LogError(exception, "Plugin {PluginId} could not be started; continuing without it", load.Id);

            await _audit.RecordAsync(
                load.Id,
                PluginAuditActions.Start,
                null,
                actor,
                PluginAuditOutcomes.Failure,
                0,
                exception.Message,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return host.State;
        }
    }

    /// <summary>读取或刷新安装态：保留用户意图（启用/配置/授权），只同步 manifest 自述与版本。</summary>
    private async Task<PluginRegistration> ResolveRegistrationAsync(
        PluginManifestLoadResult load,
        CancellationToken cancellationToken)
    {
        var manifest = load.Manifest!;

        if (_registrations.TryGetValue(manifest.Id, out var existing)
            && string.Equals(existing.Version, manifest.Version, StringComparison.Ordinal))
        {
            return existing;
        }

        var merged = existing is null
            ? PluginRegistration.Create(
                manifest.Id,
                manifest.Name,
                manifest.Version,
                manifest.Publisher,
                enabled: true,
                configJson: string.Empty,
                grantedPermissions: [])
            : existing with
            {
                Name = manifest.Name,
                Version = manifest.Version,
                Publisher = manifest.Publisher,
                UpdatedAt = DateTimeOffset.UtcNow
            };

        _registrations[manifest.Id] = merged;

        try
        {
            await _registry.SaveAsync(merged, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist registration for plugin {PluginId}", manifest.Id);
        }

        return merged;
    }

    private bool IsAllowedByConfiguration(string pluginId) =>
        _options.EnabledPluginIds.Count == 0
        || _options.EnabledPluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase);

    private static bool RequiresConfiguration(PluginManifest manifest) =>
        manifest.Config?.Required.Count > 0;

    private static IReadOnlyList<string> DeclaredPermissions(PluginManifest manifest) =>
        manifest.Capabilities
            .SelectMany(capability => capability.Permissions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>状态快照：文件布局 + 运行时 + 用户意图 + 完整性，供界面一次拿全。</summary>
    public IReadOnlyList<PluginStatusSnapshot> GetStatuses()
    {
        var ids = _loads.Keys.Union(_hosts.Keys, StringComparer.Ordinal).ToList();
        var snapshots = new List<PluginStatusSnapshot>(ids.Count);

        foreach (var id in ids)
        {
            _loads.TryGetValue(id, out var load);
            _hosts.TryGetValue(id, out var host);
            _registrations.TryGetValue(id, out var registration);
            _integrity.TryGetValue(id, out var integrity);

            var manifest = load?.Manifest;
            var health = host?.LastHealth;
            var state = host?.State
                ?? (load?.IsUsable == true ? PluginState.Discovered : PluginState.Failed);

            var declared = manifest is null ? [] : DeclaredPermissions(manifest);
            var granted = registration?.GrantedPermissions ?? [];
            var missing = declared
                .Where(permission => !granted.Contains(permission, StringComparer.OrdinalIgnoreCase))
                .ToList();

            snapshots.Add(new PluginStatusSnapshot(
                id,
                manifest?.Name ?? id,
                manifest?.Version ?? "0.0.0",
                manifest?.Publisher ?? string.Empty,
                state.ToString(),
                load?.Directory ?? string.Empty,
                host?.Capabilities.Select(capability => capability.Id).ToList()
                    ?? manifest?.Capabilities.Select(capability => capability.Id).ToList()
                    ?? [],
                health is null
                    ? null
                    : $"{health.Status}{(string.IsNullOrWhiteSpace(health.Detail) ? string.Empty : $": {health.Detail}")}",
                host?.LastError ?? (load is { IsUsable: false } ? string.Join("; ", load.Validation.Errors) : null),
                registration?.Enabled ?? true,
                load?.ActiveVersion,
                load?.InstalledVersions ?? [],
                granted,
                missing,
                integrity?.Status,
                IsConfigured(manifest, registration),
                host?.InFlightInvocations ?? 0,
                host?.IsDraining ?? false));
        }

        return snapshots.OrderBy(snapshot => snapshot.Id, StringComparer.Ordinal).ToList();
    }

    private static bool IsConfigured(PluginManifest? manifest, PluginRegistration? registration) =>
        manifest is null
        || manifest.Config?.Required.Count is null or 0
        || !string.IsNullOrWhiteSpace(registration?.ConfigJson);

    public PluginProcessHost? FindHost(string pluginId) =>
        _hosts.TryGetValue(pluginId, out var host) ? host : null;

    /// <summary>
    /// 定时同步是否应继续为该插件排队：已安装、且未被用户停用。
    ///
    /// <para>
    /// 不要求进程正在运行：任务真正执行时会按需拉起（并用退避避免崩溃循环）。
    /// 但用户明确停用过的插件绝不能被“定时同步”偷偷拉起来。
    /// </para>
    /// </summary>
    public bool IsScheduledSyncEligible(string pluginId) =>
        _hosts.ContainsKey(pluginId)
        && (!_registrations.TryGetValue(pluginId, out var registration) || registration.Enabled);

    public IReadOnlyList<PluginProcessHost> RunningHosts =>
        _hosts.Values.Where(host => host.State is PluginState.Running or PluginState.Degraded).ToList();

    public Task<IReadOnlyList<PluginAuditRecord>> GetAuditAsync(
        string? pluginId,
        int limit,
        CancellationToken cancellationToken) =>
        _registry.QueryAuditAsync(pluginId, limit, cancellationToken);

    // ───────────────────────────── 健康与调用 ─────────────────────────────

    public async Task<PluginHealth> CheckHealthAsync(string pluginId, string actor, CancellationToken cancellationToken)
    {
        var host = RequireHost(pluginId);
        var health = await host.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            pluginId,
            PluginAuditActions.HealthCheck,
            null,
            actor,
            health.IsUsable ? PluginAuditOutcomes.Success : PluginAuditOutcomes.Failure,
            0,
            health.Detail,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return health;
    }

    /// <summary>调用插件能力：先过副作用闸门，再确保进程存活，最后写审计。</summary>
    public async Task<PluginInvocationResult> InvokeAsync(
        string pluginId,
        string capabilityId,
        JsonNode? arguments,
        PluginInvocationContext context,
        CancellationToken cancellationToken)
    {
        var host = RequireHost(pluginId);
        var registration = await GetRegistrationAsync(pluginId, cancellationToken).ConfigureAwait(false);

        if (registration is { Enabled: false })
            throw new PluginRpcException(PluginProtocol.ErrorPermissionDenied, $"插件 '{pluginId}' 已停用");

        var capability = FindCapability(host, capabilityId);

        var decision = PluginAccessPolicy.CheckInvocation(
            capability,
            context.Confirmed,
            context.Actor,
            _options.RequireConfirmationForSideEffects);

        if (!decision.IsAllowed)
        {
            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Invoke,
                capabilityId,
                context.Actor,
                PluginAuditOutcomes.Denied,
                0,
                decision.Reason,
                arguments,
                cancellationToken).ConfigureAwait(false);

            throw new PluginRpcException(PluginProtocol.ErrorPermissionDenied, decision.Reason ?? "调用被拒绝");
        }

        await EnsureStartedAsync(host, registration, cancellationToken).ConfigureAwait(false);

        var started = Stopwatch.StartNew();
        try
        {
            var result = await host.InvokeAsync(capabilityId, arguments, cancellationToken).ConfigureAwait(false);
            started.Stop();

            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Invoke,
                capabilityId,
                context.Actor,
                PluginAuditOutcomes.Success,
                started.ElapsedMilliseconds,
                $"sideEffect={capability.SideEffect}",
                arguments,
                cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            started.Stop();
            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Invoke,
                capabilityId,
                context.Actor,
                PluginAuditOutcomes.Cancelled,
                started.ElapsedMilliseconds,
                "调用方取消",
                arguments,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (PluginRpcException exception)
        {
            started.Stop();
            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Invoke,
                capabilityId,
                context.Actor,
                PluginAuditOutcomes.Failure,
                started.ElapsedMilliseconds,
                $"code={exception.Code} {exception.Message}",
                arguments,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static PluginCapability FindCapability(PluginProcessHost host, string capabilityId) =>
        host.Capabilities.FirstOrDefault(capability => string.Equals(capability.Id, capabilityId, StringComparison.Ordinal))
        ?? host.Manifest.Capabilities.FirstOrDefault(capability => string.Equals(capability.Id, capabilityId, StringComparison.Ordinal))
        ?? throw new PluginRpcException(
            PluginProtocol.ErrorUnknownCapability,
            $"capability '{capabilityId}' is not declared by plugin '{host.Id}'");

    /// <summary>进程崩溃后按退避重启，避免崩溃循环打满 CPU。</summary>
    private async Task EnsureStartedAsync(
        PluginProcessHost host,
        PluginRegistration? registration,
        CancellationToken cancellationToken)
    {
        if (host.State is PluginState.Running or PluginState.Degraded or PluginState.Starting) return;

        // 排空中的插件绝不能"顺手重启"：这正是升级前主动软下线的状态，
        // 重启会把刚腾出来的进程又拉回来（文件被占用，升级必失败），
        // 也会让排空语义变成"排空了但还能调用"。
        if (host.IsDraining || host.State is PluginState.Draining)
            throw new PluginRpcException(
                PluginProtocol.ErrorUnhealthy,
                $"plugin '{host.Id}' is draining for an upgrade; retry later");

        if (host.State is PluginState.Disabled or PluginState.NeedsConfiguration or PluginState.PermissionRequired)
            throw new PluginRpcException(
                PluginProtocol.ErrorNotConfigured,
                $"plugin '{host.Id}' is not ready ({host.State}): {host.LastError}");

        if (!_options.RestartOnCrash) return;

        var backoff = TimeSpan.FromSeconds(Math.Max(5, _options.MaxRestartBackoffSeconds));

        if (_restartAttempts.TryGetValue(host.Id, out var lastAttempt)
            && DateTimeOffset.UtcNow - lastAttempt < backoff)
        {
            throw new PluginRpcException(
                PluginProtocol.ErrorUnhealthy,
                $"plugin '{host.Id}' is in restart backoff until {(lastAttempt + backoff):O}");
        }

        _restartAttempts[host.Id] = DateTimeOffset.UtcNow;
        _logger.LogInformation("Restarting plugin {PluginId}", host.Id);

        try
        {
            await host.StartAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Plugin {PluginId} restart failed", host.Id);
        }
    }

    private PluginProcessHost RequireHost(string pluginId)
    {
        if (_hosts.TryGetValue(pluginId, out var host)) return host;

        if (_loads.TryGetValue(pluginId, out var load) && !load.IsUsable)
            throw new PluginRpcException(
                PluginProtocol.ErrorUnhealthy,
                $"plugin '{pluginId}' is not usable: {string.Join("; ", load.Validation.Errors)}");

        throw new PluginRpcException(PluginProtocol.ErrorUnknownCapability, $"plugin '{pluginId}' is not installed");
    }

    private PluginProcessHost GetOrCreateHost(PluginManifestLoadResult load)
    {
        if (_hosts.TryGetValue(load.Id, out var existing))
        {
            if (string.Equals(existing.Directory, load.Directory, StringComparison.OrdinalIgnoreCase))
                return existing;

            // 版本已切换：旧宿主连同进程一起丢弃。
            _ = existing.DisposeAsync();
        }

        var dataDirectory = _paths.DataDirectory(load.Id);
        Directory.CreateDirectory(dataDirectory);

        var host = new PluginProcessHost(
            load,
            _options,
            dataDirectory,
            _loggerFactory.CreateLogger($"Plugin.{load.Id}"));

        _hosts[load.Id] = host;
        return host;
    }

    // ───────────────────────────── 生命周期操作 ─────────────────────────────

    /// <summary>启用/停用插件（持久化）。停用会先停进程，启用会重新走四道闸门。</summary>
    public async Task<PluginOperationResult> SetEnabledAsync(
        string pluginId,
        bool enabled,
        string actor,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loads.ContainsKey(pluginId))
                return PluginOperationResult.Fail(pluginId, $"插件 '{pluginId}' 未安装");

            var registration = await GetRegistrationAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (registration is null)
                return PluginOperationResult.Fail(pluginId, $"插件 '{pluginId}' 没有安装记录");

            await SaveRegistrationAsync(
                registration with { Enabled = enabled, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken).ConfigureAwait(false);

            if (enabled)
            {
                if (_loads.TryGetValue(pluginId, out var load) && load.IsUsable)
                    await StartSafelyAsync(load, actor, silent: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StopHostAsync(pluginId).ConfigureAwait(false);
                FindHost(pluginId)?.MarkNotStarted(PluginState.Disabled, "插件已停用");
            }

            await _audit.RecordAsync(
                pluginId,
                enabled ? PluginAuditActions.Enable : PluginAuditActions.Disable,
                null,
                actor,
                PluginAuditOutcomes.Success,
                0,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return PluginOperationResult.Ok(pluginId, FindHost(pluginId)?.State.ToString());
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>更新插件配置：先按 manifest 的 JSON Schema 校验，再持久化并重启插件。</summary>
    public async Task<PluginOperationResult> UpdateConfigAsync(
        string pluginId,
        string configJson,
        string actor,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loads.TryGetValue(pluginId, out var load) || load.Manifest is null)
                return PluginOperationResult.Fail(pluginId, $"插件 '{pluginId}' 未安装");

            var errors = ValidateConfig(load.Manifest, configJson);
            if (errors.Count > 0) return PluginOperationResult.Fail(pluginId, [.. errors]);

            var registration = await GetRegistrationAsync(pluginId, cancellationToken).ConfigureAwait(false)
                ?? PluginRegistration.Create(
                    pluginId,
                    load.Manifest.Name,
                    load.Manifest.Version,
                    load.Manifest.Publisher,
                    enabled: true,
                    configJson: string.Empty,
                    grantedPermissions: []);

            var normalized = string.IsNullOrWhiteSpace(configJson) ? string.Empty : configJson.Trim();
            await SaveRegistrationAsync(
                registration with { ConfigJson = normalized, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken).ConfigureAwait(false);

            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Configure,
                null,
                actor,
                PluginAuditOutcomes.Success,
                0,
                "配置已更新",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await RestartAsync(load, actor, cancellationToken).ConfigureAwait(false);
            return PluginOperationResult.Ok(pluginId, FindHost(pluginId)?.State.ToString());
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>更新授权列表。只允许授予 manifest 声明过的权限。</summary>
    public async Task<PluginOperationResult> UpdatePermissionsAsync(
        string pluginId,
        IReadOnlyList<string> permissions,
        string actor,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loads.TryGetValue(pluginId, out var load) || load.Manifest is null)
                return PluginOperationResult.Fail(pluginId, $"插件 '{pluginId}' 未安装");

            var errors = PluginAccessPolicy.ValidateGrantRequest(DeclaredPermissions(load.Manifest), permissions);
            if (errors.Count > 0) return PluginOperationResult.Fail(pluginId, [.. errors]);

            var registration = await GetRegistrationAsync(pluginId, cancellationToken).ConfigureAwait(false)
                ?? PluginRegistration.Create(
                    pluginId,
                    load.Manifest.Name,
                    load.Manifest.Version,
                    load.Manifest.Publisher,
                    enabled: true,
                    configJson: string.Empty,
                    grantedPermissions: []);

            var normalized = permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await SaveRegistrationAsync(
                registration with { GrantedPermissions = normalized, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken).ConfigureAwait(false);

            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Grant,
                null,
                actor,
                PluginAuditOutcomes.Success,
                0,
                normalized.Count == 0 ? "已收回全部权限" : $"授予：{string.Join(", ", normalized)}",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await RestartAsync(load, actor, cancellationToken).ConfigureAwait(false);
            return PluginOperationResult.Ok(pluginId, FindHost(pluginId)?.State.ToString());
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 更新写入范围与配额：允许写入哪些知识库、单库最多多少条。
    /// 范围变更会立即生效（不需要重启插件），因为判定发生在核心摄取入口。
    /// </summary>
    public async Task<PluginOperationResult> UpdateScopeAsync(
        string pluginId,
        IReadOnlyList<Guid> allowedKnowledgeBaseIds,
        int maxDocuments,
        string actor,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loads.TryGetValue(pluginId, out var load) || load.Manifest is null)
                return PluginOperationResult.Fail(pluginId, $"插件 '{pluginId}' 未安装");

            if (maxDocuments < 0)
                return PluginOperationResult.Fail(pluginId, "maxDocuments 不能为负数");

            // 允许写入的知识库必须真实存在，避免写一个永远匹配不上的 GUID。
            var errors = await PluginScopePolicy
                .ValidateAsync(allowedKnowledgeBaseIds, maxDocuments, _knowledgeBaseDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (errors.Count > 0) return PluginOperationResult.Fail(pluginId, [.. errors]);

            var registration = await GetRegistrationAsync(pluginId, cancellationToken).ConfigureAwait(false)
                ?? PluginRegistration.Create(
                    pluginId,
                    load.Manifest.Name,
                    load.Manifest.Version,
                    load.Manifest.Publisher,
                    enabled: true,
                    configJson: string.Empty,
                    grantedPermissions: []);

            var normalized = allowedKnowledgeBaseIds.Distinct().ToList();
            await SaveRegistrationAsync(
                registration with
                {
                    AllowedKnowledgeBaseIds = normalized,
                    MaxDocuments = maxDocuments,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);

            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Scope,
                null,
                actor,
                PluginAuditOutcomes.Success,
                0,
                $"knowledgeBases={(normalized.Count == 0 ? "*" : string.Join(",", normalized))} maxDocuments={(maxDocuments == 0 ? "default" : maxDocuments.ToString())}",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return PluginOperationResult.Ok(pluginId, FindHost(pluginId)?.State.ToString());
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 安装或升级插件包。
    ///
    /// <para>顺序是刻意安排的：<b>先校验包 → 再排空在途调用 → 再停进程 → 才换文件</b>。</para>
    /// <list type="number">
    ///   <item><description>先校验：包有问题时正在运行的旧版本一点不受影响。</description></item>
    ///   <item><description>先排空：升级不该让正在跑的同步/工具调用变成无解释的失败。</description></item>
    ///   <item><description>先停进程：Windows 上运行中的 exe/持锁文件删不掉，不停就没法覆盖。</description></item>
    /// </list>
    /// </summary>
    public async Task<PluginInstallResult> InstallAsync(
        Stream package,
        string actor,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 1) 只读校验：这一步不会动到任何生效目录。
            var staged = await _installer.StageAsync(package, cancellationToken).ConfigureAwait(false);
            using (staged)
            {
                if (!staged.Success)
                    return PluginInstallResult.Fail([.. staged.Errors]);

                // 2) 已安装则先排空并停止旧进程，否则文件替换会被占用。
                var upgrading = staged.PluginId is { } id && _loads.ContainsKey(id);
                if (upgrading)
                    await StopHostAsync(staged.PluginId!, cancellationToken).ConfigureAwait(false);

                // 3) 落盘 + 切换 active 指针。
                PluginInstallResult result;
                try
                {
                    result = await _installer
                        .InstallFromDirectoryAsync(staged.PackageDirectory, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 旧进程刚退出时文件可能还被系统占着一瞬间。失败不能把插件留在“已停止”状态：
                    // 调用方会看到 500，但用户看到的应该是“升级失败，旧版本仍在服务”。
                    _logger.LogError(
                        exception, "Plugin {PluginId} install failed after stopping the old process", staged.PluginId);

                    if (upgrading) await TryRestoreAsync(staged.PluginId!, cancellationToken).ConfigureAwait(false);

                    return PluginInstallResult.Fail($"写入插件文件失败：{exception.Message}");
                }

                if (!result.Success || result.PluginId is null)
                {
                    if (upgrading) await TryRestoreAsync(staged.PluginId!, cancellationToken).ConfigureAwait(false);
                    return result;
                }

                await _audit.RecordAsync(
                    result.PluginId,
                    PluginAuditActions.Install,
                    null,
                    actor,
                    PluginAuditOutcomes.Success,
                    0,
                    $"v{result.Version}{(upgrading ? " (drain upgrade)" : string.Empty)} integrity={result.Integrity?.Status ?? "unknown"} digest={result.PackageDigest ?? "-"}",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                await ReloadAndStartAsync(result.PluginId, actor, cancellationToken).ConfigureAwait(false);
                return result;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 主动排空一个插件：不再接受新调用，等已发出的调用结束。
    /// 返回 false 表示超时（已有调用超过 <c>Plugins:DrainTimeoutSeconds</c> 还没结束）。
    /// </summary>
    public async Task<bool> DrainAsync(string pluginId, string actor, CancellationToken cancellationToken)
    {
        if (!_hosts.TryGetValue(pluginId, out var host)) return true;

        var drained = await host
            .DrainAsync(TimeSpan.FromSeconds(Math.Max(0, _options.DrainTimeoutSeconds)), cancellationToken)
            .ConfigureAwait(false);

        await _audit.RecordAsync(
            pluginId,
            PluginAuditActions.Drain,
            null,
            actor,
            drained ? PluginAuditOutcomes.Success : PluginAuditOutcomes.Failure,
            Math.Max(0, _options.DrainTimeoutSeconds) * 1000L,
            $"inFlight={host.InFlightInvocations} drained={drained}",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return drained;
    }

    /// <summary>回滚到上一个版本：切换指针后重启，启动失败则切回原版本。</summary>
    public async Task<PluginRollbackResult> RollbackAsync(
        string pluginId,
        string actor,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loads.ContainsKey(pluginId))
                return PluginRollbackResult.Fail([$"插件 '{pluginId}' 未安装"]);

            await StopHostAsync(pluginId, cancellationToken).ConfigureAwait(false);

            var rolling = await _installer.RollbackAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (!rolling.Success) return rolling;

            var state = await ReloadAndStartAsync(pluginId, actor, cancellationToken).ConfigureAwait(false);

            if (state is PluginState.Failed or PluginState.PermissionRequired or PluginState.NeedsConfiguration)
            {
                // 回滚后的版本起不来：切回去，保持"当前可用的那个版本"仍在运行。
                var forward = await _installer.RollbackAsync(pluginId, cancellationToken).ConfigureAwait(false);
                if (forward.Success)
                    await ReloadAndStartAsync(pluginId, actor, cancellationToken).ConfigureAwait(false);

                return PluginRollbackResult.Fail(
                    [$"回滚到 {rolling.ToVersion} 后启动失败（{state}），已切回 {rolling.FromVersion}"]);
            }

            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Rollback,
                null,
                actor,
                PluginAuditOutcomes.Success,
                0,
                $"{rolling.FromVersion} → {rolling.ToVersion}",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return rolling;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>卸载：停进程、删版本目录与数据目录、删除安装记录。</summary>
    public async Task<PluginOperationResult> UninstallAsync(
        string pluginId,
        string actor,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopHostAsync(pluginId, cancellationToken).ConfigureAwait(false);

            if (_hosts.TryRemove(pluginId, out var removed))
                await removed.DisposeAsync().ConfigureAwait(false);

            await _installer.UninstallAsync(pluginId, cancellationToken).ConfigureAwait(false);
            _loads.TryRemove(pluginId, out _);
            _registrations.TryRemove(pluginId, out _);
            _integrity.TryRemove(pluginId, out _);

            // 平台条目映射与同步游标必须一起清除：否则重装同一插件时会
            // “以为内容已经在库”，导致一条内容也拉不下来。
            try
            {
                await _itemMap.DeleteByPluginAsync(pluginId, cancellationToken).ConfigureAwait(false);
                await _syncStates.DeleteAllForPluginAsync(pluginId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to clear platform mapping for plugin {PluginId}", pluginId);
            }

            try
            {
                await _registry.DeleteAsync(pluginId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to delete registration for plugin {PluginId}", pluginId);
            }

            await _audit.RecordAsync(
                pluginId,
                PluginAuditActions.Uninstall,
                null,
                actor,
                PluginAuditOutcomes.Success,
                0,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return PluginOperationResult.Ok(pluginId, PluginState.Stopped.ToString(), "插件已卸载");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>手动重启（配置或授权改动后会调用）。</summary>
    public async Task<PluginOperationResult> RestartAsync(
        string pluginId,
        string actor,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loads.TryGetValue(pluginId, out var load))
                return PluginOperationResult.Fail(pluginId, $"插件 '{pluginId}' 未安装");

            var state = await RestartAsync(load, actor, cancellationToken).ConfigureAwait(false);
            return PluginOperationResult.Ok(pluginId, state.ToString());
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<PluginState> RestartAsync(
        PluginManifestLoadResult load,
        string actor,
        CancellationToken cancellationToken)
    {
        await StopHostAsync(load.Id, cancellationToken).ConfigureAwait(false);

        if (!load.IsUsable) return PluginState.Failed;

        return await StartSafelyAsync(load, actor, silent: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 停止插件进程。默认<b>先排空</b>：等正在执行的调用结束，超时才强制停止。
    /// 排空超时只记录告警不阻塞——一次卡死的调用不能成为升级的永久阻碍。
    /// </summary>
    private async Task StopHostAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (!_hosts.TryGetValue(pluginId, out var host)) return;

        if (host.InFlightInvocations > 0 || host.IsDraining)
        {
            var drained = await host
                .DrainAsync(TimeSpan.FromSeconds(Math.Max(0, _options.DrainTimeoutSeconds)), cancellationToken)
                .ConfigureAwait(false);

            if (!drained)
            {
                _logger.LogWarning(
                    "Plugin {PluginId} did not drain within {Timeout}s; forcing stop with {InFlight} invocation(s) in flight",
                    pluginId,
                    _options.DrainTimeoutSeconds,
                    host.InFlightInvocations);
            }
        }

        try
        {
            await host.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to stop plugin {PluginId}", pluginId);
        }
    }

    /// <summary>
    /// 升级失败后的自救：把旧版本重新拉起来。
    ///
    /// <para>
    /// 升级是"先停旧进程再落盘"，所以落盘一旦失败，插件会停在"已停止"状态——
    /// 对用户来说等于"升级失败，插件也没了"。这里当场恢复旧版本，
    /// 让失败只影响这一次升级，而不是让插件停摆到下次重启。
    /// </para>
    /// </summary>
    private async Task TryRestoreAsync(string pluginId, CancellationToken cancellationToken)
    {
        try
        {
            var state = await ReloadAndStartAsync(pluginId, PluginActors.System, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "Plugin {PluginId} restored to its previous version after a failed upgrade (state={State})",
                pluginId,
                state);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to restore plugin {PluginId} after a failed upgrade", pluginId);
        }
    }

    /// <summary>重新从磁盘读取插件（版本可能已变）并启动。</summary>
    private async Task<PluginState> ReloadAndStartAsync(
        string pluginId,
        string actor,
        CancellationToken cancellationToken)
    {
        await StopHostAsync(pluginId, cancellationToken).ConfigureAwait(false);

        PluginManifestLoadResult load;
        try
        {
            load = _loader.LoadPlugin(_paths.PluginRoot(pluginId));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to reload plugin {PluginId}", pluginId);
            return PluginState.Failed;
        }

        _loads[pluginId] = load;

        if (!load.IsUsable)
        {
            GetOrCreateHost(load).MarkNotStarted(
                PluginState.Failed,
                string.Join("; ", load.Validation.Errors));
            return PluginState.Failed;
        }

        // 版本变化时重建宿主（旧进程已停止，目录可能已被替换）。
        if (_hosts.TryRemove(pluginId, out var stale))
            await stale.DisposeAsync().ConfigureAwait(false);

        return await StartSafelyAsync(load, actor, silent: false, cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────────── 配置与状态 ─────────────────────────────

    /// <summary>按 manifest 的 JSON Schema 校验配置；空配置只允许在没有必填项时通过。</summary>
    public static IReadOnlyList<string> ValidateConfig(PluginManifest manifest, string configJson)
    {
        var errors = new List<string>();
        var contract = manifest.Config;

        if (string.IsNullOrWhiteSpace(configJson))
        {
            if (contract?.Required.Count > 0)
                errors.Add($"缺少必填配置：{string.Join(", ", contract.Required)}");

            return errors;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(configJson);
        }
        catch (JsonException)
        {
            return ["配置不是合法的 JSON"];
        }

        if (node is not JsonObject configObject)
            return ["配置必须是 JSON 对象"];

        errors.AddRange(JsonSchemaLite.Validate(contract?.Schema, configObject));

        if (contract is not null)
        {
            foreach (var required in contract.Required)
            {
                if (!configObject.ContainsKey(required))
                    errors.Add($"必填字段 {required} 缺失");
            }
        }

        return errors;
    }

    private async Task<PluginRegistration?> GetRegistrationAsync(string pluginId, CancellationToken cancellationToken)
    {
        if (_registrations.TryGetValue(pluginId, out var cached)) return cached;

        try
        {
            var stored = await _registry.GetAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (stored is not null) _registrations[pluginId] = stored;
            return stored;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read registration for plugin {PluginId}", pluginId);
            return null;
        }
    }

    private async Task SaveRegistrationAsync(PluginRegistration registration, CancellationToken cancellationToken)
    {
        _registrations[registration.PluginId] = registration;

        try
        {
            await _registry.SaveAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist registration for plugin {PluginId}", registration.PluginId);
        }
    }

    /// <summary>把运行时状态回写到安装记录，便于重启后仍能看到上次失败原因。</summary>
    private async Task PersistStateAsync(
        PluginRegistration registration,
        PluginProcessHost host,
        CancellationToken cancellationToken)
    {
        var snapshot = registration with
        {
            LastKnownState = host.State.ToString(),
            LastError = host.LastError,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _registrations[registration.PluginId] = snapshot;

        try
        {
            await _registry.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to persist runtime state for plugin {PluginId}", registration.PluginId);
        }
    }

    // ───────────────────────────── 关闭 ─────────────────────────────

    public async Task StopAllAsync()
    {
        foreach (var host in _hosts.Values)
        {
            try { await host.StopAsync().ConfigureAwait(false); }
            catch (Exception exception) { _logger.LogWarning(exception, "Failed to stop plugin {PluginId}", host.Id); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var host in _hosts.Values)
        {
            try { await host.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* 关闭阶段异常忽略 */ }
        }

        _hosts.Clear();
        _lifecycleGate.Dispose();
    }
}
