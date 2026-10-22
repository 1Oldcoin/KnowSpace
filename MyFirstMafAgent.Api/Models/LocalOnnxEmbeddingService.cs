using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Models;

/// <summary>单个 ONNX 向量模型的运行参数；每个向量规格持有独立实例。</summary>
public sealed record LocalOnnxEmbeddingSettings(
    string ModelDirectory,
    int Dimensions,
    int MaxSequenceLength,
    int IntraOpNumThreads);

public sealed class LocalOnnxEmbeddingService(LocalOnnxEmbeddingSettings settings) : IEmbeddingGenerator, IDisposable
{
    private readonly SemaphoreSlim inferenceLock = new(1, 1);
    private InferenceSession? session;
    private BertTokenizer? tokenizer;
    private int separatorTokenId;

    public async Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Embedding input cannot be empty.", nameof(text));

        await inferenceLock.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => Embed(text), cancellationToken);
        }
        finally
        {
            inferenceLock.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (session is not null) return;

        var options = settings;
        var modelDirectory = Path.IsPathRooted(options.ModelDirectory)
            ? options.ModelDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.ModelDirectory));
        var modelPath = Path.Combine(modelDirectory, "model.onnx");
        var tokenizerPath = Path.Combine(modelDirectory, "tokenizer.json");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The local embedding ONNX model was not found.", modelPath);
        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException("The local embedding tokenizer was not found.", tokenizerPath);

        tokenizer = CreateTokenizer(tokenizerPath, out separatorTokenId);
        using var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        if (options.IntraOpNumThreads > 0) sessionOptions.IntraOpNumThreads = options.IntraOpNumThreads;
        var loadedSession = new InferenceSession(modelPath, sessionOptions);
        if (!loadedSession.OutputMetadata.TryGetValue("last_hidden_state", out var output)
            || output.Dimensions[^1] != options.Dimensions)
        {
            loadedSession.Dispose();
            throw new InvalidDataException($"The ONNX model output dimension does not match Embedding:Dimensions ({options.Dimensions}).");
        }
        session = loadedSession;
    }

    /// <summary>读取 ONNX 模型 last_hidden_state 的隐藏维度，用于导入时自动识别向量维度。</summary>
    public static int ReadOutputDimensions(string modelPath)
    {
        using var session = new InferenceSession(modelPath);
        EnsureBertInputs(session);
        if (!session.OutputMetadata.TryGetValue("last_hidden_state", out var output) || output.Dimensions.Length < 2)
            throw new InvalidDataException("The ONNX model does not expose a 2-D 'last_hidden_state' output.");

        var dimensions = output.Dimensions[^1];
        if (dimensions <= 0)
            throw new InvalidDataException("The ONNX model reports an unknown hidden dimension.");
        return dimensions;
    }

    /// <summary>校验 tokenizer.json 是否为受支持的 BERT WordPiece 词表。</summary>
    public static void ValidateTokenizerFile(string tokenizerPath) => CreateTokenizer(tokenizerPath, out _);

    private static void EnsureBertInputs(InferenceSession session)
    {
        if (!session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal).IsSupersetOf(
                ["input_ids", "attention_mask", "token_type_ids"]))
        {
            throw new InvalidDataException("The ONNX model is missing one or more required BERT inputs.");
        }
    }

    private float[] Embed(string text)
    {
        var options = settings;
        var tokenIds = tokenizer!.EncodeToIds(text).Select(static id => (long)id).ToArray();
        if (tokenIds.Length > options.MaxSequenceLength)
        {
            Array.Resize(ref tokenIds, options.MaxSequenceLength);
            tokenIds[^1] = separatorTokenId;
        }

        var sequenceLength = tokenIds.Length;
        var attentionMask = Enumerable.Repeat(1L, sequenceLength).ToArray();
        var tokenTypeIds = new long[sequenceLength];
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(tokenIds, [1, sequenceLength])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, sequenceLength])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, [1, sequenceLength]))
        };

        using var outputs = session!.Run(inputs);
        var hiddenStates = outputs.Single(output => output.Name == "last_hidden_state").AsTensor<float>().ToArray();
        var embedding = new float[options.Dimensions];
        for (var tokenIndex = 0; tokenIndex < sequenceLength; tokenIndex++)
        {
            var offset = tokenIndex * options.Dimensions;
            for (var dimension = 0; dimension < embedding.Length; dimension++)
                embedding[dimension] += hiddenStates[offset + dimension];
        }

        var inverseTokenCount = 1f / sequenceLength;
        var squaredNorm = 0f;
        for (var dimension = 0; dimension < embedding.Length; dimension++)
        {
            embedding[dimension] *= inverseTokenCount;
            squaredNorm += embedding[dimension] * embedding[dimension];
        }

        var norm = MathF.Sqrt(squaredNorm);
        if (norm <= 0f) throw new InvalidOperationException("The embedding model returned a zero vector.");
        for (var dimension = 0; dimension < embedding.Length; dimension++)
            embedding[dimension] /= norm;
        return embedding;
    }

    private static BertTokenizer CreateTokenizer(string tokenizerPath, out int separatorTokenId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(tokenizerPath));
        var model = document.RootElement.GetProperty("model");
        if (model.GetProperty("type").GetString() != "WordPiece")
            throw new InvalidDataException("The local embedding tokenizer must use the BERT WordPiece model.");

        var vocabulary = model.GetProperty("vocab")
            .EnumerateObject()
            .Select(property => (Token: property.Name, Id: property.Value.GetInt32()))
            .OrderBy(entry => entry.Id)
            .ToArray();
        if (vocabulary.Length == 0 || vocabulary.Where((entry, index) => entry.Id != index).Any())
            throw new InvalidDataException("The BERT tokenizer vocabulary must contain contiguous token IDs starting at zero.");

        separatorTokenId = vocabulary.Single(entry => entry.Token == "[SEP]").Id;
        var vocabText = string.Join('\n', vocabulary.Select(entry => entry.Token)) + "\n";
        using var vocabStream = new MemoryStream(Encoding.UTF8.GetBytes(vocabText));
        return BertTokenizer.Create(vocabStream, new BertOptions
        {
            LowerCaseBeforeTokenization = false,
            ApplyBasicTokenization = true,
            IndividuallyTokenizeCjk = true,
            RemoveNonSpacingMarks = false,
            ClassificationToken = "[CLS]",
            SeparatorToken = "[SEP]",
            PaddingToken = "[PAD]",
            MaskingToken = "[MASK]"
        });
    }

    public void Dispose()
    {
        session?.Dispose();
        inferenceLock.Dispose();
    }
}
