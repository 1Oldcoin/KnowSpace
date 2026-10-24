using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyFirstMafAgent.Api.Application.KnowledgePackages;

/// <summary>
/// 可移植知识库包（<c>.zjpack</c>，本质是 zip）的清单。
///
/// <para>
/// 为什么坚持“清单 + 原始文件”而不是只导出向量库：向量是某个模型、某个版本、某台机器算出来的，
/// 换机器几乎一定对不上；原始文件 + 笔记 + 专题才是真正属于用户的东西。导入方重新解析、重新建索引，
/// 慢一点，但结果对得上。
/// </para>
/// </summary>
public sealed class KnowledgePackageManifest
{
    /// <summary>格式标识，用来挡住“这不是本应用的包”。</summary>
    public const string FormatName = "zhijian-knowledge-package";

    /// <summary>
    /// 当前格式版本。导入端只接受 ≤ 自己的版本，且对未知字段宽容（见 <see cref="JsonOptions"/>）。
    /// </summary>
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;
    public int FormatVersion { get; set; } = CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; }
    public string App { get; set; } = "MyFirstMafAgent";

    /// <summary>导出时的应用版本，仅作诊断用；不参与兼容性判断。</summary>
    public string? AppVersion { get; set; }

    public KnowledgePackageSource KnowledgeBase { get; set; } = new();
    public List<KnowledgePackageTopic> Topics { get; set; } = [];
    public List<KnowledgePackageDocument> Documents { get; set; } = [];
    public List<KnowledgePackageNote> Notes { get; set; } = [];

    /// <summary>导出过程中发现的、非致命的异常情况（例如某个原始文件已不在磁盘上）。</summary>
    public List<string> Warnings { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class KnowledgePackageSource
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class KnowledgePackageTopic
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "lavender";
}

public sealed class KnowledgePackageDocument
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public Guid? TopicId { get; set; }

    /// <summary>摘要跟着资料走：用户花时间生成的总结，不该因为换机器就丢失。</summary>
    public string? Summary { get; set; }

    /// <summary>原始文件的 SHA-256（十六进制小写），导入时用来校验完整性。</summary>
    public string? Sha256 { get; set; }

    /// <summary>包内相对路径，例如 <c>files/0001.pdf</c>。</summary>
    public string File { get; set; } = string.Empty;
}

public sealed class KnowledgePackageNote
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Category { get; set; } = "灵感随记";
    public string Color { get; set; } = "lilac";
    public List<string> Tags { get; set; } = [];
    public bool IsPinned { get; set; }
    public Guid? SourceDocumentId { get; set; }
    public Guid? TopicId { get; set; }
    public List<KnowledgePackageNoteSource> Sources { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class KnowledgePackageNoteSource
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int ChunkIndex { get; set; } = -1;
    public double Similarity { get; set; }
    public string? Snippet { get; set; }
}

/// <summary>导入结果：新知识库 + 实际落库的数量 + 被跳过的条目。</summary>
public sealed record KnowledgePackageImportResult(
    Guid KnowledgeBaseId,
    string KnowledgeBaseName,
    int Documents,
    int Notes,
    int Topics,
    IReadOnlyList<string> Warnings);

/// <summary>包的读取/校验失败，或导入失败后已回滚：与“服务端内部错误”区分开，端点返回 400。</summary>
public sealed class KnowledgePackageException(string message, Exception? innerException = null)
    : Exception(message, innerException);
