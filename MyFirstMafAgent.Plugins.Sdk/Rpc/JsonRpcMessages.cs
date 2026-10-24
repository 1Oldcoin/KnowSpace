using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MyFirstMafAgent.Plugins.Sdk.Rpc;

/// <summary>JSON-RPC 2.0 请求。id 为空表示通知（不返回响应）。</summary>
public sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonNode? Id { get; init; }

    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonNode? Params { get; init; }

    public bool IsNotification => Id is null;

    /// <summary>用于关联日志与取消通知的稳定键。</summary>
    public string IdText => Id?.ToJsonString() ?? string.Empty;
}

/// <summary>JSON-RPC 2.0 响应。</summary>
public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonNode? Id { get; init; }

    [JsonPropertyName("result")]
    public JsonNode? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }

    [JsonIgnore]
    public bool IsError => Error is not null;
}

public sealed record JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>结构化附加信息（字段名稳定，便于客户端分支处理）。</summary>
    [JsonPropertyName("data")]
    public JsonNode? Data { get; init; }

    public static JsonRpcError Create(int code, string message, object? data = null) => new()
    {
        Code = code,
        Message = message,
        Data = data is null ? null : JsonSerializer.SerializeToNode(data)
    };
}

/// <summary>协议层异常。Code 取值为 <see cref="PluginProtocol"/> 中的错误码。</summary>
public sealed class PluginRpcException : Exception
{
    public PluginRpcException(int code, string message, JsonNode? data = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        ErrorData = data;
    }

    public int Code { get; }

    /// <summary>错误附加信息（与 <see cref="JsonRpcError.Data"/> 对应）。</summary>
    public JsonNode? ErrorData { get; }

    public bool IsCancelled => Code == PluginProtocol.ErrorCancelled;

    public static PluginRpcException FromError(JsonRpcError error) =>
        new(error.Code, error.Message, error.Data);
}

/// <summary>线路序列化设置：小驼峰、忽略 null、允许大整数。</summary>
public static class PluginJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static JsonNode? ToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, Options);

    /// <summary>把 JSON 节点反序列化为指定类型；节点为空时返回 default。</summary>
    public static T? ToObject<T>(JsonNode? node) =>
        node is null ? default : JsonSerializer.Deserialize<T>(node.ToJsonString(), Options);
}
