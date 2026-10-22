using System.Collections.Concurrent;
using System.Text.Json;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 行为自动记录器：把用户在应用内的活动（知识库问答、学习打卡等）写入分身素材流，
/// 作为“时刻记录”的一部分，后续由人格归纳统一吸收。同一来源在 30 分钟内只记录一次。
/// </summary>
public sealed class AvatarActivityRecorder(
    IAvatarRepository repository,
    AvatarMemoryIndexer indexer,
    ILogger<AvatarActivityRecorder> logger)
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRecorded = new(StringComparer.Ordinal);

    public async Task TryRecordAsync(
        string category,
        string description,
        int minutes,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            var key = $"{source}:{category}";
            var now = DateTimeOffset.UtcNow;

            if (_lastRecorded.TryGetValue(key, out var last) && now - last < Throttle) return;
            _lastRecorded[key] = now;

            var content = minutes > 0 ? $"{description}（约 {minutes} 分钟）" : description;
            var fragment = AvatarFragment.Create(
                new AvatarFragmentDraft(
                    KindFor(category),
                    $"{category} · {now:MM-dd}",
                    content,
                    now,
                    "chat",
                    ["自动记录"]),
                now,
                "chat");

            await repository.AddFragmentsAsync([fragment], cancellationToken);

            // 同时写入一条可直接检索的记忆卡，避免自动记录只能当“底稿”。
            var memory = AvatarMemory.Create(
                new AvatarMemoryDraft(
                    fragment.Id,
                    KindFor(category),
                    $"{category} · {now:MM-dd}",
                    content,
                    content,
                    now,
                    null,
                    ["自动记录"],
                    null,
                    2,
                    "auto"),
                now);
            await repository.AddMemoriesAsync([memory], cancellationToken);

            // 自动记录同样要有证据与版本，保证每条记忆都可追溯、可修订。
            var evidence = AvatarMemoryEvidence.Create(
                memory.Id,
                fragment.Id,
                content,
                0,
                content.Length,
                null,
                null,
                null,
                now,
                now);
            await repository.AddMemoryEvidenceAsync([evidence], cancellationToken);
            await repository.AddMemoryVersionsAsync(
                [AvatarMemoryVersion.Create(
                    memory.Id,
                    1,
                    JsonSerializer.Serialize(new
                    {
                        memory.Id,
                        memory.Title,
                        memory.Summary,
                        memory.MemoryType,
                        memory.LifecycleStatus,
                        memory.OccurredAt
                    }),
                    AvatarMemoryVersion.ChangeCreated,
                    null,
                    "自动记录",
                    now)],
                cancellationToken);

            await repository.MarkFragmentsDigestedAsync([fragment.Id], cancellationToken);
            await indexer.IndexAsync([memory], cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 记录失败不影响主流程。
            logger.LogWarning(exception, "Failed to auto-record avatar fragment {Category}", category);
        }
    }

    private static string KindFor(string category) => category switch
    {
        "学习" or "工作" or "创作" => "habit",
        "情绪" => "personality",
        "社交" or "关系" => "relationship",
        _ => "journal"
    };
}
