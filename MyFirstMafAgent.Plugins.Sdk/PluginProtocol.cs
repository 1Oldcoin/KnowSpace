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
}
