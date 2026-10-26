using System.Text.Json;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 桌面宿主与 API 进程之间的启动契约（命令行参数 + 环境变量）。
/// 只有 API 可执行文件被宿主当作子进程拉起时才会用到这些参数；
/// 控制台模式下全部为空，行为不变。
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

        try
        {
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 写就绪文件失败时，宿主只会看到"等待就绪超时"，那条信息对排查毫无帮助。
            // 这里把真实原因（哪个路径、什么权限）直接打到启动日志，再抛出明确异常。
            StartupTimeline.Mark($"无法写入就绪文件 {ReadyFilePath}：{exception.GetType().Name}: {exception.Message}");
            throw new InvalidOperationException(
                $"无法写入就绪文件（{ReadyFilePath}）：{exception.Message}。"
                + "宿主与数据目录可能在同一个盘上但权限不一致，请检查该目录是否可写。",
                exception);
        }
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
