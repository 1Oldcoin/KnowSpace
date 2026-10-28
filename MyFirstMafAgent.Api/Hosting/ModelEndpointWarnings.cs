using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Settings;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 启动期的模型端点自检：把「少填一个配置项」提前成一条日志，而不是等用户第一次提问才 401。
/// </summary>
internal static class ModelEndpointWarnings
{
    /// <summary>
    /// 聊天模型指向公网端点、却没有配密钥时给出警告。
    ///
    /// <para>
    /// 为什么值得单开一条：这个组合不会导致启动失败，只会让问答、摘要、分身与伴侣对话
    /// 全部在调用时失败（远端 401）。外部同事部署 Docker 时正是踩在这里——
    /// 上传资料、切块、向量化都正常，只有「摘要」这一步悄悄失败。
    /// </para>
    ///
    /// <para>
    /// 必须在 <c>DatabaseInitializer.InitializeAsync</c> **之后**调用：界面里保存的模型配置
    /// 存在数据库中，启动中途才被读进 <see cref="ILlmSettingsAccessor"/>。放在这之前会误报。
    /// </para>
    ///
    /// <para>
    /// 本机 / 局域网端点不告警：LM Studio、Ollama 这类服务不校验密钥，
    /// 留空是正常用法（<c>ModelEndpointAuthorization</c> 会补占位值）。
    /// </para>
    /// </summary>
    public static void WarnMissingLlmApiKey(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<ILlmSettingsAccessor>().Current;
        var selected = settings.UseCloudApi ? settings.Online : settings.Local;
        if (selected is null || string.IsNullOrWhiteSpace(selected.Endpoint)) return;

        if (!Uri.TryCreate(selected.Endpoint, UriKind.Absolute, out var endpoint)) return;
        if (SafeUrlGuard.IsBlockedHost(endpoint.Host)) return;
        if (!string.IsNullOrWhiteSpace(selected.ApiKey)) return;

        var section = settings.UseCloudApi ? "Online" : "Local";
        app.Logger.LogWarning(
            "聊天模型使用公网端点 {Endpoint}（模型 {Model}），但没有配置 API Key："
            + "知识问答、资料摘要、AI 伴侣与我的分身都会在调用时失败（远端 401）。"
            + "填法二选一：环境变量 Llm__{Section}__ApiKey=<密钥>，或界面「设置 → 模型」填入后保存。",
            selected.Endpoint, selected.Model, section);
    }
}
