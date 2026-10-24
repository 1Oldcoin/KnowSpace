using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyFirstMafAgent.Plugins.Sdk.Rpc;

/// <summary>
/// 一条基于 stdio 的双向 JSON-RPC 通道（NDJSON 分帧：一行一个消息，UTF-8）。
///
/// <para>
/// 客户端与插件服务端共用同一实现：谁调用 <see cref="SendRequestAsync"/> 谁就是请求方。
/// 核心进程把它包在子进程的 stdin/stdout 上；插件进程把它包在自己的 stdin/stdout 上。
/// </para>
///
/// <para>
/// 约定：<b>stdout 只允许出现协议消息</b>，任何日志必须写 stderr，否则会破坏分帧。
/// </para>
/// </summary>
public sealed class JsonRpcPeer : IAsyncDisposable
{
    /// <summary>单行上限，防止异常输入耗尽内存。</summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Stream _input;
    private readonly Func<JsonRpcRequest, CancellationToken, Task<JsonNode?>>? _handler;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _readLoop;
    private int _disposed;

    public JsonRpcPeer(
        Stream input,
        Stream output,
        Func<JsonRpcRequest, CancellationToken, Task<JsonNode?>>? handler = null,
        Action<string>? log = null)
    {
        // leaveOpen: true —— 调用方负责关闭底层流（子进程句柄或控制台流）。
        _input = input;
        _reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        _handler = handler;
        _log = log;
    }

    /// <summary>收到通知（无 id 的消息）时触发，参数为方法名与 params。</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>读取循环结束（对端关闭或出错）时完成。宿主可据此在 EOF 时退出。</summary>
    public Task Completion => _readLoop ?? Task.CompletedTask;

    /// <summary>
    /// 成功写出一条响应后回调（参数为请求方法名）。
    /// 宿主用它实现“先回响应、再真正退出”的关闭语义，避免响应被丢弃。
    /// </summary>
    public Action<string>? ResponseSent { get; set; }

