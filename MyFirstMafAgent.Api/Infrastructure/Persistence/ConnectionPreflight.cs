using System.Net.Sockets;
using System.Text.RegularExpressions;
using Npgsql;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 启动期数据库**可达性预检**：在 SqlSugar 初始化架构之前，先用一条真实连接确认外部库能用。
///
/// <para>
/// 为什么需要它：配置了外部连接串时便携 PostgreSQL 会被**故意跳过**，于是"库没启动"表现为
/// 5 秒后的一条 <c>SqlSugarException: 连接数据库过程中发生错误 … Failed to connect to 127.0.0.1:5432
/// DbType="PostgreSQL";ConfigId=""</c>。这条消息里既没有库名、也没有"便携库被跳过"这个关键因果，
/// 用户只能看到一个数据库驱动内部堆栈——而真实原因可能只是"那个 Windows 服务没启动"。
/// </para>
///
/// <para>
/// 分层：本类只做"连一下 + 说人话"，不建表、不写数据，因此可以放启动路径上，也可以直接单测。
/// 口令一律经 <see cref="Mask"/> 脱敏后才进入日志与异常消息。
/// </para>
/// </summary>
public static partial class ConnectionPreflight
{
    /// <summary>预检默认超时；超时只影响启动体验，不影响正确性。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 预检结论。分开这几种是因为**修法完全不同**：端口没开要去启动数据库，
    /// 口令错要去改连接串，主机名错是 DNS/拼写问题。
    /// </summary>
    public enum ProbeStatus
    {
        /// <summary>连接成功。</summary>
        Ok,

        /// <summary>连接串本身解析不了（SqlSugar 也一样解析不了）。</summary>
        Malformed,

        /// <summary>主机名解析不到。</summary>
        DnsFailure,

        /// <summary>TCP 连不上：端口没监听、网络不可达或被防火墙丢弃。</summary>
        Unreachable,

        /// <summary>TCP 握手超时。</summary>
        TimedOut,

        /// <summary>TCP 通了，但 PostgreSQL 拒绝：认证失败、库不存在等。</summary>
        ServerRejected,

        /// <summary>TCP 通了，但对端不是（或不兼容的）PostgreSQL。</summary>
        ClientFailure
    }

    /// <summary>一次预检的结果。<see cref="Endpoint"/> 已脱敏（不含口令）。</summary>
    public sealed record ProbeResult(ProbeStatus Status, string Endpoint, string Detail)
    {
        public bool Succeeded => Status == ProbeStatus.Ok;
    }

    /// <summary>把连接串里的口令替换成 <c>***</c>；其余部分原样保留，便于用户一眼认出是哪条配置。</summary>
    public static string Mask(string? connectionString) =>
        string.IsNullOrWhiteSpace(connectionString)
            ? "(空)"
            : PasswordPattern().Replace(connectionString, "${1}***");

