using ModelContextProtocol.Protocol;
using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Mcp;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>MCP 服务端是否启用及挂载路径。</summary>
public sealed record McpServerRegistration(bool Enabled, string Path);

/// <summary>
/// MCP 模块：把本机知识库以 Streamable HTTP 暴露给外部智能体，并维护外部 MCP 客户端注册表。
/// 工具实现只做协议适配，检索/写入必须委托给知识应用服务。
/// </summary>
public static class McpModule
{
    public static McpServerRegistration AddMcpModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<McpClientRegistry>();

        // 外部接入方：客户端密钥校验、限流与工具准入都依赖这三个服务。
        services.AddHttpContextAccessor();
        services.AddSingleton<AgentClientService>();
        services.AddSingleton<AgentRateLimiter>();

        var enabled = configuration.GetValue<bool?>($"{McpOptions.SectionName}:EnableServer") ?? true;
        var path = configuration.GetValue<string?>($"{McpOptions.SectionName}:ServerPath");
        if (string.IsNullOrWhiteSpace(path)) path = "/mcp";

        if (enabled)
        {
            services
                .AddMcpServer(options => options.ServerInfo = new Implementation
                {
                    Name = "MyFirstMafAgent.Knowledge",
                    Version = "1.0.0"
                })
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools<KnowledgeMcpTools>()
                .WithTools<LocalFileMcpTools>()
                .WithTools<AvatarMcpTools>()
                .WithTools<ChatHistoryMcpTools>();
        }

        return new McpServerRegistration(enabled, path);
    }
}
