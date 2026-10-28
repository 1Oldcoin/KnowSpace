using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Domain.Agents;
using MyFirstMafAgent.Api.OAuth;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;
using ModelContextProtocol.Protocol;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// MCP 数据面的统一准入闸门：<b>协议探测 → 认证 → 启停 → 限流 → 工具准入 → 审计</b>。
///
/// <para>
/// 为什么放在中间件而不是逐个工具里做：工具会不断增加，逐点加固必然漏。
/// 把判定集中在这里，并且让「清单之外的工具默认拒绝」，
/// 新增工具忘记登记时是「不可用」，而不是「对所有人开放」。
/// </para>
///
/// <para>
/// 有两条认证通道，<b>共用同一个闸门</b>：
/// </para>
/// <list type="bullet">
///   <item><description><c>maf_</c> 客户端密钥：CI、脚本、不支持 OAuth 的客户端（长期保留）。</description></item>
///   <item><description>OAuth Bearer 令牌：普通用户的主路径。令牌会被翻译成同样的
///   <see cref="AgentClient"/> 形状，因此能力/白名单/知识库范围/限流的判定逻辑只有一份。</description></item>
/// </list>
///
/// <para>
/// 桌面模式的一次性令牌只守 <c>/api</c>：它按设计不下发给渲染进程、不落盘，
/// 外部 Agent 根本拿不到。因此 <c>/mcp</c> 使用上面两条长期通道。
/// </para>
/// </summary>
public static class AgentClientGate
{
    private const string ClientItemKey = "MyFirstMafAgent.AgentClient";
    private const string OAuthPrincipalItemKey = "MyFirstMafAgent.OAuthPrincipal";

    /// <summary>取当前请求命中的客户端；未命中（例如关闭了密钥校验）返回 null。</summary>
    public static AgentClient? GetClient(HttpContext? context) =>
        context?.Items.TryGetValue(ClientItemKey, out var value) == true && value is AgentClient client
            ? client
            : null;

    /// <summary>取当前请求的 OAuth 主体；走 <c>maf_</c> 密钥时返回 null。</summary>
    public static OAuthPrincipal? GetOAuthPrincipal(HttpContext? context) =>
        context?.Items.TryGetValue(OAuthPrincipalItemKey, out var value) == true && value is OAuthPrincipal principal
            ? principal
            : null;

