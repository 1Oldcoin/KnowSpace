using MyFirstMafAgent.Api.Agents;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Chats;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 拒答链路里「资料里没有」这件事是怎么传给模型的：检索判定拒答 ⇒ 生成一句提示词约束
/// ⇒ 拼进 system prompt。这三段都必须可测，否则"拒答"只剩一个布尔值，模型照样会瞎编。
/// </summary>
public sealed class KnowledgeAbstainPromptTests
{
    private static RetrievedChunk Chunk(string fileName, string content) =>
        new(Guid.NewGuid(), fileName, content, 0.5d);

    private static KnowledgeRetrievalResult Retrieval(
        IReadOnlyList<RetrievedChunk> chunks,
        bool abstained,
        double topScore = 0d,
        double threshold = 0d,
        string? warning = null) =>
        new(chunks, Guid.NewGuid(), "bge-small-zh-v1.5", warning, ChunkMatchSources.Rerank, abstained, topScore, threshold);

    [Fact]
    public void Abstained_retrieval_produces_a_prompt_note()
    {
        var note = KnowledgeChatService.BuildReferenceNote(
            Retrieval(Array.Empty<RetrievedChunk>(), abstained: true, topScore: 0.31, threshold: 0.45, warning: "低于阈值"));

        Assert.NotNull(note);
        Assert.Contains("没有", note);
        Assert.Contains("不要", note);
    }

    [Fact]
    public void Normal_retrieval_produces_no_note()
    {
        var note = KnowledgeChatService.BuildReferenceNote(
            Retrieval(new[] { Chunk("a.md", "内容") }, abstained: false, topScore: 0.81));

        Assert.Null(note);
    }

    [Fact]
    public void The_note_is_appended_to_the_system_prompt_even_when_there_are_no_references()
    {
        // 拒答时参考列表是空的——这正是"没有资料可依"的时刻，提示词必须还在。
        var prompt = ToolCallingAgent.BuildSystemPrompt(
            Array.Empty<RetrievedChunk>(),
            "你是知识库助手。",
            "本轮检索已判定为“资料里没有相关内容”。");

        Assert.Contains("你是知识库助手。", prompt);
        Assert.Contains("资料里没有相关内容", prompt);
    }

    [Fact]
    public void The_note_is_appended_after_the_references()
    {
        var prompt = ToolCallingAgent.BuildSystemPrompt(
            new[] { Chunk("任务-租约与心跳.md", "领取任务的执行者必须每 30 秒汇报一次心跳。") },
            "你是知识库助手。",
            "本轮检索已判定为“资料里没有相关内容”。");

        var referenceIndex = prompt.IndexOf("30 秒", StringComparison.Ordinal);
        var noteIndex = prompt.IndexOf("资料里没有相关内容", StringComparison.Ordinal);

        Assert.True(referenceIndex >= 0, "参考资料必须出现在提示词里");
        Assert.True(noteIndex > referenceIndex, "提示必须在参考资料之后，否则会被资料淹没");
    }

    [Fact]
    public void Without_a_note_the_prompt_is_unchanged()
    {
        var withNull = ToolCallingAgent.BuildSystemPrompt(new[] { Chunk("a.md", "内容") }, "指令", null);
        var withBlank = ToolCallingAgent.BuildSystemPrompt(new[] { Chunk("a.md", "内容") }, "指令", "   ");

        Assert.Equal(withNull, withBlank);
        Assert.DoesNotContain("资料里没有", withNull);
    }
}
