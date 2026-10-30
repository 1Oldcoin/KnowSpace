namespace MyFirstMafAgent.Api.Application.Health;

/// <summary>
/// 「业务连接池还能不能用」的探针：走应用真正使用的那个 <c>SqlSugarScope</c>。
///
/// <para>
/// 它和 <see cref="IDatabaseHealthProbe"/> 回答的是两个不同问题，必须分开：
/// </para>
///
/// <list type="bullet">
/// <item><see cref="IDatabaseHealthProbe"/>：PostgreSQL 这个服务本身活着吗？用的是独立、不进池的连接。</item>
/// <item><b>本接口</b>：应用还能从池里拿到连接吗？走共享池。池被占满时只有它失败。</item>
/// </list>
///
/// <para>
/// 两个一起看，才能把「数据库挂了」和「我们自己把连接池耗尽了」区分开——
/// 这两者的处置方式完全不同（前者等数据库恢复，后者只能重启进程把池重建）。
/// </para>
/// </summary>
public interface IDatabasePoolProbe
{
    /// <summary>从业务连接池取一条连接执行 <c>SELECT 1</c>；拿不到连接时抛出异常。</summary>
    Task CheckAsync(CancellationToken cancellationToken);
}
