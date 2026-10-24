using System.Text.Json;
using System.Text.Json.Nodes;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Plugin.SamplePlatform;

/// <summary>
/// 假平台插件：用来验证插件协议的完整链路（发现 → 握手 → 健康检查 → 调用 → 摄取）。
/// 真实平台插件应替换 <see cref="InvokeAsync"/> 中的假数据访问。
/// </summary>
internal sealed class SamplePlatformPlugin : PluginBase
{
    private static readonly PlatformIngestionItem[] Notes =
    [
        new()
        {
            Source = "com.example.sample-platform",
            ExternalId = "note-1001",
            Title = "欢迎使用插件系统",
            Type = PlatformItemTypes.Note,
            Content = "这是一条来自示例平台的笔记，用于验证插件能否把外部内容交给核心摄取流程。",
            UpdatedAt = DateTimeOffset.Parse("2026-10-20T09:00:00+08:00"),
            Metadata = new Dictionary<string, string>
            {
                ["author"] = "示例用户",
                ["url"] = "https://example.invalid/notes/1001"
            }
        },
        new()
        {
            Source = "com.example.sample-platform",
            ExternalId = "note-1002",
            Title = "插件协议要点",
            Type = PlatformItemTypes.Article,
            Content = "插件以独立进程运行，通过版本化 JSON-RPC over stdio 与核心通信；写入知识库必须调用核心摄取用例。",
            UpdatedAt = DateTimeOffset.Parse("2026-10-21T15:30:00+08:00"),
            Metadata = new Dictionary<string, string>
            {
                ["author"] = "示例用户",
                ["url"] = "https://example.invalid/notes/1002"
            }
        }
    ];

    private string _workspace = "demo";

    /// <summary>平台侧的“版本标签”：改它就能模拟笔记内容被编辑，用于验证去重与更新。</summary>
    private string? _contentVersion;

    /// <summary>要模拟撤回的 externalId，用于验证“平台删除 → 核心删资料”。</summary>
    private string? _retractNote;

    /// <summary>
    /// 告诉核心“多久之后可以再同步一次”。设为 &gt; 0 就能看到定时同步自动排任务，
    /// 这是验证任务系统与插件联动的最短路径。
    /// </summary>
    private int _syncIntervalSeconds;

    public override string Id => "com.example.sample-platform";

    /// <summary>
    /// 版本取自程序集本身，与 manifest.version 必须一致（握手时会校验）。
    /// 升级时用 <c>dotnet build -p:Version=1.0.1</c> 构建即可，不需要改源码。
    /// </summary>
    public override string Version =>
        typeof(SamplePlatformPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public override IReadOnlyList<PluginCapability> Capabilities { get; } =
    [
        new PluginCapability
        {
            Id = "platform.notes.sync",
            Kind = PluginCapabilityKinds.PlatformConnector,
            Name = "同步示例笔记",
            Description = "返回两条假平台笔记，用于验证摄取链路。",
            Permissions = [PluginPermissions.Network],
            SideEffect = PluginSideEffectLevels.Read
        },
        new PluginCapability
        {
            Id = "notes.search",
            Kind = PluginCapabilityKinds.AgentTool,
            Name = "示例笔记搜索",
            Description = "在假平台笔记中按关键词检索标题。",
            Parameters = """
                {"type":"object","properties":{"query":{"type":"string","description":"关键词"},"maxResults":{"type":"integer","minimum":1,"maximum":20,"default":5}},"required":["query"],"additionalProperties":false}
                """,
            Permissions = [PluginPermissions.Network],
            SideEffect = PluginSideEffectLevels.Read
        },
        new PluginCapability
        {
            Id = "notes.slow-echo",
            Kind = PluginCapabilityKinds.AgentTool,
            Name = "慢回显（升级排空验证用）",
            Description = "等一会儿再返回，用于验证升级时的排空行为。",
            Parameters = """
                {"type":"object","properties":{"delayMs":{"type":"integer","minimum":0,"maximum":10000,"default":2000}},"required":[],"additionalProperties":false}
                """,
            Permissions = [],
            SideEffect = PluginSideEffectLevels.Read
        }
    ];

    public override Task ConfigureAsync(JsonNode? config, CancellationToken cancellationToken)
    {
        var workspace = config?["workspace"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(workspace)) _workspace = workspace;

        // 未提供时重置，方便在同一个进程里反复切换场景。
        _contentVersion = config?["contentVersion"]?.GetValue<string>();
        _retractNote = config?["retractNote"]?.GetValue<string>();
        _syncIntervalSeconds = config?["syncIntervalSeconds"]?.GetValue<int>() ?? 0;

        Log($"configured workspace={_workspace} contentVersion={_contentVersion ?? "-"} retractNote={_retractNote ?? "-"} syncInterval={_syncIntervalSeconds}s");
        return Task.CompletedTask;
    }

    public override Task<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(PluginHealth.Healthy($"workspace={_workspace}"));

    public override Task<JsonNode?> InvokeAsync(
        string capabilityId,
        JsonNode? arguments,
        CancellationToken cancellationToken)
    {
        return capabilityId switch
        {
            "platform.notes.sync" => Task.FromResult<JsonNode?>(SyncNotes(arguments)),
            "notes.search" => Task.FromResult<JsonNode?>(SearchNotes(arguments, cancellationToken)),
            "notes.slow-echo" => SlowEchoAsync(arguments, cancellationToken),
            _ => throw new PluginRpcException(
                PluginProtocol.ErrorUnknownCapability,
                $"'{capabilityId}' is not implemented by the sample plugin")
        };
    }

    /// <summary>
    /// 等一会儿再返回。核心在升级前会先排空：这个能力就是用来证明
    /// “在途调用会跑完，而不是被硬断”的。
    /// </summary>
    private static async Task<JsonNode?> SlowEchoAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var delayMs = Math.Clamp(arguments?["delayMs"]?.GetValue<int>() ?? 2000, 0, 10_000);
        var startedAt = DateTimeOffset.UtcNow;

        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToNode(new
        {
            waitedMs = delayMs,
            elapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            completedAt = DateTimeOffset.UtcNow
        });
    }

