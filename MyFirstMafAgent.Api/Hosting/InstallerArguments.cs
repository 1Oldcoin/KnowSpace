namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 安装器（NSIS 卸载/升级钩子）调用 API 可执行文件时使用的一次性参数。
/// 这类参数<b>不</b>启动 Web 主机，只做进程外的清理工作后立即退出。
/// <para>
/// 与 <see cref="DesktopHostArguments"/> 的区别：后者由桌面壳传入（启动服务），
/// 这里由安装器传入（停止服务）。两者都不改变控制台模式的默认行为。
/// </para>
/// </summary>
public static class InstallerArguments
{
    /// <summary>
    /// 停止便携数据库后退出。卸载/升级时 <c>postgres.exe</c> 仍在运行会锁住程序目录，
    /// 导致安装器删除文件失败或残留。安装器在删除文件之前调用。
    /// </summary>
    public const string StopPortablePostgres = "--stop-portable-postgres";

    /// <summary>是否请求停止便携数据库。忽略大小写，只做精确匹配（不支持 <c>=value</c> 形式）。</summary>
    public static bool IsStopPortablePostgresRequested(string[] args) =>
        args.Any(argument => string.Equals(argument, StopPortablePostgres, StringComparison.OrdinalIgnoreCase));
}
