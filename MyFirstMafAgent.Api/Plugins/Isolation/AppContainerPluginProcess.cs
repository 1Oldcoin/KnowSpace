using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MyFirstMafAgent.Api.Plugins.Isolation;

/// <summary>
/// 用 <c>CreateProcess</c> + <c>SECURITY_CAPABILITIES</c> 在 AppContainer 里启动插件。
///
/// <para>
/// 为什么不能用 <see cref="System.Diagnostics.Process"/>：它没有暴露
/// <c>STARTUPINFOEX</c> / <c>PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES</c>，
/// 也就是无法把容器令牌交给子进程。这里直接调 Win32，并自己建三条管道。
/// </para>
///
/// <para>
/// 进程先以 <c>CREATE_SUSPENDED</c> 创建，挂到 Job 对象（<c>KILL_ON_JOB_CLOSE</c>）后再恢复执行：
/// 否则插件可能在 <c>AssignProcessToJobObject</c> 之前就拉起孙子进程，那些进程会逃出 Job，
/// 插件崩掉以后还留在系统里。
/// </para>
/// </summary>
internal sealed class AppContainerPluginProcess : PluginProcess
{
    private readonly IntPtr _processHandle;
    private readonly IntPtr _threadHandle;
    private readonly IntPtr _jobHandle;
    private readonly int _processId;

    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly FileStream _error;

    private int _exitRaised;

    private AppContainerPluginProcess(
        IntPtr processHandle,
        IntPtr threadHandle,
        IntPtr jobHandle,
        int processId,
        FileStream input,
        FileStream output,
        FileStream error,
        PluginIsolationReport isolation)
    {
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        _jobHandle = jobHandle;
        _processId = processId;
        _input = input;
        _output = output;
        _error = error;
        Isolation = isolation;

        StartExitWatcher();
    }

    public override int Id => _processId;

    public override PluginIsolationReport Isolation { get; }

    public override Stream Output => _output;

    public override Stream Input => _input;

    public override Stream Error => _error;

    public override bool HasExited => Native.WaitForSingleObject(_processHandle, 0) == Native.WaitObject0;

    public override int ExitCode =>
        Native.GetExitCodeProcess(_processHandle, out var code) ? unchecked((int)code) : -1;

    /// <summary>
    /// 启动。任何一步失败都抛异常——由 <see cref="PluginProcessFactory"/> 决定
    /// 「拒绝启动」还是「降级」，这里不做策略。
    /// </summary>
    public static AppContainerPluginProcess Start(
        PluginProcess.StartSpec spec,
        string containerSid,
        IReadOnlyList<string> capabilitySids,
        PluginIsolationReport isolation)
    {
        // 先建三条管道：子进程那端可继承，宿主这端不可继承。
        var (stdinChild, stdinParent) = CreatePipeEnds(childGetsReadEnd: true);
        var (stdoutChild, stdoutParent) = CreatePipeEnds(childGetsReadEnd: false);
        var (stderrChild, stderrParent) = CreatePipeEnds(childGetsReadEnd: false);

        var processHandle = IntPtr.Zero;
        var threadHandle = IntPtr.Zero;
        var jobHandle = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var capabilitiesPointer = IntPtr.Zero;
        var sidPointer = IntPtr.Zero;
        var environmentPointer = IntPtr.Zero;
        var securityCapabilitiesPointer = IntPtr.Zero;

        try
        {
            sidPointer = CreateSid(containerSid);

            var capabilityCount = 0u;
            if (capabilitySids.Count > 0) capabilitiesPointer = BuildSidAndAttributes(capabilitySids, out capabilityCount);

            var securityCapabilities = new SecurityCapabilities
            {
                AppContainerSid = sidPointer,
                Capabilities = capabilitiesPointer,
                CapabilityCount = capabilityCount
            };

            securityCapabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(securityCapabilities, securityCapabilitiesPointer, false);

            attributeList = CreateAttributeList(securityCapabilitiesPointer);

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = Native.StartfUseStdHandles,
                    hStdInput = stdinChild,
                    hStdOutput = stdoutChild,
                    hStdError = stderrChild
                },
                lpAttributeList = attributeList
            };

            environmentPointer = Marshal.StringToHGlobalUni(EnvironmentBlock.Build(spec.Environment));
            jobHandle = CreateKillOnCloseJob();

            var created = Native.CreateProcess(
                spec.Executable,
                BuildCommandLine(spec.Executable, spec.Arguments),
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: true,
                Native.ExtendedStartupInfoPresent | Native.CreateSuspended | Native.CreateUnicodeEnvironment | Native.CreateNoWindow,
                environmentPointer,
                spec.WorkingDirectory,
                ref startupInfo,
                out var processInformation);

