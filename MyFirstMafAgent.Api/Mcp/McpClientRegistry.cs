using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Mcp;

public sealed record McpServerStatus(string Name, bool Enabled, bool Connected, string? Error, int ToolCount);

public sealed record McpRemoteTool(
    string Server,
    string Name,
    string Description,
    string ParametersJsonSchema,
    McpClientTool Tool);

/// <summary>
/// 外部 MCP 服务器的客户端注册表。连接按需建立并缓存，单个服务器不可用时只影响它自己的工具。
/// </summary>
public sealed class McpClientRegistry : IAsyncDisposable
{
    private readonly McpOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientRegistry> _logger;
    private readonly Dictionary<string, Lazy<Task<McpClient?>>> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    private IReadOnlyList<McpRemoteTool> _cachedTools = [];
    private IReadOnlyList<McpServerStatus> _cachedStatuses = [];
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public McpClientRegistry(
        IOptions<McpOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<McpClientRegistry> logger)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;

        foreach (var server in _options.Servers.Where(item => item.Enabled))
        {
            if (string.IsNullOrWhiteSpace(server.Name)) continue;
            _clients[server.Name] = new Lazy<Task<McpClient?>>(
                () => ConnectAsync(server),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public bool HasServers => _clients.Count > 0;

    public async Task<(IReadOnlyList<McpServerStatus> Statuses, IReadOnlyList<McpRemoteTool> Tools)> GetToolsAsync(
        CancellationToken cancellationToken)
    {
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _cacheExpiresAt)
                return (_cachedStatuses, _cachedTools);

            var statuses = new List<McpServerStatus>(_clients.Count);
            var tools = new List<McpRemoteTool>();

            foreach (var (name, lazy) in _clients)
            {
                var client = await lazy.Value.WaitAsync(cancellationToken);
                if (client is null)
                {
                    statuses.Add(new McpServerStatus(name, true, false, _errors.TryGetValue(name, out var error) ? error : "连接失败", 0));
                    continue;
                }

                try
                {
                    var list = await client.ListToolsAsync(cancellationToken: cancellationToken);
                    statuses.Add(new McpServerStatus(name, true, true, null, list.Count));
                    tools.AddRange(list.Select(tool => new McpRemoteTool(
                        name,
                        tool.Name,
                        tool.Description ?? string.Empty,
                        tool.JsonSchema.GetRawText(),
                        tool)));
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Failed to list MCP tools from server {Server}", name);
                    statuses.Add(new McpServerStatus(name, true, false, exception.Message, 0));
                }
            }

            _cachedTools = tools;
            _cachedStatuses = statuses;
            _cacheExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30);
            return (statuses, tools);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<McpClient?> ConnectAsync(McpRemoteServerOptions server)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(server.TimeoutSeconds, 5, 600));
            var transport = CreateTransport(server, timeout);
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "MyFirstMafAgent", Version = "1.0.0" }
                },
                _loggerFactory,
                CancellationToken.None);

            _errors.TryRemove(server.Name, out _);
            _logger.LogInformation("Connected to MCP server {Server} ({Transport})", server.Name, server.Transport);
            return client;
        }
        catch (Exception exception)
        {
            _errors[server.Name] = exception.Message;
            _logger.LogWarning(exception, "Failed to connect to MCP server {Server}", server.Name);
            return null;
        }
    }

    private IClientTransport CreateTransport(McpRemoteServerOptions server, TimeSpan timeout)
    {
        if (string.Equals(server.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                throw new InvalidOperationException($"MCP 服务器 {server.Name} 使用 stdio 传输但未配置 Command。");

            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command,
                Arguments = server.Arguments,
                WorkingDirectory = server.WorkingDirectory
            }, _loggerFactory);
        }

        if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException($"MCP 服务器 {server.Name} 的 Endpoint 不是有效的绝对地址。");

        var httpClient = new HttpClient { Timeout = timeout };
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = server.Headers,
            ConnectionTimeout = timeout
        }, httpClient, _loggerFactory);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _clients.Values)
        {
            if (!lazy.IsValueCreated) continue;
            var client = await lazy.Value;
            if (client is not null) await client.DisposeAsync();
        }

        _cacheGate.Dispose();
    }
}
