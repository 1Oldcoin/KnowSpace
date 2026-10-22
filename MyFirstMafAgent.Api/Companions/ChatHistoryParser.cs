using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Api.Companions;

public sealed class ChatHistoryParser
{
    private const int MaximumMessages = 100_000;
    private const int MaximumMessageLength = 20_000;
    private static readonly Regex TextMessagePattern = new(
        @"^(?:\[(?<timestamp>[^\]]+)\]\s*)?(?<sender>[^:：]{1,100})[:：]\s*(?<content>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<ImportedChatMessage> Parse(string fileName, string content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var messages = extension switch
        {
            ".json" => ParseJson(content),
            ".txt" => ParseText(content),
            _ => throw new ChatImportException("首版只支持可读的 .json 和 .txt 聊天导出文件。")
        };

        if (messages.Count == 0)
            throw new ChatImportException("文件中没有识别到聊天消息。请检查导出格式。 ");
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

    private static List<ImportedChatMessage> ParseText(string content)
    {
        var result = new List<ImportedChatMessage>();
        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var match = TextMessagePattern.Match(line);
            if (match.Success)
            {
                var timeText = match.Groups["timestamp"].Value;
                DateTimeOffset? timestamp = DateTimeOffset.TryParse(timeText, CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
                result.Add(new ImportedChatMessage(match.Groups["sender"].Value.Trim(),
                    match.Groups["content"].Value.Trim(), timestamp));
            }
            else if (result.Count > 0 && !string.IsNullOrWhiteSpace(line))
            {
                var previous = result[^1];
                result[^1] = previous with { Content = $"{previous.Content}\n{line.Trim()}" };
            }
        }
        return result;
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
