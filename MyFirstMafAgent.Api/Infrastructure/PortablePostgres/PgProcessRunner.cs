using System.Diagnostics;
using System.Text;

namespace MyFirstMafAgent.Api.Infrastructure.PortablePostgres;

/// <summary>
/// 运行 PostgreSQL 自带的命令行程序（<c>pg_ctl</c> / <c>initdb</c>），把它们的 stdout/stderr
/// 重定向到**文件**而不是管道。
///
/// <para>
/// 为什么不能直接用 <c>ProcessStartInfo.RedirectStandardOutput = true</c>：
/// <c>pg_ctl start</c> 拉起的 <c>postgres.exe</c> 会**继承** pg_ctl 的标准句柄。如果那是管道，
/// postgres 只要还活着，管道写端就一直开着，父进程永远等不到 EOF ——
/// 实测（PG 使用说明 §4.1）：命令不返回、进程管理器里 postgres.exe 活得好好的、日志显示已启动成功。
/// Electron 拉起 API 时 API 的 stdio 本身就是管道，所以这里必须彻底避开。
/// </para>
///
/// <para>
/// 做法：Windows 走 <c>cmd.exe /d /s /c "... &gt; out 2&gt;&amp;1"</c>，Unix 走
/// <c>/bin/sh -c 'exec ... &gt; out 2&gt;&amp;1'</c>。重定向发生在创建子进程之前，
/// 于是 pg_ctl 与 postgres 拿到的是文件句柄，与本进程的 stdio 彻底无关。
/// </para>
///
/// <para>
/// 注意：pg_ctl 自己的输出文件**不能**和 <c>-l</c> 的服务器日志是同一个文件，
/// 否则 Windows 报 “The process cannot access the file because it is being used by another process”。
/// </para>
/// </summary>
internal static class PgProcessRunner
{
    internal sealed record Result(int ExitCode, string Output, bool TimedOut)
    {
        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

    /// <summary>
    /// 在 <paramref name="outputFile"/> 里收集子进程输出。文件先被清空，
    /// 读取时用共享方式打开（postgres 可能仍持有句柄）。
    /// </summary>
    public static async Task<Result> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string outputFile,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        TryTruncate(outputFile);

        // Unix 上补偿 +x：从 zip/tar 解出来、或跨平台复制的 postgres 二进制会丢可执行位，
        // 否则这里只会在 /bin/sh 里报一句 Permission denied，看不出是权限问题。Windows 上是空操作。
        PlatformRuntime.EnsureExecutable(executable);

        var startInfo = CreateShellStartInfo(executable, arguments, outputFile);
        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new Result(-1, ReadAll(outputFile), TimedOut: true);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new Result(process.ExitCode, ReadAll(outputFile), TimedOut: false);
    }

    private static ProcessStartInfo CreateShellStartInfo(
        string executable, IReadOnlyList<string> arguments, string outputFile)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // 刻意不重定向：重定向交给 shell 的 > 运算符，句柄落在文件上。
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false
        };

        if (OperatingSystem.IsWindows())
        {
            // cmd /s 会把整串外层引号剥掉后原样执行，是最可靠的“整条命令行转交”方式。
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/d /s /c \"" + BuildWindowsCommand(executable, arguments, outputFile) + "\"";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildPosixCommand(executable, arguments, outputFile));
        }

        return startInfo;
    }

    private static string BuildWindowsCommand(
        string executable, IReadOnlyList<string> arguments, string outputFile)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteForCmd(executable));
        foreach (var argument in arguments)
            builder.Append(' ').Append(QuoteForCmd(argument));

        // 与 -l 日志文件分开（Windows 上同一文件会被 postgres 独占）。
        builder.Append(" > ").Append(QuoteForCmd(outputFile)).Append(" 2>&1");
        return builder.ToString();
    }

    private static string BuildPosixCommand(
        string executable, IReadOnlyList<string> arguments, string outputFile)
    {
        var builder = new StringBuilder("exec ");
        builder.Append(SingleQuote(executable));
        foreach (var argument in arguments)
            builder.Append(' ').Append(SingleQuote(argument));

        builder.Append(" > ").Append(SingleQuote(outputFile)).Append(" 2>&1");
        return builder.ToString();
    }

    /// <summary>cmd.exe 里无法转义双引号，遇到就明确失败而不是悄悄拼出一条错命令。</summary>
    private static string QuoteForCmd(string value)
    {
        if (value.Contains('"'))
            throw new ArgumentException($"命令行参数不能包含双引号：{value}", nameof(value));

        return '"' + value + '"';
    }

    private static string SingleQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程已经自己退出，忽略。
        }
    }

    private static void TryTruncate(string path)
    {
        try
        {
            File.WriteAllText(path, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 打不开就当作追加写，反正读的时候是共享打开。
        }
    }

    /// <summary>共享读：postgres 可能仍然持有该文件的写句柄。</summary>
    private static string ReadAll(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>读日志文件的末尾若干行，用于把真正的原因带进异常消息。</summary>
    public static string ReadTail(string path, int lineCount = 15)
    {
        var text = ReadAll(path);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.Length > 0)
            .ToArray();

        return string.Join(Environment.NewLine, lines.TakeLast(lineCount));
    }
}
