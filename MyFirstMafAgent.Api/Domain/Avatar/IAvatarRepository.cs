namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>分身模块仓储端口（由基础设施层实现）。</summary>
public interface IAvatarRepository
{
    Task<AvatarProfile?> GetProfileAsync(CancellationToken cancellationToken);

    Task SaveProfileAsync(AvatarProfile profile, CancellationToken cancellationToken);

    // ---- 碎片素材 ----

    Task<IReadOnlyList<AvatarFragment>> ListFragmentsAsync(int limit, CancellationToken cancellationToken);

    Task<int> CountFragmentsAsync(CancellationToken cancellationToken);

    Task<int> CountUndigestedFragmentsAsync(CancellationToken cancellationToken);

    Task AddFragmentsAsync(IReadOnlyList<AvatarFragment> fragments, CancellationToken cancellationToken);

    /// <summary>写入原文并附带幂等元数据（source_hash + segment_index + extractor_version）。</summary>
    Task AddFragmentsAsync(
        IReadOnlyList<AvatarFragment> fragments,
        string? sourceHash,
        int startSegmentIndex,
        string extractorVersion,
        Guid? ingestionId,
        CancellationToken cancellationToken);

    Task<bool> DeleteFragmentAsync(Guid fragmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarFragment>> ListUndigestedFragmentsAsync(int limit, CancellationToken cancellationToken);

    Task MarkFragmentsDigestedAsync(IReadOnlyList<Guid> fragmentIds, CancellationToken cancellationToken);

    // ---- 整理后的记忆（AI 自动归纳，分身的检索单位） ----

    Task AddMemoriesAsync(IReadOnlyList<AvatarMemory> memories, CancellationToken cancellationToken);

    Task UpdateMemoryAsync(AvatarMemory memory, CancellationToken cancellationToken);

    Task UpdateMemoriesAsync(IReadOnlyList<AvatarMemory> memories, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarMemory>> ListMemoriesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>问答检索用：默认只返回当前有效状态；history 意图才包含历史/已被取代。</summary>
    Task<IReadOnlyList<AvatarMemory>> ListMemoriesForRetrievalAsync(
        int limit,
        bool includeHistorical,
        CancellationToken cancellationToken);


    Task<IReadOnlyList<AvatarMemory>> GetMemoriesAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken);

    Task<AvatarMemory?> GetMemoryAsync(Guid memoryId, CancellationToken cancellationToken);

    Task<bool> DeleteMemoryAsync(Guid memoryId, CancellationToken cancellationToken);

    Task<int> CountMemoriesAsync(CancellationToken cancellationToken);

    Task<int> CountPendingMemoryVectorsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarMemory>> ListMemoriesPendingVectorAsync(int limit, CancellationToken cancellationToken);

    Task MarkMemoryVectorStateAsync(Guid memoryId, string state, CancellationToken cancellationToken);

    Task TouchMemoriesUsedAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken);

    // ---- 证据 / 版本 ----

