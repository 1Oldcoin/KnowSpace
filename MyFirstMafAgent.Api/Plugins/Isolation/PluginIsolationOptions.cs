namespace MyFirstMafAgent.Api.Plugins.Isolation;

/// <summary>
/// 隔离强度。
///
/// <para>
/// 三档而不是一个开关：<see cref="Off"/> 是"我知道我在做什么"的显式选择，
/// <see cref="Require"/> 是产品默认（做不到就拒绝启动），<see cref="Warn"/> 只在
/// 平台确实没有实现时用（例如 Linux/macOS 的沙箱还没做）——它必须被记日志并回显到接口，
/// 否则就成了"静默降级"。
/// </para>
/// </summary>
public enum PluginIsolationMode
{
    Off = 0,
    Warn = 1,
    Require = 2
}

/// <summary>
/// 一次启动的隔离实况。这个对象会原样出现在 <c>GET /api/plugins/{id}</c> 里，
/// 让用户能回答"这个插件现在到底能碰什么"。
/// </summary>
/// <param name="Mode">配置解析后的隔离强度。</param>
/// <param name="Applied">是否真的在受限容器里跑起来了。</param>
/// <param name="Kind">实现方式：<c>windows-appcontainer</c> / <c>none</c> / <c>unsupported</c>。</param>
/// <param name="Network">是否授予了出站网络能力。</param>
/// <param name="CapabilitySids">附加到容器令牌上的能力 SID。</param>
/// <param name="WritablePaths">容器内可写目录。</param>
/// <param name="ReadablePaths">额外获得只读授权的目录（不含插件自身目录）。</param>
/// <param name="Detail">给人看的补充说明（失败原因 / 降级原因）。</param>
public sealed record PluginIsolationReport(
    string Mode,
    bool Applied,
    string Kind,
    bool Network,
    IReadOnlyList<string> CapabilitySids,
    IReadOnlyList<string> WritablePaths,
    IReadOnlyList<string> ReadablePaths,
    string? Detail = null)
{
    public static PluginIsolationReport NotStarted(string mode, string reason) =>
        new(mode, Applied: false, Kind: PluginIsolationKinds.None, Network: false, [], [], [], reason);

    /// <summary>状态快照里的紧凑写法，例如 <c>appcontainer:network</c> / <c>none</c>。</summary>
    public string Short => Applied
        ? $"{Kind}:{(Network ? "network" : "offline")}"
        : $"{Kind}{(Detail is null ? string.Empty : ": " + Detail)}";
}

public static class PluginIsolationKinds
{
    /// <summary>Windows AppContainer（低完整性 + 能力令牌）。</summary>
    public const string WindowsAppContainer = "windows-appcontainer";

    /// <summary>没有隔离，普通子进程。</summary>
    public const string None = "none";

    /// <summary>当前平台没有可用实现。</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// 隔离配置。默认值就是产品态度：Windows 上"必须"，其它平台"先别静默骗自己"。
/// </summary>
public sealed class PluginIsolationOptions
{
    /// <summary>
    /// <c>off</c> / <c>warn</c> / <c>require</c>。留空表示按平台默认
    /// （Windows → <c>require</c>，其它 → <c>warn</c>）。
    /// </summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>
    /// 额外需要给容器只读权限的目录。典型用途：把某个独立安装的运行时
    /// （nodejs / python / dotnet）放进来，让插件能用它。
    /// 这些目录必须<b>当前用户能改 ACL</b>，否则授权会失败并拒绝启动。
    /// </summary>
    public List<string> ReadableRoots { get; set; } = [];

    /// <summary>额外需要给容器可写权限的目录（慎用：写权限等于给插件一个落脚点）。</summary>
    public List<string> WritableRoots { get; set; } = [];

    /// <summary>AppContainer 配置名前缀。最终名是 <c>{前缀}.{pluginId}</c>。</summary>
    public string ContainerNamePrefix { get; set; } = "MyFirstMafAgent.Plugin";

    /// <summary>
    /// 卸载插件时是否删除它的 AppContainer 配置。
    /// 默认保留：配置里可能还有用户后来手工加的授权，删了不可恢复。
    /// </summary>
    public bool DeleteProfileOnUninstall { get; set; }

    /// <summary>
    /// 是否把 <c>TEMP</c>/<c>USERPROFILE</c>/<c>APPDATA</c>/<c>LOCALAPPDATA</c> 重定向到插件数据目录。
    /// 默认开启：容器进程写不了用户目录，不重定向的话 node/python 这类运行时连临时文件都建不出来。
    /// </summary>
    public bool RedirectUserDirectories { get; set; } = true;
}
