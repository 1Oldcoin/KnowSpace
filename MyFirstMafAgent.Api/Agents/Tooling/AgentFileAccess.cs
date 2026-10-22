using System.Text;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>一个被授权访问的本机目录。</summary>
public sealed record AgentFileRoot(string Name, string Path, bool AllowWrite, bool AllowDelete);

/// <summary>路径解析结果：命中的根目录 + 规范化后的绝对路径。</summary>
public sealed record AgentResolvedPath(AgentFileRoot Root, string FullPath);

/// <summary>
/// 本机文件访问白名单。与 <see cref="AgentWorkspace"/> 不同，这里允许访问用户在
/// <c>Agent:FileRoots</c> 中显式授权的任意目录（例如 D:\学习资料），
/// 仍然是白名单模型：未授权目录与 <c>..</c> 穿越一律拒绝。
/// </summary>
public sealed class AgentFileAccess
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly AgentOptions _options;
    private readonly IReadOnlyList<AgentFileRoot> _roots;

    static AgentFileAccess() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public AgentFileAccess(IOptions<AgentOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value;
        var roots = new List<AgentFileRoot>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _options.FileRoots.Where(root => root.Enabled))
        {
            if (string.IsNullOrWhiteSpace(item.Path)) continue;

            var fullPath = Path.GetFullPath(Path.IsPathRooted(item.Path)
                ? item.Path
                : Path.Combine(environment.ContentRootPath, item.Path)).TrimEnd(Path.DirectorySeparatorChar);

            var name = string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileName(fullPath) : item.Name.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = fullPath;
            // 名称作为路径前缀使用，必须唯一。
            var unique = name;
            var suffix = 2;
            while (!usedNames.Add(unique)) unique = $"{name}_{suffix++}";

            roots.Add(new AgentFileRoot(unique, fullPath, item.AllowWrite, item.AllowDelete));
        }

        _roots = roots;
    }

    public IReadOnlyList<AgentFileRoot> Roots => _roots;

    public bool HasRoots => _roots.Count > 0;

    public int MaxDirectoryEntries => Math.Clamp(_options.MaxDirectoryEntries, 1, 2_000);

    public int MaxSearchMatches => Math.Clamp(_options.MaxSearchMatches, 1, 500);

    /// <summary>
    /// 解析模型给出的路径。支持三种写法：
    /// 1) 绝对路径（必须落在某个授权目录内）；
    /// 2) “根名称/相对路径”（例如“学习资料/拆文/概要.md”）；
    /// 3) 直接相对路径（在所有授权目录中查找，优先命中已存在的文件）。
    /// </summary>
    public AgentResolvedPath Resolve(string? path)
    {
        if (_roots.Count == 0)
            throw new InvalidOperationException(
                "当前没有配置可访问的本机目录。请在 appsettings.json 的 Agent:FileRoots 中添加目录后重启 API。");

        var trimmed = (path ?? string.Empty).Trim().Trim('"');
        // 省略路径时默认第一个授权目录，便于模型先“看一眼根目录”。
        if (trimmed.Length == 0)
            return new AgentResolvedPath(_roots[0], _roots[0].Path);

        if (Path.IsPathRooted(trimmed))
        {
            var fullPath = Normalize(trimmed);
            var root = _roots.FirstOrDefault(item => IsInside(item.Path, fullPath));
            if (root is null)
                throw new InvalidOperationException(
                    $"路径不在允许访问的目录内：{trimmed}。可用目录：{string.Join(" | ", _roots.Select(item => $"{item.Name}({item.Path})"))}");
            return new AgentResolvedPath(root, EnsureNoEscapingLink(fullPath, root));
        }

        var normalized = trimmed.Replace('\\', '/').TrimStart('/');

        // 根名称前缀写法。
        foreach (var root in _roots)
        {
            if (normalized.Equals(root.Name, StringComparison.OrdinalIgnoreCase))
                return new AgentResolvedPath(root, root.Path);

            if (!normalized.StartsWith(root.Name + "/", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = normalized[(root.Name.Length + 1)..];
            var fullPath = Normalize(Path.Combine(root.Path, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(root.Path, fullPath))
                throw new InvalidOperationException($"路径超出授权目录 {root.Name}，操作被拒绝。");
            return new AgentResolvedPath(root, EnsureNoEscapingLink(fullPath, root));
        }

        // 直接相对路径：在所有根目录中解析。
        var candidates = new List<AgentResolvedPath>(_roots.Count);
        foreach (var root in _roots)
        {
            var fullPath = Normalize(Path.Combine(root.Path, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (IsInside(root.Path, fullPath))
                candidates.Add(new AgentResolvedPath(root, fullPath));
        }

        var existing = candidates.FirstOrDefault(item => File.Exists(item.FullPath) || Directory.Exists(item.FullPath));
        if (existing is not null) return existing;
        if (candidates.Count == 1) return candidates[0];

        throw new InvalidOperationException(
            $"无法确定相对路径 {trimmed} 指向哪个目录，请使用绝对路径或“根名称/路径”写法。可用目录：{string.Join(" | ", _roots.Select(item => item.Name))}");
    }

    /// <summary>列举目录内容；跳过符号链接与隐藏的系统项，并限制返回条数。</summary>
    public (IReadOnlyList<AgentFileEntry> Entries, bool Truncated) Enumerate(
        string? path,
        string? pattern,
        bool recursive,
        int? maxEntries,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!Directory.Exists(resolved.FullPath))
            throw new DirectoryNotFoundException($"目录不存在：{resolved.FullPath}");

        var limit = Math.Clamp(maxEntries ?? MaxDirectoryEntries, 1, 2_000);
        var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = new List<AgentFileEntry>();
        var truncated = false;

        foreach (var item in new DirectoryInfo(resolved.FullPath).EnumerateFileSystemInfos(searchPattern, option))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            if (entries.Count >= limit)
            {
                truncated = true;
                break;
            }

            entries.Add(item is FileInfo file
                ? new AgentFileEntry(file.FullName, "file", file.Length, file.LastWriteTimeUtc.ToString("O"))
                : new AgentFileEntry(item.FullName, "dir", null, item.LastWriteTimeUtc.ToString("O")));
        }

        return (entries, truncated);
    }

    /// <summary>只按文件名查找文件（不读取内容）。</summary>
    public (IReadOnlyList<string> Files, bool Truncated) FindFiles(
        string? path,
        string pattern,
        bool recursive,
        int? maxResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("pattern 不能为空，例如 *.md 或 *.txt。");

        var (entries, _) = Enumerate(path, pattern, recursive, maxResults, cancellationToken);
        var files = entries.Where(item => item.Type == "file").Select(item => item.Path).ToList();
        var truncated = entries.Count >= Math.Clamp(maxResults ?? MaxDirectoryEntries, 1, 2_000);
        return (files, truncated);
    }

    /// <summary>读取文本文件，支持按字符偏移分页；自动识别 UTF-8/UTF-16/GB18030。</summary>
    public async Task<string> ReadAsync(
        string? path,
        int offset,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (Directory.Exists(resolved.FullPath))
            throw new InvalidOperationException("目标是一个目录，请使用 local_list_directory 列举内容。");
        if (!File.Exists(resolved.FullPath))
            throw new FileNotFoundException($"文件不存在：{resolved.FullPath}");

        var info = new FileInfo(resolved.FullPath);
        var encoding = DetectEncoding(resolved.FullPath, out var isBinary);
        if (isBinary)
            throw new InvalidOperationException("该文件看起来是二进制文件，无法按文本读取。");

        var safeOffset = Math.Max(0, offset);
        var limit = Math.Clamp(maxChars <= 0 ? _options.MaxReadChars : maxChars, 1, 40_000);

        using var stream = new FileStream(resolved.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024, leaveOpen: false);

        var skipBuffer = new char[8_192];
        var skipped = 0;
        while (skipped < safeOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = Math.Min(skipBuffer.Length, safeOffset - skipped);
            var readSkipped = await reader.ReadAsync(skipBuffer.AsMemory(0, wanted), cancellationToken);
            if (readSkipped == 0) break;
            skipped += readSkipped;
        }

        var buffer = new char[limit];
        var total = 0;
        while (total < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(total, limit - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }

        var content = new string(buffer, 0, total);
        var htmlExtracted = false;

        // HTML 文件不把标签原样交给模型：首次读取（offset=0）时抽取正文。
        // 分页继续读取仍返回原始片段，避免破坏已有的 offset 续读行为。
        if (safeOffset == 0 && IsHtmlFile(resolved.FullPath) && content.Length > 0)
        {
            var extractedText = HtmlTextExtractor.ExtractText(content);
            if (extractedText.Length > 0)
            {
                content = extractedText;
                htmlExtracted = true;
            }
        }

        // 探测是否还有内容；读到的字符不计入本次结果，下次从 offset 继续。
        var hasMore = false;
        if (total == limit)
        {
            var probe = new char[1];
            hasMore = await reader.ReadAsync(probe.AsMemory(0, 1), cancellationToken) > 0;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"[文件] {resolved.FullPath}");
        builder.AppendLine($"[目录] {resolved.Root.Name}（{(resolved.Root.AllowWrite ? "可写" : "只读")}）  大小={FormatSize(info.Length)}  编码={encoding.WebName}  偏移={skipped}  返回={total}{(htmlExtracted ? "  格式=HTML正文" : string.Empty)}");
        builder.AppendLine(content);
        if (hasMore)
            builder.AppendLine($"[提示] 内容未读完，可继续读取 offset={skipped + total}。");
        return builder.ToString();
    }

    /// <summary>在授权目录的文本文件中检索包含指定文本的行。</summary>
    public string Grep(
        string query,
        string? path,
        string? pattern,
        int? maxMatches,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("检索关键词不能为空。");

        var limit = Math.Clamp(maxMatches ?? MaxSearchMatches, 1, 500);
        var matches = new List<string>();
        var scanned = 0;
        var textOnly = string.IsNullOrWhiteSpace(pattern);

        foreach (var file in Enumerate(path, textOnly ? "*" : pattern!.Trim(), true, 2_000, cancellationToken).Entries)
        {
            if (file.Type != "file" || file.Size is null or > 5 * 1024 * 1024) continue;
            if (textOnly && !IsTextExtension(file.Path)) continue;

            var encoding = DetectEncoding(file.Path, out var isBinary);
            if (isBinary) continue;

            scanned++;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file.Path, encoding))
            {
                lineNumber++;
                if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                matches.Add($"{file.Path}:{lineNumber}: {Trim(line)}");
                if (matches.Count >= limit) goto Done;
            }
        }

    Done:
        return matches.Count == 0
            ? $"[无匹配] 在 {scanned} 个文本文件中没有找到包含“{query}”的行。"
            : $"[匹配 {matches.Count} 条，已扫描 {scanned} 个文件]\n" + string.Join('\n', matches);
    }

    public async Task<string> WriteAsync(
        string? path,
        string content,
        bool append,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Root.AllowWrite)
            throw new InvalidOperationException(
                $"目录 {resolved.Root.Name} 为只读，禁止写入。请在 Agent:FileRoots 中为该目录设置 AllowWrite=true。");
        if (Directory.Exists(resolved.FullPath))
            throw new InvalidOperationException("目标是目录，不能写入文件。");
        if (content.Length > _options.MaxWriteChars)
            throw new InvalidOperationException($"写入内容超过上限 {_options.MaxWriteChars} 字符。");

        var directory = Path.GetDirectoryName(resolved.FullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (append && File.Exists(resolved.FullPath))
            await File.AppendAllTextAsync(resolved.FullPath, content, new UTF8Encoding(false), cancellationToken);
        else
            await File.WriteAllTextAsync(resolved.FullPath, content, new UTF8Encoding(false), cancellationToken);

        return $"[已写入] {resolved.FullPath}  模式={(append ? "追加" : "覆盖")}  字节={new FileInfo(resolved.FullPath).Length}";
    }

    public string Delete(string? path)
    {
        var resolved = Resolve(path);
        if (!resolved.Root.AllowDelete)
            throw new InvalidOperationException(
                $"目录 {resolved.Root.Name} 未开启删除权限。请在 Agent:FileRoots 中设置 AllowDelete=true。");

        if (Directory.Exists(resolved.FullPath))
        {
            if (Directory.EnumerateFileSystemEntries(resolved.FullPath).Any())
                throw new InvalidOperationException("目录非空，拒绝删除。");
            Directory.Delete(resolved.FullPath);
            return $"[已删除目录] {resolved.FullPath}";
        }

        if (!File.Exists(resolved.FullPath))
            throw new FileNotFoundException($"文件不存在：{resolved.FullPath}");

        File.Delete(resolved.FullPath);
        return $"[已删除文件] {resolved.FullPath}";
    }

    /// <summary>识别文本编码；检测到 NUL 字节时判定为二进制。</summary>
    public static Encoding DetectEncoding(string path, out bool isBinary)
    {
        var buffer = new byte[8 * 1024];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            read = stream.Read(buffer, 0, buffer.Length);
        }

        isBinary = Array.IndexOf(buffer, (byte)0, 0, read) >= 0
                   && !(read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)));

        if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (isBinary) return Encoding.UTF8;

        // 末尾可能截断多字节序列，最多回退 3 个字节再判断。
        for (var length = read; length >= Math.Max(0, read - 3); length--)
        {
            try
            {
                StrictUtf8.GetCharCount(buffer, 0, length);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                // 继续尝试更短的切片。
            }
        }

        return Encoding.GetEncoding("GB18030");
    }

    private static string Normalize(string path)
    {
        if (path.Contains('\0')) throw new InvalidOperationException("路径包含非法字符。");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool IsInside(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    /// <summary>防止通过符号链接把访问范围带到授权目录之外。</summary>
    private static string EnsureNoEscapingLink(string path, AgentFileRoot root)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists || info.LinkTarget is null) return path;

            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is not null && !IsInside(root.Path, Path.GetFullPath(target)))
                throw new InvalidOperationException($"路径通过符号链接指向授权目录之外，操作被拒绝。");
        }
        catch (IOException)
        {
            // 无法解析链接时不做额外限制，后续读写仍受白名单目录约束。
        }

        return path;
    }

    private static string Trim(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }

    internal static bool IsTextExtension(string path) =>
        TextExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>是否为 HTML 文件（读取时抽取正文而不是把标签交给模型）。</summary>
    internal static bool IsHtmlFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".xhtml";

    private static readonly HashSet<string> TextExtensions =
    [
        ".txt", ".md", ".markdown", ".json", ".jsonl", ".yaml", ".yml", ".xml", ".csv", ".tsv",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx", ".vue", ".cs", ".py", ".java",
        ".go", ".rs", ".sql", ".sh", ".ps1", ".bat", ".log", ".ini", ".toml", ".env",
        ".csproj", ".slnx", ".sln", ".props", ".targets", ".gitignore", ".editorconfig"
    ];

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };
}

public sealed record AgentFileEntry(string Path, string Type, long? Size, string Modified);
