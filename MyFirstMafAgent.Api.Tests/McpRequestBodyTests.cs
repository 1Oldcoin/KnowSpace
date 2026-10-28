using System.Text;
using Microsoft.AspNetCore.Http;
using MyFirstMafAgent.Api.Hosting;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 网关的请求体解析：这里的每一条都是"fail-closed"的具体落点。
///
/// <para>
/// 旧实现把"解析不了"当作"不需要判定"并放行，于是非 JSON、超大、批量请求都能绕过
/// 能力与知识库范围检查。现在的约定是：只有 <see cref="McpBodyStatus.Json"/> 才继续。
/// </para>
/// </summary>
public sealed class McpRequestBodyTests
{
    private const int MaxBytes = 1024 * 1024;

    private static HttpContext Post(string body, string contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    [Fact]
    public async Task Non_post_requests_are_not_parsed()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;

        var peek = await McpRequestPeekReader.TryPeekAsync(context, MaxBytes, CancellationToken.None);

        Assert.Equal(McpBodyStatus.None, peek.Status);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Wrong_content_type_is_rejected(string contentType)
    {
        var peek = await McpRequestPeekReader.TryPeekAsync(
            Post("{\"jsonrpc\":\"2.0\"}", contentType), MaxBytes, CancellationToken.None);

        Assert.Equal(McpBodyStatus.NotJson, peek.Status);
    }

    [Fact]
    public async Task Malformed_json_is_rejected()
    {
        var peek = await McpRequestPeekReader.TryPeekAsync(Post("{ not json"), MaxBytes, CancellationToken.None);

        Assert.Equal(McpBodyStatus.Malformed, peek.Status);
    }

    [Fact]
    public async Task Oversized_body_is_rejected_before_parsing()
    {
        var context = Post(new string('x', 4096));
        context.Request.ContentLength = 4096;

        var peek = await McpRequestPeekReader.TryPeekAsync(context, maxBytes: 128, CancellationToken.None);

        Assert.Equal(McpBodyStatus.TooLarge, peek.Status);
    }

    [Fact]
    public async Task Duplicate_property_names_are_rejected()
    {
        // 网关与 SDK 两套解析器对重复键的取舍可能不同，这种请求不该被接受。
        var peek = await McpRequestPeekReader.TryPeekAsync(
            Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"x\"," +
                 "\"arguments\":{\"knowledgeBaseId\":\"11111111-1111-1111-1111-111111111111\"," +
                 "\"knowledgeBaseId\":\"22222222-2222-2222-2222-222222222222\"}}}"),
            MaxBytes, CancellationToken.None);

        Assert.Equal(McpBodyStatus.Malformed, peek.Status);
        Assert.Contains("重复属性名", peek.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Case_variant_property_names_are_rejected()
    {
        var peek = await McpRequestPeekReader.TryPeekAsync(
            Post("{\"method\":\"tools/call\",\"Method\":\"tools/call\"}"), MaxBytes, CancellationToken.None);

        Assert.Equal(McpBodyStatus.Malformed, peek.Status);
    }

    [Fact]
    public async Task Batch_request_is_flagged()
    {
        var peek = await McpRequestPeekReader.TryPeekAsync(
            Post("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\",\"params\":{}}," +
                 "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"list_documents\",\"arguments\":{}}}]"),
            MaxBytes, CancellationToken.None);

        Assert.Equal(McpBodyStatus.Json, peek.Status);
        Assert.True(peek.IsBatch);
        Assert.Equal(2, peek.Calls.Count);
        // 批量里出现第二种方法时绝不能只按第一条判"这是探测请求"。
        Assert.False(peek.IsSingleServerDiscover);
    }

    [Fact]
    public async Task Single_discover_is_detected()
    {
        var peek = await McpRequestPeekReader.TryPeekAsync(
            Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\",\"params\":{\"_meta\":{" +
                 "\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}"),
            MaxBytes, CancellationToken.None);

        Assert.True(peek.IsSingleServerDiscover);
        Assert.Equal("modern", peek.Era);
        Assert.Equal("2026-07-28", peek.ProtocolVersion);
    }

    [Fact]
    public async Task Tool_call_arguments_are_extracted_for_gating()
    {
        var knowledgeBaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var documentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var peek = await McpRequestPeekReader.TryPeekAsync(
            Post("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"get_document_content\"," +
                 "\"arguments\":{\"documentId\":\"" + documentId.ToString("D") +
                 "\",\"knowledgeBaseId\":\"" + knowledgeBaseId.ToString("D") + "\"}}}"),
            MaxBytes, CancellationToken.None);

        var call = Assert.Single(peek.Calls);
        Assert.Equal("tools/call", call.Method);
        Assert.Equal("get_document_content", call.Tool);
        Assert.Equal(knowledgeBaseId, call.KnowledgeBaseId);
        Assert.Equal(documentId, call.DocumentId);
        Assert.NotNull(call.Id);
    }

    [Fact]
    public async Task Body_is_rewound_for_the_actual_handler()
    {
        var context = Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");

        await McpRequestPeekReader.TryPeekAsync(context, MaxBytes, CancellationToken.None);

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        Assert.Contains("tools/list", await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }
}
