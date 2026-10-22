using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Contracts;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>向量模型、索引生命周期、Outbox 检查点与摘要管理接口。</summary>
public static class IndexEndpoints
{
    public static void MapIndexEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        var specRoutes = api.MapGroup("/embedding-specs").WithTags("向量模型");
        var indexRoutes = api.MapGroup("/knowledge-bases/{knowledgeBaseId:guid}").WithTags("索引生命周期");
        var summaryRoutes = api.MapGroup("/documents").WithTags("摘要");

        specRoutes.MapGet("", async (IEmbeddingSpecStore store, CancellationToken cancellationToken) =>
        {
            var specs = await store.ListAsync(cancellationToken);
            return Results.Ok(specs.Select(ToView));
        });

        specRoutes.MapPost("", (CreateEmbeddingSpecRequest request, EmbeddingSpecService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var draft = new EmbeddingSpecDraft(
                    request.Name ?? "default",
                    request.Provider ?? EmbeddingProviders.LocalOnnx,
                    request.Model ?? string.Empty,
                    request.Dimensions ?? 512,
                    request.MaxSequenceLength ?? 512,
                    request.Revision ?? "1",
                    new EmbeddingRuntimeConfig(request.ModelDirectory, request.Endpoint, request.ApiKey,
                        request.IntraOpNumThreads ?? 0));
                var spec = await service.CreateAsync(draft, cancellationToken);
                return Results.Ok(ToView(spec));
            }));

        // 本地模型导入：multipart 上传 model.onnx + tokenizer.json，服务端自动识别维度并做推理验证。
        specRoutes.MapPost("/import-local", async Task<IResult> (
            HttpRequest request,
            EmbeddingSpecService service,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "请使用 multipart/form-data 上传模型文件。" });

            var form = await request.ReadFormAsync(cancellationToken);
            var modelFile = form.Files.GetFile("modelFile");
            var tokenizerFile = form.Files.GetFile("tokenizerFile");
            if (modelFile is null || tokenizerFile is null)
                return Results.BadRequest(new { error = "必须同时上传 modelFile(.onnx) 与 tokenizerFile(.json)。" });

            try
            {
                await using var modelStream = modelFile.OpenReadStream();
                await using var tokenizerStream = tokenizerFile.OpenReadStream();
                var spec = await service.ImportLocalAsync(
                    form["name"].FirstOrDefault() ?? modelFile.FileName,
                    form["model"].FirstOrDefault(),
                    form["revision"].FirstOrDefault() ?? "1",
                    ParseInt(form["dimensions"].FirstOrDefault()),
                    ParseInt(form["maxSequenceLength"].FirstOrDefault()) ?? 512,
                    modelFile.FileName,
                    modelStream,
                    tokenizerFile.FileName,
                    tokenizerStream,
                    cancellationToken);
                return Results.Ok(ToView(spec));
            }
            catch (Exception exception) when (exception is ArgumentException
                                                  or InvalidDataException
                                                  or FileNotFoundException
                                                  or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        // 云端向量模型注册：注册前会真实调用一次 /embeddings 验证端点与维度。
        specRoutes.MapPost("/register-cloud", (RegisterCloudEmbeddingSpecRequest request, EmbeddingSpecService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(request.Endpoint) || string.IsNullOrWhiteSpace(request.Model))
                    return Results.BadRequest(new { error = "云端向量模型必须填写接口地址与模型名称。" });

                var spec = await service.RegisterCloudAsync(
                    request.Name ?? request.Model,
                    request.Endpoint.Trim(),
                    request.Model.Trim(),
                    request.ApiKey,
                    request.Dimensions ?? 1024,
                    request.MaxSequenceLength ?? 512,
                    request.Revision ?? "1",
                    cancellationToken);
                return Results.Ok(ToView(spec));
            }));

        specRoutes.MapPost("/{specId:guid}/probe", (Guid specId, EmbeddingSpecService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var result = await service.ProbeAsync(specId, cancellationToken);
                return Results.Ok(new EmbeddingProbeResponse(result.Dimensions, result.ElapsedMilliseconds));
            }));

        indexRoutes.MapGet("/indexes", async (
            Guid knowledgeBaseId,
            IKnowledgeBaseDirectory knowledgeBases,
            IKnowledgeIndexStore store,
            CancellationToken cancellationToken) =>
        {
            if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken)) return Results.NotFound();
            return Results.Ok(await store.ListAsync(knowledgeBaseId, cancellationToken));
        });

        indexRoutes.MapPost("/indexes", (
            Guid knowledgeBaseId,
            CreateKnowledgeIndexRequest request,
            IKnowledgeBaseDirectory knowledgeBases,
            IndexLifecycleManager lifecycle,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken)) return Results.NotFound();
                var index = await lifecycle.CreateIndexAsync(knowledgeBaseId, request.EmbeddingSpecId,
                    request.Name, request.ActivateWhenReady, cancellationToken);
                return Results.Ok(index);
            }));

        indexRoutes.MapPost("/indexes/{indexId:guid}/rebuild", (
            Guid knowledgeBaseId,
            Guid indexId,
            IKnowledgeIndexStore store,
            IndexLifecycleManager lifecycle,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var index = await store.GetAsync(indexId, cancellationToken);
                if (index is null || index.KnowledgeBaseId != knowledgeBaseId) return Results.NotFound();
                return Results.Ok(await lifecycle.ScheduleRebuildAsync(indexId, cancellationToken));
            }));

        indexRoutes.MapPost("/indexes/{indexId:guid}/activate", (
            Guid knowledgeBaseId,
            Guid indexId,
            IKnowledgeIndexStore store,
            IndexLifecycleManager lifecycle,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var index = await store.GetAsync(indexId, cancellationToken);
                if (index is null || index.KnowledgeBaseId != knowledgeBaseId) return Results.NotFound();
                return Results.Ok(await lifecycle.ActivateAsync(indexId, cancellationToken));
            }));

        indexRoutes.MapPost("/indexes/{indexId:guid}/check", (
            Guid knowledgeBaseId,
            Guid indexId,
            IKnowledgeIndexStore store,
            IndexLifecycleManager lifecycle,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var index = await store.GetAsync(indexId, cancellationToken);
                if (index is null || index.KnowledgeBaseId != knowledgeBaseId) return Results.NotFound();
                var consistency = await lifecycle.CheckConsistencyAsync(indexId, cancellationToken);
                var quality = await lifecycle.EvaluateQualityAsync(indexId, cancellationToken);
                return Results.Ok(new { consistency, quality });
            }));

        indexRoutes.MapPost("/rollback", (
            Guid knowledgeBaseId,
            IKnowledgeBaseDirectory knowledgeBases,
            IndexLifecycleManager lifecycle,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken)) return Results.NotFound();
                var active = await lifecycle.RollbackAsync(knowledgeBaseId, cancellationToken);
                return active is null
                    ? Results.NotFound(new { error = "回滚窗口内没有可用的历史索引。" })
                    : Results.Ok(active);
            }));

        indexRoutes.MapGet("/retrieval-status", (
            Guid knowledgeBaseId,
            IKnowledgeBaseDirectory knowledgeBases,
            RetrievalRouter router,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken)) return Results.NotFound();
                return Results.Ok(await router.GetStatusAsync(knowledgeBaseId, cancellationToken));
            }));

        indexRoutes.MapGet("/changelog", async (
            Guid knowledgeBaseId,
            long? after,
            int? limit,
            IKnowledgeBaseDirectory knowledgeBases,
            IOutboxStore outbox,
            CancellationToken cancellationToken) =>
        {
            if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken)) return Results.NotFound();
            var events = await outbox.ReadKnowledgeBaseAfterAsync(
                knowledgeBaseId, Math.Max(0, after ?? 0),
                Math.Clamp(limit ?? 50, 1, 200), cancellationToken);
            return Results.Ok(events);
        });

        indexRoutes.MapGet("/model-binding", (
            Guid knowledgeBaseId,
            IKnowledgeBaseDirectory knowledgeBases,
            IModelBindingStore bindings,
            IEmbeddingSpecStore specs,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken)) return Results.NotFound();

                var specId = await bindings.GetEmbeddingSpecIdAsync(knowledgeBaseId, cancellationToken);
                var spec = specId is Guid id ? await specs.GetAsync(id, cancellationToken) : null;
                var summary = await bindings.GetSummaryBindingAsync(knowledgeBaseId, cancellationToken);
                return Results.Ok(new ModelBindingView(knowledgeBaseId, specId, spec?.Name,
                    summary is null ? null : ToView(summary)));
            }));

        indexRoutes.MapPut("/model-binding", (
            Guid knowledgeBaseId,
            UpdateModelBindingRequest request,
            IKnowledgeBaseDirectory knowledgeBases,
            IModelBindingStore bindings,
            IEmbeddingSpecStore specs,
            IKnowledgeIndexStore indexStore,
            IndexLifecycleManager lifecycle,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken)) return Results.NotFound();

                if (request.ClearEmbeddingSpec) await bindings.SetEmbeddingSpecIdAsync(knowledgeBaseId, null, cancellationToken);
                else if (request.EmbeddingSpecId is Guid specId)
                {
                    _ = await specs.GetAsync(specId, cancellationToken) ?? throw new EmbeddingSpecNotFoundException(specId);
                    await bindings.SetEmbeddingSpecIdAsync(knowledgeBaseId, specId, cancellationToken);

                    // 模型切换采用「新建阴影索引 → 全量重建 → 一致性与质量检查 → 原子切换」，
                    // 活动索引在此期间继续提供服务。
                    var active = await indexStore.GetActiveAsync(knowledgeBaseId, cancellationToken);
                    if (active?.EmbeddingSpecId != specId)
                    {
                        await lifecycle.CreateIndexAsync(knowledgeBaseId, specId, null,
                            request.ActivateWhenReady, cancellationToken);
                    }
                }

                if (request.Summary is not null)
                {
                    var existing = await bindings.GetSummaryBindingAsync(knowledgeBaseId, cancellationToken);
                    await bindings.SetSummaryBindingAsync(knowledgeBaseId, Merge(existing, request.Summary), cancellationToken);
                }

                var currentSpecId = await bindings.GetEmbeddingSpecIdAsync(knowledgeBaseId, cancellationToken);
                var currentSpec = currentSpecId is Guid id ? await specs.GetAsync(id, cancellationToken) : null;
                var summaryBinding = await bindings.GetSummaryBindingAsync(knowledgeBaseId, cancellationToken);
                return Results.Ok(new ModelBindingView(knowledgeBaseId, currentSpecId, currentSpec?.Name,
                    summaryBinding is null ? null : ToView(summaryBinding)));
            }));

        summaryRoutes.MapGet("/{documentId:guid}/summary", (
            Guid documentId,
            SummaryService summaries,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var summary = await summaries.GetLatestAsync(documentId, cancellationToken);
                return summary is null
                    ? Results.NotFound(new { error = "该资料还没有生成摘要。" })
                    : Results.Ok(summary);
            }));

        summaryRoutes.MapPost("/{documentId:guid}/summary/regenerate", (
            Guid documentId,
            SummaryService summaries,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await summaries.GenerateAsync(documentId, force: true, cancellationToken))));
    }

    private static EmbeddingSpecView ToView(EmbeddingSpec spec) => new(
        spec.Id, spec.Name, spec.Provider, spec.Model, spec.Dimensions, spec.MaxSequenceLength,
        spec.Revision, spec.Fingerprint, spec.Runtime.Endpoint,
        !string.IsNullOrWhiteSpace(spec.Runtime.ApiKey), spec.CreatedAt);

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static SummaryModelView ToView(SummaryModelBinding binding) => new(
        binding.UseCloudApi,
        binding.Local?.Endpoint, binding.Local?.Model, !string.IsNullOrWhiteSpace(binding.Local?.ApiKey),
        binding.Online?.Endpoint, binding.Online?.Model, !string.IsNullOrWhiteSpace(binding.Online?.ApiKey));

    private static SummaryModelBinding Merge(SummaryModelBinding? existing, SummaryModelInput input)
    {
        var useCloud = input.UseCloudApi ?? existing?.UseCloudApi ?? false;
        return new SummaryModelBinding(
            useCloud,
            MergeEndpoint(existing?.Local, input.LocalEndpoint, input.LocalModel, input.LocalApiKey),
            MergeEndpoint(existing?.Online, input.OnlineEndpoint, input.OnlineModel, input.OnlineApiKey));
    }

    private static ModelEndpointConfig? MergeEndpoint(
        ModelEndpointConfig? existing, string? endpoint, string? model, string? apiKey)
    {
        if (endpoint is null && model is null && apiKey is null) return existing;
        return new ModelEndpointConfig(
            endpoint ?? existing?.Endpoint ?? string.Empty,
            model ?? existing?.Model ?? string.Empty,
            apiKey ?? existing?.ApiKey ?? string.Empty);
    }
}
