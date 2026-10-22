using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Models;

public sealed class OpenAiCompatibleModelClient(
    HttpClient httpClient,
    ILlmSettingsAccessor llmSettings)
{
    public async Task<string> DescribeImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        var settings = llmSettings.Selected;
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("The selected LLM endpoint and model must be configured.");

        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        var mimeType = extension switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
        var data = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var imageUrl = $"data:{mimeType};base64,{Convert.ToBase64String(data)}";
        using var request = CreateRequest(settings.Endpoint, "chat/completions", settings.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "请用中文准确描述这张图片中可检索的主要信息；只描述可见内容，不要推测。" },
                        new { type = "image_url", image_url = new { url = imageUrl } }
                    }
                }
            }
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Image vision", cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("The vision model returned no description.");
    }

    public Task<string> CompleteChatAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
        CompleteChatAsync(llmSettings.Selected, systemPrompt, userPrompt, cancellationToken);

    public async Task<string> CompleteChatAsync(ModelEndpointOptions settings, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("The selected LLM endpoint and model must be configured.");

        using var request = CreateRequest(settings.Endpoint, "chat/completions", settings.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.7
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Chat", cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("The chat model returned no response text.");
    }

    /// <summary>
    /// 带工具定义的 Chat Completions 调用。返回模型的文本或工具调用请求，
    /// 由上层的工具循环负责执行并回填结果。
    /// </summary>
    public async Task<AgentCompletion> CompleteChatWithToolsAsync(
        ModelEndpointOptions settings,
        IReadOnlyList<AgentChatTurn> turns,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("The selected LLM endpoint and model must be configured.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["messages"] = turns.Select(ToWireMessage).ToList(),
            ["temperature"] = 0.3
        };
        if (tools.Count > 0)
        {
            payload["tools"] = tools.Select(tool => (object?)new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = ParseSchema(tool.ParametersJsonSchema)
                }
            }).ToList();
            payload["tool_choice"] = "auto";
        }

        using var request = CreateRequest(settings.Endpoint, "chat/completions", settings.ApiKey);
        request.Content = JsonContent.Create(payload);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Chat", cancellationToken);

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");

        string? content = message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;

        var toolCalls = new List<AgentToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCallsElement)
            && toolCallsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in toolCallsElement.EnumerateArray())
            {
                if (!element.TryGetProperty("function", out var function)) continue;
                var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                toolCalls.Add(new AgentToolCall(
                    element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? Guid.NewGuid().ToString("N")
                        : Guid.NewGuid().ToString("N"),
                    name,
                    function.TryGetProperty("arguments", out var argumentsElement)
                        && argumentsElement.ValueKind == JsonValueKind.String
                            ? argumentsElement.GetString() ?? "{}"
                            : "{}"));
            }
        }

        return new AgentCompletion(content, toolCalls);
    }

    private static Dictionary<string, object?> ToWireMessage(AgentChatTurn turn)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = turn.Role,
            ["content"] = turn.Content
        };
        if (!string.IsNullOrWhiteSpace(turn.ToolCallId)) message["tool_call_id"] = turn.ToolCallId;
        // tool 角色的消息只允许 role/content/tool_call_id，附带 name 会被部分严格实现拒绝。
        if (!string.IsNullOrWhiteSpace(turn.Name) && !string.Equals(turn.Role, "tool", StringComparison.Ordinal))
            message["name"] = turn.Name;
        if (turn.ToolCalls is { Count: > 0 } calls)
        {
            message["tool_calls"] = calls.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new { name = call.Name, arguments = call.Arguments }
            }).ToList();
        }
        return message;
    }

    private static JsonElement ParseSchema(string schema)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(schema)
                ? """{"type":"object","properties":{}}"""
                : schema);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var fallback = JsonDocument.Parse("""{"type":"object","properties":{}}""");
            return fallback.RootElement.Clone();
        }
    }

    private static HttpRequestMessage CreateRequest(string endpoint, string resource, string apiKey)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"Invalid model endpoint: {endpoint}");
        var path = baseUri.AbsolutePath.TrimEnd('/');
        var uri = path.EndsWith("/" + resource, StringComparison.OrdinalIgnoreCase)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + resource);
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        var key = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string service, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 500) body = body[..500];
        throw new InvalidOperationException($"{service} API returned {(int)response.StatusCode}: {body}");
    }
}