    /// <summary>
    /// 提取 <c>host:port/database</c> 形式的端点（不含用户名口令），解析不了时返回说明性文本。
    /// </summary>
    public static string ResolveEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "(未配置)";

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
            var database = string.IsNullOrWhiteSpace(builder.Database) ? "(未指定库名)" : builder.Database;
            return $"{host}:{builder.Port}/{database}";
        }
        catch (Exception)
        {
            return "(无法解析)";
        }
    }

    /// <summary>
    /// 真正连一次。<paramref name="timeout"/> 为 0 或负数表示跳过预检（直接按成功处理）。
    /// </summary>
    public static async Task<ProbeResult> CheckAsync(
        string? connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ResolveEndpoint(connectionString);

        if (string.IsNullOrWhiteSpace(connectionString))
            return new ProbeResult(ProbeStatus.Malformed, endpoint, "ConnectionStrings:KnowledgeBase 是空的。");

        if (timeout <= TimeSpan.Zero)
            return new ProbeResult(ProbeStatus.Ok, endpoint, "预检已被配置关闭。");

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception)
        {
            return new ProbeResult(ProbeStatus.Malformed, endpoint, exception.Message);
        }

        var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        // 第一步：TCP。把"端口没有监听"与"口令/库名不对"分开——这两种失败的修法毫无共同点。
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, builder.Port, deadline.Token);
        }
        catch (SocketException socket) when (socket.SocketErrorCode is SocketError.HostNotFound
                                                 or SocketError.NoData
                                                 or SocketError.TryAgain)
        {
            return new ProbeResult(ProbeStatus.DnsFailure, endpoint, socket.Message);
        }
        catch (SocketException socket)
        {
            return new ProbeResult(ProbeStatus.Unreachable, endpoint, socket.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(
                ProbeStatus.TimedOut,
                endpoint,
                $"{timeout.TotalSeconds:0.#} 秒内没有建立 TCP 连接。");
        }

        // 第二步：TCP 通了不代表能用，让 Npgsql 真正开一条连接，才能看出库不存在 / 口令错。
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(deadline.Token);
            return new ProbeResult(ProbeStatus.Ok, endpoint, "连接成功。");
        }
        catch (PostgresException postgres) when (postgres.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return new ProbeResult(
                ProbeStatus.ServerRejected,
                endpoint,
                $"库不存在（{postgres.SqlState}）：{postgres.MessageText}");
        }
        catch (PostgresException postgres)
        {
            return new ProbeResult(
                ProbeStatus.ServerRejected,
                endpoint,
                $"服务器拒绝连接（{postgres.SqlState}）：{postgres.MessageText}");
        }
        catch (NpgsqlException exception)
        {
            return new ProbeResult(ProbeStatus.ClientFailure, endpoint, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(
                ProbeStatus.TimedOut,
                endpoint,
                $"TCP 已连通，但 {timeout.TotalSeconds:0.#} 秒内没有完成 PostgreSQL 握手。");
        }
    }

    /// <summary>
    /// 把失败的预检结果拼成一段**能直接照着做**的中文说明（日志与异常共用同一份文本）。
    /// <para>
    /// 必须包含三件事，缺一件用户就得自己猜：连的是哪个端点、为什么连不上、
    /// 以及"便携数据库被跳过"这个因果（否则用户会以为便携库坏了）。
    /// </para>
    /// </summary>
    public static string BuildMessage(ProbeResult result, string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(result);

        var reason = result.Status switch
        {
            ProbeStatus.Malformed => $"连接串格式无法解析：{result.Detail}",
            ProbeStatus.DnsFailure => $"主机名解析不到：{result.Detail}",
            ProbeStatus.Unreachable =>
                $"TCP 连不上（{result.Detail}）。Windows 上的『连接被拒绝』通常意味着那个地址上"
                + "根本没有进程在监听——也就是数据库没有启动。",
            ProbeStatus.TimedOut => $"连接超时：{result.Detail}",
            ProbeStatus.ServerRejected => $"对方是 PostgreSQL，但拒绝提供服务：{result.Detail}。请检查用户名、口令与库名。",
            ProbeStatus.ClientFailure => $"端口是通的，但对端不是可用的 PostgreSQL：{result.Detail}",
            _ => result.Detail
        };

        var firstRemedy = result.Status is ProbeStatus.ServerRejected or ProbeStatus.ClientFailure
            ? "1) 核对这条连接串里的用户名、口令与库名（大小写、库是否已创建）。"
            : "1) 先把那台数据库启动起来。本机若装的是 Windows 服务版 PostgreSQL，"
              + "用 `Get-Service *postgresql*` 查出服务名，再以管理员身份 `Start-Service <服务名>`。";

        return string.Join(
            Environment.NewLine,
            $"外部数据库不可用，启动已中止。",
            $"  端点：{result.Endpoint}",
            $"  连接串：{Mask(connectionString)}",
            $"  原因：{reason}",
            "  为什么没有自动改用内置数据库：配置了外部连接串时便携 PostgreSQL 会被跳过"
            + "（PortablePostgres:RespectExternalConnectionString=true），所以本次启动没有可用的数据库。",
            "  三种修法任选其一：",
            $"    {firstRemedy}",
            "    2) 改用内置便携数据库：清空这条连接串，例如 "
            + "`dotnet user-secrets remove \"ConnectionStrings:KnowledgeBase\" --project MyFirstMafAgent.Api`"
            + "（也可以删掉 appsettings / 环境变量里的同名项）。",
            "    3) 保留连接串但强制走便携库：设置 `PortablePostgres:RespectExternalConnectionString=false`"
            + "（环境变量 `PortablePostgres__RespectExternalConnectionString=false`）。");
    }

    /// <summary>匹配 <c>Password=…;</c>；只用于脱敏，不解析语义。</summary>
    [GeneratedRegex(@"(?i)(password\s*=\s*)[^;]*")]
    private static partial Regex PasswordPattern();
}
