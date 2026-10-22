using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using MyFirstMafAgent.Api.Agents.Tooling;

namespace MyFirstMafAgent.Api.Mcp;

/// <summary>
/// 把“授权本机目录”的文件访问能力通过 MCP 暴露给其他大模型/智能体。
/// 权限完全由 <c>Agent:FileRoots</c> 决定，默认只读。
/// </summary>
[McpServerToolType]
public sealed class LocalFileMcpTools(AgentFileAccess fileAccess)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "list_file_roots")]
    [Description("列出本机被授权访问的目录（名称、绝对路径、读写权限）。")]
    public Task<string> ListFileRootsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = fileAccess.Roots.Select(root => new
        {
            name = root.Name,
            path = root.Path,
            writable = root.AllowWrite,
            deletable = root.AllowDelete,
            exists = Directory.Exists(root.Path)
        }).ToList();
        return Task.FromResult(JsonSerializer.Serialize(new { count = roots.Count, roots }, JsonOptions));
    }

    [McpServerTool(Name = "local_list_directory")]
    [Description("列出本机授权目录中的文件与子目录（含大小、修改时间）。")]
    public Task<string> ListDirectoryAsync(
        [Description("目录路径，可留空表示第一个授权目录")] string? path = null,
        [Description("文件名通配符，例如 *.md")] string? pattern = null,
        [Description("是否递归子目录，默认 false")] bool recursive = false,
        [Description("最多返回条数，默认 300")] int maxEntries = 300,
        CancellationToken cancellationToken = default)
    {
        var (entries, truncated) = fileAccess.Enumerate(path, pattern, recursive, maxEntries, cancellationToken);
        var payload = new
        {
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
    }

    [McpServerTool(Name = "local_read_file")]
    [Description("读取本机授权目录中的文本文件，自动识别 UTF-8/UTF-16/GB18030，支持按字符偏移分页。")]
    public Task<string> ReadFileAsync(
        [Description("文件绝对路径或“根名称/子路径”")] string path,
        [Description("起始字符偏移，默认 0")] int offset = 0,
        [Description("最多读取字符数，默认 8000，最大 40000")] int maxChars = 8_000,
        CancellationToken cancellationToken = default) =>
        fileAccess.ReadAsync(path, offset, maxChars, cancellationToken);

    [McpServerTool(Name = "local_search_files")]
    [Description("按文件名通配符在本机授权目录中查找文件，返回绝对路径。")]
    public Task<string> SearchFilesAsync(
        [Description("文件名通配符，例如 *.md")] string pattern,
        [Description("限定目录，可留空")] string? path = null,
        [Description("是否递归，默认 true")] bool recursive = true,
        [Description("最多返回条数，默认 60")] int maxResults = 60,
        CancellationToken cancellationToken = default)
    {
        var (files, truncated) = fileAccess.FindFiles(path, pattern, recursive, maxResults, cancellationToken);
        return Task.FromResult(JsonSerializer.Serialize(new { count = files.Count, truncated, files }, JsonOptions));
    }

    [McpServerTool(Name = "local_grep")]
    [Description("在本机授权目录的文本文件中检索包含指定文本的行，返回路径与行号。")]
    public Task<string> GrepAsync(
        [Description("要检索的文本")] string query,
        [Description("限定目录，可留空")] string? path = null,
        [Description("限定文件名通配符，例如 *.md")] string? pattern = null,
        [Description("最多返回匹配数，默认 60")] int maxMatches = 60,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(fileAccess.Grep(query, path, pattern, maxMatches, cancellationToken));

    [McpServerTool(Name = "local_write_file")]
    [Description("向已授权且开启写权限的本机目录写入文本文件；只读目录会返回错误。")]
    public Task<string> WriteFileAsync(
        [Description("目标文件绝对路径或“根名称/子路径”")] string path,
        [Description("要写入的完整文本")] string content,
        [Description("是否追加，默认覆盖")] bool append = false,
        CancellationToken cancellationToken = default) =>
        fileAccess.WriteAsync(path, content, append, cancellationToken);
}
