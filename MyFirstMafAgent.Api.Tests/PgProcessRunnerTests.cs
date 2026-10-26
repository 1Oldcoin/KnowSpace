using System.Diagnostics;
using MyFirstMafAgent.Api.Infrastructure.PortablePostgres;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// <see cref="PgProcessRunner"/> 守的是实测踩到的坑：<c>pg_ctl start</c> 拉起的
/// <c>postgres.exe</c> 会继承父进程的标准句柄。只要把输出重定向成**管道**，父进程就永远等不到 EOF
/// ——命令不返回，而数据库其实已经起来了（日志里已经写着 ready to accept connections）。
///
/// <para>
/// 所以这里断言的是「跑完一定返回」这一族性质：输出落文件、退出码拿得到、超时收得住、
/// 超时不留孤儿进程、以及每次运行前输出文件被截断（否则上一次的 ready 字样会骗过就绪判断）。
/// 注意不要把 <c>cmd.exe</c> 当目标程序来测：那是「cmd 套 cmd」的引号问题，
/// 生产路径只跑 <c>pg_ctl.exe</c> / <c>initdb.exe</c> 这种真正的可执行文件。
/// </para>
/// </summary>
public sealed class PgProcessRunnerTests : IDisposable
{
    private readonly string _directory;

    public PgProcessRunnerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "maf-pgrun-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string OutputFile => Path.Combine(_directory, "out.log");

    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        var (executable, arguments, expected) = EchoCommand();

        var result = await PgProcessRunner.RunAsync(
            executable, arguments, OutputFile, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Contains(expected, result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(OutputFile), "输出应该落在文件里，而不是被管道接走。");
    }

    [Fact]
    public async Task Reports_a_nonzero_exit_code_without_throwing()
    {
        var (executable, arguments) = FailCommand();

        var result = await PgProcessRunner.RunAsync(
            executable, arguments, OutputFile, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Times_out_and_leaves_no_orphan_process()
    {
        var (executable, arguments) = SleepCommand();

        var result = await PgProcessRunner.RunAsync(
            executable, arguments, OutputFile, TimeSpan.FromMilliseconds(700), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);

        // 超时后必须杀掉子进程树，否则残留的 pg_ctl 会挡住下一次启动和卸载。
        await Task.Delay(500);
        var survivors = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable))
            .Count(process => process.Id != Environment.ProcessId);
        Assert.True(survivors == 0, $"超时后仍残留 {survivors} 个 {Path.GetFileName(executable)} 进程。");
    }

    [Fact]
    public async Task Truncates_the_output_file_before_each_run()
    {
        // 上一轮留下的 "ready to accept connections" 会让下一次的就绪判断直接通过，
        // 这是最隐蔽的一类假成功，所以每次运行前必须清空。
        File.WriteAllText(OutputFile, "STALE-LINE-FROM-PREVIOUS-RUN");

        var (executable, arguments, _) = EchoCommand();
        var result = await PgProcessRunner.RunAsync(
            executable, arguments, OutputFile, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.DoesNotContain("STALE-LINE-FROM-PREVIOUS-RUN", result.Output);
    }

    [Fact]
    public void ReadTail_returns_the_last_lines_and_tolerates_missing_files()
    {
        var path = Path.Combine(_directory, "tail.log");
        File.WriteAllLines(path, Enumerable.Range(1, 40).Select(index => $"line-{index}"));

        var tail = PgProcessRunner.ReadTail(path, lineCount: 3);

        Assert.Contains("line-40", tail);
        Assert.Contains("line-38", tail);
        Assert.DoesNotContain("line-37", tail);
        Assert.Equal(string.Empty, PgProcessRunner.ReadTail(Path.Combine(_directory, "missing.log")));
    }

    [Fact]
    public async Task Rejects_arguments_that_cannot_be_quoted_for_cmd()
    {
        if (!OperatingSystem.IsWindows()) return; // 只有 cmd 包装路径有这条限制。

        var (executable, _, _) = EchoCommand();

        await Assert.ThrowsAsync<ArgumentException>(() => PgProcessRunner.RunAsync(
            executable, ["say \"hi\""], OutputFile, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    private static string System32(string fileName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);

    private static (string Executable, string[] Arguments, string Expected) EchoCommand() =>
        OperatingSystem.IsWindows()
            // hostname.exe 是真正的可执行文件（不是 cmd 内建命令），输出即本机名。
            ? (System32("hostname.exe"), [], Environment.MachineName)
            : ("/bin/echo", ["hello-portable-pg"], "hello-portable-pg");

    private static (string Executable, string[] Arguments) FailCommand() =>
        OperatingSystem.IsWindows()
            ? (System32("where.exe"), ["definitely-not-a-real-command-xyz"])
            : ("/bin/sh", ["-c", "exit 3"]);

    private static (string Executable, string[] Arguments) SleepCommand() =>
        OperatingSystem.IsWindows()
            // ping 当 sleep 用：不依赖 timeout 命令是否存在。
            ? (System32("ping.exe"), ["-n", "20", "127.0.0.1"])
            : ("/bin/sh", ["-c", "sleep 20"]);
}