    Task AddMemoryEvidenceAsync(IReadOnlyList<AvatarMemoryEvidence> evidence, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarMemoryEvidence>> ListMemoryEvidenceAsync(Guid memoryId, CancellationToken cancellationToken);

    Task AddMemoryVersionsAsync(IReadOnlyList<AvatarMemoryVersion> versions, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarMemoryVersion>> ListMemoryVersionsAsync(Guid memoryId, CancellationToken cancellationToken);

    Task<int> CountMemoryVersionsAsync(Guid memoryId, CancellationToken cancellationToken);

    // ---- 投喂任务（异步整理） ----

    Task AddIngestionAsync(AvatarIngestion ingestion, CancellationToken cancellationToken);

    Task<AvatarIngestion?> GetIngestionAsync(Guid ingestionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarIngestion>> ListIngestionsAsync(int limit, CancellationToken cancellationToken);

    Task<AvatarIngestion?> ClaimNextIngestionAsync(CancellationToken cancellationToken);

    /// <summary>把进程崩溃后遗留的 running 任务重新排队。</summary>
    Task<int> RequeueStaleIngestionsAsync(TimeSpan staleAfter, CancellationToken cancellationToken);

    Task UpdateIngestionAsync(AvatarIngestion ingestion, CancellationToken cancellationToken);

    Task<AvatarIngestion?> FindIngestionByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarFragment>> GetFragmentsAsync(
        IReadOnlyList<Guid> fragmentIds,
        CancellationToken cancellationToken);

    Task<bool> FragmentExistsAsync(
        string sourceHash,
        int segmentIndex,
        string extractorVersion,
        CancellationToken cancellationToken);

    // ---- 人格洞察候选版本 ----

    Task AddInsightVersionAsync(AvatarInsightVersion version, CancellationToken cancellationToken);

    Task<AvatarInsightVersion?> GetInsightVersionAsync(Guid versionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarInsightVersion>> ListInsightVersionsAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken);

    Task UpdateInsightVersionAsync(AvatarInsightVersion version, CancellationToken cancellationToken);

    // ---- 回答反馈 ----

    Task AddFeedbackAsync(AvatarFeedback feedback, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarFeedback>> ListFeedbackAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, (int Like, int Unlike)>> GetMemoryFeedbackStatsAsync(
        IReadOnlyList<Guid> memoryIds,
        CancellationToken cancellationToken);

    // ---- 实体 ----

    Task<IReadOnlyList<AvatarEntity>> ListEntitiesAsync(CancellationToken cancellationToken);

    Task UpsertEntitiesAsync(IReadOnlyList<AvatarEntity> entities, CancellationToken cancellationToken);

    Task AddMemoryEntityLinksAsync(
        IReadOnlyList<AvatarMemoryEntityLink> links,
        CancellationToken cancellationToken);

    // ---- 固定评测集 ----

    Task AddEvalCaseAsync(AvatarEvalCase evalCase, CancellationToken cancellationToken);

    Task<AvatarEvalCase?> GetEvalCaseAsync(Guid caseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarEvalCase>> ListEvalCasesAsync(bool includeInactive, CancellationToken cancellationToken);

    Task<bool> DeleteEvalCaseAsync(Guid caseId, CancellationToken cancellationToken);

    Task AddEvalRunAsync(AvatarEvalRun run, CancellationToken cancellationToken);

    Task UpdateEvalRunAsync(AvatarEvalRun run, CancellationToken cancellationToken);

    Task<AvatarEvalRun?> GetEvalRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarEvalRun>> ListEvalRunsAsync(int limit, CancellationToken cancellationToken);

    Task AddEvalResultsAsync(IReadOnlyList<AvatarEvalResult> results, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarEvalResult>> ListEvalResultsAsync(Guid runId, CancellationToken cancellationToken);


    Task UpsertMemoryVectorsAsync(IReadOnlyList<AvatarMemoryVector> vectors, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarMemoryVectorMatch>> SearchMemoryVectorsAsync(
        float[] embedding,
        string embeddingFingerprint,
        int limit,
        CancellationToken cancellationToken);

    // ---- 投喂对话（聊天式投喂的原文与整理回执） ----

    Task AddFeedEntryAsync(AvatarFeedEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarFeedEntry>> ListFeedEntriesAsync(int limit, CancellationToken cancellationToken);

    Task ClearFeedAsync(CancellationToken cancellationToken);

    // ---- 人格洞察 ----

    Task<IReadOnlyList<AvatarInsight>> ListInsightsAsync(CancellationToken cancellationToken);

    Task SaveInsightsAsync(IReadOnlyList<AvatarInsight> insights, CancellationToken cancellationToken);

    Task ClearInsightsAsync(CancellationToken cancellationToken);

    // ---- 分身对话 ----

    Task<IReadOnlyList<AvatarChatSession>> ListChatSessionsAsync(CancellationToken cancellationToken);

    Task<AvatarChatSession?> GetChatSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task AddChatSessionAsync(AvatarChatSession session, CancellationToken cancellationToken);

    Task TouchChatSessionAsync(Guid sessionId, string? title, CancellationToken cancellationToken);

    Task AddChatMessageAsync(AvatarChatMessage message, CancellationToken cancellationToken);

    Task<long> AddChatMessageReturningIdAsync(AvatarChatMessage message, CancellationToken cancellationToken);

    Task<AvatarChatMessage?> GetChatMessageAsync(long messageId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarChatMessage>> ListChatMessagesAsync(Guid sessionId, int limit, CancellationToken cancellationToken);

    Task<bool> DeleteChatSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    // ---- 经历 / 行为 / 关系（补充输入） ----

    Task<IReadOnlyList<AvatarExperience>> ListExperiencesAsync(int limit, CancellationToken cancellationToken);

    Task AddExperienceAsync(AvatarExperience experience, CancellationToken cancellationToken);

    Task<bool> DeleteExperienceAsync(Guid experienceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarBehavior>> ListBehaviorsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken);

    Task AddBehaviorsAsync(IReadOnlyList<AvatarBehavior> behaviors, CancellationToken cancellationToken);

    Task<bool> DeleteBehaviorAsync(Guid behaviorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarRelationship>> ListRelationshipsAsync(CancellationToken cancellationToken);

    Task AddRelationshipAsync(AvatarRelationship relationship, CancellationToken cancellationToken);

    Task<bool> UpdateRelationshipAsync(AvatarRelationship relationship, CancellationToken cancellationToken);

    Task<bool> DeleteRelationshipAsync(Guid relationshipId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvatarSimulation>> ListSimulationsAsync(int limit, CancellationToken cancellationToken);

    Task<AvatarSimulation?> GetSimulationAsync(Guid simulationId, CancellationToken cancellationToken);

    Task AddSimulationAsync(AvatarSimulation simulation, CancellationToken cancellationToken);

    Task UpdateSimulationAsync(AvatarSimulation simulation, CancellationToken cancellationToken);

    Task<bool> DeleteSimulationAsync(Guid simulationId, CancellationToken cancellationToken);
}
