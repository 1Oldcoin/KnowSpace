using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Domain.Agents;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.OAuth;
using MyFirstMafAgent.Api.Options;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>OAuth scope 与内部能力词表的映射（只有一套词表）。</summary>
public sealed class OAuthScopeTests
{
    [Theory]
    [InlineData("knowledge:read", "knowledge:read")]
    [InlineData("knowspace:read", "knowledge:read")]
    [InlineData("knowspace:write", "knowledge:write")]
    [InlineData("kb:read", "knowledge:read")]
    [InlineData("files:write", "files:write")]
    [InlineData("knowledge:*", "knowledge:*")]
    [InlineData("*", "*")]
    public void Known_scopes_canonicalize(string input, string expected) =>
        Assert.Equal(expected, OAuthScopes.Canonicalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("unknown:read")]
    [InlineData("admin")]
    [InlineData(null)]
    public void Unknown_scopes_are_rejected(string? input) =>
        Assert.Null(OAuthScopes.Canonicalize(input));

    [Fact]
    public void Parse_drops_unknown_and_deduplicates()
    {
        var scopes = OAuthScopes.Parse("knowspace:read knowledge:read files:read nope:read");

        Assert.Equal(new[] { "knowledge:read", "files:read" }, scopes);
        Assert.Equal(new[] { "nope:read" }, OAuthScopes.ParseUnknown("knowspace:read nope:read"));
    }

    [Fact]
    public void Wildcard_scope_expands_to_concrete_capabilities()
    {
        var capabilities = OAuthScopes.ToCapabilities(["knowledge:*"]);

        Assert.Contains(AgentClientCapabilities.KnowledgeRead, capabilities);
        Assert.Contains(AgentClientCapabilities.KnowledgeWrite, capabilities);
        Assert.DoesNotContain(AgentClientCapabilities.FilesRead, capabilities);
    }

    [Fact]
    public void Global_wildcard_expands_to_every_known_capability() =>
        Assert.Equal(AgentClientCapabilities.AllKnown, OAuthScopes.ToCapabilities(["*"]));

    [Theory]
    [InlineData("knowledge:write", true)]
    [InlineData("files:write", true)]
    [InlineData("knowledge:read", false)]
    public void Write_scopes_are_detected(string scope, bool expected) =>
        Assert.Equal(expected, OAuthScopes.IsWriteScope(scope));
}

/// <summary>PKCE、重定向地址与资源标识的纯函数。</summary>
public sealed class OAuthProtocolTests
{
    [Fact]
    public void Pkce_matches_rfc7636_example()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthProtocol.ComputeCodeChallenge(verifier));
        Assert.True(OAuthProtocol.VerifyCodeChallenge(verifier, OAuthProtocol.ComputeCodeChallenge(verifier), "S256"));
        Assert.False(OAuthProtocol.VerifyCodeChallenge(verifier, "wrong", "S256"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("bad verifier with spaces and more characters to reach the length limit easily")]
    public void Invalid_verifiers_are_rejected(string verifier) =>
        Assert.False(OAuthProtocol.IsValidCodeVerifier(verifier));

    [Fact]
    public void Plain_pkce_is_rejected() =>
        Assert.False(OAuthProtocol.VerifyCodeChallenge(
            "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk",
            "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk",
            "plain"));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://127.0.0.1:33418/callback", true)]
    [InlineData("http://localhost:1455/auth/callback", true)]
    [InlineData("http://example.com/callback", false)]
    [InlineData("https://example.com/callback#frag", false)]
    [InlineData("https://user:pass@example.com/callback", false)]
    [InlineData("not-a-uri", false)]
    public void Redirect_uri_rules_follow_rfc8252(string value, bool expected) =>
        Assert.Equal(expected, OAuthProtocol.IsValidRedirectUri(value));

    [Fact]
    public void Resource_and_issuer_are_derived_from_configuration_only()
    {
        var options = new McpOAuthOptions
        {
            Enabled = true,
            Issuer = "https://knowspace.example.com"
        };

        var context = OAuthServerContext.TryCreate(options, "/mcp");

        Assert.NotNull(context);
        Assert.Equal("https://knowspace.example.com", context!.IssuerString);
        Assert.Equal("https://knowspace.example.com/mcp", context.ResourceString);
        Assert.Equal("https://knowspace.example.com/.well-known/oauth-protected-resource",
            context.ProtectedResourceMetadataUrl);
    }

    [Fact]
    public void Oauth_is_disabled_without_a_valid_issuer()
    {
        Assert.Null(OAuthServerContext.TryCreate(new McpOAuthOptions { Enabled = true }, "/mcp"));
        Assert.Null(OAuthServerContext.TryCreate(
            new McpOAuthOptions { Enabled = true, Issuer = "http://knowspace.example.com" }, "/mcp"));
    }

    [Fact]
    public void Loopback_issuer_is_allowed_for_local_development()
    {
        var context = OAuthServerContext.TryCreate(
            new McpOAuthOptions { Enabled = true, Issuer = "http://127.0.0.1:5057" }, "/mcp");

        Assert.NotNull(context);
        Assert.Equal("http://127.0.0.1:5057/mcp", context!.ResourceString);
    }
}

