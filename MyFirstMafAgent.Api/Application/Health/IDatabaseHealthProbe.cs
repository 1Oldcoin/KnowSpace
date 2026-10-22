namespace MyFirstMafAgent.Api.Application.Health;

/// <summary>
/// 数据库可用性探针端口。端点只依赖该接口，不直接引用 SqlSugar 或连接实现。
/// </summary>
public interface IDatabaseHealthProbe
{
    /// <summary>检查数据库是否可用；不可用时抛出异常。</summary>
    Task CheckAsync(CancellationToken cancellationToken);
}
