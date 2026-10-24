using MyFirstMafAgent.Api.Domain.Jobs;

namespace MyFirstMafAgent.Api.Application.Jobs;

/// <summary>任务执行失败但「可重试」的异常（例如解析报错、下游瞬时不可用）。</summary>
public sealed class JobExecutionException(string message, bool fatal = false, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>true 表示这类错误重试也没用（例如资料不存在）。</summary>
    public bool Fatal { get; } = fatal;

    public static JobExecutionException NotRetryable(string message) => new(message, fatal: true);
}

/// <summary>
/// 交给处理器的执行上下文：进度上报 + 取消令牌。
/// </summary>
public sealed class JobExecutionContext(
    JobRecord job,
    Func<int, string?, CancellationToken, Task> reportProgress)
{
    public JobRecord Job { get; } = job;

    /// <summary>上报进度；同时也会续租，因此长任务不必额外心跳。</summary>
    public Task ReportProgressAsync(int progress, string? message, CancellationToken cancellationToken) =>
        reportProgress(progress, message, cancellationToken);

    public override string ToString() => $"{Job.Type}#{Job.Id}（第 {Job.Attempts} 次）";
}

/// <summary>
/// 任务处理器契约。
///
/// <para>
/// 处理器实现放在 <c>Application</c> 或 <c>Documents</c> 层并注册为 <b>scoped</b>：
/// 每个任务在自己的 DI 作用域内执行，避免跨任务共享 DbContext 之类的状态。
/// </para>
/// </summary>
public interface IJobHandler
{
    /// <summary>该处理器负责的任务类型（见 <see cref="JobTypes"/>）。</summary>
    string Type { get; }

    /// <summary>
    /// 执行任务。返回的结果文本会写进任务的 <c>result</c> 字段（可为 null）。
    /// 抛出 <see cref="JobExecutionException"/> 表示可读的失败；其他异常按可重试处理。
    /// 抛出 <see cref="OperationCanceledException"/> 表示被取消或正在停机。
    /// </summary>
    Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 已注册的处理器索引。
///
/// <para>
/// 处理器是 scoped（每个任务一个作用域），所以这里<b>不能</b>直接注入
/// <c>IEnumerable&lt;IJobHandler&gt;</c>——那会在根作用域里创建处理器并把它锁成单例。
/// 改为惰性开一个临时作用域读取类型集合，之后用 <see cref="Resolve"/> 在任务自己的作用域里取实例。
/// </para>
/// </summary>
public sealed class JobHandlerRegistry(IServiceScopeFactory scopeFactory)
{
    private HashSet<string>? _types;

    public IReadOnlyCollection<string> Types => _types ??= LoadTypes();

    public bool IsRegistered(string type) => Types.Contains(type);

    /// <summary>在任务作用域里解析处理器；未注册返回 null。</summary>
    public static IJobHandler? Resolve(IServiceProvider scopedServices, string type) =>
        scopedServices.GetServices<IJobHandler>().FirstOrDefault(handler => handler.Type == type);

    private HashSet<string> LoadTypes()
    {
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider
            .GetServices<IJobHandler>()
            .Select(handler => handler.Type)
            .ToHashSet(StringComparer.Ordinal);
    }
}
