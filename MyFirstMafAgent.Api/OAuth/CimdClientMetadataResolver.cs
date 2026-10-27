using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.OAuth;

/// <summary>CIMD 解析结果。</summary>
public sealed record CimdResolveResult(bool Success, OAuthClientRegistration? Client, string? Error)
{
    public static CimdResolveResult Ok(OAuthClientRegistration client) => new(true, client, null);
    public static CimdResolveResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// CIMD（Client ID Metadata Document）：客户端把 <c>client_id</c> 设成 HTTPS URL，
/// 授权服务器拉取该 URL 下的 JSON 元数据。
///
/// <para>
/// 这是<b>服务端主动发起的出站请求</b>，因此每一条限制都是 SSRF 防线：
/// </para>
/// <list type="number">
///   <item><description>只允许绝对 HTTPS（<c>http</c> 需显式开启，仅本机调试）。</description></item>
///   <item><description>禁止用户信息与 fragment；限制 URL 长度。</description></item>
///   <item><description>禁止重定向（<c>AllowAutoRedirect=false</c>）。</description></item>
///   <item><description>DNS 解析后逐个校验地址，拒绝回环/私有/链路本地/组播/保留段。</description></item>
///   <item><description>连接时<b>直接连已校验的 IP</b>（<c>ConnectCallback</c>），因此不存在 DNS 重绑定窗口。</description></item>
///   <item><description>响应体积与超时都有上限；只接受 JSON。</description></item>
/// </list>
///
/// <para>
/// 元数据本身还要自证：<c>client_id</c> 必须与请求的 URL <b>逐字符一致</b>，
/// 否则任何人都能托管一份声明别人 <c>client_id</c> 的文档。
/// </para>
/// </summary>
public sealed class CimdClientMetadataResolver : IDisposable
{
    private readonly McpOAuthOptions _options;
    private readonly ILogger<CimdClientMetadataResolver> _logger;
    private readonly HttpClient _http;

    public CimdClientMetadataResolver(IOptions<McpOAuthOptions> options, ILogger<CimdClientMetadataResolver> logger)
    {
        _options = options.Value;
        _logger = logger;

        var cimd = _options.Cimd;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(cimd.TimeoutSeconds, 1, 30)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(cimd.TimeoutSeconds, 1, 30))
        };

