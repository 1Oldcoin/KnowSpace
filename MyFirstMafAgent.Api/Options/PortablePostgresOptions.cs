namespace MyFirstMafAgent.Api.Options;

/// <summary>
/// 便携式 PostgreSQL 配置。应用启动时用随包的 PostgreSQL 目录树（<c>initdb</c> / <c>pg_ctl</c>）
/// 拉起数据库，等它就绪后再启动 Web 服务，退出时优雅停止；随后自动执行
/// <c>CREATE EXTENSION IF NOT EXISTS vector</c> 激活 pgvector。
///
/// <para>
/// 数据库的生命周期**只在 .NET API 里**：桌面壳（Electron）只负责启动 API、等健康检查通过、
/// 加载页面，不认识数据库。
/// </para>
/// </summary>
public sealed class PortablePostgresOptions
{
    public const string SectionName = "PortablePostgres";

    /// <summary>是否启用便携数据库。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 当 appsettings / 用户机密 / 环境变量中已经配置了连接串时，是否优先使用外部数据库。
    /// 开发机通常保留已有 PostgreSQL，因此默认 true；发布包没有连接串时会自动走便携库。
    /// </summary>
    public bool RespectExternalConnectionString { get; set; } = true;

    /// <summary>
    /// 启动时对外部连接串做一次可达性预检的超时（秒）；0 表示跳过预检。
    /// <para>
    /// 预检只是为了让“库没启动 / 口令错”在**启动时就用人话报出来**，
    /// 而不是 5 秒后抛一条 SqlSugar 堆栈。库正常时它只多一次 TCP 连接。
    /// </para>
    /// </summary>
    public double PreflightTimeoutSeconds { get; set; } = 5d;

    /// <summary>
    /// 便携 PostgreSQL 的 <c>bin</c> 目录，相对路径基于应用内容根目录。
    ///
    /// <para>
    /// 留空（推荐）= 按当前平台自动定位：
    /// <c>ExternalTools/pgSQL/{rid}/bin</c> → <c>ExternalTools/pgSQL/bin</c>。
    /// 允许填 pgsql 根目录（自动补 <c>bin</c>）；填了但目录里没有 <c>postgres[.exe]</c>
    /// 时回落自动定位并记日志，不会因此启动失败。
    /// </para>
    ///
    /// <para>
    /// 注意必须是**整棵树**（<c>bin</c> / <c>lib</c> / <c>share</c>），PostgreSQL 靠可执行文件
    /// 自身的相对位置推导 <c>lib</c> 与 <c>share</c>，只拷 <c>bin</c> 会起不来。
    /// </para>
    /// </summary>
    public string BinDirectory { get; set; } = "";

    /// <summary>监听端口。端口被占用时会自动向后寻找可用端口。</summary>
    public int Port { get; set; } = 55432;

    /// <summary>
    /// 便携数据库根目录（数据目录与日志目录都放在它下面）。
    ///
    /// <para>
    /// 留空（推荐）= 自动挑选：<c>%LOCALAPPDATA%\MyFirstMafAgentData</c> → <c>%TEMP%\MyFirstMafAgent</c>
    /// → <c>%ProgramData%\MyFirstMafAgent</c> → <c>%USERPROFILE%\.myfirstmafagent</c>
    /// → <c>{程序目录}\App_Data\pgdata</c>（便携兜底，最后才用）。
    /// </para>
    ///
    /// <para>
    /// 显式配置时**不再回落**，推荐纯 ASCII 可写目录（PostgreSQL 官方建议数据目录无中文、无空格）。
    /// 适合「用户目录被安全软件整片拦截」的机器：配成 <c>D:\Apps\MyFirstMafAgentData</c>
    /// 即可绕开所有用户配置目录。
    /// </para>
    /// </summary>
    public string RootDirectory { get; set; } = "";

    /// <summary>
    /// PostgreSQL 数据目录（集群目录）。留空时使用 <c>{便携根目录}\pgdata</c>；
    /// 首次启动会在该目录里执行 <c>initdb</c>，数据随安装/升级保留。
    /// </summary>
    public string DataDirectory { get; set; } = "";

    public string Username { get; set; } = "postgres";

    public string Password { get; set; } = "postgres";

    /// <summary>
    /// 客户端连接池上限。<b>默认 16，不要上调。</b>
    ///
    /// <para>
    /// 这里曾经是 50，而 PostgreSQL 的 <c>max_connections</c> 默认也是 100——两个数字互不相干，
    /// 却让「连接池上限」看起来像个安全值。实际峰值并发只有个位数
    /// （<c>Jobs:MaxConcurrentJobs</c> 默认 4 + 3 个后台 Worker + HTTP 请求），
    /// 池子开到 50 却<b>永远收不回去</b>：每个新的 ExecutionContext 都会让 SqlSugar 在
    /// <c>AsyncLocal</c> 里新建一个 <c>SqlSugarClient</c>，连带一条 Npgsql 物理连接；
    /// <c>IsAutoCloseConnection</c> 只把连接还进池，不关它。于是「只增不减」，
    /// 挂机几小时后池子被灌满，50 个空闲 backend 常驻，
    /// 在任务管理器里就是几十个 postgres.exe 进程 + 数百 MB 工作集。
    /// </para>
    ///
    /// <para>
    /// 真正的收敛靠下面两个空闲回收参数，而不是靠把上限调大。
    /// 上限的作用只是<b>封顶</b>：万一再出现泄漏，16 条也不会把
    /// <c>max_connections</c> 吃掉一半。改成 16 后即使池子被灌满，
    /// 空闲连接也会在 5 分钟内被回收。
    /// </para>
    /// </summary>
    public int MaxPoolSize { get; set; } = 16;

    /// <summary>
    /// 连接在池中空闲多久后被关闭（秒）。默认 300 秒。
    ///
    /// <para>
    /// 这是治「只增不减」的关键参数。0 表示永不回收——那就会回到旧行为：
    /// 进程活多久，池子就涨到多大（上限为 <see cref="MaxPoolSize"/>）。
    /// </para>
    /// </summary>
    public int ConnectionIdleLifetimeSeconds { get; set; } = 300;

    /// <summary>空闲连接回收线程的扫描间隔（秒）。默认 60 秒。</summary>
    public int ConnectionPruningIntervalSeconds { get; set; } = 60;

    public string Database { get; set; } = "myfirstmafagent";

    /// <summary>
    /// 启动超时（秒）：首次运行要在空目录里 <c>initdb</c>（约 3~10 秒），
    /// 再加上 <c>pg_ctl start -w</c> 等待就绪，所以留足余量。
    /// </summary>
    public int StartupTimeoutSeconds { get; set; } = 120;

    /// <summary>应用退出时是否停止由本进程启动的数据库。</summary>
    public bool StopOnExit { get; set; } = true;
}
