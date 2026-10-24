using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Api.Companions;

public sealed class ChatHistoryParser
{
    private const int MaximumMessages = 100_000;
    private const int MaximumMessageLength = 20_000;

    /// <summary>
    /// 一行里同时带"时间 + 发送者 + 内容"。覆盖：
    /// <c>[2023-05-12 21:03:11] 张三: 你好</c>（老版文本导出）、
    /// <c>2023-05-12 21:03:11 张三: 你好</c>（微信/QQ 常见形）、
    /// <c>张三 2023-05-12 21:03:11: 你好</c>（发送者在前）。
    /// </summary>
    private static readonly Regex InlineMessagePattern = new(
        @"^\s*(?:\[(?<ts1>[^\]]+)\]\s*|(?<ts2>\d{4}[-/.]\d{1,2}[-/.]\d{1,2}[ T]\d{1,2}:\d{2}(?::\d{2})?)\s+)"
        + @"(?<sender>[^:：]{1,100})[:：]\s*(?<content>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>发送者在时间前面：<c>张三 2023-05-12 21:03:11: 你好</c>。</summary>
    private static readonly Regex SenderFirstPattern = new(
        @"^\s*(?<sender>[^:：]{1,60}?)\s+(?<ts>\d{4}[-/.]\d{1,2}[-/.]\d{1,2}[ T]\d{1,2}:\d{2}(?::\d{2})?)\s*[:：]\s*(?<content>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// QQ 文本导出的"块式"：先一行 <c>2023-05-12 21:03:11 张三(123456)</c>，
    /// 之后若干行是内容，直到下一个块头。发送者后面的 <c>(QQ号)</c> 要去掉。
    /// </summary>
    private static readonly Regex BlockHeaderPattern = new(
        @"^\s*(?<ts>\d{4}[-/.]\d{1,2}[-/.]\d{1,2}[ T]\d{1,2}:\d{2}(?::\d{2})?)\s+(?<sender>[^:：]{1,60}?)\s*(?:[（(][^）)]*[）)])?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>没有时间戳的 <c>发送者: 内容</c>（早期版本就支持这种手写格式）。</summary>
    private static readonly Regex PlainSenderPattern = new(
        @"^\s*(?<sender>[^:：]{1,100})[:：]\s*(?<content>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>QQ 导出里的装饰行（消息分组/消息对象/等号分隔线），直接跳过。</summary>
    private static readonly Regex NoisePattern = new(
        @"^\s*(=+|-+|消息记录|消息分组|消息对象|导出时间|以下为|【.*】)\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<ImportedChatMessage> Parse(string fileName, string content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var messages = extension switch
        {
            ".json" => ParseJson(content),
            ".txt" => ParseText(content),
            ".csv" => ParseCsv(content),
            _ => throw new ChatImportException(
                "支持的聊天导出格式：QQ/微信的 .txt 文本记录、.csv 表格，以及通用的 .json 消息数组。")
        };

        if (messages.Count == 0)
            throw new ChatImportException(
                "文件中没有识别到聊天消息。支持 QQ/微信的文本导出（时间 + 发送者 + 内容）、CSV 表格，以及 JSON 消息数组。");
        if (messages.Count > MaximumMessages)
            throw new ChatImportException($"单个文件最多支持 {MaximumMessages:N0} 条消息。");
        if (messages.Any(message => message.Content.Length > MaximumMessageLength))
            throw new ChatImportException($"单条消息不能超过 {MaximumMessageLength:N0} 个字符。");
        return messages;
    }

    private static List<ImportedChatMessage> ParseJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root
                : TryGetProperty(root, "messages", out var messages) || TryGetProperty(root, "records", out messages)
                    ? messages
                    : default;
            if (items.ValueKind != JsonValueKind.Array)
                throw new ChatImportException("JSON 顶层必须是消息数组，或包含 messages/records 数组。");

            var result = new List<ImportedChatMessage>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var sender = ReadString(item, "sender", "from", "speaker", "name", "role");
                var text = ReadString(item, "content", "text", "message", "body");
                if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(text)) continue;
                var timestampText = ReadString(item, "timestamp", "time", "date", "sentAt");
                DateTimeOffset? timestamp = DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
                result.Add(new ImportedChatMessage(sender.Trim(), text.Trim(), timestamp));
            }
            return result;
        }
        catch (JsonException exception)
        {
            throw new ChatImportException($"JSON 格式无效：{exception.Message}");
        }
    }

