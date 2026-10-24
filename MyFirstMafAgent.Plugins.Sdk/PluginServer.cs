using System.Text.Json;
using System.Text.Json.Nodes;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>
/// 插件进程入口。负责 stdio 上的 JSON-RPC 生命周期，插件只需继承 <see cref="PluginBase"/>。
///
/// <para>
/// 安全约定：握手后把 <see cref="Console.Out"/> 重定向到空写入器，
/// 这样插件作者误用 <c>Console.WriteLine</c> 也不会污染协议分帧。
/// </para>
/// </summary>
public static class PluginServer
{
    /// <summary>运行插件（标准输入输出），返回进程退出码。建议在 <c>Main</c> 中直接返回该值。</summary>
    public static Task<int> RunAsync(PluginBase plugin, string[] args, CancellationToken cancellationToken = default) =>
        RunAsync(plugin, args, Console.OpenStandardInput(), Console.OpenStandardOutput(), redirectConsole: true, cancellationToken);

    /// <summary>
    /// 在给定的双向流上运行插件。用于测试与嵌入式宿主（例如把插件挂到内存管道上）。
    /// </summary>
    public static async Task<int> RunAsync(
        PluginBase plugin,
        string[] args,
        Stream input,
        Stream output,
        bool redirectConsole,
        CancellationToken cancellationToken = default)
    {
        PluginRuntimeLog.Write($"{plugin.Id} {plugin.Version} starting (args: {string.Join(' ', args)})");

        var capabilities = plugin.Capabilities;
        var initialized = false;
        var shutdownSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 先拿到原始输出流，再把 Console.Out 指向空写入器，避免误用破坏协议。
        await using var peer = new JsonRpcPeer(input, output, HandleAsync, PluginRuntimeLog.Write);
        // 关闭响应写完之后才真正退出，保证宿主能收到 plugin/shutdown 的结果。
        peer.ResponseSent = method =>
        {
            if (method == PluginProtocol.MethodShutdown) shutdownSignal.TrySetResult();
        };
        if (redirectConsole) Console.SetOut(TextWriter.Null);
        peer.Start();
        PluginRuntimeLog.Write("rpc peer started");

        try
        {
            // 关闭信号或对端 EOF（宿主关闭管道）都会结束插件进程。
            await Task.WhenAny(shutdownSignal.Task, peer.Completion).ConfigureAwait(false);
            if (shutdownSignal.Task.IsCompleted)
                PluginRuntimeLog.Write("shutdown requested by core");
            else
                PluginRuntimeLog.Write("stdin closed; exiting");
        }
        catch (OperationCanceledException)
        {
            PluginRuntimeLog.Write("cancelled; shutting down");
        }

        try { await plugin.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { PluginRuntimeLog.Write($"shutdown failed: {exception.Message}"); }

        PluginRuntimeLog.Write("plugin stopped");
        return 0;

        async Task<JsonNode?> HandleAsync(JsonRpcRequest request, CancellationToken requestToken)
        {
            switch (request.Method)
            {
                case PluginProtocol.MethodInitialize:
                    return await InitializeAsync(request, requestToken).ConfigureAwait(false);

                case PluginProtocol.MethodConfigure:
                    await plugin.ConfigureAsync(request.Params?["config"], requestToken).ConfigureAwait(false);
                    return PluginJson.ToNode(new { ok = true });

                case PluginProtocol.MethodHealth:
                {
                    var health = await plugin.CheckHealthAsync(requestToken).ConfigureAwait(false);
                    return PluginJson.ToNode(health);
                }

                case PluginProtocol.MethodListCapabilities:
                    return PluginJson.ToNode(new { capabilities });

                case PluginProtocol.MethodInvoke:
                    return await InvokeAsync(request, requestToken).ConfigureAwait(false);

                case PluginProtocol.MethodShutdown:
                    // 信号由 ResponseSent 在响应写完后触发，这里只回执。
                    return PluginJson.ToNode(new { ok = true });

                default:
                    throw new PluginRpcException(
                        PluginProtocol.ErrorMethodNotFound,
                        $"method '{request.Method}' is not supported");
            }
        }

        async Task<JsonNode?> InitializeAsync(JsonRpcRequest request, CancellationToken requestToken)
        {
            var requested = request.Params?["protocolVersion"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(requested))
                throw new PluginRpcException(PluginProtocol.ErrorInvalidParams, "params.protocolVersion is required");

            if (!SemanticVersion.TryParse(requested, out var version))
                throw new PluginRpcException(PluginProtocol.ErrorInvalidParams, $"protocolVersion '{requested}' is not a valid semantic version");

            if (version.Major != PluginProtocol.SupportedMajorVersion)
                throw new PluginRpcException(
                    PluginProtocol.ErrorIncompatibleProtocol,
                    $"plugin supports protocol major {PluginProtocol.SupportedMajorVersion}, core offered {version}",
                    PluginJson.ToNode(new { supported = PluginProtocol.ProtocolVersion, offered = requested }));

            var context = new PluginInitializeContext
            {
                CoreProtocolVersion = requested,
                CoreVersion = request.Params?["coreVersion"]?.GetValue<string>(),
                DataDirectory = request.Params?["dataDirectory"]?.GetValue<string>(),
                Config = request.Params?["config"]
            };

            await plugin.InitializeAsync(context, requestToken).ConfigureAwait(false);
            initialized = true;
            PluginRuntimeLog.Write($"initialized by core {context.CoreVersion ?? "(unknown)"}");

            return PluginJson.ToNode(new
            {
                protocolVersion = PluginProtocol.ProtocolVersion,
                pluginId = plugin.Id,
                pluginVersion = plugin.Version,
                runtime = "dotnet",
                capabilities
            });
        }

        async Task<JsonNode?> InvokeAsync(JsonRpcRequest request, CancellationToken requestToken)
        {
            if (!initialized)
                throw new PluginRpcException(PluginProtocol.ErrorNotConfigured, "plugin/initialize must be called first");

            var capabilityId = request.Params?["capabilityId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(capabilityId))
                throw new PluginRpcException(PluginProtocol.ErrorInvalidParams, "params.capabilityId is required");

            var capability = capabilities.FirstOrDefault(item =>
                string.Equals(item.Id, capabilityId, StringComparison.Ordinal));
            if (capability is null)
                throw new PluginRpcException(
                    PluginProtocol.ErrorUnknownCapability,
                    $"capability '{capabilityId}' is not declared by this plugin");

            var arguments = request.Params?["arguments"];
            var result = await plugin.InvokeAsync(capability.Id, arguments, requestToken).ConfigureAwait(false);

            return new JsonObject
            {
                ["capabilityId"] = capability.Id,
                ["result"] = result
            };
        }
    }

    /// <summary>把异常转换为结构化错误，供插件在自定义 <c>Main</c> 中使用。</summary>
    public static PluginRpcException Failure(int code, string message, object? data = null) =>
        new(code, message, data is null ? null : JsonSerializer.SerializeToNode(data, PluginJson.Options));
}
