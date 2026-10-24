using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Plugins;

/// <summary>
/// 插件写入范围（允许写哪些知识库、单库配额）的校验。
///
/// <para>
/// 与 <see cref="PluginAccessPolicy"/> 分开放：那里管"能不能做"，这里管"能对谁做"。
/// 单独抽出来是因为它需要查知识库是否存在，但仍应能在测试里用桩穷举。
/// </para>
/// </summary>
public static class PluginScopePolicy
{
    /// <summary>返回空列表表示通过；否则是给用户看的错误。</summary>
    public static async Task<IReadOnlyList<string>> ValidateAsync(
        IReadOnlyList<Guid> allowedKnowledgeBaseIds,
        int maxDocuments,
        IKnowledgeBaseDirectory directory,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        if (maxDocuments < 0)
            errors.Add("maxDocuments 不能为负数");

        foreach (var knowledgeBaseId in allowedKnowledgeBaseIds.Distinct())
        {
            if (knowledgeBaseId == Guid.Empty)
            {
                errors.Add("知识库 ID 不能为空 GUID");
                continue;
            }

            if (!await directory.ExistsAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false))
                errors.Add($"知识库 '{knowledgeBaseId}' 不存在");
        }

        return errors;
    }
}
