using System.Text.Json;
using MyFirstMafAgent.Api.Infrastructure;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>运行时发现信息：后台服务监听在哪、是哪个进程。</summary>
/// <remarks>
/// <see cref="ProcessId"/> 不只是给人看的：退出时只有信息里的 PID 等于自己才删文件，
/// 否则「后启动的实例覆盖了文件、先启动的实例退出时把它删了」会让窗口再也找不到后台。
/// </remarks>
public sealed record DesktopRuntimeInfo(string Url, int ProcessId, DateTimeOffset StartedAt, string Version);

/// <summary>
/// 运行时发现文件（<c>{数据根}/runtime/desktop.json</c>）。
///
/// <para>
/// 解决的是启动顺序问题：后台服务可能早就以命令行/服务方式跑着了，这时候再开窗口
/// 不该拉起第二个后端（两个实例会抢同一个便携 PostgreSQL 和同一份数据目录）。
/// 窗口先读这个文件，再探活一次——通了就直接接管这个已经运行的后台。
/// </para>
///
/// <para>
/// 令牌也要跨进程共享：窗口关掉后后端继续跑，下次开窗必须带上同一个令牌才能通过
/// <see cref="LocalApiTokenValidator"/>。所以桌面模式（宿主生成了令牌）下把令牌写到
/// <c>{数据根}/runtime/token</c>，非 Windows 上置 0600。
/// 这个取舍是明确的：文件落在用户自己的数据目录里，权限随用户配置文件，
/// 换来的是「关窗不断服务、再开窗继续用」这个核心体验。
/// 命令行/服务方式启动（没有宿主令牌）时不会生成该文件，也就没有令牌可漏。
/// </para>
/// </summary>
public static class DesktopRuntimeFile
{
    public const string InfoFileName = "desktop.json";
    public const string TokenFileName = "token";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string DirectoryPath(string dataRoot) => Path.Combine(dataRoot, "runtime");

    public static string InfoPathFor(string dataRoot) => Path.Combine(DirectoryPath(dataRoot), InfoFileName);

    public static string TokenPathFor(string dataRoot) => Path.Combine(DirectoryPath(dataRoot), TokenFileName);

    /// <summary>
    /// 选一个真正写得进去的数据根：宿主指定的优先，其次用户数据目录，最后退回程序目录。
    /// 全都写不了就返回 null——发现文件只是锦上添花，不能因此让服务起不来。
    /// </summary>
    public static string? ResolveRoot(string? configuredRoot, string contentRoot)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredRoot)) candidates.Add(configuredRoot);
        candidates.Add(AppPaths.UserDataRoot);
        candidates.Add(Path.Combine(Path.GetFullPath(contentRoot), "App_Data"));

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath(candidate));
                return candidate;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 换下一个候选。
            }
        }

        return null;
    }

    /// <summary>发布发现信息（含共享令牌）。失败只记日志，不影响服务本身。</summary>
    public static void Publish(string dataRoot, string url, string? token, Action<string>? log = null)
    {
        try
        {
            var directory = DirectoryPath(dataRoot);
            Directory.CreateDirectory(directory);

            if (!string.IsNullOrWhiteSpace(token)) WriteToken(TokenPathFor(dataRoot), token!);

            var info = new DesktopRuntimeInfo(
                url,
                Environment.ProcessId,
                DateTimeOffset.Now,
                typeof(DesktopRuntimeFile).Assembly.GetName().Version?.ToString() ?? "unknown");

            // 先写临时文件再替换：窗口随时可能读到，不能让它读到半个 JSON。
            var path = InfoPathFor(dataRoot);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(info, JsonOptions));
            File.Move(temp, path, overwrite: true);

            log?.Invoke($"[runtime] 已发布发现文件 {path}（{url}，pid {info.ProcessId}）");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[runtime] 写发现文件失败（不影响服务）：{exception.Message}");
        }
    }

    public static DesktopRuntimeInfo? TryRead(string dataRoot)
    {
        try
        {
            var path = InfoPathFor(dataRoot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<DesktopRuntimeInfo>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string? TryReadToken(string dataRoot)
    {
        try
        {
            var path = TokenPathFor(dataRoot);
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>只清掉自己写的那份：别的实例还活着时守着它的发现文件。</summary>
    public static void RemoveIfOwned(string dataRoot, Action<string>? log = null)
    {
        try
        {
            var info = TryRead(dataRoot);
            if (info is null) return;
            if (info.ProcessId != Environment.ProcessId) return;

            File.Delete(InfoPathFor(dataRoot));
            File.Delete(TokenPathFor(dataRoot));
            log?.Invoke("[runtime] 已清理发现文件");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 退出路径上不再打扰用户。
        }
    }

    private static void WriteToken(string path, string token)
    {
        File.WriteAllText(path, token);

        if (OperatingSystem.IsWindows()) return;

        // Windows 上 %LOCALAPPDATA% 的继承 ACL 已经是「当前用户 + 系统」；
        // Unix 上要自己收紧，否则同机其它用户能读到令牌。
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 文件权限不是致命问题，忽略。
        }
    }
}
