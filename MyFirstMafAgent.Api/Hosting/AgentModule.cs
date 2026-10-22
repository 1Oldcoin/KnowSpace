using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Agents;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Infrastructure.Settings;
using MyFirstMafAgent.Api.Mcp;
using MyFirstMafAgent.Api.Models;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 智能体模块：模型客户端、运行期设置/偏好与工具链。
/// 工具通过 <see cref="IAgentToolSource"/> 聚合，MCP 与后续插件都作为适配层接入，
/// 桌面/控制台/插件不直接实例化具体工具。
/// </summary>
public static class AgentModule
{
    public static IServiceCollection AddAgentModule(this IServiceCollection services)
    {
        // 运行期设置：设置页保存后立即生效，无需重启。
        services.AddSingleton<ILlmSettingsAccessor>(sp =>
            new LlmSettingsProvider(sp.GetRequiredService<IOptions<LlmOptions>>()));
        services.AddSingleton<IRuntimeSettingsProvider, RuntimeSettingsProvider>();
        services.AddSingleton<AgentPreferenceProvider>();

        services.AddHttpClient<OpenAiCompatibleModelClient>(client => client.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient("embedding", client => client.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient(WebSearchToolSource.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton<AgentWorkspace>();
        services.AddSingleton<AgentFileAccess>();
        services.AddScoped<IAgentToolSource, WorkspaceToolSource>();
        services.AddScoped<IAgentToolSource, KnowledgeToolSource>();
        services.AddScoped<IAgentToolSource, LocalFileToolSource>();
        services.AddScoped<IAgentToolSource, AvatarToolSource>();
        services.AddScoped<IAgentToolSource, McpToolSource>();
        services.AddScoped<IAgentToolSource, WebSearchToolSource>();
        services.AddScoped<IAgentToolSource, UtilityToolSource>();
        services.AddScoped<AgentToolRegistry>();
        services.AddScoped<ToolCallingAgent>();

        return services;
    }
}