/// <summary>访问令牌的签发与校验（ES256 / at+jwt）。</summary>
public sealed class OAuthTokenServiceTests : IDisposable
{
    private const string Issuer = "https://knowspace.test";
    private const string Resource = "https://knowspace.test/mcp";

    private readonly string _keyDirectory;

    public OAuthTokenServiceTests()
    {
        _keyDirectory = Path.Combine(Path.GetTempPath(), "maf-oauth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_keyDirectory);
    }

    private OAuthTokenService CreateService(int lifetimeMinutes = 60)
    {
        var options = new McpOAuthOptions
        {
            Enabled = true,
            Issuer = Issuer,
            Resource = Resource,
            AccessTokenLifetimeMinutes = lifetimeMinutes,
            SigningKeyPath = Path.Combine(_keyDirectory, "signing-key.pem")
        };

        return new OAuthTokenService(
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<OAuthTokenService>.Instance);
    }

    private static OAuthAccessTokenRequest Request() =>
        new("owner", "dcr_client", "oc_1", ["knowledge:read"], Resource, Issuer);

    [Fact]
    public void Issued_token_validates_and_round_trips_claims()
    {
        var service = CreateService();
        var now = DateTimeOffset.UtcNow;

        var token = service.CreateAccessToken(Request(), now);

        Assert.True(service.TryValidate(token, Issuer, Resource, now, out var parsed, out var failure));
        Assert.Equal(OAuthTokenValidationFailure.None, failure);
        Assert.NotNull(parsed);
        Assert.Equal("owner", parsed!.Subject);
        Assert.Equal("dcr_client", parsed.ClientId);
        Assert.Equal("oc_1", parsed.ConsentId);
        Assert.Equal(new[] { "knowledge:read" }, parsed.Scopes);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Jti));
    }

    [Fact]
    public void Token_audience_is_enforced()
    {
        var service = CreateService();
        var now = DateTimeOffset.UtcNow;
        var token = service.CreateAccessToken(Request(), now);

        Assert.False(service.TryValidate(token, Issuer, "https://other.example.com/mcp", now,
            out _, out var failure));
        Assert.Equal(OAuthTokenValidationFailure.WrongAudience, failure);
    }

    [Fact]
    public void Token_issuer_is_enforced()
    {
        var service = CreateService();
        var now = DateTimeOffset.UtcNow;
        var token = service.CreateAccessToken(Request(), now);

        Assert.False(service.TryValidate(token, "https://evil.test", Resource, now, out _, out var failure));
        Assert.Equal(OAuthTokenValidationFailure.WrongIssuer, failure);
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var service = CreateService(lifetimeMinutes: 1);
        var issuedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var token = service.CreateAccessToken(Request(), issuedAt);

        Assert.False(service.TryValidate(token, Issuer, Resource, DateTimeOffset.UtcNow, out _, out var failure));
        Assert.Equal(OAuthTokenValidationFailure.Expired, failure);
    }

    [Fact]
    public void Tampered_token_is_rejected()
    {
        var service = CreateService();
        var now = DateTimeOffset.UtcNow;
        var token = service.CreateAccessToken(Request(), now);

        var parts = token.Split('.');
        var payload = Encoding.UTF8.GetString(OAuthProtocol.Base64UrlDecode(parts[1])!);
        var forgedPayload = payload.Replace("knowledge:read", "files:write", StringComparison.Ordinal);
        var forged = parts[0] + "." + OAuthProtocol.Base64UrlEncode(Encoding.UTF8.GetBytes(forgedPayload)) + "." + parts[2];

        Assert.False(service.TryValidate(forged, Issuer, Resource, now, out _, out var failure));
        Assert.Equal(OAuthTokenValidationFailure.BadSignature, failure);
    }

    [Fact]
    public void Algorithm_none_is_rejected()
    {
        var service = CreateService();
        var header = OAuthProtocol.Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"at+jwt"}"""));
        var payload = OAuthProtocol.Base64UrlEncode(Encoding.UTF8.GetBytes(
            $$"""{"iss":"{{Issuer}}","aud":"{{Resource}}","sub":"owner","client_id":"c","jti":"j","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""));

        Assert.False(service.TryValidate(header + "." + payload + ".AAAA", Issuer, Resource, DateTimeOffset.UtcNow,
            out _, out var failure));
        Assert.Equal(OAuthTokenValidationFailure.UnsupportedAlgorithm, failure);
    }

    [Fact]
    public void Jwks_exposes_only_the_public_key()
    {
        var service = CreateService();
        var jwks = service.CreateJwks().ToJsonString();

        Assert.Contains("\"kty\":\"EC\"", jwks);
        Assert.Contains("\"crv\":\"P-256\"", jwks);
        Assert.DoesNotContain("d\"", jwks); // 私钥参数绝不能出现在 JWKS 里
    }

    [Fact]
    public void Signing_key_is_persisted_so_tokens_survive_a_restart()
    {
        var first = CreateService();
        var now = DateTimeOffset.UtcNow;
        var token = first.CreateAccessToken(Request(), now);

        var second = CreateService(); // 同一个 key 路径 → 同一把密钥
        Assert.True(second.TryValidate(token, Issuer, Resource, now, out _, out var failure));
        Assert.Equal(OAuthTokenValidationFailure.None, failure);
        Assert.Equal(first.KeyId, second.KeyId);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_keyDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}

