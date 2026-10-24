using System.Text.Json.Serialization;

namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>
/// 插件包 manifest。字段为协议契约，改名或删除等于破坏兼容，必须提升 schema 主版本。
/// </summary>
public sealed record PluginManifest
{
    /// <summary>manifest schema 版本，当前为 <see cref="PluginProtocol.ManifestSchemaVersion"/>。</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = PluginProtocol.ManifestSchemaVersion;

    /// <summary>反向域名 ID，例如 <c>com.example.notes</c>。全局唯一，用作安装目录与表主键。</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>插件自身版本，SemVer。</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>入口：如何启动插件进程。相对路径以插件目录为基准。</summary>
    [JsonPropertyName("entry")]
    public PluginEntry? Entry { get; init; }

    /// <summary>运行时期标识，例如 <c>dotnet</c>、<c>node</c>。用于诊断与后续跨语言 SDK。</summary>
    [JsonPropertyName("runtime")]
    public string? Runtime { get; init; }

    /// <summary>插件支持的协议版本区间。核心只在交集非空时握手。</summary>
    [JsonPropertyName("protocol")]
    public ProtocolRange? Protocol { get; init; }

    /// <summary>插件声明的能力；未声明的能力不可调用。</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<PluginCapability> Capabilities { get; init; } = [];

    /// <summary>配置 schema（JSON Schema）。核心据此校验用户配置，不信任插件自述。</summary>
    [JsonPropertyName("config")]
    public PluginConfigContract? Config { get; init; }

    [JsonPropertyName("healthCheck")]
    public PluginHealthCheckContract? HealthCheck { get; init; }

    /// <summary>资源与并发限制。缺省值由核心补全，插件不能放宽。</summary>
    [JsonPropertyName("limits")]
    public PluginLimits? Limits { get; init; }

    /// <summary>完整性：发布者提供的哈希与可选签名。</summary>
    [JsonPropertyName("integrity")]
    public PluginIntegrity? Integrity { get; init; }
}

/// <summary>插件进程启动方式。</summary>
public sealed record PluginEntry
{
    /// <summary>可执行文件（相对插件目录）或宿主命令（如 <c>dotnet</c>）。</summary>
    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>工作目录，相对插件目录；默认插件目录本身。</summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }
}

/// <summary>一项能力声明。</summary>
public sealed record PluginCapability
{
    /// <summary>插件内唯一的能力 ID，例如 <c>notes.search</c>。</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>见 <see cref="PluginCapabilityKinds"/>。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// agent-tool 能力的入参 schema；platform-connector 可留空。
    /// </summary>
    [JsonPropertyName("parameters")]
    public string? Parameters { get; init; }

    /// <summary>需要的权限，见 <see cref="PluginPermissions"/>。</summary>
    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>副作用等级，见 <see cref="PluginSideEffectLevels"/>。</summary>
    [JsonPropertyName("sideEffect")]
    public string SideEffect { get; init; } = PluginSideEffectLevels.Read;
}

public sealed record PluginConfigContract
{
    /// <summary>JSON Schema（object）。为空表示插件没有配置项。</summary>
    [JsonPropertyName("schema")]
    public string? Schema { get; init; }

    /// <summary>必须由用户填写的字段名。</summary>
    [JsonPropertyName("required")]
    public IReadOnlyList<string> Required { get; init; } = [];
}

public sealed record PluginHealthCheckContract
{
    /// <summary>健康检查间隔（秒）。核心不会低于 5 秒。</summary>
    [JsonPropertyName("intervalSeconds")]
    public int IntervalSeconds { get; init; } = 30;

    /// <summary>单次检查超时（毫秒）。</summary>
    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; init; } = 5000;
}

public sealed record PluginLimits
{
    [JsonPropertyName("maxMemoryMb")]
    public int MaxMemoryMb { get; init; } = 512;

    [JsonPropertyName("maxConcurrentInvocations")]
    public int MaxConcurrentInvocations { get; init; } = 2;

    [JsonPropertyName("invocationTimeoutMs")]
    public int InvocationTimeoutMs { get; init; } = 30_000;

    [JsonPropertyName("maxRequestBytes")]
    public int MaxRequestBytes { get; init; } = 1_048_576;

    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; init; } = 0;
}

public sealed record PluginIntegrity
{
    /// <summary>插件包 SHA-256（十六进制）。为空表示未提供摘要。</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    /// <summary>发布者签名（格式后续定义）。为空表示未签名。</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }
}
