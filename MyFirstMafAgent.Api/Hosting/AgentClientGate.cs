using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Domain.Agents;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// MCP 数据面的统一准入闸门：<b>认证 → 启停 → 限流 → 工具准入 → 审计</b>。
///
/// <para>
/// 为什么放在中间件而不是逐个工具里做：工具会不断增加，逐点加固必然漏。
/// 把判定集中在这里，并且让「清单之外的工具默认拒绝」，
/// 新增工具忘记登记时是「不可用」，而不是「对所有人开放」。
/// </para>
///
/// <para>
/// 桌面模式的一次性令牌只守 <c>/api</c>：它按设计不下发给渲染进程、不落盘，
/// 外部 Agent 根本拿不到。因此 <c>/mcp</c> 改用可长期使用的客户端密钥，
/// 才能既保证桌面自用，又允许 Cursor/Claude 这类外部客户端接入。
/// </para>
/// </summary>
public static class AgentClientGate
{
    private const string ClientItemKey = "MyFirstMafAgent.AgentClient";

    /// <summary>取当前请求命中的客户端；未命中（例如关闭了密钥校验）返回 null。</summary>
    public static AgentClient? GetClient(HttpContext? context) =>
        context?.Items.TryGetValue(ClientItemKey, out var value) == true && value is AgentClient client
            ? client
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

            var services = context.RequestServices;
            var clients = services.GetRequiredService<AgentClientService>();
            var limiter = services.GetRequiredService<AgentRateLimiter>();
            var callLog = services.GetRequiredService<IAgentCallLogStore>();

            var presentedKey = AgentClientKey.ExtractFromHeaders(
                context.Request.Headers.Authorization.ToString(),
                context.Request.Headers["X-Api-Key"].ToString());

            var client = await clients.AuthenticateAsync(
                presentedKey,
                context.Connection.RemoteIpAddress?.ToString(),
                context.RequestAborted);

            if (client is null)
            {
                logger.LogWarning(
                    "Rejected MCP request with an unknown or missing client key: {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                await WriteErrorAsync(context, null, -32001, "缺少或无效的客户端密钥。", StatusCodes.Status401Unauthorized);
                return;
            }

            if (!client.Enabled)
            {
                await RecordAsync(callLog, client.Id, null, null, AgentCallOutcomes.Denied,
                    StatusCodes.Status403Forbidden, 0, "客户端已停用");
                await WriteErrorAsync(context, null, -32003, $"客户端 '{client.Name}' 已停用。", StatusCodes.Status403Forbidden);
                return;
            }

            if (!limiter.TryAcquire(client.Id, client.RequestsPerMinute, out var retryAfterSeconds))
            {
                context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
                await RecordAsync(callLog, client.Id, null, null, AgentCallOutcomes.Denied,
                    StatusCodes.Status429TooManyRequests, 0, $"超出每分钟 {client.RequestsPerMinute} 次的上限");
                await WriteErrorAsync(context, null, -32004,
                    $"请求过于频繁：上限 {client.RequestsPerMinute} 次/分钟，请 {retryAfterSeconds} 秒后重试。",
                    StatusCodes.Status429TooManyRequests);
                return;
            }

            context.Items[ClientItemKey] = client;

            // 只有 tools/call 需要读参数做权限判定；其余方法（initialize、tools/list…）直接放行。
            var calls = await ReadToolCallsAsync(context, options.MaxRequestBodyBytes, logger);
            if (calls.TooLarge)
            {
                await WriteErrorAsync(context, null, -32600,
                    $"请求体超过 {options.MaxRequestBodyBytes} 字节上限。", StatusCodes.Status413PayloadTooLarge);
                return;
            }

            var defaultKnowledgeBase = KnowledgeBaseStore.DefaultKnowledgeBaseId;
            Guid? effectiveKnowledgeBaseId = null;

            foreach (var call in calls.Items)
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
                        StatusCodes.Status200OK);
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
                    calls.Items.FirstOrDefault(item => item.Method == "tools/call")?.Tool,
                    effectiveKnowledgeBaseId?.ToString("D"),
                    outcome, context.Response.StatusCode, stopwatch.ElapsedMilliseconds, failure);
            }
        });
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
        int statusCode)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        if (statusCode is StatusCodes.Status401Unauthorized)
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"MyFirstMafAgent\", header=\"X-Api-Key\"";

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

    /// <summary>从请求体里读出所有 <c>tools/call</c> 的目标工具与关键参数。</summary>
    private static async Task<McpCallScan> ReadToolCallsAsync(
        HttpContext context,
        int maxBytes,
        ILogger logger)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) return McpCallScan.Empty;

        var contentType = context.Request.ContentType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return McpCallScan.Empty;

        if (context.Request.ContentLength is { } declared && declared > maxBytes)
            return McpCallScan.OverLimit;

        context.Request.EnableBuffering();

        string body;
        using (var reader = new StreamReader(
            context.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        // 一定要回卷，否则 MCP 处理器读到的就是空体。
        context.Request.Body.Position = 0;

        if (Encoding.UTF8.GetByteCount(body) > maxBytes) return McpCallScan.OverLimit;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            // 不是 JSON 就交给后续处理器报错，这里不越权判定。
            logger.LogDebug(exception, "MCP request body is not valid JSON; skipping tool gating.");
            return McpCallScan.Empty;
        }

        var items = new List<McpCallInfo>();
        switch (root)
        {
            case JsonObject single:
                items.Add(ParseCall(single));
                break;

            case JsonArray batch:
                foreach (var node in batch)
                {
                    if (node is JsonObject entry) items.Add(ParseCall(entry));
                }

                break;
        }

        return new McpCallScan(items, TooLarge: false);
    }

    private static McpCallInfo ParseCall(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var id = message["id"]?.DeepClone();

        if (!string.Equals(method, "tools/call", StringComparison.Ordinal))
            return new McpCallInfo(method, null, null, null, id);

        var parameters = message["params"] as JsonObject;
        var tool = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"] as JsonObject;

        return new McpCallInfo(
            method,
            tool,
            ParseGuid(arguments?["knowledgeBaseId"]),
            ParseGuid(arguments?["documentId"]),
            id);
    }

    private static Guid? ParseGuid(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (!value.TryGetValue<string>(out var text)) return null;

        return Guid.TryParse(text, out var id) && id != Guid.Empty ? id : null;
    }

    private sealed record McpCallInfo(string? Method, string? Tool, Guid? KnowledgeBaseId, Guid? DocumentId, JsonNode? Id);

    private sealed record McpCallScan(IReadOnlyList<McpCallInfo> Items, bool TooLarge)
    {
        public static McpCallScan Empty { get; } = new([], TooLarge: false);

        public static McpCallScan OverLimit { get; } = new([], TooLarge: true);
    }
}
