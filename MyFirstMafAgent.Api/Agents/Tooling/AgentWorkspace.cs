using System.Text;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 智能体的本地文件沙箱。所有路径都会被规范化并限制在工作目录内，
/// 阻止 ../ 穿越、绝对路径逃逸和符号链接到外部目录。
/// </summary>
public sealed class AgentWorkspace
{
    private readonly string _root;

    public AgentWorkspace(IOptions<AgentOptions> options, IWebHostEnvironment environment)
    {
        var configured = options.Value.WorkspaceRootPath;
        _root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
    }

    public string Root
    {
        get
        {
            Directory.CreateDirectory(_root);
            return _root;
        }
    }

    /// <summary>把模型给出的相对路径解析为沙箱内的绝对路径。</summary>
    public string Resolve(string? relativePath)
    {
        var cleaned = (relativePath ?? string.Empty).Trim().Replace('\\', Path.DirectorySeparatorChar);
        if (cleaned.Contains('\0'))
            throw new InvalidOperationException("路径包含非法字符。");

        var root = Root;
        var candidate = string.IsNullOrWhiteSpace(cleaned)
            ? root
            : Path.GetFullPath(Path.Combine(root, cleaned.TrimStart(Path.DirectorySeparatorChar)));

        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("路径超出智能体工作目录，操作被拒绝。");
        }

        return candidate;
    }

    /// <summary>返回相对沙箱根目录的展示用路径。</summary>
    public string ToRelative(string absolutePath) =>
        Path.GetRelativePath(Root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    public IEnumerable<FileInfo> EnumerateFiles(string? relativePath, string? pattern, bool recursive)
    {
        var directory = Resolve(relativePath);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"目录不存在：{relativePath}");

        var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return new DirectoryInfo(directory)
            .EnumerateFiles(searchPattern, option)
            .Where(file => !IsInsideHiddenDirectory(file.FullName))
            .Take(500);
    }

    public async Task<string> ReadAsync(string relativePath, int offset, int maxChars, CancellationToken cancellationToken)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"文件不存在：{relativePath}");
        if (!IsTextFile(path))
            throw new InvalidOperationException("只支持读取文本文件。");

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var safeOffset = Math.Clamp(offset, 0, text.Length);
        var safeLength = Math.Clamp(maxChars <= 0 ? 4_000 : maxChars, 1, 40_000);
        var length = Math.Min(safeLength, text.Length - safeOffset);
        var content = text.Substring(safeOffset, length);

        var builder = new StringBuilder();
        builder.AppendLine($"[文件] {ToRelative(path)}  长度={text.Length}  偏移={safeOffset}  返回={length}");
        builder.AppendLine(content);
        if (safeOffset + length < text.Length)
            builder.AppendLine($"[提示] 内容未读完，可继续读取 offset={safeOffset + length}。");
        return builder.ToString();
    }

    public async Task<string> WriteAsync(string relativePath, string content, bool append, CancellationToken cancellationToken)
    {
        var path = Resolve(relativePath);
        if (Directory.Exists(path))
            throw new InvalidOperationException("目标是目录，不能写入文件。");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (append && File.Exists(path))
            await File.AppendAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
        else
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);

        var length = new FileInfo(path).Length;
        return $"[已写入] {ToRelative(path)}  模式={(append ? "追加" : "覆盖")}  字节={length}";
    }

    public string Delete(string relativePath)
    {
        var path = Resolve(relativePath);
        if (Directory.Exists(path))
        {
            // 只允许删除空目录，避免误删整棵沙箱。
            if (Directory.EnumerateFileSystemEntries(path).Any())
                throw new InvalidOperationException("目录非空，拒绝删除。");
            Directory.Delete(path);
            return $"[已删除目录] {ToRelative(path)}";
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"文件不存在：{relativePath}");
        File.Delete(path);
        return $"[已删除文件] {ToRelative(path)}";
    }

    public string Search(string query, string? relativePath, string? pattern, int maxMatches, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("检索关键词不能为空。");

        var matches = new List<string>();
        foreach (var file in EnumerateFiles(relativePath, pattern, recursive: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTextFile(file.FullName) || file.Length > 5 * 1024 * 1024) continue;

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file.FullName))
            {
                lineNumber++;
                if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                matches.Add($"{ToRelative(file.FullName)}:{lineNumber}: {Trim(line)}");
                if (matches.Count >= Math.Clamp(maxMatches, 1, 200)) goto Done;
            }
        }

    Done:
        return matches.Count == 0
            ? "[无匹配] 工作目录中没有找到包含该文本的文件。"
            : string.Join('\n', matches);
    }

    private bool IsInsideHiddenDirectory(string path)
    {
        var relative = Path.GetRelativePath(Root, path);
        return relative.Split(Path.DirectorySeparatorChar).Any(segment => segment.StartsWith('.'));
    }

    private static bool IsTextFile(string path) => TextExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string Trim(string line) => line.Length > 300 ? line[..300] : line.Trim();

    private static readonly HashSet<string> TextExtensions =
    [
        ".txt", ".md", ".markdown", ".json", ".jsonl", ".yaml", ".yml", ".xml", ".csv", ".tsv",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx", ".vue", ".cs", ".py", ".java",
        ".go", ".rs", ".sql", ".sh", ".ps1", ".log", ".ini", ".toml", ".env", ".csproj", ".slnx"
    ];
}
