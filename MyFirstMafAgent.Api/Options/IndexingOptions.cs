namespace MyFirstMafAgent.Api.Options;

public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    /// <summary>回滚窗口：下线索引在该时间段内可被重新激活。</summary>
    public int RollbackWindowMinutes { get; set; } = 60;

    /// <summary>已消费 Outbox 事件的保留时长。检查点之后的事件永远不会被删除。</summary>
    public int OutboxRetentionHours { get; set; } = 168;

    /// <summary>后台索引 Worker 的轮询间隔。</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>质量评测时最多抽取的探针切片数量。</summary>
    public int QualityProbeCount { get; set; } = 8;

    /// <summary>一致性检查未通过时是否阻止索引激活。</summary>
    public bool RequireConsistencyBeforeActivation { get; set; } = true;
}
