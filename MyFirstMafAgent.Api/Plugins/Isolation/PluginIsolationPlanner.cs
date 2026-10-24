using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins.Isolation;

/// <summary>一条文件系统授权：给容器 SID 什么权限、在哪、是否递归。</summary>
/// <param name="Path">绝对路径。</param>
/// <param name="Kind">见 <see cref="PluginIsolationGrantKinds"/>。</param>
public sealed record PluginIsolationGrant(string Path, string Kind);

public static class PluginIsolationGrantKinds
{
    /// <summary>读 + 执行 + 遍历，递归继承（插件自己的代码目录）。</summary>
    public const string ReadExecute = "read-execute";

    /// <summary>修改，递归继承（插件自己的数据目录）。</summary>
    public const string Modify = "modify";

    /// <summary>只给"穿过这个目录"的权限，不列目录、不读文件。实测已不需要，保留常量仅作文档。
    /// <para>
    /// 早期设计给插件目录的每个祖先都加一条 Traverse，实测是错的：
    /// 一是<b>不需要</b>——容器凭叶子目录上的继承 ACE 就能穿越整条路径
    /// （已在 C:\Temp 与 用户目录+中文路径 上实测）；二是<b>代价极大</b>——
    /// 给 C:\ 加一条 ACE 会让 Windows 在整个驱动器上递归重整继承，实测卡死数分钟。
    /// </para>
    /// </summary>
    public const string Traverse = "traverse";
}

/// <summary>
/// 一次启动需要的能力与授权清单。
///
/// <para>
/// 这个对象刻意做成<b>纯计算结果</b>：给定插件目录、数据目录、权限声明，
/// 就能算出"要给容器哪些能力 SID、要授哪些路径"。这样它可以被单测钉住，
/// 而不需要真的创建容器（创建容器在 CI/其它平台上不一定可用）。
/// 真正的系统调用在 <see cref="WindowsAppContainer"/> 里，只负责执行这份清单。
/// </para>
/// </summary>
public sealed record PluginIsolationPlan(
    bool Network,
    IReadOnlyList<string> CapabilitySids,
    IReadOnlyList<PluginIsolationGrant> Grants,
    IReadOnlyDictionary<string, string> EnvironmentOverrides);

/// <summary>
/// 把"插件声明了什么权限"翻译成"容器要拿到什么"。
///
/// <para>
/// 三条必须保持的规则：
/// 一是<b>没有 <c>network</c> 就不附加 internetClient</b>——这是整个 D-1 的意义所在；
/// 二是<b>插件目录只给读</b>，可写范围收紧到它自己的 <c>.data</c>；
/// 三是<b>只授我们关心的那几层目录，不碰祖先</b>——祖先的 ACE 既无必要，
/// 又会在驱动器根上触发递归 ACL 传播（实测卡死数分钟）。
/// </para>
/// </summary>
public static class PluginIsolationPlanner
{
    /// <summary>ALL APPLICATION PACKAGES。任何容器都自动拥有，只用于可读性诊断。</summary>
    public const string AllApplicationPackagesSid = "S-1-15-2-1";

    /// <summary>internetClient 能力。授予它才有出站网络。</summary>
    public const string InternetClientSid = "S-1-15-3-1";

    /// <summary>privateNetworkClientServer 能力（局域网/本机回环之外的内网）。未使用，留作文档。</summary>
    public const string PrivateNetworkSid = "S-1-15-3-2";

