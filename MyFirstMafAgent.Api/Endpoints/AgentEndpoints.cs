using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Mcp;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Endpoints;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/agent").WithTags("AI 智能体");

        api.MapGet("/tools", async (
            Guid? knowledgeBaseId,
            AgentToolRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var tools = await registry.GetToolsAsync(new AgentToolContext(knowledgeBaseId), cancellationToken);
            return Results.Ok(new
            {
                count = tools.Count,
                tools = tools.Select(tool => new
                {
                    tool.Name,
                    tool.Description,
                    tool.Category
                })
            });
        });

        api.MapGet("/info", (
            IOptions<AgentOptions> agentOptions,
            IOptions<McpOptions> mcpOptions,
            AgentFileAccess fileAccess) =>
        {
            var agent = agentOptions.Value;
            var mcp = mcpOptions.Value;
            return Results.Ok(new
            {
                toolCallingEnabled = agent.EnableToolCalling,
                maxToolIterations = agent.MaxToolIterations,
                workspaceWritable = agent.AllowWorkspaceWrite,
                maxReadChars = agent.MaxReadChars,
                maxToolResultChars = agent.MaxToolResultChars,
                mcpBridgeEnabled = agent.EnableMcpToolBridge,
                fileRoots = fileAccess.Roots.Select(root => new
                {
                    root.Name,
                    root.Path,
                    writable = root.AllowWrite,
                    deletable = root.AllowDelete,
                    exists = Directory.Exists(root.Path)
                }),
                mcpServerEnabled = mcp.EnableServer,
                mcpServerPath = mcp.ServerPath,
                mcpClientEnabled = mcp.EnableClient,
                mcpServers = mcp.Servers
                    .Where(server => server.Enabled)
                    .Select(server => new { server.Name, server.Transport, server.Endpoint })
            });
        });

        var mcp = endpoints.MapGroup("/api/mcp").WithTags("MCP");

        mcp.MapGet("/status", async (
            McpClientRegistry registry,
            IOptions<McpOptions> mcpOptions,
            CancellationToken cancellationToken) =>
        {
            var (statuses, tools) = await registry.GetToolsAsync(cancellationToken);
            return Results.Ok(new
            {
                serverEnabled = mcpOptions.Value.EnableServer,
                serverPath = mcpOptions.Value.ServerPath,
                connectedCount = statuses.Count(status => status.Connected),
                toolCount = tools.Count,
                servers = statuses.Select(status => new
                {
                    status.Name,
                    status.Connected,
                    status.Error,
                    status.ToolCount
                }),
                tools = tools.Select(tool => new { tool.Server, tool.Name, tool.Description })
            });
        });
    }
}