    /// <summary>
    /// 文本导出的解析。用一个小的状态机同时吃三种形态：
    /// <list type="number">
    /// <item>行内式：<c>时间 发送者: 内容</c>（微信、QQ 新导出）；</item>
    /// <item>发送者在前：<c>发送者 时间: 内容</c>；</item>
    /// <item>块式：<c>时间 发送者(号码)</c> 独占一行，随后若干行是内容（QQ 老导出）。</item>
    /// </list>
    /// 都匹配不上、又不是装饰行时，视为上一条消息的续行——聊天记录里的换行很常见，
    /// 丢掉它们会让消息内容残缺。这是因为"宁可多留一行"比"丢内容"安全。
    /// </summary>
    private static List<ImportedChatMessage> ParseText(string content)
    {
        var result = new List<ImportedChatMessage>();

        // 是否处在"块式"里（QQ 老导出：块头之后都是这一条的内容）。
        // 这个状态是必要的：块内的正文完全可能带冒号（网址、比分、时间），
        // 不设状态就会把 "还有 21:30 见" 误判成一条新消息。
        var inBlock = false;

        foreach (var rawLine in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd();
            if (line.Trim().Length == 0) continue;

            var inline = InlineMessagePattern.Match(line);
            if (inline.Success)
            {
                var timestampText = inline.Groups["ts1"].Success
                    ? inline.Groups["ts1"].Value
                    : inline.Groups["ts2"].Value;
                result.Add(new ImportedChatMessage(
                    CleanSender(inline.Groups["sender"].Value),
                    inline.Groups["content"].Value.Trim(),
                    TryParseTimestamp(timestampText)));
                inBlock = false;
                continue;
            }

            var senderFirst = SenderFirstPattern.Match(line);
            if (senderFirst.Success)
            {
                result.Add(new ImportedChatMessage(
                    CleanSender(senderFirst.Groups["sender"].Value),
                    senderFirst.Groups["content"].Value.Trim(),
                    TryParseTimestamp(senderFirst.Groups["ts"].Value)));
                inBlock = false;
                continue;
            }

            var header = BlockHeaderPattern.Match(line);
            if (header.Success)
            {
                result.Add(new ImportedChatMessage(
                    CleanSender(header.Groups["sender"].Value),
                    string.Empty,
                    TryParseTimestamp(header.Groups["ts"].Value)));
                inBlock = true;
                continue;
            }

            if (NoisePattern.IsMatch(line)) continue;

            // 块内：一律当成当前这条消息的正文。
            if (inBlock && result.Count > 0)
            {
                var current = result[^1];
                result[^1] = current with
                {
                    Content = string.IsNullOrEmpty(current.Content) ? line.Trim() : $"{current.Content}\n{line.Trim()}"
                };
                continue;
            }

            var plain = PlainSenderPattern.Match(line);
            if (plain.Success)
            {
                result.Add(new ImportedChatMessage(
                    CleanSender(plain.Groups["sender"].Value),
                    plain.Groups["content"].Value.Trim(),
                    null));
                continue;
            }

            if (result.Count > 0)
            {
                var previous = result[^1];
                result[^1] = previous with { Content = $"{previous.Content}\n{line.Trim()}" };
            }
        }

        // 块式里只有块头、没有内容的行（例如"撤回了一条消息"后面什么都没有）不算消息。
        return result.Where(message => !string.IsNullOrWhiteSpace(message.Content)).ToList();
    }

    /// <summary>
    /// WeChatMsg / 留痕 这类工具导出的 CSV。按表头找"时间/发送者/内容"三列，
    /// 找不到表头时按 <c>时间,发送者,内容</c> 的前三列顺序读。
    /// </summary>
    private static List<ImportedChatMessage> ParseCsv(string content)
    {
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(line => line.Trim().Length > 0)
            .ToList();
        if (lines.Count == 0) return [];

        var header = SplitCsvLine(lines[0]);
        var timeIndex = FindColumn(header, "time", "时间", "strtime", "date", "日期");
        var senderIndex = FindColumn(header, "sender", "发送者", "发送人", "talker", "昵称", "from", "name");
        var contentIndex = FindColumn(header, "content", "内容", "消息", "strcontent", "text", "message");

        var hasHeader = timeIndex >= 0 && senderIndex >= 0 && contentIndex >= 0;
        if (!hasHeader)
        {
            timeIndex = 0;
            senderIndex = 1;
            contentIndex = 2;
        }

        var result = new List<ImportedChatMessage>();
        foreach (var line in hasHeader ? lines.Skip(1) : lines)
        {
            var cells = SplitCsvLine(line);
            if (Math.Max(timeIndex, Math.Max(senderIndex, contentIndex)) >= cells.Count) continue;

            var sender = CleanSender(cells[senderIndex]);
            var text = cells[contentIndex].Trim();
            if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(text)) continue;

            result.Add(new ImportedChatMessage(sender, text, TryParseTimestamp(cells[timeIndex])));
        }

        return result;
    }

    /// <summary>极简 CSV 行切分：支持双引号包裹与 <c>""</c> 转义。</summary>
    internal static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var builder = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"')
                    {
                        builder.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    builder.Append(character);
                }
                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                case '\t':
                    cells.Add(builder.ToString());
                    builder.Clear();
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        cells.Add(builder.ToString());
        return cells;
    }

    private static int FindColumn(IReadOnlyList<string> header, params string[] names)
    {
        for (var index = 0; index < header.Count; index++)
        {
            var cell = header[index].Trim().Trim('"').ToLowerInvariant();
            if (names.Any(name => cell.Contains(name, StringComparison.Ordinal))) return index;
        }

        return -1;
    }

    /// <summary>去掉 QQ 的 <c>(123456)</c> 号码后缀与首尾空白，让发送者名可以直接和用户输入对上。</summary>
    internal static string CleanSender(string sender)
    {
        var trimmed = sender.Trim();
        var match = Regex.Match(trimmed, @"^(?<name>.+?)\s*[（(][^）)]*[）)]\s*$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["name"].Value.Trim() : trimmed;
    }

    private static DateTimeOffset? TryParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
               || DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed)
            ? parsed
            : null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.ToString();
        }
        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}

public sealed record ImportedChatMessage(string Sender, string Content, DateTimeOffset? SentAt);
public sealed record CompanionProfile(Guid Id, string Name, string TargetSender, string Platform, string PersonaPrompt, int MessageCount, DateTimeOffset CreatedAt);
public sealed record CompanionSession(Guid Id, Guid ProfileId, string Title, DateTimeOffset CreatedAt);
public sealed record CompanionChatMessage(long Id, string Role, string Content, DateTimeOffset CreatedAt);
public sealed class ChatImportException(string message) : Exception(message);