            if (!created)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess 失败：{spec.Executable}");

            processHandle = processInformation.hProcess;
            threadHandle = processInformation.hThread;

            // 先挂 Job 再恢复线程：顺序反了会有"孙子进程逃出 Job"的窗口。
            Native.AssignProcessToJobObject(jobHandle, processHandle);
            Native.ResumeThread(threadHandle);

            var input = new FileStream(new SafeFileHandle(stdinParent, ownsHandle: true), FileAccess.Write, 4096);
            var output = new FileStream(new SafeFileHandle(stdoutParent, ownsHandle: true), FileAccess.Read, 4096);
            var error = new FileStream(new SafeFileHandle(stderrParent, ownsHandle: true), FileAccess.Read, 4096);

            return new AppContainerPluginProcess(
                processHandle,
                threadHandle,
                jobHandle,
                processInformation.dwProcessId,
                input,
                output,
                error,
                isolation);
        }
        catch
        {
            CloseIfNotEmpty(processHandle);
            CloseIfNotEmpty(threadHandle);
            CloseIfNotEmpty(jobHandle);
            CloseIfNotEmpty(stdinParent);
            CloseIfNotEmpty(stdoutParent);
            CloseIfNotEmpty(stderrParent);
            throw;
        }
        finally
        {
            // 子进程那三端在父进程里必须立刻关掉，否则读循环等不到 EOF。
            CloseIfNotEmpty(stdinChild);
            CloseIfNotEmpty(stdoutChild);
            CloseIfNotEmpty(stderrChild);

            if (environmentPointer != IntPtr.Zero) Marshal.FreeHGlobal(environmentPointer);

            // 进程已启动时，能力的 SID 必须活到 CreateProcess 之后才能释放（这里已经过了）；
            // 但 SECURITY_CAPABILITIES 结构体由内核在 CreateProcess 时复制，可以随即释放。
            if (capabilitiesPointer != IntPtr.Zero) Marshal.FreeHGlobal(capabilitiesPointer);
            if (sidPointer != IntPtr.Zero) Native.LocalFree(sidPointer);
            if (securityCapabilitiesPointer != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilitiesPointer);
            if (attributeList != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    public override void CloseInput()
    {
        try { _input.Dispose(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    public override Task PumpErrorAsync(Func<string, bool> onLine, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(_error, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 0 && !onLine(line)) break;
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // 进程退出时管道关闭，属正常情况。
            }
        }, CancellationToken.None);

    public override bool WaitForExit(int milliseconds)
    {
        var timeout = milliseconds < 0 ? Native.Infinite : (uint)milliseconds;
        return Native.WaitForSingleObject(_processHandle, timeout) == Native.WaitObject0;
    }

    public override void KillTree()
    {
        if (_jobHandle != IntPtr.Zero) Native.TerminateJobObject(_jobHandle, 1);
        else Native.TerminateProcess(_processHandle, 1);
    }

    public override void Dispose()
    {
        try { _input.Dispose(); } catch (Exception) { }
        try { _output.Dispose(); } catch (Exception) { }
        try { _error.Dispose(); } catch (Exception) { }

        // 关掉 Job 会连带杀掉插件拉起的孙子进程（KILL_ON_JOB_CLOSE）。
        CloseIfNotEmpty(_jobHandle);
        CloseIfNotEmpty(_threadHandle);
        CloseIfNotEmpty(_processHandle);
    }

    private void StartExitWatcher()
    {
        var thread = new Thread(() =>
        {
            try
            {
                Native.WaitForSingleObject(_processHandle, Native.Infinite);
                if (Interlocked.Exchange(ref _exitRaised, 1) == 0) RaiseExited(ExitCode);
            }
            catch (Exception)
            {
                // 关闭阶段句柄已释放，忽略。
            }
        })
        {
            IsBackground = true,
            Name = $"plugin-exit-{_processId}"
        };

        thread.Start();
    }

    // ───────────────────────────── 构造辅助 ─────────────────────────────

    private static (IntPtr Child, IntPtr Parent) CreatePipeEnds(bool childGetsReadEnd)
    {
        var attributes = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true
        };

        if (!Native.CreatePipe(out var read, out var write, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe 失败");

        var child = childGetsReadEnd ? read : write;
        var parent = childGetsReadEnd ? write : read;

        // 宿主这一端不能被继承，否则孙进程会拿到管道，读循环永远等不到 EOF。
        Native.SetHandleInformation(parent, Native.HandleFlagInherit, 0);

        return (child, parent);
    }

    private static IntPtr CreateAttributeList(IntPtr securityCapabilitiesPointer)
    {
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法查询进程属性列表大小");

        var list = Marshal.AllocHGlobal(size);
        if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(error, "InitializeProcThreadAttributeList 失败");
        }

        if (!Native.UpdateProcThreadAttribute(
                list,
                0,
                (IntPtr)Native.ProcThreadAttributeSecurityCapabilities,
                securityCapabilitiesPointer,
                (IntPtr)Marshal.SizeOf<SecurityCapabilities>(),
                IntPtr.Zero,
                IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            Native.DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(error, "无法把容器令牌写入进程属性");
        }

        return list;
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject 失败");

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = Native.JobObjectLimitKillOnJobClose
            }
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, pointer, (uint)size))
            {
                var error = Marshal.GetLastWin32Error();
                Native.CloseHandle(job);
                throw new Win32Exception(error, "SetInformationJobObject 失败");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return job;
    }

    private static IntPtr CreateSid(string sid)
    {
        if (!Native.ConvertStringSidToSid(sid, out var pointer))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法把 SID '{sid}' 转成二进制形式");

        return pointer;
    }

    /// <summary>把能力 SID 列表打包成 <c>SID_AND_ATTRIBUTES[]</c>（全部 SE_GROUP_ENABLED）。</summary>
    private static IntPtr BuildSidAndAttributes(IReadOnlyList<string> capabilitySids, out uint count)
    {
        var itemSize = Marshal.SizeOf<SidAndAttributes>();
        var pointer = Marshal.AllocHGlobal(itemSize * capabilitySids.Count);
        count = 0;

        for (var index = 0; index < capabilitySids.Count; index++)
        {
            var sid = CreateSid(capabilitySids[index]);
            Marshal.StructureToPtr(
                new SidAndAttributes { Sid = sid, Attributes = Native.SeGroupEnabled },
                pointer + (index * itemSize),
                false);

            count++;
        }

        return pointer;
    }

    /// <summary>
    /// Windows 命令行拼接。<c>CreateProcess</c> 只接受一整条字符串，
    /// 因此含空格/引号的参数必须按 <c>CommandLineToArgvW</c> 的规则转义，否则插件收到的参数会错位。
    /// </summary>
    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(Quote(executable));

        foreach (var argument in arguments) builder.Append(' ').Append(Quote(argument));

        return builder.ToString();

        static string Quote(string value)
        {
            if (value.Length > 0 && !value.Any(character => character is ' ' or '\t' or '"')) return value;

            var quoted = new System.Text.StringBuilder("\"");
            var backslashes = 0;

            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    // 引号前的反斜杠要翻倍，再补一个转义引号。
                    quoted.Append('\\', (backslashes * 2) + 1).Append('"');
                    backslashes = 0;
                    continue;
                }

                quoted.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }

            quoted.Append('\\', backslashes * 2).Append('"');
            return quoted.ToString();
        }
    }

    private static void CloseIfNotEmpty(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) Native.CloseHandle(handle);
    }

    // ───────────────────────────── 结构体与 P/Invoke ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static class Native
    {
        public const uint Infinite = 0xFFFFFFFF;
        public const uint WaitObject0 = 0;
        public const uint HandleFlagInherit = 0x00000001;
        public const int StartfUseStdHandles = 0x00000100;
        public const uint CreateSuspended = 0x00000004;
        public const uint CreateUnicodeEnvironment = 0x00000400;
        public const uint CreateNoWindow = 0x08000000;
        public const uint ExtendedStartupInfoPresent = 0x00080000;
        public const uint ProcThreadAttributeSecurityCapabilities = 0x00020009;
        public const uint SeGroupEnabled = 0x00000004;
        public const uint JobObjectLimitKillOnJobClose = 0x00002000;
        public const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SecurityAttributes lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref StartupInfoEx lpStartupInfo,
            out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr hMem);
    }
}

/// <summary>
/// 构造 <c>CreateProcess</c> 需要的环境块：<c>KEY=VALUE\0...\0\0</c>（Unicode，按键名不区分大小写排序）。
/// </summary>
internal static class EnvironmentBlock
{
    public static string Build(IReadOnlyDictionary<string, string> variables)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var pair in variables.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(pair.Key)) continue;
            builder.Append(pair.Key).Append('=').Append(pair.Value ?? string.Empty).Append('\0');
        }

        // 块尾必须是两个 \0（最后一个变量自己的结束符 + 整块结束符）。
        builder.Append('\0');
        return builder.ToString();
    }
}
