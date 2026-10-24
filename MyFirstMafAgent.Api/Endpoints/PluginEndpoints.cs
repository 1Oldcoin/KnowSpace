using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Domain.Jobs;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;
using MyFirstMafAgent.Api.Plugins;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>
/// 插件管理端点：状态、健康、调用、配置、授权、审计、安装/升级/回滚/卸载。
///
/// <para>
/// 插件只能通过协议被调用；平台内容必须经 <see cref="IPluginIngestionSink"/>
/// 进入核心摄取流程，插件无法直接写数据库。
/// </para>
/// </summary>
public static class PluginEndpoints
{
    public static void MapPluginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/plugins").WithTags("插件");

        // ───────────────────────────── 查看 ─────────────────────────────

        group.MapGet("", (PluginManager manager) => Results.Ok(new
        {
            enabled = manager.IsEnabled,
            rootPath = manager.RootPath,
            plugins = manager.GetStatuses()
        }));

        group.MapGet("/audit", async (
            string? pluginId,
            int? limit,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            var entries = await manager.GetAuditAsync(pluginId, limit ?? 50, cancellationToken);
            return Results.Ok(new { entries });
        });

        group.MapGet("/{id}", async (
            string id,
            PluginManager manager,
            PluginIntegrityVerifier verifier,
            IPluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var status = manager.GetStatuses().FirstOrDefault(item => item.Id == id);
            if (status is null) return Results.NotFound(new { message = $"Plugin '{id}' is not installed." });

            var host = manager.FindHost(id);
            var registration = await registry.GetAsync(id, cancellationToken);

            return Results.Ok(new
            {
                status,
                capabilities = status.Capabilities.Select(capabilityId => capabilityId),
                declaredCapabilities = host?.Manifest.Capabilities ?? [],
                health = host?.LastHealth?.Status,
                lastError = host?.LastError,
                inFlightInvocations = host?.InFlightInvocations ?? 0,
                draining = host?.IsDraining ?? false,
                trustedKey = verifier.FindKey(id),

                // 配置界面需要三样东西：当前值、schema、以及"哪些字段必填"。
                // 只暴露 schema 而不暴露当前值，用户每次改配置都得从零重填。
                config = string.IsNullOrWhiteSpace(registration?.ConfigJson) ? null : registration!.ConfigJson,
                configSchema = host?.Manifest.Config?.Schema,
                configRequired = host?.Manifest.Config?.Required ?? [],
                declaredPermissions = (host?.Manifest.Capabilities ?? [])
                    .SelectMany(capability => capability.Permissions)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(permission => permission, StringComparer.Ordinal)
                    .ToList(),
                // 作用域：空列表表示不限制知识库。
                allowedKnowledgeBaseIds = registration?.AllowedKnowledgeBaseIds ?? [],
                maxDocuments = registration?.MaxDocuments ?? 0,
                limits = host?.Manifest.Limits
            });
        });

        group.MapGet("/{id}/audit", async (
            string id,
            int? limit,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            var entries = await manager.GetAuditAsync(id, limit ?? 50, cancellationToken);
            return Results.Ok(new { entries });
        });

        // ───────────────────────────── 运行 ─────────────────────────────

