using System.Diagnostics;

namespace MyFirstMafAgent.Api.Plugins.Isolation;

/// <summary>
/// 插件子进程的抽象。存在的唯一理由：一条启动路径走 <see cref="Process"/>（未隔离），
/// 另一条走 <c>CreateProcess</c> + AppContainer（隔离），而宿主的后续逻辑
/// （stdin/stdout 管道、stderr 转发、退出事件、杀进程树）不该因此分叉两份。
/// </summary>
internal abstract class PluginProcess : IDisposable
{
    /// <summary>子进程 PID。</summary>
    public abstract int Id { get; }

    /// <summary>本次启动的隔离实况（会回显到接口）。</summary>
    public abstract PluginIsolationReport Isolation { get; }

    /// <summary>子进程的 stdout（宿主读端）。</summary>
    public abstract Stream Output { get; }

    /// <summary>子进程的 stdin（宿主写端）。</summary>
    public abstract Stream Input { get; }

    /// <summary>子进程的 stderr（宿主读端）。诊断/日志用，不能和 <see cref="PumpErrorAsync"/> 同时读。</summary>
    public abstract Stream Error { get; }

    /// <summary>子进程退出。参数是退出码；宿主负责在关闭阶段退订。</summary>
    public event Action<int>? Exited;

    public abstract bool HasExited { get; }

    public abstract int ExitCode { get; }

    /// <summary>关闭 stdin。对 MCP 方言这是约定的退出信号。</summary>
    public abstract void CloseInput();

    /// <summary>逐行读取 stderr 并回调（返回 false 表示停止）。</summary>
    public abstract Task PumpErrorAsync(Func<string, bool> onLine, CancellationToken cancellationToken);

    /// <summary>等待退出，返回是否已退出。</summary>
    public abstract bool WaitForExit(int milliseconds);

    /// <summary>强杀整棵进程树（含子进程）。</summary>
    public abstract void KillTree();

    public abstract void Dispose();

    protected void RaiseExited(int exitCode) => Exited?.Invoke(exitCode);

    /// <summary>启动规格：宿主算好后交给具体实现。</summary>
    public sealed record StartSpec(
        string PluginId,
        string Executable,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string> Environment,
        string PluginDirectory,
        string? DataDirectory,
        IReadOnlyList<string> Permissions);
}

/// <summary>
/// 未隔离的启动：包装 <see cref="Process"/>，行为与 D-1 之前完全一致。
/// </summary>
internal sealed class ManagedPluginProcess : PluginProcess
{
    private readonly Process _process;
    private int _exitRaised;

    public ManagedPluginProcess(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        PluginIsolationReport isolation)
    {
        Isolation = isolation;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += (_, _) => RaiseExitOnce();
    }

    public override int Id => _process.Id;

    public override PluginIsolationReport Isolation { get; }

    public override Stream Output => _process.StandardOutput.BaseStream;

    public override Stream Input => _process.StandardInput.BaseStream;

    public override Stream Error => _process.StandardError.BaseStream;

    public override bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    public override int ExitCode
    {
        get { try { return _process.ExitCode; } catch (InvalidOperationException) { return -1; } }
    }

    public void Start()
    {
        if (!_process.Start()) throw new InvalidOperationException("process did not start");
    }

    /// <summary>按启动规格创建一个未隔离的进程（已启动）。</summary>
    public static ManagedPluginProcess Start(PluginProcess.StartSpec spec, PluginIsolationReport isolation)
    {
        var process = new ManagedPluginProcess(
            spec.Executable,
            spec.Arguments,
            spec.WorkingDirectory,
            spec.Environment,
            isolation);

        process.Start();
        return process;
    }

    public override void CloseInput()
    {
        try { _process.StandardInput.Close(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public override async Task PumpErrorAsync(Func<string, bool> onLine, CancellationToken cancellationToken)
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0 && !onLine(line)) break;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // 进程退出时管道关闭，属正常情况。
        }
    }

    public override bool WaitForExit(int milliseconds)
    {
        try { return _process.WaitForExit(milliseconds); }
        catch (InvalidOperationException) { return true; }
    }

    public override void KillTree()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
    }

    public override void Dispose() => _process.Dispose();

    private void RaiseExitOnce()
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0) return;
        RaiseExited(ExitCode);
    }
}