    private JsonNode? SyncNotes(JsonNode? arguments)
    {
        // 两页分页：无游标返回第一页并给出 "done"；给了 "done" 就表示没有更多。
        // 特殊游标 "restart" 强制从头同步，便于演示“重复同步不重复建资料”。
        var cursor = arguments?["cursor"]?.GetValue<string>();
        if (string.Equals(cursor, "restart", StringComparison.OrdinalIgnoreCase)) cursor = null;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            Log("sync completed (no more pages)");
            return new JsonObject
            {
                ["items"] = new JsonArray(),
                ["nextCursor"] = null
            };
        }

        var items = new JsonArray();
        foreach (var note in Notes)
        {
            var content = string.IsNullOrWhiteSpace(_contentVersion)
                ? note.Content
                : $"{note.Content}\n\n（平台版本 {_contentVersion}）";

            var retracted = string.Equals(_retractNote, note.ExternalId, StringComparison.Ordinal);

            items.Add(JsonSerializer.SerializeToNode(new
            {
                source = note.Source,
                externalId = note.ExternalId,
                title = note.Title,
                type = note.Type,
                content,
                updatedAt = note.UpdatedAt,
                contentHash = PlatformIngestionItem.ComputeContentHash(content),
                retracted,
                metadata = note.Metadata
            }));
        }

        var nextCursor = "done";
        Log($"sync returning {items.Count} item(s) (contentVersion={_contentVersion ?? "-"}, retract={_retractNote ?? "-"})");
        return new JsonObject
        {
            ["items"] = items,
            ["nextCursor"] = nextCursor,
            ["nextSyncAfterSeconds"] = _syncIntervalSeconds > 0 ? _syncIntervalSeconds : null
        };
    }

    private static JsonNode? SearchNotes(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var query = arguments?["query"]?.GetValue<string>() ?? string.Empty;
        var maxResults = arguments?["maxResults"]?.GetValue<int>() ?? 5;
        maxResults = Math.Clamp(maxResults, 1, 20);

        var matches = new JsonArray();
        foreach (var note in Notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(query) || (note.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                matches.Add(JsonSerializer.SerializeToNode(new
                {
                    externalId = note.ExternalId,
                    title = note.Title,
                    url = note.Metadata.TryGetValue("url", out var url) ? url : null
                }));
            }
        }

        if (matches.Count > maxResults)
        {
            var trimmed = new JsonArray();
            for (var index = 0; index < maxResults; index++)
                trimmed.Add(matches[index]!.DeepClone());
            matches = trimmed;
        }

        return new JsonObject
        {
            ["query"] = query,
            ["count"] = matches.Count,
            ["items"] = matches
        };
    }
}
