namespace MyFirstMafAgent.Api.Options;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    /// <summary>历史兼容字段；未配置 <see cref="Specs"/> 时用于生成默认向量规格。</summary>
    public string ModelDirectory { get; set; } = "ExternalTools/Embedding/bge-small-zh-v1.5";
    public int Dimensions { get; set; } = 512;
    public int MaxSequenceLength { get; set; } = 512;
    public int IntraOpNumThreads { get; set; }

    /// <summary>可注册的向量模型规格；切换模型时新增一条并在索引生命周期中切换。</summary>
    public List<EmbeddingSpecOptions> Specs { get; set; } = [];

    /// <summary>新建知识库默认使用的向量规格名称；未设置时使用最早注册的规格。</summary>
    public string? DefaultSpecName { get; set; }

    /// <summary>运行时导入的本地向量模型存放目录（相对内容根目录或绝对路径）。</summary>
    public string ModelRootPath { get; set; } = "App_Data/models/embedding";

    /// <summary>单个本地模型文件大小上限，默认 1 GiB。</summary>
    public long MaxModelFileSizeBytes { get; set; } = 1_073_741_824;
}

public sealed class EmbeddingSpecOptions
{
    public string Name { get; set; } = "default";
    public string Provider { get; set; } = "onnx-local";
    public string Model { get; set; } = "bge-small-zh-v1.5";
    public int Dimensions { get; set; } = 512;
    public int MaxSequenceLength { get; set; } = 512;
    public string Revision { get; set; } = "1";
    public string? ModelDirectory { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public int IntraOpNumThreads { get; set; }
}