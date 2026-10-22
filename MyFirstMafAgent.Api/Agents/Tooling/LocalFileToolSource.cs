using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 把用户在 <see cref="AgentOptions.FileRoots"/> 中授权的外部目录（例如 D:\学习资料）
/// 暴露给聊天模型，使其可以直接浏览、检索并读取本机文件。
/// </summary>
public sealed class LocalFileToolSource(
    AgentFileAccess fileAccess,
    IOptions<AgentOptions> options) : IAgentToolSource
{
    public string Name => "files";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!fileAccess.HasRoots) return Task.FromResult<IReadOnlyList<AgentToolDefinition>>([]);

        var tools = new List<AgentToolDefinition>
        {
            Create("list_file_roots",
                "列出当前被授权访问的本机目录（名称、绝对路径、是否可写）。用户提到某个本机目录时，先调用它确认可用范围。",
                """
                {"type":"object","properties":{},"required":[]}
                """,
                (_, _) =>
                {
                    var roots = fileAccess.Roots.Select(root => new
                    {
                        name = root.Name,
                        path = root.Path,
                        writable = root.AllowWrite,
                        deletable = root.AllowDelete,
                        exists = Directory.Exists(root.Path)
                    }).ToList();
                    return Task.FromResult(JsonSerializer.Serialize(new { count = roots.Count, roots }, JsonOptions));
                }),

            Create("local_list_directory",
                "列出本机授权目录中的文件与子目录（含大小和修改时间）。path 可以是绝对路径，也可以是“根名称/子路径”。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"目录路径，留空表示第一个授权目录的根"},"pattern":{"type":"string","description":"文件名通配符，例如 *.md"},"recursive":{"type":"boolean","description":"是否递归子目录，默认 false"},"maxEntries":{"type":"integer","description":"最多返回多少条，默认 300"}},"required":[]}
                """,
                (arguments, token) =>
                {
                    var path = GetString(arguments, "path");
                    var pattern = GetString(arguments, "pattern");
                    var recursive = GetBool(arguments, "recursive");
                    var maxEntries = GetInt(arguments, "maxEntries");
                    var (entries, truncated) = fileAccess.Enumerate(path, pattern, recursive,
                        maxEntries is null ? null : Math.Clamp(maxEntries.Value, 1, 2_000), token);

                    var payload = new
                    {
                        scope = path ?? fileAccess.Roots[0].Path,
                        count = entries.Count,
                        truncated,
                        entries = entries.Select(entry => new
                        {
                            path = entry.Path,
                            type = entry.Type,
                            size = entry.Size,
                            modified = entry.Modified
                        })
                    };
                    return Task.FromResult(JsonSerializer.Serialize(payload, JsonOptions));
                }),

            Create("local_read_file",
                "读取本机授权目录中的文本文件，自动识别 UTF-8/UTF-16/GB18030 编码，支持按字符偏移分页。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"文件绝对路径或“根名称/子路径”"},"offset":{"type":"integer","description":"起始字符偏移，默认 0"},"maxChars":{"type":"integer","description":"最多读取字符数，默认 8000，最大 40000"}},"required":["path"]}
                """,
                (arguments, token) => fileAccess.ReadAsync(
                    GetRequiredString(arguments, "path"),
                    GetInt(arguments, "offset") ?? 0,
                    GetInt(arguments, "maxChars") ?? options.Value.MaxReadChars,
                    token)),

            Create("local_search_files",
                "按文件名通配符在本机授权目录中查找文件，返回绝对路径列表。适合先摸清一个目录里有哪些资料。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"限定目录，留空表示第一个授权目录"},"pattern":{"type":"string","description":"文件名通配符，例如 *.md"},"recursive":{"type":"boolean","description":"是否递归，默认 true"},"maxResults":{"type":"integer","description":"最多返回多少条，默认 60"}},"required":["pattern"]}
                """,
                (arguments, token) =>
                {
                    var (files, truncated) = fileAccess.FindFiles(
                        GetString(arguments, "path"),
                        GetRequiredString(arguments, "pattern"),
                        !arguments.TryGetProperty("recursive", out var recursive) || recursive.ValueKind != JsonValueKind.False,
                        GetInt(arguments, "maxResults"),
                        token);
                    return Task.FromResult(JsonSerializer.Serialize(new { count = files.Count, truncated, files }, JsonOptions));
                }),

            Create("local_grep",
                "在本机授权目录的文本文件中检索包含指定文本的行，返回文件路径、行号与命中行。适合在总结前定位关键内容。",
                """
                {"type":"object","properties":{"query":{"type":"string","description":"要检索的文本"},"path":{"type":"string","description":"限定目录，留空表示第一个授权目录"},"pattern":{"type":"string","description":"限定文件名通配符，例如 *.md"},"maxMatches":{"type":"integer","description":"最多返回多少条匹配，默认 60"}},"required":["query"]}
                """,
                (arguments, token) => Task.FromResult(fileAccess.Grep(
                    GetRequiredString(arguments, "query"),
                    GetString(arguments, "path"),
                    GetString(arguments, "pattern"),
                    GetInt(arguments, "maxMatches"),
                    token)))
        };

        if (fileAccess.Roots.Any(root => root.AllowWrite))
        {
            tools.Add(Create("local_write_file",
                "向已授权且开启写权限的本机目录写入文本文件。仅在用户明确要求保存或导出时使用；写入前应确认路径。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"目标文件绝对路径或“根名称/子路径”"},"content":{"type":"string","description":"要写入的完整文本"},"append":{"type":"boolean","description":"是否追加到文件末尾，默认覆盖"}},"required":["path","content"]}
                """,
                (arguments, token) => fileAccess.WriteAsync(
                    GetRequiredString(arguments, "path"),
                    GetRequiredString(arguments, "content"),
                    GetBool(arguments, "append"),
                    token)));
        }

        if (fileAccess.Roots.Any(root => root.AllowDelete))
        {
            tools.Add(Create("local_delete_file",
                "删除已授权且开启删除权限的本机目录中的文件或空目录。属于危险操作，仅在用户明确要求时使用。",
                """
                {"type":"object","properties":{"path":{"type":"string","description":"要删除的文件或空目录路径"}},"required":["path"]}
                """,
                (arguments, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(fileAccess.Delete(GetRequiredString(arguments, "path")));
                }));
        }

        return Task.FromResult<IReadOnlyList<AgentToolDefinition>>(tools);
    }

    private static AgentToolDefinition Create(
        string name,
        string description,
        string schema,
        Func<JsonElement, CancellationToken, Task<string>> handler) =>
        new(name, description, schema, handler, "files");

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetRequiredString(JsonElement arguments, string name) =>
        GetString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"缺少必填参数 {name}。");

    private static bool GetBool(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

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
