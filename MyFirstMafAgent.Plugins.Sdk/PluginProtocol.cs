namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>
/// 插件协议的版本常量。协议与 manifest schema 各自独立演进：
/// 新增可选字段只提升 schema 次版本；删除字段或改变语义必须提升主版本。
/// </summary>
public static class PluginProtocol
{
    /// <summary>manifest.json 的 schema 版本。</summary>
    public const string ManifestSchemaVersion = "1.0";

    /// <summary>JSON-RPC 线路协议版本（完整 SemVer，便于区间比较）。</summary>
    public const string ProtocolVersion = "1.0.0";

    /// <summary>核心与插件都声明支持该主版本时才允许握手。</summary>
    public const int SupportedMajorVersion = 1;

    // 方法名：全部带 plugin/ 前缀，避免与未来其它命名空间冲突。
    public const string MethodInitialize = "plugin/initialize";
    public const string MethodConfigure = "plugin/configure";
    public const string MethodHealth = "plugin/health";
    public const string MethodListCapabilities = "plugin/capabilities/list";
    public const string MethodInvoke = "plugin/capabilities/invoke";
    public const string MethodShutdown = "plugin/shutdown";

    /// <summary>取消通知（无响应）。params: { "targetId": "&lt;原请求 id&gt;" }。</summary>
    public const string MethodCancel = "plugin/cancel";

    /// <summary>插件主动推送的日志通知（无响应）。</summary>
    public const string MethodLog = "plugin/log";

    // JSON-RPC 标准错误码
    public const int ErrorParse = -32700;
    public const int ErrorInvalidRequest = -32600;
    public const int ErrorMethodNotFound = -32601;
    public const int ErrorInvalidParams = -32602;
    public const int ErrorInternal = -32603;

    // 插件协议错误码
    public const int ErrorIncompatibleProtocol = -32001;
    public const int ErrorNotConfigured = -32002;
    public const int ErrorPermissionDenied = -32003;
    public const int ErrorUnhealthy = -32004;
    public const int ErrorUnknownCapability = -32005;
    public const int ErrorCancelled = -32800;

    /// <summary>
    /// 核心侧的准入控制拒绝了本次调用（并发已满且排队已满，或排队超时）。
    /// 这是<b>核心产生的</b>错误码，插件自己不需要也不应该返回它；调用方应退避后重试。
    /// </summary>
    public const int ErrorOverloaded = -32006;
}

/// <summary>
/// 插件对话方言：核心用哪一套方法名与插件说话。
///
/// <para>
/// <see cref="Native"/> 是本项目的自有协议（<c>plugin/*</c>），<see cref="Mcp"/>
/// 是标准 Model Context Protocol（<c>initialize</c> / <c>tools/list</c> / <c>tools/call</c>）。
/// 两者的共同点比想象中多得多——都是 JSON-RPC 2.0 over stdio、一行一条消息、stdout 只放协议消息，
/// 所以差异被收敛在方法名与结果形状这一层，进程管理、并发、审计、排空全部复用。
/// </para>
///
/// <para>
/// 为什么要有方言而不是"只支持一种"：让作者为本平台重写一份插件是没有必要的摩擦。
/// 一个现成的 stdio MCP server 只要能配上清单就该能用；反过来，需要配置下发、
/// 健康检查、平台连接器能力时再用自有协议。
/// </para>
/// </summary>
public static class PluginDialects
{
    /// <summary>本项目自有协议（<c>plugin/*</c> 方法）。</summary>
    public const string Native = "plugin";

    /// <summary>标准 MCP。核心用 <c>initialize</c> / <c>tools/list</c> / <c>tools/call</c> 驱动。</summary>
    public const string Mcp = "mcp";

    /// <summary>把别名规范成 <see cref="Native"/> / <see cref="Mcp"/>；无法识别时返回空串。</summary>
    public static string Normalize(string? dialect) => dialect?.Trim().ToLowerInvariant() switch
    {
        null or "" => Native,
        "plugin" or "native" or "builtin" or "zhijian" or "zhijian-plugin" => Native,
        "mcp" or "modelcontextprotocol" or "model-context-protocol" or "model_context_protocol" => Mcp,
        _ => string.Empty
    };

    public static bool IsKnown(string? dialect) => Normalize(dialect).Length > 0;

    public static bool IsMcp(string? dialect) => Normalize(dialect) == Mcp;
}

/// <summary>
/// MCP 侧的线路常量。只保留核心作为<b>客户端</b>需要用到的那几个方法与通知。
/// </summary>
public static class McpProtocol
{
    /// <summary>核心在 <c>initialize</c> 里声明支持的 MCP 版本。</summary>
    public const string Version = "2024-11-05";

    public const string MethodInitialize = "initialize";

    /// <summary>握手完成后的通知（无 id、无响应）。不发这条，部分 server 会拒绝后续调用。</summary>
    public const string MethodInitialized = "notifications/initialized";

    public const string MethodPing = "ping";
    public const string MethodListTools = "tools/list";
    public const string MethodCallTool = "tools/call";

