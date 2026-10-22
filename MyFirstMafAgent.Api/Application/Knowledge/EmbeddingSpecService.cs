using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Models;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Application.Knowledge;

public sealed record EmbeddingProbeResult(int Dimensions, long ElapsedMilliseconds);

/// <summary>
/// 向量模型规格注册服务。启动时把配置中的规格写入数据库；运行时可以导入本地 ONNX 模型
/// 或注册 OpenAI 兼容的云端模型。新模型只会影响新建的阴影索引，不会改变正在服务的活动索引。
/// </summary>
public sealed class EmbeddingSpecService(
    IEmbeddingSpecStore store,
    IEmbeddingGeneratorProvider generatorProvider,
    IOptions<EmbeddingOptions> embeddingOptions,
    IWebHostEnvironment environment,
    ILogger<EmbeddingSpecService> logger)
{
    private const int MaxTokenizerFileSizeBytes = 64 * 1024 * 1024;

    public async Task<IReadOnlyList<EmbeddingSpec>> EnsureConfiguredAsync(CancellationToken cancellationToken)
    {
        var drafts = BuildDrafts();
        foreach (var draft in drafts)
        {
            var spec = await store.UpsertAsync(draft, cancellationToken);
            logger.LogInformation("Embedding spec available: {Name} ({Provider}/{Model}, fingerprint {Fingerprint})",
                spec.Name, spec.Provider, spec.Model, spec.Fingerprint);
        }
        return await store.ListAsync(cancellationToken);
    }

    public async Task<EmbeddingSpec> GetDefaultAsync(CancellationToken cancellationToken) =>
        await ResolveDefaultAsync(null, cancellationToken);

    /// <summary>按「显式指定 → 配置中的默认名称 → 最早注册」的优先级解析默认规格。</summary>
    public async Task<EmbeddingSpec> ResolveDefaultAsync(Guid? preferredSpecId, CancellationToken cancellationToken)
    {
        var specs = await EnsureConfiguredAsync(cancellationToken);
        if (specs.Count == 0)
            throw new ConflictException("尚未注册任何向量模型规格。请先导入本地模型或注册云端模型。");

        if (preferredSpecId is Guid id)
        {
            var preferred = specs.FirstOrDefault(spec => spec.Id == id);
            if (preferred is not null) return preferred;
        }

        var defaultName = embeddingOptions.Value.DefaultSpecName;
        return (!string.IsNullOrWhiteSpace(defaultName)
                ? specs.FirstOrDefault(spec => string.Equals(spec.Name, defaultName, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? specs[0];
    }

    public async Task<EmbeddingSpec> CreateAsync(EmbeddingSpecDraft draft, CancellationToken cancellationToken)
    {
        Validate(draft);
        return await store.UpsertAsync(draft, cancellationToken);
    }

    public async Task<EmbeddingSpec> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetAsync(id, cancellationToken)
        ?? throw new EmbeddingSpecNotFoundException(id);

    /// <summary>
    /// 导入本地 ONNX 向量模型。文件写入 Embedding:ModelRootPath 下的独立目录，
    /// 自动识别输出维度并做一次真实推理验证，成功后注册为可切换的规格。
    /// </summary>
    public async Task<EmbeddingSpec> ImportLocalAsync(
        string name,
        string? modelName,
        string revision,
        int? dimensions,
        int maxSequenceLength,
        string modelFileName,
        Stream modelStream,
        string tokenizerFileName,
        Stream tokenizerStream,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(modelFileName), ".onnx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("本地向量模型必须是 .onnx 文件。");
        if (!string.Equals(Path.GetExtension(tokenizerFileName), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Tokenizer 必须是 .json 文件。");

        var safeName = NormalizeName(name, modelName);
        var slug = Slugify(safeName);
        var directory = Path.Combine(ResolveModelRoot(), $"{slug}-{Guid.NewGuid():N}"[..(slug.Length + 9)]);
        Directory.CreateDirectory(directory);

        var modelPath = Path.Combine(directory, "model.onnx");
        var tokenizerPath = Path.Combine(directory, "tokenizer.json");
        try
        {
            await CopyToFileAsync(modelStream, modelPath, embeddingOptions.Value.MaxModelFileSizeBytes, cancellationToken);
            await CopyToFileAsync(tokenizerStream, tokenizerPath, MaxTokenizerFileSizeBytes, cancellationToken);

            var detectedDimensions = LocalOnnxEmbeddingService.ReadOutputDimensions(modelPath);
            LocalOnnxEmbeddingService.ValidateTokenizerFile(tokenizerPath);
            if (dimensions is int requested && requested != detectedDimensions)
                throw new ArgumentException($"模型实际输出维度为 {detectedDimensions}，与填写的 {requested} 不一致。");

            var safeRevision = string.IsNullOrWhiteSpace(revision) ? "1" : revision.Trim();
            var safeModelName = string.IsNullOrWhiteSpace(modelName) ? safeName : modelName.Trim();
            var fingerprint = EmbeddingSpec.CreateFingerprint(
                EmbeddingProviders.LocalOnnx, safeModelName, detectedDimensions, maxSequenceLength, safeRevision);

            // 相同规格已存在时直接复用，避免重复占用磁盘。
            var existing = await store.FindByFingerprintAsync(fingerprint, cancellationToken);
            if (existing is not null)
            {
                TryDeleteDirectory(directory);
                return existing;
            }

            var draft = new EmbeddingSpecDraft(
                safeName,
                EmbeddingProviders.LocalOnnx,
                safeModelName,
                detectedDimensions,
                maxSequenceLength,
                safeRevision,
                new EmbeddingRuntimeConfig(directory, null, null, 0));
            Validate(draft);

            // 真实跑一次推理，确保 tokenizer 与模型配套、推理管线可用。
            await ProbeSpecAsync(draft, cancellationToken);
            var spec = await store.UpsertAsync(draft, cancellationToken);
            logger.LogInformation("Imported local embedding model {Name} ({Dimensions}d) into {Directory}, fingerprint {Fingerprint}",
                spec.Name, spec.Dimensions, directory, spec.Fingerprint);
            return spec;
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    /// <summary>注册 OpenAI 兼容的云端向量模型；注册前先验证端点与维度。</summary>
    public async Task<EmbeddingSpec> RegisterCloudAsync(
        string name,
        string endpoint,
        string model,
        string? apiKey,
        int dimensions,
        int maxSequenceLength,
        string revision,
        CancellationToken cancellationToken)
    {
        var safeRevision = string.IsNullOrWhiteSpace(revision) ? "1" : revision.Trim();
        var draft = new EmbeddingSpecDraft(
            string.IsNullOrWhiteSpace(name) ? model : name.Trim(),
            EmbeddingProviders.OpenAiCompatible,
            model.Trim(),
            dimensions,
            maxSequenceLength,
            safeRevision,
            new EmbeddingRuntimeConfig(null, endpoint.Trim(), apiKey, 0),
            // 同一模型名在不同服务商/端点下可能产生不同向量，端点参与指纹。
            endpoint.Trim());
        Validate(draft);

        await ProbeSpecAsync(draft, cancellationToken);
        var spec = await store.UpsertAsync(draft, cancellationToken);
        logger.LogInformation("Registered cloud embedding model {Name} ({Model}, {Dimensions}d), fingerprint {Fingerprint}",
            spec.Name, spec.Model, spec.Dimensions, spec.Fingerprint);
        return spec;
    }

    /// <summary>对已注册规格做一次真实向量化探测，用于设置页的“测试连接”。</summary>
    public async Task<EmbeddingProbeResult> ProbeAsync(Guid specId, CancellationToken cancellationToken)
    {
        var spec = await GetRequiredAsync(specId, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var generator = generatorProvider.GetOrCreate(spec);
        var vector = await generator.CreateEmbeddingAsync("知识库向量模型连接测试。", cancellationToken);
        stopwatch.Stop();
        if (vector.Length != spec.Dimensions)
            throw new InvalidDataException($"模型返回 {vector.Length} 维向量，与规格声明的 {spec.Dimensions} 维不一致。");
        return new EmbeddingProbeResult(vector.Length, stopwatch.ElapsedMilliseconds);
    }

    private async Task ProbeSpecAsync(EmbeddingSpecDraft draft, CancellationToken cancellationToken)
    {
        var fingerprint = EmbeddingSpec.CreateFingerprint(
            draft.Provider, draft.Model, draft.Dimensions, draft.MaxSequenceLength, draft.Revision,
            draft.FingerprintVariant);
        var spec = new EmbeddingSpec(Guid.Empty, draft.Name, draft.Provider, draft.Model, draft.Dimensions,
            draft.MaxSequenceLength, draft.Revision, fingerprint, draft.Runtime, DateTimeOffset.UtcNow);
        var generator = generatorProvider.GetOrCreate(spec);
        var vector = await generator.CreateEmbeddingAsync("知识库向量模型连接测试。", cancellationToken);
        if (vector.Length != draft.Dimensions)
            throw new InvalidDataException($"模型返回 {vector.Length} 维向量，与声明的 {draft.Dimensions} 维不一致。");
    }

    private string ResolveModelRoot()
    {
        var configured = embeddingOptions.Value.ModelRootPath;
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
    }

    private static async Task CopyToFileAsync(
        Stream source, string destinationPath, long maxBytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
                throw new ArgumentException($"文件超过允许大小 {maxBytes} 字节。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0) throw new ArgumentException("上传的文件为空。");
    }

    private static string NormalizeName(string? name, string? modelName)
    {
        var value = name?.Trim();
        if (string.IsNullOrWhiteSpace(value)) value = modelName?.Trim();
        if (string.IsNullOrWhiteSpace(value)) value = "local-embedding";
        if (value.Length > 100) value = value[..100];
        return value;
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) && character < 128) builder.Append(char.ToLowerInvariant(character));
            else if (character is '-' or '_' or '.') builder.Append(character);
        }
        var slug = builder.ToString().Trim('.', '-', '_');
        return string.IsNullOrWhiteSpace(slug) ? "model" : slug.Length > 40 ? slug[..40] : slug;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响向调用方抛出原始错误。
        }
    }

    private List<EmbeddingSpecDraft> BuildDrafts()
    {
        var options = embeddingOptions.Value;
        if (options.Specs.Count == 0)
        {
            var model = Path.GetFileName(options.ModelDirectory.TrimEnd('/', '\\'));
            return
            [
                new EmbeddingSpecDraft(
                    "default",
                    EmbeddingProviders.LocalOnnx,
                    string.IsNullOrWhiteSpace(model) ? "bge-small-zh-v1.5" : model,
                    options.Dimensions,
                    options.MaxSequenceLength,
                    "1",
                    new EmbeddingRuntimeConfig(options.ModelDirectory, null, null, options.IntraOpNumThreads))
            ];
        }

        return options.Specs.Select(spec => new EmbeddingSpecDraft(
            string.IsNullOrWhiteSpace(spec.Name) ? "default" : spec.Name,
            string.IsNullOrWhiteSpace(spec.Provider) ? EmbeddingProviders.LocalOnnx : spec.Provider,
            spec.Model,
            spec.Dimensions,
            spec.MaxSequenceLength,
            string.IsNullOrWhiteSpace(spec.Revision) ? "1" : spec.Revision,
            new EmbeddingRuntimeConfig(
                spec.ModelDirectory ?? (string.Equals(spec.Provider, EmbeddingProviders.LocalOnnx, StringComparison.OrdinalIgnoreCase)
                    ? options.ModelDirectory
                    : null),
                spec.Endpoint,
                spec.ApiKey,
                spec.IntraOpNumThreads))).ToList();
    }

    private static void Validate(EmbeddingSpecDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 100)
            throw new ArgumentException("向量规格名称必填且不能超过 100 个字符。", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Model))
            throw new ArgumentException("向量模型标识必填。", nameof(draft));
        if (draft.Dimensions is < 1 or > 8192)
            throw new ArgumentException("向量维度必须在 1 到 8192 之间。", nameof(draft));
        if (draft.MaxSequenceLength is < 2 or > 8192)
            throw new ArgumentException("最大序列长度必须在 2 到 8192 之间。", nameof(draft));
        if (draft.Provider is not (EmbeddingProviders.LocalOnnx or EmbeddingProviders.OpenAiCompatible))
            throw new ArgumentException($"不支持的向量模型 Provider：{draft.Provider}。", nameof(draft));

        if (string.Equals(draft.Provider, EmbeddingProviders.OpenAiCompatible, StringComparison.OrdinalIgnoreCase)
            && (!Uri.TryCreate(draft.Runtime.Endpoint, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(draft.Model)))
        {
            throw new ArgumentException("OpenAI 兼容向量模型必须提供有效的绝对 Endpoint 和模型名称。", nameof(draft));
        }
    }
}

public sealed class EmbeddingSpecNotFoundException(Guid id)
    : NotFoundException($"向量模型规格 {id} 不存在。");