        // 只请求 JSON；不给压缩协商留空间，减少解压炸弹面。
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyFirstMafAgent-MCP/1.0 (CIMD)");
    }

    /// <summary>client_id 是否是一个可接受的元数据文档地址（纯函数，便于单测）。</summary>
    public static bool IsAllowedClientIdUrl(string? clientId, bool allowInsecureHttp)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 2048) return false;
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var uri)) return false;
        if (uri.Fragment.Length > 0) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.Host.Length == 0) return false;

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;

        return allowInsecureHttp
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>目标地址是否是可连的公网地址（纯函数，便于单测）。</summary>
    public static bool IsAllowedAddress(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (allowPrivate) return true;

        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.Equals(IPAddress.Broadcast)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;

            var v6 = address.GetAddressBytes();
            // 唯一本地地址 fc00::/7
            if ((v6[0] & 0xFE) == 0xFC) return false;
            // 文档/保留段 2001:db8::/32
            if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0D && v6[3] == 0xB8) return false;

            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 => false,                                  // 0.0.0.0/8
            10 => false,                                 // 10.0.0.0/8
            127 => false,                                // 127.0.0.0/8
            169 when bytes[1] == 254 => false,           // 169.254.0.0/16 链路本地（含云元数据）
            172 when bytes[1] is >= 16 and <= 31 => false, // 172.16.0.0/12
            192 when bytes[1] == 168 => false,           // 192.168.0.0/16
            192 when bytes[1] == 0 && bytes[2] == 0 => false, // 192.0.0.0/24
            198 when bytes[1] is 18 or 19 => false,      // 198.18.0.0/15 基准测试
            100 when bytes[1] is >= 64 and <= 127 => false, // 100.64.0.0/10 CGNAT
            >= 224 => false,                             // 组播 + 保留 + 广播
            _ => true
        };
    }

    /// <summary>解析客户端元数据（不做缓存，缓存由用例层负责）。</summary>
    public async Task<CimdResolveResult> ResolveAsync(string clientId, CancellationToken cancellationToken)
    {
        if (!_options.Cimd.Enabled) return CimdResolveResult.Fail("CIMD 未启用。");

        if (!IsAllowedClientIdUrl(clientId, _options.Cimd.AllowInsecureHttp))
            return CimdResolveResult.Fail("client_id 必须是绝对 HTTPS URL，且不能带用户信息或 fragment。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Cimd.TimeoutSeconds, 1, 30)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, clientId);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
                return CimdResolveResult.Fail($"元数据文档返回 {(int)response.StatusCode}。");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return CimdResolveResult.Fail($"元数据文档的 Content-Type 不是 JSON（{mediaType ?? "缺失"}）。");

            var body = await ReadLimitedAsync(response.Content, _options.Cimd.MaxResponseBytes, timeout.Token);
            if (body is null)
                return CimdResolveResult.Fail($"元数据文档超过 {_options.Cimd.MaxResponseBytes} 字节上限。");

            return ParseMetadata(clientId, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CimdResolveResult.Fail("拉取元数据文档超时。");
        }
        catch (HttpRequestException exception)
        {
            // 目标地址被 SSRF 规则拦下时也从这里抛出：不把内部地址细节回给客户端。
            _logger.LogWarning(exception, "CIMD fetch failed for {ClientId}", clientId);
            return CimdResolveResult.Fail("无法拉取元数据文档（地址不可达或被拒绝）。");
        }
    }

    /// <summary>解析并校验元数据 JSON。</summary>
    internal static CimdResolveResult ParseMetadata(string clientId, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return CimdResolveResult.Fail("元数据文档不是合法 JSON。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return CimdResolveResult.Fail("元数据文档必须是 JSON 对象。");

            if (!root.TryGetProperty("client_id", out var clientIdElement)
                || clientIdElement.ValueKind != JsonValueKind.String)
            {
                return CimdResolveResult.Fail("元数据文档缺少 client_id。");
            }

            // 关键校验：client_id 必须与请求的 URL 完全一致。
            if (!string.Equals(clientIdElement.GetString(), clientId, StringComparison.Ordinal))
                return CimdResolveResult.Fail("元数据文档的 client_id 与请求的 URL 不一致。");

            var redirectUris = new List<string>();
            if (root.TryGetProperty("redirect_uris", out var redirectElements)
                && redirectElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in redirectElements.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String) continue;
                    var value = element.GetString();
                    if (OAuthProtocol.IsValidRedirectUri(value) && !redirectUris.Contains(value!, StringComparer.Ordinal))
                        redirectUris.Add(value!);
                }
            }

            if (redirectUris.Count == 0)
                return CimdResolveResult.Fail("元数据文档没有可用的 redirect_uris（必须是绝对地址，非回环时必须是 HTTPS）。");

            if (redirectUris.Count > 10)
                return CimdResolveResult.Fail("redirect_uris 数量超过上限（10）。");

            var clientName = root.TryGetProperty("client_name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(clientName)) clientName = new Uri(clientId).Host;
            if (clientName!.Length > 200) clientName = clientName[..200];

            var grantTypes = ReadStringArray(root, "grant_types");
            if (grantTypes.Count == 0) grantTypes = ["authorization_code", "refresh_token"];
            if (grantTypes.Any(type => type is not ("authorization_code" or "refresh_token")))
                return CimdResolveResult.Fail("只支持 authorization_code 与 refresh_token 两种授权类型。");

            // CIMD 面向公开客户端：带 secret 的机密客户端必须走预注册。
            if (root.TryGetProperty("token_endpoint_auth_method", out var authMethod)
                && authMethod.ValueKind == JsonValueKind.String
                && !string.Equals(authMethod.GetString(), "none", StringComparison.Ordinal))
            {
                return CimdResolveResult.Fail("CIMD 只支持 token_endpoint_auth_method=none 的公开客户端。");
            }

            var requestedScopes = OAuthScopes.Parse(ReadString(root, "scope"));
            if (requestedScopes.Count == 0) requestedScopes = [OAuthScopes.DefaultScope];

            var now = DateTimeOffset.UtcNow;
            var client = new OAuthClientRegistration(
                clientId,
                clientName!,
                redirectUris,
                grantTypes,
                OAuthScopes.Sort(requestedScopes),
                "none",
                OAuthClientSources.Cimd,
                clientId,
                now,
                now);

            return CimdResolveResult.Ok(client);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value) && !result.Contains(value, StringComparer.Ordinal))
                    result.Add(value);
            }
        }

        return result;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var allowPrivate = _options.Cimd.AllowPrivateAddresses;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }

        foreach (var address in addresses)
        {
            if (!IsAllowedAddress(address, allowPrivate)) continue;

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
            }
        }

        throw new HttpRequestException("目标地址不在允许范围内。");
    }

    private static async Task<string?> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;

            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes) return null;
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