    /// <summary>MCP 的取消通知（params: <c>{ requestId, reason }</c>）。</summary>
    public const string MethodCancelled = "notifications/cancelled";

    /// <summary>Server 主动推送的日志（params: <c>{ level, data }</c>）。</summary>
    public const string MethodMessage = "notifications/message";

    /// <summary>Tool 列表变化。当前只记录日志，不热重建能力表。</summary>
    public const string MethodToolsChanged = "notifications/tools/list_changed";
}

/// <summary>能力类型。两类能力的调用语义与授权要求不同，不能混用。</summary>
public static class PluginCapabilityKinds
{
    /// <summary>平台连接器：拉取/订阅外部平台内容，产出统一的摄取数据。</summary>
    public const string PlatformConnector = "platform-connector";

    /// <summary>Agent 工具：可被聊天模型调用的函数式能力。</summary>
    public const string AgentTool = "agent-tool";

    public static bool IsKnown(string? kind) =>
        kind is PlatformConnector or AgentTool;
}

/// <summary>副作用等级。调用前由核心按等级决定是否需要用户确认或记录审计。</summary>
public static class PluginSideEffectLevels
{
    /// <summary>只读：不修改任何外部状态。</summary>
    public const string Read = "read";

    /// <summary>写入本机数据（知识库、文件）。</summary>
    public const string WriteLocal = "write-local";

    /// <summary>写入外部平台（发帖、修改远端数据）。</summary>
    public const string WriteExternal = "write-external";

    /// <summary>破坏性操作（删除、撤回）。</summary>
    public const string Destructive = "destructive";

    public static bool IsKnown(string? level) =>
        level is Read or WriteLocal or WriteExternal or Destructive;

    /// <summary>是否需要用户显式确认。</summary>
    public static bool RequiresConfirmation(string? level) =>
        level is WriteExternal or Destructive;
}

/// <summary>插件可申请的权限。默认不授予任何权限。</summary>
public static class PluginPermissions
{
    public const string Network = "network";
    public const string FileRead = "file-read";
    public const string FileWrite = "file-write";
    public const string Clipboard = "clipboard";
    public const string Process = "process";

    /// <summary>全部已定义权限，用于校验 manifest 与授权请求。</summary>
    public static IReadOnlyList<string> All { get; } =
        [Network, FileRead, FileWrite, Clipboard, Process];

    public static bool IsKnown(string? permission) =>
        permission is Network or FileRead or FileWrite or Clipboard or Process;

    /// <summary>
    /// 生态里常见的权限写法到本项目词汇的映射。
    ///
    /// <para>
    /// 两点值得解释：<c>fs</c> 是<b>一个别名展开成两个权限</b>（读写无法从一个词里区分，
    /// 默认按"两者都要"处理，用户授予时可以在界面上只勾读）；带点的 <c>net.outbound</c>
    /// 之类是插件生态的既有词汇，照抄过来就能用，不必逼作者改写清单。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["network"] = [Network],
        ["net"] = [Network],
        ["net.outbound"] = [Network],
        ["net.inbound"] = [Network],
        ["internet"] = [Network],
        ["http"] = [Network],
        ["web"] = [Network],

        ["file-read"] = [FileRead],
        ["read-file"] = [FileRead],
        ["fs.read"] = [FileRead],
        ["file_read"] = [FileRead],

        ["file-write"] = [FileWrite],
        ["write-file"] = [FileWrite],
        ["fs.write"] = [FileWrite],
        ["file_write"] = [FileWrite],

        // 一个词 → 两个权限：包里只能说明"要碰文件"，读写分不开。
        ["fs"] = [FileRead, FileWrite],
        ["file"] = [FileRead, FileWrite],
        ["files"] = [FileRead, FileWrite],
        ["filesystem"] = [FileRead, FileWrite],
        ["file-system"] = [FileRead, FileWrite],

        ["clipboard"] = [Clipboard],
        ["clip"] = [Clipboard],

        ["process"] = [Process],
        ["proc"] = [Process],
        ["exec"] = [Process],
        ["shell"] = [Process]
    };

    /// <summary>
    /// 规范化一个权限词。无法识别时返回 false（调用方应把原词留给校验器报错，
    /// 而不是静默丢弃——作者要能看见自己写错了什么）。
    /// </summary>
    public static bool TryNormalize(string? permission, out IReadOnlyList<string> normalized)
    {
        normalized = [];
        if (string.IsNullOrWhiteSpace(permission)) return false;

        var trimmed = permission.Trim();
        if (Aliases.TryGetValue(trimmed, out var expanded))
        {
            normalized = expanded;
            return true;
        }

        return false;
    }

    /// <summary>批量规范化：已知词汇展开、未知词汇原样保留、结果去重且保持顺序。</summary>
    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string>? permissions)
    {
        if (permissions is null) return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var permission in permissions)
        {
            if (string.IsNullOrWhiteSpace(permission)) continue;

            if (TryNormalize(permission, out var expanded))
            {
                foreach (var item in expanded)
                    if (seen.Add(item)) result.Add(item);
            }
            else if (seen.Add(permission.Trim()))
            {
                result.Add(permission.Trim());
            }
        }

        return result;
    }
}