/// <summary>CIMD 的 URL/地址策略与元数据自证校验。</summary>
public sealed class CimdPolicyTests
{
    [Theory]
    [InlineData("https://claude.ai/.well-known/mcp-client", false, true)]
    [InlineData("http://claude.ai/client.json", false, false)]
    [InlineData("http://127.0.0.1:5199/client.json", true, true)]
    [InlineData("https://user:pass@claude.ai/client.json", false, false)]
    [InlineData("https://claude.ai/client.json#frag", false, false)]
    [InlineData("file:///etc/passwd", false, false)]
    public void Client_id_url_policy(string url, bool allowInsecureHttp, bool expected) =>
        Assert.Equal(expected, CimdClientMetadataResolver.IsAllowedClientIdUrl(url, allowInsecureHttp));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)] // 云元数据端点
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Private_and_special_addresses_are_blocked(string address, bool expected) =>
        Assert.Equal(expected, CimdClientMetadataResolver.IsAllowedAddress(IPAddress.Parse(address), allowPrivate: false));

    [Fact]
    public void Private_addresses_allowed_only_when_explicitly_enabled() =>
        Assert.True(CimdClientMetadataResolver.IsAllowedAddress(IPAddress.Parse("127.0.0.1"), allowPrivate: true));

    [Fact]
    public void Metadata_document_must_echo_its_client_id()
    {
        const string url = "https://claude.ai/client.json";
        var json = $$"""
        {
          "client_id": "https://attacker.example/client.json",
          "redirect_uris": ["https://claude.ai/callback"]
        }
        """;

        var result = CimdClientMetadataResolver.ParseMetadata(url, json);

        Assert.False(result.Success);
        Assert.Contains("不一致", result.Error);
    }

    [Fact]
    public void Valid_metadata_document_is_accepted()
    {
        const string url = "https://claude.ai/client.json";
        var json = $$"""
        {
          "client_id": "{{url}}",
          "client_name": "Claude",
          "redirect_uris": ["https://claude.ai/api/mcp/auth_callback"],
          "grant_types": ["authorization_code", "refresh_token"],
          "token_endpoint_auth_method": "none",
          "scope": "knowspace:read"
        }
        """;

        var result = CimdClientMetadataResolver.ParseMetadata(url, json);

        Assert.True(result.Success);
        Assert.NotNull(result.Client);
        Assert.Equal(OAuthClientSources.Cimd, result.Client!.Source);
        Assert.Equal(new[] { "knowledge:read" }, result.Client.Scopes);
        Assert.True(result.Client.IsPublicClient);
    }

    [Fact]
    public void Metadata_without_usable_redirect_uri_is_rejected()
    {
        const string url = "https://claude.ai/client.json";
        var json = $$"""
        {
          "client_id": "{{url}}",
          "redirect_uris": ["http://evil.example/callback"]
        }
        """;

        Assert.False(CimdClientMetadataResolver.ParseMetadata(url, json).Success);
    }

    [Fact]
    public void Confidential_clients_must_not_use_cimd()
    {
        const string url = "https://claude.ai/client.json";
        var json = $$"""
        {
          "client_id": "{{url}}",
          "redirect_uris": ["https://claude.ai/callback"],
          "token_endpoint_auth_method": "client_secret_basic"
        }
        """;

        Assert.False(CimdClientMetadataResolver.ParseMetadata(url, json).Success);
    }
}

