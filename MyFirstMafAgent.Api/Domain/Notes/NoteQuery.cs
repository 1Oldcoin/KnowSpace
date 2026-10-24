namespace MyFirstMafAgent.Api.Domain.Notes;

/// <summary>
/// 笔记列表的过滤条件。
///
/// <para>
/// 存在的意义是把"筛什么"显式化：以前服务层先把整个知识库的笔记读进内存再过滤，
/// 而每篇笔记的正文上限是 50 000 字——上千条笔记时光是搬运正文就够慢了。
/// 现在条件交给仓储翻译成 WHERE，只在 SQL 侧命中需要的行。
/// </para>
/// </summary>
/// <param name="KnowledgeBaseId">必填：只查这个知识库的笔记。</param>
/// <param name="Search">关键词：标题 / 正文 / 标签的子串匹配（大小写不敏感）。</param>
/// <param name="Category">分类精确匹配（大小写不敏感）。</param>
/// <param name="DocumentId">反向查询：引用了这份资料的笔记。</param>
/// <param name="TopicId">归属这个专题的笔记。</param>
public sealed record NoteQuery(
    Guid KnowledgeBaseId,
    string? Search = null,
    string? Category = null,
    Guid? DocumentId = null,
    Guid? TopicId = null)
{
    public string? NormalizedSearch => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

    public string? NormalizedCategory => string.IsNullOrWhiteSpace(Category) ? null : Category.Trim();

    /// <summary>`Guid.Empty` 与 null 同样表示"不按资料过滤"。</summary>
    public Guid? EffectiveDocumentId => DocumentId is { } id && id != Guid.Empty ? id : null;

    /// <summary>`Guid.Empty` 与 null 同样表示"不按专题过滤"。</summary>
    public Guid? EffectiveTopicId => TopicId is { } id && id != Guid.Empty ? id : null;
}
