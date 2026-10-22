using System.Net;
using System.Net.Sockets;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 联网工具的地址护栏：只允许访问公网 http/https 地址，
/// 拒绝 localhost、环回、私网、链路本地与云元数据等地址，避免模型被诱导访问本机或内网服务（SSRF）。
/// </summary>
public static class SafeUrlGuard
{
    /// <summary>解析并校验地址；失败时通过 <paramref name="error"/> 返回中文原因。</summary>
    public static bool TryValidate(string? rawUrl, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "url 不能为空。";
            return false;
        }

        if (!Uri.TryCreate(rawUrl.Trim().Trim('"', '<', '>'), UriKind.Absolute, out var parsed))
        {
            error = "url 不是合法的绝对地址。";
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            error = $"只允许访问 http/https 地址，当前是 {parsed.Scheme}。";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "url 不能包含用户名或密码。";
            return false;
        }

        if (IsBlockedHost(parsed.Host))
        {
            error = $"出于安全考虑，不允许访问本机或内网地址：{parsed.Host}。";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>主机名或 IP 字面量是否指向本机/内网。</summary>
    public static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        var normalized = host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        if (normalized is "localhost" or "localhost.localdomain" or "ip6-localhost" or "ip6-loopback")
            return true;
        if (normalized.EndsWith(".localhost", StringComparison.Ordinal)
            || normalized.EndsWith(".local", StringComparison.Ordinal)
            || normalized.EndsWith(".internal", StringComparison.Ordinal))
            return true;

        if (!IPAddress.TryParse(normalized, out var address)) return false;

        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                       // 0.0.0.0/8
                10 => true,                                      // 10.0.0.0/8
                127 => true,                                     // 环回
                169 when b[1] == 254 => true,                    // 链路本地 / 云元数据 169.254.169.254
                172 when b[1] is >= 16 and <= 31 => true,        // 172.16.0.0/12
                192 when b[1] == 168 => true,                    // 192.168.0.0/16
                100 when b[1] is >= 64 and <= 127 => true,       // 运营商级 NAT 100.64.0.0/10
                >= 224 => true,                                  // 组播与保留
                _ => false
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            var bytes = address.GetAddressBytes();
            // 唯一本地地址 fc00::/7
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            // ::1 已由 IsLoopback 覆盖；:: 为未指定地址
            if (address.Equals(IPAddress.IPv6Any)) return true;
        }

        return false;
    }
}
