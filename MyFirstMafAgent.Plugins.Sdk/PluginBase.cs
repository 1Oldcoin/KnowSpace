using System.Text.Json.Nodes;

namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>健康状态。核心据此决定是否继续路由调用。</summary>
public sealed record PluginHealth
{
    public const string HealthyStatus = "healthy";
    public const string DegradedStatus = "degraded";
    public const string UnhealthyStatus = "unhealthy";

    public string Status { get; init; } = HealthyStatus;

    /// <summary>面向用户/日志的简短说明，不要放密钥。</summary>
    public string? Detail { get; init; }

    public static PluginHealth Healthy(string? detail = null) => new() { Status = HealthyStatus, Detail = detail };

    public static PluginHealth Degraded(string detail) => new() { Status = DegradedStatus, Detail = detail };

    public static PluginHealth Unhealthy(string detail) => new() { Status = UnhealthyStatus, Detail = detail };

    public bool IsUsable => !string.Equals(Status, UnhealthyStatus, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 插件实现的基类。派生类只声明能力并实现调用，不需要处理协议、分帧或错误码。
/// </summary>
public abstract class PluginBase
{
    /// <summary>与 manifest 的 id 必须一致；核心在握手时校验。</summary>
    public abstract string Id { get; }

    /// <summary>与 manifest 的 version 必须一致；核心在握手时校验。</summary>
    public abstract string Version { get; }

    /// <summary>与 manifest 的 capabilities 必须一致（用于运行期自检）。</summary>
    public abstract IReadOnlyList<PluginCapability> Capabilities { get; }

    /// <summary>收到用户配置后调用。抛异常会被包装为结构化错误返回核心。</summary>
    public virtual Task ConfigureAsync(JsonNode? config, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>初始化（协议握手完成后、首次调用前）。可用于打开连接。</summary>
    public virtual Task InitializeAsync(PluginInitializeContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public virtual Task<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(PluginHealth.Healthy());

    /// <summary>
    /// 执行能力。未声明的 capabilityId 不会到达这里（服务端先校验）。
    /// 参数为 JSON；返回值为 JSON（可以是对象、数组或字符串节点）。
    /// </summary>
    public abstract Task<JsonNode?> InvokeAsync(string capabilityId, JsonNode? arguments, CancellationToken cancellationToken);

    /// <summary>进程退出前清理（关闭连接、刷新缓冲）。</summary>
    public virtual Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>向核心推送日志（写 stderr + plugin/log 通知）。</summary>
    protected void Log(string message) => PluginRuntimeLog.Write(message);
}

/// <summary>握手时提供给插件的核心信息。</summary>
public sealed record PluginInitializeContext
{
    public required string CoreProtocolVersion { get; init; }

    public string? CoreVersion { get; init; }

    /// <summary>核心分配给插件的可写目录（插件不得写到其它位置）。</summary>
    public string? DataDirectory { get; init; }

    /// <summary>握手时附带的初始配置（用户已保存的值）。</summary>
    public JsonNode? Config { get; init; }
}

/// <summary>插件进程的日志出口。始终写 stderr，保证 stdout 只承载协议消息。</summary>
internal static class PluginRuntimeLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        var line = $"[plugin] {message}";
        lock (Gate)
        {
            try { Console.Error.WriteLine(line); }
            catch (IOException) { /* 宿主已关闭管道 */ }
            catch (ObjectDisposedException) { /* 同上 */ }
        }
    }
}
