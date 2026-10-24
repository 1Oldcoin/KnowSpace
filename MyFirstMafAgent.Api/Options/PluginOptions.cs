namespace MyFirstMafAgent.Api.Options;

/// <summary>
/// 插件运行时配置。插件以独立子进程运行，通过 stdio JSON-RPC 与核心通信。
/// </summary>
public sealed class PluginOptions
{
    public const string SectionName = "Plugins";

    /// <summary>插件根目录。每个插件一个子目录，内含 manifest.json。</summary>
    public string RootPath { get; set; } = "App_Data/plugins";

    /// <summary>是否启用插件运行时。关闭时不会启动任何插件进程。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 启动白名单。为空表示启动所有通过校验的插件；非空时只启动列出的插件 ID。
    /// 该白名单<b>不能</b>覆盖持久化的“已停用”状态，只用于额外收紧。
    /// </summary>
    public List<string> EnabledPluginIds { get; set; } = [];

    /// <summary>插件进程启动 + 握手的总超时（秒）。</summary>
    public int StartupTimeoutSeconds { get; set; } = 30;

    /// <summary>调用超时上限（毫秒）。插件 manifest 的值不能超过该上限。</summary>
    public int MaxInvocationTimeoutMs { get; set; } = 120_000;

    /// <summary>默认健康检查间隔（秒）；manifest 可缩短到 5 秒，不可更长。</summary>
    public int HealthIntervalSeconds { get; set; } = 30;

    /// <summary>是否把插件声明的 agent-tool 能力桥接给聊天模型。</summary>
    public bool EnableToolBridge { get; set; } = true;

    /// <summary>插件被停止后是否自动重启（按退避策略）。</summary>
    public bool RestartOnCrash { get; set; } = true;

    /// <summary>重启退避上限（秒）。</summary>
    public int MaxRestartBackoffSeconds { get; set; } = 60;

    /// <summary>单次同步最多处理的条目数，防止插件返回超大批次。</summary>
    public int MaxIngestionItemsPerBatch { get; set; } = 200;

    /// <summary>
    /// 每个插件在每个知识库下最多保留多少条活跃平台条目；0 表示不限制。
    /// 插件安装态里的 <c>MaxDocuments</c> 优先于此值。
    /// </summary>
    public int DefaultMaxDocumentsPerPlugin { get; set; } = 5000;

    /// <summary>
    /// 完整性校验策略：<c>off</c> 跳过；<c>warn</c> 只记录警告；
    /// <c>enforce</c> 要求 manifest 同时提供 sha256 与签名，任一不符即拒绝启动与安装。
    /// </summary>
    public string IntegrityPolicy { get; set; } = "warn";

    /// <summary>受信任的发布者公钥。未配置时签名一律视为不可信。</summary>
    public List<PluginTrustedKey> TrustedPublisherKeys { get; set; } = [];

    /// <summary>
    /// 是否自动授予 manifest 声明的全部权限。
    /// 默认 <c>false</c>：声明了权限的插件必须由用户显式授权后才能启动。
    /// </summary>
    public bool AutoGrantPermissions { get; set; } = false;

    /// <summary>
    /// 是否要求 <c>write-external</c> / <c>destructive</c> 能力显式确认，
    /// 并因此禁止模型直接调用这类能力。默认开启。
    /// </summary>
    public bool RequireConfirmationForSideEffects { get; set; } = true;

    /// <summary>每个插件保留的历史版本数（用于回滚）。小于 1 时按 1 处理。</summary>
    public int KeepVersions { get; set; } = 3;

    /// <summary>允许安装的插件包大小上限（字节），默认 256 MB。</summary>
    public long MaxPackageBytes { get; set; } = 268_435_456;

    /// <summary>
    /// 升级/停用前等待在途调用结束的秒数。
    /// 超过它仍未结束时强制停止进程并在审计中记录——绝不允许一次卡死的调用把升级永久阻塞。
    /// </summary>
    public int DrainTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// 是否启用“定时同步”：插件返回 <c>nextSyncAfterSeconds</c> 后，由任务系统按该间隔自动再次同步。
    /// 关闭后同步只能手动触发。
    /// </summary>
    public bool ScheduledSyncEnabled { get; set; } = true;

    /// <summary>
    /// 定时同步的最小间隔（秒）。插件报的间隔比它小就按它算，
    /// 避免一个写错单位的插件把任务队列刷爆。
    /// </summary>
    public int MinScheduledSyncIntervalSeconds { get; set; } = 60;
}

/// <summary>受信任的发布者公钥。按 pluginId 前缀匹配（按域名段）。</summary>
public sealed class PluginTrustedKey
{
    /// <summary>插件 ID 或 ID 前缀，例如 <c>com.example</c> 覆盖 <c>com.example.notes</c>。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary><c>ecdsa-p256</c> 或 <c>rsa</c>。</summary>
    public string Algorithm { get; set; } = "ecdsa-p256";

    /// <summary>PEM 编码公钥。</summary>
    public string PublicKeyPem { get; set; } = string.Empty;
}
