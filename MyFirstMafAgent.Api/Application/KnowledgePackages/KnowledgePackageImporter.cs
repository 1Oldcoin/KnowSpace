using System.IO.Compression;
using System.Security.Cryptography;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Notes;
using MyFirstMafAgent.Api.Domain.Topics;

namespace MyFirstMafAgent.Api.Application.KnowledgePackages;

/// <summary>
/// 把 <c>.zjpack</c> 导入成一个**新的**知识库。
///
/// <para>
/// 刻意只支持“导入为新知识库”，不支持合并到已有知识库：合并要处理同名资料、同名专题、
/// 同名笔记的冲突策略，每一种都需要用户决策。一期先把「备份迁移 / 分享别人的库」打通，
/// 冲突留给用户自己整理。
/// </para>
///
/// <para>
/// 所有 ID 都会重新生成，包里的旧 ID 只用于在包内建立映射（笔记引用 → 新资料）。
/// 导入后资料处于排队状态，由既有的解析队列重新切片建索引，因此导入完成 ≠ 可检索。
/// </para>
/// </summary>
public sealed class KnowledgePackageImporter(
    IKnowledgePackageStore packageStore,
    LocalDocumentStore documentStore,
    ITopicRepository topicRepository,
    INoteRepository noteRepository,
    ILogger<KnowledgePackageImporter> logger)
{
    /// <summary>包上限，防止一个构造出来的大包把磁盘吃满。</summary>
    public const long MaxPackageBytes = 4L * 1024 * 1024 * 1024;

    private const int MaxDocuments = 5_000;
    private const int MaxNotes = 20_000;

    public async Task<KnowledgePackageImportResult> ImportAsync(Stream package, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"zjpack-import-{Guid.NewGuid():N}.zip");
        var warnings = new List<string>();

        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await package.CopyToAsync(temp, cancellationToken);
                if (temp.Length == 0) throw new KnowledgePackageException("导入文件是空的。");
                if (temp.Length > MaxPackageBytes)
                    throw new KnowledgePackageException($"导入包超过 {MaxPackageBytes / (1024 * 1024)} MB 上限。");
            }

            await using var file = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);

            ZipArchive archive;
            try
            {
                archive = new ZipArchive(file, ZipArchiveMode.Read);
            }
            catch (InvalidDataException)
            {
                // 用户很可能选错了文件（比如把导出的 md 当包上传）。这属于输入问题，不是服务器故障。
                throw new KnowledgePackageException("导入包不是有效的 zip 文件，可能选错了文件。");
            }

            using (archive)
            {
                var manifest = await KnowledgePackageReader.ReadManifestAsync(archive, cancellationToken);
                return await ImportManifestAsync(archive, manifest, warnings, cancellationToken);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // 临时文件删不掉不应影响已经成功的导入结果。
            }
        }
    }

    private async Task<KnowledgePackageImportResult> ImportManifestAsync(
        ZipArchive archive,
        KnowledgePackageManifest manifest,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var knowledgeBaseName = KnowledgePackageReader.ResolveKnowledgeBaseName(
            manifest.KnowledgeBase.Name,
            (await packageStore.ListKnowledgeBasesAsync(cancellationToken)).Select(item => item.Name).ToList());
        var knowledgeBase = await packageStore.CreateKnowledgeBaseAsync(knowledgeBaseName, cancellationToken)
            ?? throw new KnowledgePackageException($"无法创建知识库“{knowledgeBaseName}”。");

        // 1) 专题 → 2) 资料 → 3) 笔记，全部放在 try 里：任何一步抛错都要把已写入的东西收回去。
        //
        // 这里不追求"事务"而是"补偿"：导入过程要往文件系统写原件，事务包不住文件。
        // 记账（记下每个已落库/落盘的 ID）然后逆序删，能得到同样的结果——要么全在，要么全清。
        var createdTopicIds = new List<Guid>();
        var createdDocuments = new List<(Guid Id, string StoragePath)>();
        var createdNoteIds = new List<Guid>();

        try
        {
            // 条目名可能是 `files\0001.md`（Windows 自带压缩/第三方打包器），
            // 清单里写的是 `files/0001.md`。不归一化就会全部 miss。
            var entryIndex = KnowledgePackageReader.BuildEntryIndex(archive);

            // 1) 专题：包内 ID → 新专题 ID；同名专题只建一次。
            var topicIdMap = new Dictionary<Guid, Guid>();
            var createdTopics = 0;
            var usedTopicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var topic in manifest.Topics)
            {
                string name;
                try
                {
                    name = Topic.NormalizeName(topic.Name);
                }
                catch (TopicValidationException exception)
                {
                    warnings.Add($"跳过专题“{topic.Name}”：{exception.Message}");
                    continue;
                }

                if (!usedTopicNames.Add(name))
                {
                    warnings.Add($"跳过重复专题“{name}”。");
                    continue;
                }

                var entity = new Topic(Guid.NewGuid(), knowledgeBase.Id, name, Topic.NormalizeColor(topic.Color),
                    DateTimeOffset.UtcNow, 0);
                await topicRepository.AddAsync(entity, cancellationToken);
                createdTopicIds.Add(entity.Id);
                topicIdMap[topic.Id] = entity.Id;
                createdTopics++;
            }

            // 2) 资料：写文件 → 建记录（排队解析）→ 归入专题 → 恢复摘要。
            var documentIdMap = new Dictionary<Guid, Guid>();
            var createdDocumentCount = 0;
            var documentLimit = Math.Min(manifest.Documents.Count, MaxDocuments);
            if (manifest.Documents.Count > MaxDocuments)
                warnings.Add($"资料数量超过 {MaxDocuments} 条，仅导入前 {MaxDocuments} 条。");

            for (var index = 0; index < documentLimit; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = manifest.Documents[index];
                var entry = entryIndex.TryGetValue(
                    KnowledgePackageReader.NormalizeEntryPath(document.File), out var resolved)
                    ? resolved
                    : null;
                if (entry is null)
                {
                    warnings.Add($"跳过《{document.FileName}》：包内缺少文件 {document.File}。");
                    continue;
                }

                try
                {
                    // 分两趟读 zip 条目（包内文件是不压缩存放的，重读代价很低）：
                    // 第一趟算哈希做校验，第二趟直接落盘，避免把整份文件塞进内存。
                    var actualHash = await KnowledgePackageReader.ComputeEntryHashAsync(entry, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(document.Sha256)
                        && !string.Equals(document.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"跳过《{document.FileName}》：文件校验失败（SHA-256 不一致）。");
                        continue;
                    }

                    StoredFile stored;
                    await using (var entryStream = entry.Open())
                    {
                        stored = await documentStore.SaveStreamAsync(
                            knowledgeBase.Id, document.FileName, document.ContentType, entry.Length, entryStream,
                            cancellationToken);
                    }

                    await packageStore.AddQueuedDocumentAsync(
                        stored.Id, knowledgeBase.Id, stored.FileName, stored.Extension, stored.ContentType,
                        stored.SizeBytes, stored.Path, cancellationToken);

                    // 文件已落盘、行已入库，从这一刻起就必须能被回滚。
                    createdDocuments.Add((stored.Id, stored.Path));
                    documentIdMap[document.Id] = stored.Id;
                    createdDocumentCount++;

                    if (document.TopicId is { } oldTopicId && topicIdMap.TryGetValue(oldTopicId, out var newTopicId))
                        await topicRepository.AssignDocumentAsync(stored.Id, newTopicId, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(document.Summary))
                        await packageStore.SetDocumentSummaryAsync(stored.Id, document.Summary, cancellationToken);
                }
                catch (DocumentUploadException exception)
                {
                    warnings.Add($"跳过《{document.FileName}》：{exception.Message}");
                }
            }

            // 3) 笔记：重新生成 ID，把引用里的资料 ID 换成新 ID；引用了未导入资料的条目降级为纯文本笔记。
            var createdNotes = 0;
            var noteLimit = Math.Min(manifest.Notes.Count, MaxNotes);
            if (manifest.Notes.Count > MaxNotes)
                warnings.Add($"笔记数量超过 {MaxNotes} 条，仅导入前 {MaxNotes} 条。");

            foreach (var packageNote in manifest.Notes.Take(noteLimit))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sources = new List<NoteSource>();
                foreach (var source in packageNote.Sources)
                {
                    if (documentIdMap.TryGetValue(source.DocumentId, out var newDocumentId))
                    {
                        sources.Add(new NoteSource(newDocumentId, source.FileName, source.ChunkIndex, source.Similarity, source.Snippet));
                    }
                }

                if (sources.Count == 0 && packageNote.Sources.Count > 0)
                    warnings.Add($"笔记《{packageNote.Title}》的引用资料未被导入，已保留正文并移除引用。");

                var mappedTopicId = packageNote.TopicId is { } oldTopic && topicIdMap.TryGetValue(oldTopic, out var newTopic)
                    ? newTopic
                    : (Guid?)null;

                try
                {
                    // 先用 Create 走一遍校验与归一化，再用 Restore 保留原始时间与置顶状态。
                    var validated = Note.Create(
                        knowledgeBase.Id,
                        packageNote.Title,
                        packageNote.Content,
                        packageNote.Category,
                        packageNote.Color,
                        packageNote.Tags,
                        null,
                        DateTimeOffset.UtcNow,
                        mappedTopicId,
                        sources);

                    var note = Note.Restore(
                        Guid.NewGuid(),
                        knowledgeBase.Id,
                        validated.Title,
                        validated.Content,
                        validated.Category,
                        validated.Color,
                        validated.Tags,
                        packageNote.IsPinned,
                        validated.SourceDocumentId,
                        packageNote.CreatedAt == default ? DateTimeOffset.UtcNow : packageNote.CreatedAt,
                        packageNote.UpdatedAt == default ? DateTimeOffset.UtcNow : packageNote.UpdatedAt,
                        validated.TopicId,
                        validated.Sources);

                    await noteRepository.AddAsync(note, cancellationToken);
                    createdNoteIds.Add(note.Id);
                    createdNotes++;
                }
                catch (NoteValidationException exception)
                {
                    warnings.Add($"跳过笔记《{packageNote.Title}》：{exception.Message}");
                }
            }

            // 一条都没导入成功就不应该留下一个空知识库：这基本意味着清单和包对不上，
            // 抛出去交给上面的补偿逻辑把壳子连同专题一起删掉。
            if (createdDocumentCount == 0 && createdNotes == 0)
            {
                var reason = warnings.Count > 0 ? warnings[0] : "包里既没有资料也没有笔记";
                throw new KnowledgePackageException($"导入包没有可导入的内容：{reason}");
            }

            logger.LogInformation(
                "Knowledge package imported: kb={KnowledgeBaseId} documents={Documents} notes={Notes} topics={Topics} warnings={Warnings}",
                knowledgeBase.Id, createdDocumentCount, createdNotes, createdTopics, warnings.Count);

            return new KnowledgePackageImportResult(
                knowledgeBase.Id, knowledgeBase.Name, createdDocumentCount, createdNotes, createdTopics, warnings);
        }
        catch (Exception exception)
        {
            await RollbackAsync(knowledgeBase.Id, createdNoteIds, createdTopicIds, createdDocuments);
            logger.LogError(exception,
                "Knowledge package import failed and was rolled back: kb={KnowledgeBaseId} notes={Notes} topics={Topics} documents={Documents}",
                knowledgeBase.Id, createdNoteIds.Count, createdTopicIds.Count, createdDocuments.Count);

            if (exception is OperationCanceledException) throw;

            throw new KnowledgePackageException(
                $"导入失败，已回滚写入的 {createdDocuments.Count} 份资料 / {createdNoteIds.Count} 篇笔记 / " +
                $"{createdTopicIds.Count} 个专题（原因：{exception.Message}）。", exception);
        }
    }

    /// <summary>
    /// 逆序补偿删除：笔记 → 专题 → 资料（行 + 落盘文件）→ 知识库。
    ///
    /// <para>
    /// 用 <see cref="CancellationToken.None"/>：请求可能已经被用户取消，但半成品数据不能留在库里。
    /// 单条删除失败只记日志，不能让一次回滚失败掩盖真正的原因。
    /// </para>
    /// </summary>
    private async Task RollbackAsync(
        Guid knowledgeBaseId,
        IReadOnlyList<Guid> noteIds,
        IReadOnlyList<Guid> topicIds,
        IReadOnlyList<(Guid Id, string StoragePath)> documents)
    {
        var cancellationToken = CancellationToken.None;

        foreach (var noteId in noteIds)
        {
            try
            {
                await noteRepository.DeleteAsync(noteId, knowledgeBaseId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Rollback: failed to delete note {NoteId}", noteId);
            }
        }

        foreach (var topicId in topicIds)
        {
            try
            {
                await topicRepository.DeleteAsync(knowledgeBaseId, topicId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Rollback: failed to delete topic {TopicId}", topicId);
            }
        }

        foreach (var document in documents)
        {
            try
            {
                await packageStore.DeleteDocumentAsync(document.Id, cancellationToken);
                documentStore.DeleteStoredFile(document.StoragePath);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Rollback: failed to delete document {DocumentId}", document.Id);
            }
        }

        try
        {
            await packageStore.DeleteKnowledgeBaseAsync(knowledgeBaseId, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Rollback: failed to delete knowledge base {KnowledgeBaseId}", knowledgeBaseId);
        }
    }
}
