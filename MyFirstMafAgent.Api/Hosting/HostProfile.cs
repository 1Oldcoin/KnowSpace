using System.Net;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>宿主形态：本机桌面（回环 + 一次性令牌）或服务端（公网/反代后面）。</summary>
public enum HostProfile
{
    Desktop,
    Server
}

/// <summary>解析后的宿主形态与判定依据（依据会打到启动日志里）。</summary>
public sealed record HostProfileInfo(HostProfile Profile, string Reason)
{
    public bool IsServer => Profile == HostProfile.Server;
}

/// <summary>
/// 决定本次进程按哪种形态运行。形态在启动期一次性确定，之后不再切换。
///
/// <para>
/// <c>Auto</c>（默认）的规则刻意偏向 Server：只要监听了任何非回环地址就按服务端处理，
/// 这样"把桌面版直接丢到云主机上 <c>--urls http://0.0.0.0:5056</c>"会触发服务端的启动检查，
/// 而不是带着"本机即可信"的假设裸奔。反代后面监听回环的部署必须显式设 <c>Server</c>。
/// </para>
/// </summary>
public static class HostProfileResolver
{
    public const string ConfigurationKey = "Hosting:Profile";

    public static HostProfileInfo Resolve(IConfiguration configuration, string? explicitUrls = null)
    {
        var configured = configuration[ConfigurationKey]?.Trim();

        if (!string.IsNullOrEmpty(configured) && !configured.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            if (configured.Equals(nameof(HostProfile.Desktop), StringComparison.OrdinalIgnoreCase))
                return new HostProfileInfo(HostProfile.Desktop, $"{ConfigurationKey}=Desktop");

            if (configured.Equals(nameof(HostProfile.Server), StringComparison.OrdinalIgnoreCase))
                return new HostProfileInfo(HostProfile.Server, $"{ConfigurationKey}=Server");

            throw new InvalidOperationException(
                $"{ConfigurationKey} 只能是 Auto / Desktop / Server，当前值：'{configured}'。");
        }

        var exposed = CollectListenUrls(configuration, explicitUrls).FirstOrDefault(url => !IsLoopbackUrl(url));
        if (exposed is not null)
            return new HostProfileInfo(HostProfile.Server, $"Auto：监听了非回环地址 {exposed}");

        return new HostProfileInfo(HostProfile.Desktop, "Auto：仅监听回环地址");
    }

    internal static IEnumerable<string> CollectListenUrls(IConfiguration configuration, string? explicitUrls)
    {
        var urls = !string.IsNullOrWhiteSpace(explicitUrls) ? explicitUrls : configuration["urls"];

        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return url;
        }
        else
        {
            // 没有 urls 时 ASP.NET 会按 HTTP(S)_PORTS 绑定所有网卡（aspnet 镜像默认 8080）。
            foreach (var key in new[] { "http_ports", "https_ports" })
            {
                var ports = configuration[key];
                if (string.IsNullOrWhiteSpace(ports)) continue;
                foreach (var port in ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return $"http://*:{port}";
            }
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url)) yield return url.Trim();
        }
    }

    internal static bool IsLoopbackUrl(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;

        var rest = url[(schemeEnd + 3)..];
        string host;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0) return false;
            host = rest[1..close];
        }
        else
        {
            var end = rest.IndexOfAny([':', '/']);
            host = end < 0 ? rest : rest[..end];
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