    /// <summary>启动读取循环。未启动前不会处理任何入站消息。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _readLoop ??= Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    /// <summary>发送请求并等待响应。</summary>
    /// <param name="method">方法名。</param>
    /// <param name="parameters">params 节点；为 null 时不发送该字段。</param>
    /// <param name="timeout">超时；超时会向对端发送取消通知并抛出 ErrorCancelled。</param>
    /// <param name="cancellationToken">调用方取消。</param>
    public async Task<JsonNode?> SendRequestAsync(
        string method,
        JsonNode? parameters,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var id = JsonValue.Create(Guid.NewGuid().ToString("N"));
        var key = id!.ToJsonString();
        var request = new JsonRpcRequest { Id = id, Method = method, Params = parameters };

        var completion = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = completion;

        try
        {
            await WriteLineAsync(PluginJson.Serialize(request), cancellationToken).ConfigureAwait(false);

            JsonRpcResponse response;
            if (timeout is { } effective)
            {
                try
                {
                    response = await completion.Task.WaitAsync(effective, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await TryCancelAsync(key, cancellationToken).ConfigureAwait(false);
                    throw new PluginRpcException(
                        PluginProtocol.ErrorCancelled,
                        $"'{method}' timed out after {effective.TotalMilliseconds:0} ms",
                        PluginJson.ToNode(new { method, timeoutMs = (int)effective.TotalMilliseconds }));
                }
            }
            else
            {
                response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (response.Error is not null) throw PluginRpcException.FromError(response.Error);
            return response.Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消：通知对端停止工作，再把取消传递给上层。
            await TryCancelAsync(key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>发送请求并把结果反序列化为指定类型。</summary>
    public async Task<T?> SendRequestAsync<T>(
        string method,
        JsonNode? parameters,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        return result is null ? default : result.Deserialize<T>(PluginJson.Options);
    }

    /// <summary>发送通知（不等待响应）。</summary>
    public Task SendNotificationAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default) =>
        WriteLineAsync(
            PluginJson.Serialize(new JsonRpcRequest { Id = null, Method = method, Params = parameters }),
            cancellationToken);

    /// <summary>插件向核心推送日志（不会阻塞业务）。</summary>
    public Task LogAsync(string level, string message, CancellationToken cancellationToken = default) =>
        SendNotificationAsync(
            PluginProtocol.MethodLog,
            PluginJson.ToNode(new { level, message }),
            cancellationToken);

    private async Task TryCancelAsync(string targetIdKey, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new JsonObject { ["targetId"] = JsonNode.Parse(targetIdKey) };
            await SendNotificationAsync(PluginProtocol.MethodCancel, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException)
        {
            // 对端已关闭，取消通知不再有意义。
            _log?.Invoke($"cancel notification skipped: {exception.Message}");
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                if (line.Length > MaxLineBytes)
                {
                    _log?.Invoke($"dropped oversized message ({line.Length} chars)");
                    continue;
                }

                await DispatchAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
        catch (ObjectDisposedException)
        {
            // 宿主已关闭底层流。
        }
        catch (Exception exception)
        {
            _log?.Invoke($"read loop failed: {exception.Message}");
        }
        finally
        {
            FailPending(new PluginRpcException(PluginProtocol.ErrorInternal, "the peer connection was closed"));
        }
    }

    private async Task DispatchAsync(string line, CancellationToken cancellationToken)
    {
        JsonRpcRequest? request;
        try
        {
            request = PluginJson.Deserialize<JsonRpcRequest>(line);
        }
        catch (JsonException exception)
        {
            _log?.Invoke($"invalid JSON message: {exception.Message}");
            await WriteLineAsync(
                PluginJson.Serialize(new JsonRpcResponse
                {
                    Error = JsonRpcError.Create(PluginProtocol.ErrorParse, "invalid JSON message")
                }),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Method))
        {
            // 可能是对端对我们请求的响应。
            if (!await TryCompletePendingAsync(line).ConfigureAwait(false))
                _log?.Invoke("dropped message without method or matching id");
            return;
        }

        // 取消通知：中止对应的在途请求。
        if (request.Method == PluginProtocol.MethodCancel)
        {
            var target = request.Params?["targetId"];
            if (target is not null && _inFlight.TryGetValue(target.ToJsonString(), out var cts))
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            return;
        }

        if (request.IsNotification)
        {
            NotificationReceived?.Invoke(request.Method, request.Params);
            if (_handler is not null)
            {
                // 允许服务端通过 handler 处理通知，但忽略返回值。
                _ = Task.Run(async () =>
                {
                    try { await _handler(request, cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) { _log?.Invoke($"notification handler failed ({request.Method}): {exception.Message}"); }
                }, CancellationToken.None);
            }

            return;
        }

        if (_handler is null)
        {
            await WriteErrorAsync(request, JsonRpcError.Create(
                PluginProtocol.ErrorMethodNotFound,
                $"method '{request.Method}' is not supported by this peer"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var requestKey = request.IdText;
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inFlight[requestKey] = requestCts;
        try
        {
            var result = await _handler(request, requestCts.Token).ConfigureAwait(false);
            await WriteLineAsync(
                PluginJson.Serialize(new JsonRpcResponse { Id = request.Id, Result = result }),
                CancellationToken.None).ConfigureAwait(false);

            // 响应已发出后才通知宿主，避免“先信号后退、响应丢失”。
            try { ResponseSent?.Invoke(request.Method); }
            catch (Exception exception) { _log?.Invoke($"response-sent callback failed: {exception.Message}"); }
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(request, JsonRpcError.Create(
                PluginProtocol.ErrorCancelled,
                $"'{request.Method}' was cancelled"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (PluginRpcException exception)
        {
            await WriteErrorAsync(request, JsonRpcError.Create(exception.Code, exception.Message, null), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"handler failed ({request.Method}): {exception}");
            await WriteErrorAsync(request, JsonRpcError.Create(
                PluginProtocol.ErrorInternal,
                exception.Message), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(requestKey, out _);
        }
    }

    private Task WriteErrorAsync(JsonRpcRequest request, JsonRpcError error, CancellationToken cancellationToken) =>
        WriteLineAsync(
            PluginJson.Serialize(new JsonRpcResponse { Id = request.Id, Error = error }),
            cancellationToken);

    private Task<bool> TryCompletePendingAsync(string line)
    {
        JsonRpcResponse? response;
        try
        {
            response = PluginJson.Deserialize<JsonRpcResponse>(line);
        }
        catch (JsonException)
        {
            return Task.FromResult(false);
        }

        if (response?.Id is null) return Task.FromResult(false);

        return Task.FromResult(
            _pending.TryRemove(response.Id.ToJsonString(), out var completion)
            && completion.TrySetResult(response));
    }

    private async Task WriteLineAsync(string json, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 单行写入：JsonSerializer 默认不产生裸换行，这里再兜底转义一次分帧字符。
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 对端关闭，忽略。
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
                completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }

        var loop = _readLoop;
        if (loop is not null && !loop.IsCompleted)
        {
            // 管道上的阻塞读不一定响应取消（Windows 匿名管道尤其如此），
            // 超时后直接关闭输入流来解除阻塞，否则插件进程会卡在退出路径上。
            var finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            if (!ReferenceEquals(finished, loop))
            {
                try { _input.Dispose(); }
                catch (Exception) { /* 已关闭 */ }

                try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception) { /* 关闭阶段异常忽略 */ }
            }
        }

        FailPending(new PluginRpcException(PluginProtocol.ErrorInternal, "the peer connection was closed"));

        foreach (var cts in _inFlight.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        _inFlight.Clear();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}