        group.MapPost("/{id}/health", async (
            string id,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await manager.CheckHealthAsync(id, PluginActors.Api, cancellationToken));
            }
            catch (PluginRpcException exception)
            {
                return ToProblem(exception);
            }
        });

        group.MapPost("/{id}/invoke", async (
            string id,
            PluginInvokeRequest request,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.CapabilityId))
                return Results.BadRequest(new { message = "capabilityId is required" });

            try
            {
                var result = await manager.InvokeAsync(
                    id,
                    request.CapabilityId,
                    request.Arguments,
                    PluginInvocationContext.FromApi(request.Confirm ?? false),
                    cancellationToken);

                return Results.Ok(new
                {
                    result.PluginId,
                    result.CapabilityId,
                    result.ElapsedMilliseconds,
                    result = result.Result
                });
            }
            catch (PluginRpcException exception)
            {
                return ToProblem(exception);
            }
        });

        group.MapPost("/{id}/sync", async (
            string id,
            PluginSyncRequest request,
            PluginSyncService sync,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var outcome = await sync.SyncAsync(
                    id,
                    request.CapabilityId,
                    request.KnowledgeBaseId,
                    request.Cursor,
                    progress: null,
                    cancellationToken).ConfigureAwait(false);

                return Results.Ok(outcome);
            }
            catch (PluginSyncException exception)
            {
                return Results.Json(
                    new { message = exception.Message },
                    statusCode: exception.Retryable
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status502BadGateway);
            }
        });

        // 手动把一次同步排进任务队列（而不是阻塞等待）：大同步应该能在任务中心看到进度、能取消。
        group.MapPost("/{id}/sync-async", async (
            string id,
            PluginSyncRequest request,
            IJobQueue queue,
            JobWakeSignal wake,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.CapabilityId))
                return Results.BadRequest(new { error = "capabilityId 必填。" });

            var knowledgeBaseId = request.KnowledgeBaseId ?? KnowledgeBaseStore.DefaultKnowledgeBaseId;
            var job = await queue.EnqueueAsync(
                new EnqueueJobCommand(
                    JobTypes.PluginSync,
                    JsonSerializer.Serialize(new
                    {
                        pluginId = id,
                        capabilityId = request.CapabilityId,
                        knowledgeBaseId
                    }),
                    JobPriorities.Normal,
                    knowledgeBaseId,
                    null,
                    IdempotencyKey: $"{JobTypes.PluginSync}:{IPluginSyncStateStore.BuildScopeKey(id, request.CapabilityId, knowledgeBaseId)}"),
                cancellationToken).ConfigureAwait(false);

            // 幂等键命中一个「已经结束」的任务时，这次点击的意思就是「再同步一次」：
            // 不重新武装的话，界面上点"立即同步"会毫无反应（返回一个几天前的失败任务）。
            if (job.IsTerminal)
            {
                job = await queue.RetryAsync(job.Id, cancellationToken).ConfigureAwait(false) ?? job;
            }

            wake.Notify();
            return Results.Ok(job);
        });

        group.MapGet("/{id}/sync-state", async (
            string id,
            IPluginSyncStateStore syncStates,
            CancellationToken cancellationToken) =>
            Results.Ok(new { states = await syncStates.ListAsync(id, cancellationToken) }));

        group.MapGet("/{id}/items", async (
            string id,
            Guid? knowledgeBaseId,
            int? limit,
            IPluginItemMap itemMap,
            CancellationToken cancellationToken) =>
        {
            var items = await itemMap.ListAsync(id, knowledgeBaseId, limit ?? 100, cancellationToken);
            return Results.Ok(new { items });
        });

        group.MapPost("/{id}/restart", async (
            string id,
            PluginManager manager,
            CancellationToken cancellationToken) =>
            ToResult(await manager.RestartAsync(id, PluginActors.Api, cancellationToken)));

        // 主动排空：不再接受新调用，等在途调用结束。升级脚本可以先用它“软下线”，再替换文件。
        group.MapPost("/{id}/drain", async (
            string id,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            var drained = await manager.DrainAsync(id, PluginActors.Api, cancellationToken);
            return Results.Ok(new { drained, inFlight = manager.FindHost(id)?.InFlightInvocations ?? 0 });
        });

        // ───────────────────────── 受信任发布者公钥 ─────────────────────────
        // 公钥决定“谁的签名算数”。放在数据库而不是只放配置文件，才能在运行期信任新发布者。

        group.MapGet("/trusted-keys", (PluginTrustedKeyRegistry keys) =>
            Results.Ok(new { keys = keys.Keys }));

        group.MapPut("/trusted-keys/{keyId}", async (
            string keyId,
            TrustedKeyRequest request,
            IPluginTrustedKeyStore store,
            PluginTrustedKeyRegistry keys,
            PluginAuditWriter audit,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(keyId))
                return Results.BadRequest(new { message = "keyId 必填。" });

            if (string.IsNullOrWhiteSpace(request.PluginId))
                return Results.BadRequest(new { message = "pluginId 必填。" });

            if (!PluginTrustedKeys.TryNormalize(
                    request.Algorithm,
                    request.PublicKeyPem,
                    out var algorithm,
                    out var pem,
                    out var fingerprint,
                    out var error))
            {
                return Results.BadRequest(new { message = error });
            }

            var existing = await store.GetAsync(keyId, cancellationToken);
            await store.SaveAsync(new PluginTrustedKeyRecord(
                keyId,
                request.PluginId.Trim(),
                algorithm,
                pem,
                fingerprint,
                request.Note,
                existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow), cancellationToken);

            // 写完立即刷新快照：否则“刚信任的发布者”要等重启才生效，用户会以为没生效。
            await keys.ReloadAsync(cancellationToken);

            await audit.RecordAsync(
                request.PluginId.Trim(),
                PluginAuditActions.TrustKey,
                null,
                PluginActors.Api,
                PluginAuditOutcomes.Success,
                0,
                $"key={keyId} algorithm={algorithm} fingerprint={fingerprint}",
                cancellationToken: cancellationToken);

            return Results.Ok(new { keyId, algorithm, fingerprint, pluginId = request.PluginId.Trim() });
        });

        group.MapDelete("/trusted-keys/{keyId}", async (
            string keyId,
            IPluginTrustedKeyStore store,
            PluginTrustedKeyRegistry keys,
            PluginAuditWriter audit,
            CancellationToken cancellationToken) =>
        {
            if (keyId.StartsWith("config:", StringComparison.Ordinal))
                return Results.BadRequest(new { message = "该公钥来自配置文件，请修改 Plugins:TrustedPublisherKeys。" });

            var existing = await store.GetAsync(keyId, cancellationToken);
            if (existing is null) return Results.NotFound();

            await store.DeleteAsync(keyId, cancellationToken);
            await keys.ReloadAsync(cancellationToken);

            await audit.RecordAsync(
                existing.PluginId,
                PluginAuditActions.TrustKey,
                null,
                PluginActors.Api,
                PluginAuditOutcomes.Success,
                0,
                $"removed key={keyId} fingerprint={existing.Fingerprint}",
                cancellationToken: cancellationToken);

            return Results.NoContent();
        });

        // ───────────────────────────── 生命周期 ─────────────────────────────

        group.MapPost("/{id}/enabled", async (
            string id,
            PluginEnabledRequest request,
            PluginManager manager,
            CancellationToken cancellationToken) =>
            ToResult(await manager.SetEnabledAsync(id, request.Enabled, PluginActors.Api, cancellationToken)));

        group.MapPut("/{id}/config", async (
            string id,
            JsonObject body,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            // 必须显式带上 config：漏写字段就清空配置的代价太大（会把插件打回未配置状态）。
            // 想清空配置请写 {"config": null}。
            if (!body.TryGetPropertyValue("config", out var config))
                return Results.BadRequest(new { error = "请求体必须包含 config 字段；清空配置请传 {\"config\": null}。" });

            var json = config is null ? string.Empty : config.ToJsonString();
            return ToResult(await manager.UpdateConfigAsync(id, json, PluginActors.Api, cancellationToken));
        });

        group.MapPut("/{id}/permissions", async (
            string id,
            PluginPermissionsRequest request,
            PluginManager manager,
            CancellationToken cancellationToken) =>
            ToResult(await manager.UpdatePermissionsAsync(
                id,
                request.Permissions ?? [],
                PluginActors.Api,
                cancellationToken)));

        group.MapPut("/{id}/scope", async (
            string id,
            PluginScopeRequest request,
            PluginManager manager,
            CancellationToken cancellationToken) =>
            ToResult(await manager.UpdateScopeAsync(
                id,
                request.AllowedKnowledgeBaseIds ?? [],
                request.MaxDocuments ?? 0,
                PluginActors.Api,
                cancellationToken)));

        // ───────────────────────────── 安装与版本 ─────────────────────────────

        group.MapPost("/install", async (
            HttpRequest request,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { message = "请以 multipart/form-data 上传插件包（ZIP）。" });

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { message = "缺少插件包文件。" });

            await using var stream = file.OpenReadStream();
            var result = await manager.InstallAsync(stream, PluginActors.Api, cancellationToken);

            return result.Success
                ? Results.Ok(new
                {
                    result.PluginId,
                    result.Version,
                    result.PackageDigest,
                    result.Warnings,
                    state = manager.FindHost(result.PluginId!)?.State.ToString()
                })
                : Results.Json(
                    new { message = "插件安装失败。", errors = result.Errors, warnings = result.Warnings },
                    statusCode: StatusCodes.Status400BadRequest);
        })
        .DisableAntiforgery();

        group.MapPost("/{id}/rollback", async (
            string id,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            var result = await manager.RollbackAsync(id, PluginActors.Api, cancellationToken);

            return result.Success
                ? Results.Ok(new { result.PluginId, result.FromVersion, result.ToVersion })
                : Results.Json(
                    new { message = "回滚失败。", errors = result.Errors },
                    statusCode: StatusCodes.Status409Conflict);
        });

        group.MapDelete("/{id}", async (
            string id,
            PluginManager manager,
            CancellationToken cancellationToken) =>
        {
            var result = await manager.UninstallAsync(id, PluginActors.Api, cancellationToken);
            return result.Success ? Results.NoContent() : ToResult(result);
        });
    }

    private static IResult ToResult(PluginOperationResult result) =>
        result.Success
            ? Results.Ok(new { result.PluginId, result.State, result.Message })
            : Results.Json(
                new { message = "操作失败。", errors = result.Errors },
                statusCode: StatusCodes.Status400BadRequest);

    /// <summary>把插件错误码映射为 HTTP 语义，便于前端与运维区分故障类型。</summary>
    private static IResult ToProblem(PluginRpcException exception)
    {
        var status = exception.Code switch
        {
            PluginProtocol.ErrorUnknownCapability => StatusCodes.Status404NotFound,
            PluginProtocol.ErrorInvalidParams => StatusCodes.Status400BadRequest,
            PluginProtocol.ErrorPermissionDenied => StatusCodes.Status403Forbidden,
            PluginProtocol.ErrorNotConfigured => StatusCodes.Status409Conflict,
            PluginProtocol.ErrorCancelled => StatusCodes.Status408RequestTimeout,
            PluginProtocol.ErrorIncompatibleProtocol => StatusCodes.Status409Conflict,
            PluginProtocol.ErrorUnhealthy => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway
        };

        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: status);
    }

    /// <summary>能力调用请求。<c>arguments</c> 原样透传给插件；<c>confirm</c> 用于副作用能力授权。</summary>
    public sealed record PluginInvokeRequest(string CapabilityId, JsonNode? Arguments, bool? Confirm);

    /// <summary>平台同步请求。</summary>
    public sealed record PluginSyncRequest(string CapabilityId, Guid? KnowledgeBaseId, string? Cursor);

    /// <summary>受信任公钥的写入请求。只接受公钥；私钥会被拒绝。</summary>
    public sealed record TrustedKeyRequest(string PluginId, string? PublicKeyPem, string? Algorithm, string? Note);

    /// <summary>启用/停用请求。</summary>
    public sealed record PluginEnabledRequest(bool Enabled);

    /// <summary>权限授予请求。<c>permissions</c> 为完整集合（不是增量）。</summary>
    public sealed record PluginPermissionsRequest(IReadOnlyList<string>? Permissions);

    /// <summary>
    /// 写入范围与配额。
    /// <c>allowedKnowledgeBaseIds</c> 为空表示不限制；<c>maxDocuments</c> 为 0 表示跟随全局默认。
    /// </summary>
    public sealed record PluginScopeRequest(IReadOnlyList<Guid>? AllowedKnowledgeBaseIds, int? MaxDocuments);
}