/// <summary>OAuth 主体 → 既有 AgentClient 形状的翻译（闸门复用同一套判定）。</summary>
public sealed class OAuthPrincipalTests
{
    [Fact]
    public void Principal_becomes_an_agent_client_with_scope_derived_capabilities()
    {
        var principal = new OAuthPrincipal(
            "dcr_client",
            "Claude",
            "owner",
            "oc_1",
            ["knowledge:read", "files:read"],
            [Guid.Parse("22222222-2222-2222-2222-222222222222")],
            DateTimeOffset.UtcNow.AddHours(1),
            "jti");

        var client = principal.ToAgentClient(requestsPerMinute: 60);

        Assert.StartsWith("oauth:", client.Id);
        Assert.Equal("Claude", client.Name);
        Assert.Contains(AgentClientCapabilities.KnowledgeRead, client.Capabilities);
        Assert.Contains(AgentClientCapabilities.FilesRead, client.Capabilities);
        Assert.DoesNotContain(AgentClientCapabilities.KnowledgeWrite, client.Capabilities);
        Assert.True(client.IsKnowledgeBaseRestricted);
        Assert.Equal(60, client.RequestsPerMinute);
    }

    [Fact]
    public void Write_scope_is_required_for_write_tools()
    {
        var readOnly = new OAuthPrincipal("c", "n", "owner", "oc", ["knowledge:read"], [],
            DateTimeOffset.UtcNow.AddHours(1), "jti").ToAgentClient(60);

        var decision = MyFirstMafAgent.Api.Application.Agents.AgentCallGate.Decide(
            readOnly, "upload_document", null, Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.False(decision.Allowed);
        Assert.Contains("knowledge:write", decision.Message);
    }
}

/// <summary>本地配置一键复制与 Server Card 的形状。</summary>
public sealed class LocalMcpConfigTests
{
    [Fact]
    public void Cursor_config_contains_url_and_authorization_header()
    {
        var json = LocalMcpConfig.BuildCursorConfig("http://127.0.0.1:5057", "/mcp", "maf_abc");
        using var document = JsonDocument.Parse(json);

        var server = document.RootElement.GetProperty("mcpServers").GetProperty(LocalMcpConfig.ServerKey);
        Assert.Equal("http://127.0.0.1:5057/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer maf_abc", server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void Claude_desktop_config_uses_mcp_remote()
    {
        var json = LocalMcpConfig.BuildClaudeDesktopConfig("http://127.0.0.1:5057", "/mcp", "maf_abc");
        using var document = JsonDocument.Parse(json);

        var server = document.RootElement.GetProperty("mcpServers").GetProperty(LocalMcpConfig.ServerKey);
        Assert.Equal("npx", server.GetProperty("command").GetString());

        var args = server.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains("mcp-remote", args);
        Assert.Contains("http://127.0.0.1:5057/mcp", args);
        Assert.Contains("Authorization: Bearer maf_abc", args);
    }

    [Fact]
    public void Endpoint_builder_tolerates_trailing_slash_and_missing_leading_slash()
    {
        Assert.Equal("http://127.0.0.1:5057/mcp", LocalMcpConfig.BuildEndpoint("http://127.0.0.1:5057/", "mcp"));
        Assert.Equal("https://host/mcp", LocalMcpConfig.BuildEndpoint("https://host", "/mcp"));
    }

    [Fact]
    public void Server_card_reports_protocol_versions_and_tool_categories()
    {
        var card = MyFirstMafAgent.Api.Endpoints.McpAccessEndpoints.BuildServerCard(
            "https://knowspace.test",
            new McpServerRegistration(true, "/mcp"),
            null);

        var versions = card["protocolVersion"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();

        Assert.Contains("2026-07-28", versions);
        Assert.Equal("https://knowspace.test/mcp", card["transports"]![0]!["url"]!.GetValue<string>());
        Assert.True(card["authentication"]!["required"]!.GetValue<bool>());
        Assert.Equal(McpToolCatalog.All.Count, card["tools"]!["count"]!.GetValue<int>());
    }
}
