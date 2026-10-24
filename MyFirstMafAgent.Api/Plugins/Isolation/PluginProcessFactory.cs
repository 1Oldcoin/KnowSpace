using System.ComponentModel;
using MyFirstMafAgent.Api.Infrastructure;

namespace MyFirstMafAgent.Api.Plugins.Isolation;

/// <summary>启动结果：要么拿到进程，要么拿到"为什么起不来"。</summary>
internal sealed record PluginProcessLaunch(PluginProcess? Process, PluginIsolationReport Isolation, string? Error)
{
    public bool Success => Process is not null;
}

/// <summary>
/// 把「插件启动规格 + 隔离配置」变成「一个已启动的受限进程」。
///
/// <para>
/// 这里是唯一做<b>策略决定</b>的地方：隔离建不起来时，<c>require</c> 拒绝启动，
/// <c>warn</c> 记一条警告然后普通启动，<c>off</c> 根本不尝试。
/// 拒绝是默认，因为静默降级等于没有隔离——用户以为有，其实没有。
/// </para>
/// </summary>
internal static class PluginProcessFactory
{
    /// <summary>容器进程必须能读到的环境变量白名单。其它一律不传（连接串之类不该外泄）。</summary>
    private static readonly string[] EssentialEnvironment =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec",
        "PATH", "Path", "PATHEXT",
        "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)",
        "CommonProgramFiles", "CommonProgramFiles(x86)",
        "ALLUSERSPROFILE", "PUBLIC",
        "COMPUTERNAME", "USERNAME", "USERDOMAIN",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "OS", "LANG", "LC_ALL", "DOTNET_ROOT", "DOTNET_CLI_HOME"
    ];

    public static PluginProcessLaunch Start(
        PluginProcess.StartSpec spec,
        PluginIsolationOptions options,
        string? runtimeRoot,
        IReadOnlyList<string>? readableRoots,
        IReadOnlyList<string>? writableRoots,
        ILogger logger)
    {
        var isWindows = OperatingSystem.IsWindows();
        var mode = PluginIsolationPlanner.ResolveMode(options.Mode, isWindows);
        var modeValue = PluginIsolationPlanner.ToConfigValue(mode);

        if (mode == PluginIsolationMode.Off)
        {
            var report = new PluginIsolationReport(
                modeValue, Applied: false, Kind: PluginIsolationKinds.None, Network: false, [], [], [],
                "隔离已被配置关闭（Plugins:Isolation:Mode=off）");

            return new PluginProcessLaunch(StartManaged(spec, report), report, null);
        }

        if (!isWindows)
        {
            // Linux/macOS 的沙箱（bwrap / sandbox-exec）还没实现，也未实测。
            // 这里如实报告"没有隔离"，而不是假装成功。
            const string detail = "当前平台没有实现插件沙箱（Linux 需 bwrap/unshare -n，macOS 需 sandbox-exec），未实测；"
                + "在实现之前，非 Windows 平台请把 Plugins:Isolation:Mode 设为 warn。";

            var report = new PluginIsolationReport(
                modeValue, Applied: false, Kind: PluginIsolationKinds.Unsupported, Network: false, [], [], [], detail);

            if (mode == PluginIsolationMode.Require)
                return new PluginProcessLaunch(null, report, $"拒绝启动插件：{detail}");

            logger.LogWarning("Plugin {PluginId} 未隔离启动：{Detail}", spec.PluginId, detail);
            return new PluginProcessLaunch(StartManaged(spec, report), report, null);
        }

        try
        {
            return StartIsolated(spec, options, runtimeRoot, readableRoots, writableRoots, modeValue, logger);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var report = new PluginIsolationReport(
                modeValue, Applied: false, Kind: PluginIsolationKinds.None, Network: false, [], [], [],
                exception.Message);

            if (mode == PluginIsolationMode.Require)
            {
                logger.LogError(exception, "Plugin {PluginId} 无法进入沙箱，拒绝启动", spec.PluginId);
                return new PluginProcessLaunch(
                    null,
                    report,
                    $"插件无法在受限沙箱里启动，已拒绝运行：{exception.Message}");
            }

            logger.LogWarning(exception, "Plugin {PluginId} 无法进入沙箱，按配置降级为普通子进程", spec.PluginId);
            return new PluginProcessLaunch(StartManaged(spec, report), report, null);
        }
    }

    private static PluginProcessLaunch StartIsolated(
        PluginProcess.StartSpec spec,
        PluginIsolationOptions options,
        string? runtimeRoot,
        IReadOnlyList<string>? readableRoots,
        IReadOnlyList<string>? writableRoots,
        string modeValue,
        ILogger logger)
    {
        var runtimeHint = runtimeRoot ?? "plugins/.runtimes";
        var executable = ResolveExecutable(spec.Executable, runtimeRoot);
        if (executable is null)
            throw new InvalidOperationException(
                $"找不到可执行文件 '{spec.Executable}'，无法创建受限进程"
                + $"（可把它放进 '{runtimeHint}\\{spec.Executable}\\'）");

        var plan = PluginIsolationPlanner.Build(
            spec.PluginDirectory,
            spec.DataDirectory,
            spec.Permissions,
            readableRoots,
            writableRoots,
            runtimeRoot,
            options.RedirectUserDirectories);

        var containerName = $"{options.ContainerNamePrefix}.{SanitizeContainerName(spec.PluginId)}";
        var containerSid = WindowsAppContainer.EnsureProfile(containerName, out var profileCreated);

        if (profileCreated)
            logger.LogInformation("Plugin {PluginId} 创建了 AppContainer 配置 {Container}（SID {Sid}）", spec.PluginId, containerName, containerSid);

        IReadOnlyList<string> applied;
        try
        {
            applied = WindowsAppContainer.ApplyGrants(containerSid, plan.Grants);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or Win32Exception)
        {
            throw new InvalidOperationException(
                $"给容器授权失败：{exception.Message}。"
                + "插件运行时若装在受保护目录（例如 Program Files 下的 nodejs/dotnet），请把它放到插件的 "
                + $"'{runtimeRoot ?? "plugins/.runtimes"}' 目录，或把该目录加入 Plugins:Isolation:ReadableRoots。",
                exception);
        }

        // 提前把"运行时读不到"变成一条能看懂的失败，而不是启动后一个语焉不详的超时。
        if (!WindowsAppContainer.IsReadableByContainer(executable, containerSid))
        {
            throw new InvalidOperationException(
                $"容器没有读取入口程序 '{executable}' 的权限。"
                + "Windows 上装在 Program Files 的运行时默认只授权给 Users 组，容器令牌用不到。"
                + $"解决办法：把运行时放进 '{runtimeRoot ?? "plugins/.runtimes"}'（该目录会自动授权），"
                + "或把它加入 Plugins:Isolation:ReadableRoots，或改用自带运行时的插件包。");
        }

        // 重定向出来的家目录必须先建好，否则运行时连临时文件都建不出来。
        foreach (var directory in plan.EnvironmentOverrides.Values
                     .Where(value => value.StartsWith(spec.DataDirectory ?? "\u0000", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(directory);
        }

        var environment = BuildEnvironment(spec.Environment, plan.EnvironmentOverrides);

        // Node 在解析入口脚本时会 realpath 整条路径（包括驱动器根 C:\）。
        // 容器的令牌读不到 C:\（那里只授了 Users 组，普通用户也无权改它的 ACL），
        // 于是 node <script> 会以 EPERM: lstat 'C:\' 失败。
        // --preserve-symlinks-main 让 node 跳过主模块的 realpath，这是不改 ACL 的唯一解。
        // 对非 node 进程这个变量无意义，不会被读。
        environment = WithNodeMainPreserveSymlinks(environment);

        var isolation = new PluginIsolationReport(
            modeValue,
            Applied: true,
            Kind: PluginIsolationKinds.WindowsAppContainer,
            Network: plan.Network,
            plan.CapabilitySids,
            // 报告的是"这次真正授予的范围"，不是"这次新写了哪几条 ACE"——
            // 已经授过的目录会被跳过，否则接口上会显示成什么都没给。
            plan.Grants.Where(grant => grant.Kind == PluginIsolationGrantKinds.Modify).Select(grant => grant.Path).ToList(),
            plan.Grants
                .Where(grant => grant.Kind == PluginIsolationGrantKinds.ReadExecute)
                .Select(grant => grant.Path)
                .Where(path => path != spec.PluginDirectory)
                .ToList());

        var process = AppContainerPluginProcess.Start(
            spec with { Executable = executable, Environment = environment },
            containerSid,
            plan.CapabilitySids,
            isolation);

        logger.LogInformation(
            "Plugin {PluginId} 在 AppContainer 中启动：pid={Pid} 网络={Network} 授权路径={Planned}（本次新写 {Written}）",
            spec.PluginId,
            process.Id,
            plan.Network ? "允许" : "禁止",
            plan.Grants.Count,
            applied.Count);

        return new PluginProcessLaunch(process, isolation, null);
    }

    private static PluginProcess StartManaged(PluginProcess.StartSpec spec, PluginIsolationReport report) =>
        ManagedPluginProcess.Start(spec, report);

    /// <summary>
    /// 构造子进程环境：白名单 + 调用方指定的变量 + 容器重定向。
    ///
    /// <para>
    /// 白名单不是洁癖：普通 <see cref="System.Diagnostics.Process"/> 会把父进程全部环境变量
    /// 复制给子进程，连接串、API Key 就这样悄悄漏给了一个第三方程序。隔离路径顺手把这件事收紧了。
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment(
        IReadOnlyDictionary<string, string> extra,
        IReadOnlyDictionary<string, string> overrides)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in EssentialEnvironment)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) env[name] = value;
        }

        // 核心给插件注入的标识必须带上。
        foreach (var pair in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
        {
            if (pair.Key is string key && key.StartsWith("MYFIRSTMAFAGENT_", StringComparison.OrdinalIgnoreCase)
                && pair.Value is string value)
            {
                env[key] = value;
            }
        }

        foreach (var pair in extra) env[pair.Key] = pair.Value ?? string.Empty;
        foreach (var pair in overrides) env[pair.Key] = pair.Value ?? string.Empty;

        return env;
    }

    /// <summary>
    /// 把 <c>node</c> 这类裸命令解析成绝对路径（CreateProcess 的 lpApplicationName 需要它）。
    ///
    /// <para>
    /// <b>先查 <c>plugins/.runtimes</c></b>，再查 PATH。顺序很关键：PATH 上的
    /// <c>C:\Program Files\nodejs\node.exe</c> 对容器是拒绝访问的（那些目录只授了 Users 组），
    /// 而 <c>.runtimes</c> 会被自动授权。用户只要把运行时丢进 <c>.runtimes\node\node.exe</c>，
    /// 清单里继续写 <c>"command": "node"</c> 就能用。
    /// </para>
    /// </summary>
    internal static string? ResolveExecutable(string command, string? runtimeRoot = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        if (Path.IsPathRooted(command)) return File.Exists(command) ? Path.GetFullPath(command) : null;

        // 带分隔符的相对路径按当前工作目录解析（调用方已把它拼成插件目录下的路径）。
        if (command.Contains('\\') || command.Contains('/'))
        {
            var candidate = Path.GetFullPath(command);
            return File.Exists(candidate) ? candidate : null;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(string.Empty);

        if (!string.IsNullOrWhiteSpace(runtimeRoot))
        {
            foreach (var extension in extensions)
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(runtimeRoot!, command, command + extension),
                             Path.Combine(runtimeRoot!, command + extension)
                         })
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), command + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>AppContainer 名称只接受字母数字与 <c>.</c>/<c>-</c>/<c>_</c>。</summary>
    private static string SanitizeContainerName(string pluginId)
    {
        var builder = new System.Text.StringBuilder(pluginId.Length);

        foreach (var character in pluginId)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-');
        }

        var name = builder.ToString().Trim('-', '.');
        if (name.Length == 0) name = "plugin";
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>给诊断用：当前平台是否支持真正的隔离（界面据此显示"未支持"而不是"已关闭"）。</summary>
    public static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    /// <summary>Node 主模块 realpath 规避：见 <see cref="StartIsolated"/> 里的说明。</summary>
    private static IReadOnlyDictionary<string, string> WithNodeMainPreserveSymlinks(
        IReadOnlyDictionary<string, string> environment)
    {
        const string flag = "--preserve-symlinks-main";

        var options = environment.TryGetValue("NODE_OPTIONS", out var existing) ? existing : string.Empty;
        if (options.Contains(flag, StringComparison.Ordinal)) return environment;

        var merged = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase)
        {
            ["NODE_OPTIONS"] = string.IsNullOrWhiteSpace(options) ? flag : flag + " " + options
        };

        return merged;
    }

    /// <summary>运行时缓存目录（<c>plugins/.runtimes</c>）：放第三方运行时的地方，容器会自动获得只读权限。</summary>
    public static string RuntimeRoot(string pluginsRoot) => Path.Combine(pluginsRoot, ".runtimes");
}
