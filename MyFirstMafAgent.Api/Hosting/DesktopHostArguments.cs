using System.Text.Json;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 桌面宿主与 API 进程之间的启动契约（命令行参数 + 环境变量）。
/// 只有 API 可执行文件被宿主当作子进程拉起时才会用到这些参数；
/// 控制台与 Photino 模式下全部为空，行为不变。
/// </summary>
public sealed record DesktopHostArguments(string? ReadyFilePath, string? DataRoot)
{
    public const string ReadyFileOption = "--desktop-ready-file";
    public const string DataRootOption = "--desktop-data-root";

    public static DesktopHostArguments Parse(string[] args) =>
        new(ReadOption(args, ReadyFileOption), ReadOption(args, DataRootOption));

    /// <summary>
    /// 写就绪文件，内容只包含监听地址与进程 ID。<b>不</b>写入令牌：
    /// 令牌由宿主生成并自己持有，避免落盘。
    /// </summary>
    public async Task WriteReadyFileAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ReadyFilePath)) return;

        var directory = Path.GetDirectoryName(ReadyFilePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var payload = JsonSerializer.Serialize(new
        {
            url,
            pid = Environment.ProcessId,
            startedAt = DateTimeOffset.Now
        });

        // 先写临时文件再替换，避免宿主读到写了一半的内容。
        var temp = ReadyFilePath + ".tmp";
        await File.WriteAllTextAsync(temp, payload, cancellationToken);
        File.Move(temp, ReadyFilePath, overwrite: true);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;

            if (args[index].Length > name.Length && args[index][name.Length] == '=')
                return NullIfBlank(args[index][(name.Length + 1)..]);

            if (index + 1 < args.Length) return NullIfBlank(args[index + 1]);
        }

        return null;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
