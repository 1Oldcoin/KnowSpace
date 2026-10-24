namespace MyFirstMafAgent.Api.Options;

/// <summary>
/// 便携式 PostgreSQL（pg0）配置。应用启动时自动拉起数据库子进程，退出时停止，
/// 并自动执行 <c>CREATE EXTENSION IF NOT EXISTS vector</c> 激活 pgvector。
/// </summary>
public sealed class PortablePostgresOptions
{
    public const string SectionName = "PortablePostgres";

    /// <summary>是否启用便携数据库。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 当 appsettings / 用户机密 / 环境变量中已经配置了连接串时，是否优先使用外部数据库。
    /// 开发机通常保留已有 PostgreSQL，因此默认 true；发布包没有连接串时会自动走 pg0。
    /// </summary>
    public bool RespectExternalConnectionString { get; set; } = true;

    /// <summary>
    /// pg0 可执行文件路径，相对路径基于应用内容根目录。
    ///
    /// <para>
    /// 留空（推荐）= 按当前平台自动定位：
    /// <c>ExternalTools/pgSQL/{rid}/pg0[.exe]</c> → <c>ExternalTools/pgSQL/pg0-{rid}[.exe]</c>
    /// → <c>ExternalTools/pgSQL/pg0[.exe]</c>。
    /// 这样同一份 appsettings.json 能在 Windows / Linux / macOS 上通用，
    /// 也避免「把 Windows 包里的配置带到 Linux 后启动失败」。
    /// </para>
    ///
    /// <para>
    /// 配了具体路径但文件不存在时，仍会回落到自动定位；两者都找不到才报错。
    /// 二进制可从 <see cref="MyFirstMafAgent.Api.Infrastructure.PlatformRuntime.Pg0ReleasesUrl"/> 下载。
    /// </para>
    /// </summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>pg0 实例名，同时用于默认数据目录。</summary>
    public string InstanceName { get; set; } = "myfirstmafagent";

    /// <summary>监听端口。端口被占用时会自动向后寻找可用端口。</summary>
    public int Port { get; set; } = 55432;

    /// <summary>数据目录。留空时使用 %LOCALAPPDATA%\MyFirstMafAgent\pgsql\{InstanceName}。</summary>
    public string DataDirectory { get; set; } = "";

    public string Username { get; set; } = "postgres";

    public string Password { get; set; } = "postgres";

    public string Database { get; set; } = "myfirstmafagent";

    /// <summary>首次启动需要解压 PostgreSQL 并安装 pgvector，超时时间适当放宽。</summary>
    public int StartupTimeoutSeconds { get; set; } = 180;

    /// <summary>应用退出时是否停止由本进程启动的数据库。</summary>
    public bool StopOnExit { get; set; } = true;

    /// <summary>
    /// 把 pg0 复制到 %LOCALAPPDATA% 的纯英文路径后再运行。
    /// pg0 在包含中文等非 ASCII 字符的安装路径下可能无法解压（os error 5），因此默认开启。
    /// </summary>
    public bool CacheExecutableInLocalAppData { get; set; } = true;
}