    /// <summary>
    /// 计算授权清单。
    /// </summary>
    /// <param name="pluginDirectory">当前生效的插件包目录（只读）。</param>
    /// <param name="dataDirectory">插件可写数据目录。</param>
    /// <param name="permissions">manifest 声明并经用户授予的权限。</param>
    /// <param name="readableRoots">额外只读目录（已解析成绝对路径）。</param>
    /// <param name="writableRoots">额外可写目录。</param>
    /// <param name="runtimeRoot">插件根目录下的运行时缓存（<c>plugins/.runtimes</c>），给只读。</param>
    /// <param name="redirectUserDirectories">是否重定向 TEMP/USERPROFILE/APPDATA/LOCALAPPDATA。</param>
    public static PluginIsolationPlan Build(
        string pluginDirectory,
        string? dataDirectory,
        IReadOnlyList<string> permissions,
        IReadOnlyList<string>? readableRoots = null,
        IReadOnlyList<string>? writableRoots = null,
        string? runtimeRoot = null,
        bool redirectUserDirectories = true)
    {
        var network = permissions.Contains(PluginPermissions.Network, StringComparer.OrdinalIgnoreCase);

        var capabilitySids = new List<string>();
        if (network) capabilitySids.Add(InternetClientSid);

        var grants = new List<PluginIsolationGrant>();

        // 只授插件目录自己：容器凭这条带继承的 ACE 就能从磁盘根一路穿到插件文件，
        // 不需要（也绝不能）去改祖先目录的 ACL。
        grants.Add(new PluginIsolationGrant(pluginDirectory, PluginIsolationGrantKinds.ReadExecute));

        if (!string.IsNullOrWhiteSpace(dataDirectory))
            grants.Add(new PluginIsolationGrant(dataDirectory!, PluginIsolationGrantKinds.Modify));

        foreach (var root in readableRoots ?? [])
            if (!string.IsNullOrWhiteSpace(root))
                grants.Add(new PluginIsolationGrant(root, PluginIsolationGrantKinds.ReadExecute));

        if (!string.IsNullOrWhiteSpace(runtimeRoot))
            grants.Add(new PluginIsolationGrant(runtimeRoot!, PluginIsolationGrantKinds.ReadExecute));

        foreach (var root in writableRoots ?? [])
            if (!string.IsNullOrWhiteSpace(root))
                grants.Add(new PluginIsolationGrant(root, PluginIsolationGrantKinds.Modify));

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (redirectUserDirectories && !string.IsNullOrWhiteSpace(dataDirectory))
        {
            // 容器进程写不了用户目录：不给它一个可写的家，node/npx/python 会在启动阶段就失败，
            // 用户看到的只会是"插件启动超时"，完全看不出原因。
            environment["USERPROFILE"] = dataDirectory!;
            environment["HOME"] = dataDirectory!;
            environment["APPDATA"] = Path.Combine(dataDirectory!, "appdata");
            environment["LOCALAPPDATA"] = Path.Combine(dataDirectory!, "localappdata");
            environment["TEMP"] = Path.Combine(dataDirectory!, "temp");
            environment["TMP"] = Path.Combine(dataDirectory!, "temp");
        }

        return new PluginIsolationPlan(network, capabilitySids, Deduplicate(grants), environment);
    }

    /// <summary>
    /// 解析隔离强度。<paramref name="configured"/> 为空时按平台默认：
    /// Windows 强制，其它平台只警告（沙箱还没实现，硬性要求会让插件功能整体不可用）。
    /// </summary>
    public static PluginIsolationMode ResolveMode(string? configured, bool isWindows)
    {
        switch (configured?.Trim().ToLowerInvariant())
        {
            case "off":
            case "none":
            case "false":
                return PluginIsolationMode.Off;
            case "warn":
            case "best-effort":
            case "besteffort":
                return PluginIsolationMode.Warn;
            case "require":
            case "required":
            case "on":
            case "true":
                return PluginIsolationMode.Require;
        }

        return isWindows ? PluginIsolationMode.Require : PluginIsolationMode.Warn;
    }

    public static string ToConfigValue(PluginIsolationMode mode) => mode switch
    {
        PluginIsolationMode.Off => "off",
        PluginIsolationMode.Require => "require",
        _ => "warn"
    };

    private static IReadOnlyList<PluginIsolationGrant> Deduplicate(IEnumerable<PluginIsolationGrant> grants)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PluginIsolationGrant>();

        // 同一个路径出现多次时保留"更强"的那条：Modify > ReadExecute > Traverse。
        foreach (var grant in grants.OrderByDescending(item => Rank(item.Kind)))
        {
            if (seen.Add(grant.Path)) result.Add(grant);
        }

        return result;
    }

    private static int Rank(string kind) => kind switch
    {
        PluginIsolationGrantKinds.Modify => 3,
        PluginIsolationGrantKinds.ReadExecute => 2,
        _ => 1
    };
}
