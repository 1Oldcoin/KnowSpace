namespace MyFirstMafAgent.Api.Domain.Agents;

/// <summary>
/// 「在线」的判定口径。
///
/// <para>
/// MCP 走的是 Streamable HTTP 且服务端配置为 <c>Stateless = true</c>：每个请求都是一次独立的
/// HTTP 往返，没有长连接、没有会话表。所以**服务端并不知道谁"正连着"**，
/// 只能退一步用「最近一次带有效密钥的请求距今多久」来近似。
/// </para>
///
/// <para>
/// 这也是为什么界面上的措辞是「最近活跃」而不是「已连接」——把近似值说成事实，
/// 会让用户去排查一个根本不存在的连接问题。
/// </para>
/// </summary>
public static class AgentClientPresence
{
    /// <summary>最后活跃时间在这个窗口内就算「在线」。</summary>
    public const int OnlineWindowMinutes = 5;
}

/// <summary>
/// 一个客户端在统计窗口内的调用汇总。窗口由调用方给定（界面默认 24 小时）。
/// </summary>
/// <param name="ClientId">客户端 ID。</param>
/// <param name="Total">总调用次数。</param>
/// <param name="Success">成功次数。</param>
/// <param name="Denied">被鉴权或权限闸门拒绝的次数（未执行工具）。</param>
/// <param name="Failed">执行报错或协议错误的次数。</param>
/// <param name="LastCallAt">窗口内最后一次调用的时间。</param>
public sealed record AgentCallSummary(
    string ClientId,
    int Total,
    int Success,
    int Denied,
    int Failed,
    DateTimeOffset LastCallAt);
