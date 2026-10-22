using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>把本地文件沙箱能力暴露为聊天模型可调用的工具。</summary>
public sealed class WorkspaceToolSource(
    AgentWorkspace workspace,
    IOptions<AgentOptions> options) : IAgentToolSource
{
    public string Name => "workspace";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(AgentToolContext context, CancellationToken cancellationToken)
    {
        var tools = new List<AgentToolDefinition>
        {
            Create("workspace_list_files",
                "列出智能体本地工作目录中的文件。用于确认存在哪些文件、文件大小与修改时间。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"相对工作目录的子目录，留空表示根目录"},"pattern":{"type":"string","description":"文件通配符，例如 *.md"},"recursive":{"type":"boolean","description":"是否递归子目录，默认 false"}},"required":[]}
                """,
                (arguments, token) =>
                {
                    var path = GetString(arguments, "path");
                    var pattern = GetString(arguments, "pattern");
                    var recursive = arguments.TryGetProperty("recursive", out var value) && value.ValueKind == JsonValueKind.True;
                    var root = workspace.Root;
                    var files = workspace.EnumerateFiles(path, pattern, recursive)
                        .Select(file => new
                        {
                            path = workspace.ToRelative(file.FullName),
                            size = file.Length,
                            modified = file.LastWriteTimeUtc.ToString("O")
                        })
                        .ToList();
                    return Task.FromResult(JsonSerializer.Serialize(new { root, count = files.Count, files }, JsonOptions));
                }),

            Create("workspace_read_file",
                "读取智能体本地工作目录中的文本文件内容，支持按字符偏移分页。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"相对工作目录的文件路径"},"offset":{"type":"integer","description":"起始字符偏移，默认 0"},"maxChars":{"type":"integer","description":"最多读取多少个字符，默认 4000"}},"required":["path"]}
                """,
                (arguments, token) => workspace.ReadAsync(
                    GetRequiredString(arguments, "path"),
                    GetInt(arguments, "offset") ?? 0,
                    options.Value.MaxReadChars,
                    token))
        };

        if (options.Value.AllowWorkspaceWrite)
        {
            tools.Add(Create("workspace_write_file",
                "把文本内容写入智能体本地工作目录（会自动创建子目录）。append=true 时追加到文件末尾，否则覆盖。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"相对工作目录的文件路径"},"content":{"type":"string","description":"要写入的完整文本"},"append":{"type":"boolean","description":"是否追加，默认 false"}},"required":["path","content"]}
                """,
                (arguments, token) =>
                {
                    var content = GetRequiredString(arguments, "content");
                    if (content.Length > options.Value.MaxWriteChars)
                        throw new InvalidOperationException($"写入内容超过上限 {options.Value.MaxWriteChars} 字符。");
                    var append = arguments.TryGetProperty("append", out var value) && value.ValueKind == JsonValueKind.True;
                    return workspace.WriteAsync(GetRequiredString(arguments, "path"), content, append, token);
                }));

            tools.Add(Create("workspace_delete_file",
                "删除智能体本地工作目录中的文件或空目录。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"相对工作目录的文件或空目录路径"}},"required":["path"]}
                """,
                (arguments, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(workspace.Delete(GetRequiredString(arguments, "path")));
                }));
        }

        tools.Add(Create("workspace_search",
            "在当前工作目录的文本文件中检索包含指定文本的行，返回文件路径与行号。",
            """
            {"type":"object","properties":{"query":{"type":"string","description":"要检索的文本"},"path":{"type":"string","description":"限定检索的子目录，可留空"},"pattern":{"type":"string","description":"限定文件通配符，例如 *.md"},"maxMatches":{"type":"integer","description":"最多返回多少条，默认 50"}},"required":["query"]}
            """,
            (arguments, token) => Task.FromResult(workspace.Search(
                GetRequiredString(arguments, "query"),
                GetString(arguments, "path"),
                GetString(arguments, "pattern"),
                GetInt(arguments, "maxMatches") ?? 50,
                token))));

        return Task.FromResult<IReadOnlyList<AgentToolDefinition>>(tools);
    }

    private static AgentToolDefinition Create(
        string name,
        string description,
        string schema,
        Func<JsonElement, CancellationToken, Task<string>> handler) =>
        new(name, description, schema, handler, "workspace");

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetRequiredString(JsonElement arguments, string name) =>
        GetString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"缺少必填参数 {name}。");

    private static int? GetInt(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}
