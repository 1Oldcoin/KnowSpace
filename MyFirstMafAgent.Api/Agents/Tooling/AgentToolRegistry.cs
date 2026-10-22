using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Domain.Settings;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 聚合所有工具来源。单个来源失败（例如某个 MCP 服务器连不上）不会影响其他工具。
/// </summary>
public sealed class AgentToolRegistry(
    IEnumerable<IAgentToolSource> sources,
    IOptions<AgentOptions> options,
    ILogger<AgentToolRegistry> logger)
{
    /// <summary>联网搜索类工具的分类（web_search / fetch_url 等）。</summary>
    private const string WebCategory = "web";

    /// <summary>文件查看类工具的分类：智能体工作目录、本机授权目录、旧版默认分类。</summary>
    private static readonly HashSet<string> FileCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "workspace",
        "local",
        "files",
        "file"
    };

    /// <summary>
    /// 获取本次对话可用的工具。
    /// <paramref name="preferences"/> 为 null 时沿用全局开关的旧行为（MCP 等调用方不受影响）；
    /// 不为 null 时以用户偏好为准，但全局 <see cref="AgentOptions.EnableToolCalling"/> 仍是总闸。
    /// </summary>
    public async Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(
        AgentToolContext context,
        CancellationToken cancellationToken,
        AgentPreferences? preferences = null)
    {
        // 全局开关是总闸：关闭时无论用户如何设置都不可用。
        if (!options.Value.EnableToolCalling) return [];
        if (preferences is not null && !preferences.EnableToolCalling) return [];

        var tools = new List<AgentToolDefinition>();
        foreach (var source in sources)
        {
            try
            {
                tools.AddRange(await source.GetToolsAsync(context, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Tool source {Source} is unavailable", source.Name);
            }
        }

        // 工具名必须唯一；同名时保留先注册的来源（内置工具优先于 MCP）。
        var unique = tools
            .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());

        if (preferences is not null)
            unique = unique.Where(tool => IsAllowed(tool, preferences));

        return unique.ToList();
    }

    /// <summary>按用户开关过滤：联网搜索与会话内文件访问各自独立，知识库与分身工具不受影响。</summary>
    private static bool IsAllowed(AgentToolDefinition tool, AgentPreferences preferences)
    {
        var category = tool.Category ?? string.Empty;
        if (!preferences.EnableWebSearch && string.Equals(category, WebCategory, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!preferences.EnableFileAccess && FileCategories.Contains(category))
            return false;
        return true;
    }
}
