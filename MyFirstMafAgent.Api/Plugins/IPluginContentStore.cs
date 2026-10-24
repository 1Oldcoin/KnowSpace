using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 平台内容仓储端口：把"一条平台条目变成知识库资料"这件事收敛成四个动作。
///
/// <para>
/// 存在的理由是两条约束同时成立：
/// 1. 摄取必须走核心用例（解析/分块/版本/Outbox/索引），不能由插件或映射层直接写表；
/// 2. 幂等与撤回的判定逻辑必须能不启动数据库就测试。
/// </para>
///
/// <para>实现在 <see cref="KnowledgePluginContentStore"/>，是本端口唯一的落地点。</para>
/// </summary>
public interface IPluginContentStore
{
    Task<bool> KnowledgeBaseExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    /// <summary>新建资料并返回资料 ID；失败返回 null。</summary>
    Task<Guid?> CreateAsync(
        PlatformIngestionItem item,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 用条目内容替换已有资料的源文件并重新排队解析。
    /// 返回 false 表示资料正在排队/处理中，调用方应保留旧哈希、下次同步重试。
    /// </summary>
    Task<bool> ReplaceAsync(
        Guid documentId,
        PlatformIngestionItem item,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken);

    /// <summary>删除资料及其源文件；资料不存在返回 false。</summary>
    Task<bool> DeleteAsync(Guid documentId, CancellationToken cancellationToken);
}