    public static void UseAgentClientGate(this WebApplication app, McpServerRegistration mcp)
    {
        if (!mcp.Enabled) return;

        var options = app.Services.GetRequiredService<IOptions<McpOptions>>().Value;
        if (!options.RequireClientKey)
        {
            // 显式的调试后门：不鉴权、不限流、不审计。生产与桌面环境不要打开。
            app.Logger.LogWarning(
                "Mcp:RequireClientKey=false — /mcp 未鉴权，任何本机进程都可读取知识库。仅用于调试。");
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MyFirstMafAgent.Api.AgentClientGate");

        var serverOptions = app.Services.GetRequiredService<IOptions<ServerSecurityOptions>>().Value;
        var oauthOptions = app.Services.GetRequiredService<IOptions<McpOAuthOptions>>().Value;
        var oauthContext = OAuthServerContext.TryCreate(oauthOptions, mcp.Path);

        if (oauthOptions.Enabled && oauthContext is null)
        {
            logger.LogWarning(
                "Mcp:OAuth:Enabled=true 但 Issuer 未配置或不合法，OAuth 通道已禁用（仍可用 maf_ 密钥）。");
        }

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(mcp.Path, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                await next();
                return;
            }

            var diagnostics = context.RequestServices.GetRequiredService<McpProtocolDiagnostics>();
            var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var sample = new SampleState();

            try
            {
                await HandleAsync(context, next, options, serverOptions, oauthOptions, oauthContext,
                    logger, remoteAddress, sample);
            }
            finally
            {
                // 诊断永远记录，包括被拒的请求——「连不上」时最需要这些样本。
                diagnostics.Record(new McpProtocolSample(
                    DateTimeOffset.UtcNow,
                    remoteAddress,
                    context.Request.Headers.UserAgent.ToString(),
                    sample.Method,
                    sample.ProtocolVersion,
                    sample.Era,
                    sample.Authenticated,
                    context.Response.StatusCode));
            }
        });
    }

    private static async Task HandleAsync(
        HttpContext context,
        Func<Task> next,
        McpOptions options,
        ServerSecurityOptions serverOptions,
        McpOAuthOptions oauthOptions,
        OAuthServerContext? oauthContext,
        ILogger logger,
        string remoteAddress,
        SampleState sample)
    {
        var services = context.RequestServices;
        var clients = services.GetRequiredService<AgentClientService>();
        var limiter = services.GetRequiredService<AgentRateLimiter>();
        var throttle = services.GetRequiredService<AgentAuthThrottle>();
        var callLog = services.GetRequiredService<IAgentCallLogStore>();

        // ── 1. 解析请求体（鉴权之前）──
        // 结果同时用于「discover 免认证判定」与「工具准入」，只有一份解析、一个结论。
        var peek = await McpRequestPeekReader.TryPeekAsync(
            context, options.MaxRequestBodyBytes, context.RequestAborted);
        sample.Method = peek.Method;
        sample.ProtocolVersion = peek.ProtocolVersion;
        sample.Era = peek.Era;

        // ── 2. 传输层拒绝（fail-closed）──
        // 解析不出结论就拒绝，不再"当作无需判定"放行：改配置或换客户端都能修，静默越权不能。
        switch (peek.Status)
        {
            case McpBodyStatus.NotJson:
                await WriteErrorAsync(context, null, -32600,
                    $"Content-Type 必须是 application/json（{peek.Error}）。",
                    StatusCodes.Status415UnsupportedMediaType, oauthContext);
                return;

            case McpBodyStatus.TooLarge:
                await WriteErrorAsync(context, null, -32600,
                    $"请求体超过 {options.MaxRequestBodyBytes} 字节上限。",
                    StatusCodes.Status413PayloadTooLarge, oauthContext);
                return;

            case McpBodyStatus.Malformed:
                await WriteErrorAsync(context, null, -32700, $"请求体不是合法的 JSON-RPC 消息：{peek.Error}",
                    StatusCodes.Status400BadRequest, oauthContext);
                return;

            case McpBodyStatus.Unreadable:
                await WriteErrorAsync(context, null, -32700, "请求体读取失败。",
                    StatusCodes.Status400BadRequest, oauthContext);
                return;
        }

        if (peek.IsBatch)
        {
            // 批量请求只解析第一条就判定，等于给「第二条越权」留了口子；直接不支持。
            await WriteErrorAsync(context, null, -32600,
                "不支持 JSON-RPC 批量请求，请逐条发送。",
                StatusCodes.Status400BadRequest, oauthContext);
            return;
        }

        // ── 3. server/discover 预认证放行 ──
        // 2026-07-28 移除了 initialize 握手，客户端第一件事就是问「你支持哪些版本」。
        // 这个请求不碰任何业务数据，只返回静态能力，因此不需要凭据；
        // 真正的数据面（tools/*）仍然要过认证。
        if (peek.IsSingleServerDiscover)
        {
            sample.Authenticated = true;
            await next();
            return;
        }

        // 先拦被封禁的来源：失败请求没有客户端 ID，只能按 IP 计。
        if (throttle.IsBlocked(remoteAddress, out var blockedForSeconds))
        {
            logger.LogWarning(
                "Blocked MCP request from {Remote} after too many failed authentications ({RetryAfter}s left).",
                remoteAddress, blockedForSeconds);

            context.Response.Headers.RetryAfter = blockedForSeconds.ToString();
            await WriteErrorAsync(context, null, -32006,
                $"鉴权失败次数过多，请 {blockedForSeconds} 秒后重试。",
                StatusCodes.Status429TooManyRequests, oauthContext);
            return;
        }

        // ── 3. 认证：OAuth Bearer 或 maf_ 客户端密钥 ──
        var presented = AgentClientKey.ExtractFromHeaders(
            context.Request.Headers.Authorization.ToString(),
            context.Request.Headers["X-Api-Key"].ToString());

        AgentClient? client = null;

        var looksLikeClientKey = !string.IsNullOrWhiteSpace(presented)
            && presented!.StartsWith(AgentClientKey.Prefix, StringComparison.Ordinal);

        if (!looksLikeClientKey && !string.IsNullOrWhiteSpace(presented))
        {
            // 不是 maf_ 前缀 → 只可能是 OAuth 访问令牌。
            if (oauthContext is null)
            {
                logger.LogWarning(
                    "Rejected non-maf credential on /mcp from {Remote}: OAuth is not enabled.",
                    remoteAddress);
                await RejectAsync(context, throttle, serverOptions, remoteAddress, -32001,
                    "缺少或无效的客户端密钥（OAuth 未启用）。", oauthContext);
                return;
            }

            var oauth = services.GetRequiredService<OAuthService>();
            var principal = await oauth.AuthenticateBearerAsync(
                presented, oauthContext, remoteAddress, context.RequestAborted);

            if (principal is null)
            {
                await RejectAsync(context, throttle, serverOptions, remoteAddress, -32001,
                    "OAuth 访问令牌无效或已失效。", oauthContext);
                return;
            }

            client = principal.ToAgentClient(oauthOptions.RequestsPerMinute);
            context.Items[OAuthPrincipalItemKey] = principal;
        }
        else
        {
            client = await clients.AuthenticateAsync(
                presented,
                context.Connection.RemoteIpAddress?.ToString(),
                context.RequestAborted);

            if (client is null)
            {
                await RejectAsync(context, throttle, serverOptions, remoteAddress, -32001,
                    "缺少或无效的客户端密钥。", oauthContext);
                return;
            }
        }

        throttle.RecordSuccess(remoteAddress);
        sample.Authenticated = true;

        if (!client.Enabled)
        {
            await RecordAsync(callLog, client.Id, null, null, AgentCallOutcomes.Denied,
                StatusCodes.Status403Forbidden, 0, "客户端已停用");
            await WriteErrorAsync(context, null, -32003, $"客户端 '{client.Name}' 已停用。",
                StatusCodes.Status403Forbidden, oauthContext);
            return;
        }

        if (!limiter.TryAcquire(client.Id, client.RequestsPerMinute, out var retryAfterSeconds))
        {
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            await RecordAsync(callLog, client.Id, null, null, AgentCallOutcomes.Denied,
                StatusCodes.Status429TooManyRequests, 0, $"超出每分钟 {client.RequestsPerMinute} 次的上限");
            await WriteErrorAsync(context, null, -32004,
                $"请求过于频繁：上限 {client.RequestsPerMinute} 次/分钟，请 {retryAfterSeconds} 秒后重试。",
                StatusCodes.Status429TooManyRequests, oauthContext);
            return;
        }

        context.Items[ClientItemKey] = client;

        // ── 4. 工具准入 + 审计 ──
        await ContinueWithToolGatingAsync(context, next, client, options, logger, callLog, peek, oauthContext);
    }

    /// <summary>工具调用的准入判定与审计（认证通过之后）。</summary>
    private static async Task ContinueWithToolGatingAsync(
        HttpContext context,
        Func<Task> next,
        AgentClient client,
        McpOptions options,
        ILogger logger,
        IAgentCallLogStore callLog,
        McpRequestPeek peek,
        OAuthServerContext? oauthContext)
    {
        var services = context.RequestServices;

        // 参数来自上面那份解析结果，不存在"第二套解析器给出不同参数"的空间。
        var calls = peek.Calls;

        var defaultKnowledgeBase = KnowledgeBaseStore.DefaultKnowledgeBaseId;
        Guid? effectiveKnowledgeBaseId = null;

        foreach (var call in calls)
        {
            if (call.Method != "tools/call") continue;

            var descriptor = call.Tool is null ? null : McpToolCatalog.Find(call.Tool);
            var decision = AgentCallGate.Decide(
                client,
                call.Tool ?? string.Empty,
                call.KnowledgeBaseId,
                defaultKnowledgeBase);

            // get_document_content / get_document_summary 只带资料 ID，
            // 必须回查资料所属知识库，否则受限客户端可以按 GUID 猜读。
            if (decision.Allowed && descriptor is not null && call.DocumentId is { } documentId)
            {
                var documentKnowledgeBase = await services
                    .GetRequiredService<KnowledgeBaseStore>()
                    .GetDocumentKnowledgeBaseIdAsync(documentId, context.RequestAborted);

                decision = documentKnowledgeBase is { } owner
                    ? AgentCallGate.DecideDocumentAccess(client, owner)
                    : AgentCallDecision.Deny(-32005, $"资料不存在：{documentId:D}");
            }

            if (!decision.Allowed)
            {
                logger.LogWarning(
                    "Denied MCP tool {Tool} for client {ClientId}: {Reason}",
                    call.Tool, client.Id, decision.Message);

                await RecordAsync(callLog, client.Id, call.Tool, call.KnowledgeBaseId?.ToString("D"),
                    AgentCallOutcomes.Denied, (int)StatusCodes.Status403Forbidden, 0, decision.Message);

                await WriteErrorAsync(context, call.Id, decision.JsonRpcErrorCode, decision.Message,
                    StatusCodes.Status200OK, oauthContext);
                return;
            }

            effectiveKnowledgeBaseId ??= decision.EffectiveKnowledgeBaseId;
        }

        var stopwatch = Stopwatch.StartNew();
        var failed = false;
        string? failure = null;

        try
        {
            await next();
        }
        catch (Exception exception)
        {
            failed = true;
            failure = exception.GetType().Name + ": " + exception.Message;
            throw;
        }
        finally
        {
            stopwatch.Stop();

            var outcome = failed || context.Response.StatusCode >= 500
                ? AgentCallOutcomes.Failed
                : context.Response.StatusCode >= 400
                    ? AgentCallOutcomes.Denied
                    : AgentCallOutcomes.Success;

            // 审计失败不能反过来阻断调用。
            await RecordAsync(callLog, client.Id,
                calls.FirstOrDefault(item => item.Method == "tools/call")?.Tool,
                effectiveKnowledgeBaseId?.ToString("D"),
                outcome, context.Response.StatusCode, stopwatch.ElapsedMilliseconds, failure);
        }
    }

    private static async Task RejectAsync(
        HttpContext context,
        AgentAuthThrottle throttle,
        ServerSecurityOptions serverOptions,
        string remoteAddress,
        int code,
        string message,
        OAuthServerContext? oauthContext)
    {
        // 只在「失败」上计数：正常客户端的偶发拼错会被下一次成功清除。
        throttle.RecordFailure(
            remoteAddress,
            serverOptions.FailedAuthLimitPerMinute,
            serverOptions.FailedAuthBlockSeconds);

        await WriteErrorAsync(context, null, code, message, StatusCodes.Status401Unauthorized, oauthContext);
    }

    private static async Task RecordAsync(
        IAgentCallLogStore store,
        string clientId,
        string? tool,
        string? knowledgeBaseId,
        string outcome,
        int statusCode,
        long durationMs,
        string? error)
    {
        try
        {
            await store.RecordAsync(
                AgentCallRecord.Pending(clientId, tool, knowledgeBaseId, outcome, statusCode, durationMs, error),
                CancellationToken.None);
        }
        catch
        {
            // 审计是旁路：写失败只丢这条记录，不影响已经完成的调用。
        }
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        JsonNode? id,
        int code,
        string message,
        int statusCode,
        OAuthServerContext? oauthContext)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        if (statusCode is StatusCodes.Status401Unauthorized)
        {
            // 同时给出两条线索：OAuth 客户端据此自动发现授权服务器；
            // 不支持 OAuth 的客户端仍能看懂 header="X-Api-Key"。
            context.Response.Headers.WWWAuthenticate = oauthContext is not null
                ? OAuthEndpoints.BuildChallenge(oauthContext)
                : "Bearer realm=\"MyFirstMafAgent\", header=\"X-Api-Key\"";
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        await context.Response.WriteAsync(payload.ToJsonString(), Encoding.UTF8);
    }

    private sealed class SampleState
    {
        public string? Method { get; set; }
        public string? ProtocolVersion { get; set; }
        public string? Era { get; set; }
        public bool Authenticated { get; set; }
    }
}
