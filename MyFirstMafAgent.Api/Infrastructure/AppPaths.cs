using System.Text;

namespace MyFirstMafAgent.Api.Infrastructure;

/// <summary>应用级路径约定（便携数据库、日志、内容根目录）。</summary>
public static class AppPaths
{
    /// <summary>
    /// Velopack 安装目录名（%LOCALAPPDATA%\MyFirstMafAgent）。
    /// 该目录由安装器管理，卸载时会被整体删除，因此不能存放用户数据。
    /// </summary>
    public const string ProductFolderName = "MyFirstMafAgent";

    /// <summary>应用程序目录（%LOCALAPPDATA%\MyFirstMafAgent），仅安装版使用。</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);

    /// <summary>
    /// 用户数据根目录（%LOCALAPPDATA%\MyFirstMafAgentData）。
    /// 与安装目录分开，卸载/升级不会丢失上传文件、日志、模型缓存与数据库。
    /// </summary>
    public static string UserDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName + "Data");

    /// <summary>可写数据目录（%LOCALAPPDATA%\MyFirstMafAgentData\data）。</summary>
    public static string DataDirectory => Path.Combine(UserDataRoot, "data");

    /// <summary>日志目录（%LOCALAPPDATA%\MyFirstMafAgentData\logs）。</summary>
    public static string LogsDirectory => Path.Combine(UserDataRoot, "logs");

    /// <summary>
    /// 解析应用内容根目录：优先显式环境变量，其次当前目录（dotnet run / 从发布目录启动），
    /// 最后回退到程序所在目录（从任意工作目录启动 exe）。
    /// </summary>
    public static string ResolveContentRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "appsettings.json")))
            return current;

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 判断程序是否运行在 Windows 受保护的用户目录（桌面 / 文档 / 下载）下。
    /// 这些位置受「受控文件夹访问」等策略保护，从其中启动的未签名程序写用户目录会被拒绝，
    /// 便携 PostgreSQL 因此无法初始化。返回命中的目录名，未命中返回 null。
    /// </summary>
    public static string? GetRestrictedLocation(string? directory = null)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var target = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        var folders = new (string Name, Environment.SpecialFolder Folder)[]
        {
            ("桌面", Environment.SpecialFolder.DesktopDirectory),
            ("文档", Environment.SpecialFolder.MyDocuments),
            ("下载", Environment.SpecialFolder.UserProfile)
        };

        foreach (var (name, folder) in folders)
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root)) continue;

            var rootPath = Path.GetFullPath(root);
            if (name == "下载")
            {
                var downloads = Path.Combine(rootPath, "Downloads");
                if (IsUnder(target, downloads)) return name;
                continue;
            }

            if (IsUnder(target, rootPath)) return name;
        }

        return null;
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把受限位置与缓存目录的失败原因组合成可操作的建议文本。</summary>
    public static string BuildLocationAdvice()
    {
        var restricted = GetRestrictedLocation();
        return restricted is null
            ? "请把整个应用目录移动到纯英文路径（例如 D:\\Apps\\MyFirstMafAgent）后重试"
            : $"应用当前位于 Windows 受保护目录（{restricted}）。受控文件夹访问会阻止程序写入用户目录，"
              + "请把整个应用文件夹移动到其他位置（例如 D:\\Apps\\MyFirstMafAgent 或 C:\\Apps\\MyFirstMafAgent）后重新运行";
    }
}
