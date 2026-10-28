using System.Net.Http.Headers;
using MyFirstMafAgent.Api.Agents.Tooling;

namespace MyFirstMafAgent.Api.Models;

/// <summary>
/// OpenAI 兼容端点的 <c>Authorization</c> 头设置规则：全项目唯一出口。
///
/// <para>
/// 曾经的写法是「密钥为空就发 <c>Bearer local</c>」，本意是照顾 LM Studio 这类本机服务
/// —— 它们不校验密钥，有些甚至要求这个头必须存在。问题在于这条兜底对**公网端点**一视同仁：
/// <c>Llm:Online:ApiKey</c> 留空时照样发 <c>Bearer local</c>，远端只能回一句
/// <c>Your api key: ****ocal is invalid</c>（「****ocal」就是被截断的 local）。
/// 这句话看不出出处，形似「密钥泄露」或「密钥填错了」，排障方向直接被带偏；
/// 而真实原因只是少填了一个配置项。
/// </para>
///
/// <para>
/// 现在按端点位置分流：
/// <list type="bullet">
///   <item>有密钥 → 原样使用，不做任何判断。</item>
///   <item>无密钥 + 本机/内网端点 → 继续发占位值 <c>local</c>，行为与以前一致。</item>
///   <item>无密钥 + 公网端点 → 直接抛异常，报错里写清缺哪个配置项、有哪几种填法。</item>
/// </list>
/// 于是「摘要生成失败」变成一句能照着做的中文提示，而不是远端 401 的回声。
/// </para>
///
/// <para>
/// 为什么不在启动时用 <c>ValidateOnStart</c> 直接拒绝启动：密钥可以在界面「设置 → 模型」
/// 里保存，而那份配置存在数据库里、由 <c>DatabaseInitializer</c> 在启动中途才加载。
/// 启动校验只看得到 appsettings，会把「密钥在库里」的部署全部误杀。
/// </para>
/// </summary>
internal static class ModelEndpointAuthorization
{
    /// <summary>本机模型的占位密钥，LM Studio / Ollama / vLLM 的通行约定。</summary>
    public const string LocalPlaceholderKey = "local";

    /// <summary>
    /// 给请求设置 <c>Authorization</c> 头；公网端点缺密钥时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="request">待发送的请求。</param>
    /// <param name="endpoint">实际请求的绝对地址（用来判断是公网还是内网）。</param>
    /// <param name="apiKey">配置里的密钥，可为空。</param>
    /// <param name="missingSetting">为空时要在报错里点名的配置项，例如 <c>Llm:Online:ApiKey</c>。</param>
    /// <param name="remedy">针对该配置项的填法说明（调用方各写各的，因为入口不同）。</param>
    public static void Apply(
        HttpRequestMessage request,
        Uri endpoint,
        string? apiKey,
        string missingSetting,
        string remedy)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            return;
        }

        // 回环与内网：本机模型服务通常不校验密钥，保留占位值以维持旧行为。
        // 复用 SafeUrlGuard 的判断，避免再写一套 172.16/12、100.64/10 之类的地址段。
        if (SafeUrlGuard.IsBlockedHost(endpoint.Host))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LocalPlaceholderKey);
            return;
        }

        throw new InvalidOperationException(
            $"端点 {endpoint} 是公网地址，必须有 API Key，但 {missingSetting} 为空。"
            + Environment.NewLine
            + "（本机 / 局域网模型服务允许留空，公网端点不允许：留空会退化成占位密钥 "
            + $"'{LocalPlaceholderKey}'，远端只会回 401，并在报错里显示成 ****ocal，"
            + "看起来像密钥填错，实际是没填。）"
            + Environment.NewLine
            + remedy);
    }
}
