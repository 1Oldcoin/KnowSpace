using System.Net;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 同意页的临时访问闸门（Phase 0）。
///
/// <para>
/// 直接暴露在公网的 <c>/oauth/authorize</c> 等于一个"自助发令牌"页面：任何人注册一个客户端、
/// 打开同意页、勾满 scope（含 <c>files:write</c> / <c>chat:read</c>），就能拿到长期凭据。
/// 在账号登录（Phase 1）就绪之前，先用两条最小可行条件把口子收紧：
/// </para>
///
/// <list type="number">
///   <item><description><b>回环来源</b>：本机浏览器直接访问，与桌面版行为一致。</description></item>
///   <item><description><b>管理令牌</b>：请求带正确 <c>X-Api-Token</c>。浏览器自己加不了头，但反向代理
///   可以在完成自己的认证后注入（本项目既有的部署方式），因此"公网 + 反代认证"仍然可用。</description></item>
/// </list>
///
/// <para>
/// <b>它不替代 Phase 1 的账号体系</b>：这里没有用户身份，只有"是否处在一个受信任的通道上"。
/// </para>
/// </summary>
public static class ConsentAccessGuard
{
    public static string? Validate(HttpContext context)
    {
        if (IsLoopback(context.Connection.RemoteIpAddress)) return null;

        // 直接取已注册的单例：桌面安全选项不是 IOptions<T>，它是启动期算好的一份快照。
        var options = context.RequestServices.GetRequiredService<DesktopSecurityOptions>();
        if (!options.Enabled)
        {
            return "服务端未配置管理令牌，授权页只能从本机访问。请设置 MAF_SERVER_TOKEN（openssl rand -hex 32）。";
        }

        var provided = context.Request.Headers[DesktopSecurityOptions.TokenHeaderName].ToString();
        if (!string.IsNullOrEmpty(provided)
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(provided),
                System.Text.Encoding.UTF8.GetBytes(options.Token ?? string.Empty)))
        {
            return null;
        }

        return $"来源 {context.Connection.RemoteIpAddress} 既不是本机，也没有携带有效的 " +
               $"{DesktopSecurityOptions.TokenHeaderName}。";
    }

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);
}
