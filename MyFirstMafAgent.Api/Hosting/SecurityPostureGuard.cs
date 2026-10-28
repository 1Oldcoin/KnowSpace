using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>启动期安全检查的一条结论。</summary>
public sealed record SecurityPostureFinding(bool IsError, string Message);

/// <summary>
/// 服务端形态的启动守卫：<b>不安全的组合直接拒绝启动</b>，而不是打一条没人看的警告。
/// 桌面形态只做提示，行为与之前一致。
/// </summary>
public static class SecurityPostureGuard
{
    /// <summary>服务端 API 令牌的最短长度（<c>openssl rand -hex 32</c> 是 64 个字符）。</summary>
    public const int MinServerTokenLength = 32;

    public static IReadOnlyList<SecurityPostureFinding> Evaluate(
        HostProfileInfo profile,
        IConfiguration configuration,
        DesktopSecurityOptions apiSecurity)
    {
        var findings = new List<SecurityPostureFinding>();
        if (!profile.IsServer) return findings;

        void Error(string message) => findings.Add(new SecurityPostureFinding(true, message));
        void Warn(string message) => findings.Add(new SecurityPostureFinding(false, message));

        if (!apiSecurity.Enabled)
        {
            Error($"未设置 {ServerSecurityOptions.TokenEnvironmentVariable}（或 Server:ApiToken）：/api 管理面（含签发 MCP 密钥）将完全不鉴权。" +
                  "生成方法：openssl rand -hex 32");
        }
        else if (apiSecurity.Token!.Length < MinServerTokenLength)
        {
            Error($"{ServerSecurityOptions.TokenEnvironmentVariable} 太短（{apiSecurity.Token.Length} 个字符），至少 {MinServerTokenLength} 个。" +
                  "生成方法：openssl rand -hex 32");
        }

        if (configuration.GetValue<bool?>($"{McpOptions.SectionName}:RequireClientKey") == false)
            Error("Mcp:RequireClientKey=false 会让 /mcp 不鉴权、不限流、不审计，服务端形态禁止使用。");

        const string oauth = $"{McpOptions.SectionName}:OAuth";
        if (configuration.GetValue<bool?>($"{oauth}:Cimd:AllowInsecureHttp") == true)
            Error("Mcp:OAuth:Cimd:AllowInsecureHttp=true 仅供本机联调，服务端形态禁止使用。");
        if (configuration.GetValue<bool?>($"{oauth}:Cimd:AllowPrivateAddresses") == true)
            Error("Mcp:OAuth:Cimd:AllowPrivateAddresses=true 会打开 SSRF 面（可访问内网与云元数据），服务端形态禁止使用。");

        if (configuration.GetValue<bool?>($"{oauth}:Enabled") == true)
        {
            var issuer = configuration[$"{oauth}:Issuer"]?.Trim();
            if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps)
                Error($"Mcp:OAuth:Enabled=true 但 Issuer 不是 https 绝对地址（当前：'{issuer}'）。服务端形态的令牌受众必须绑定到公网 https 地址。");

            if (configuration.GetValue<bool?>($"{oauth}:AllowDynamicRegistration") ?? true)
                Warn("Mcp:OAuth:AllowDynamicRegistration=true：任何人都能注册 OAuth 客户端（同意授权仍需管理令牌）。不需要时建议关闭。");
        }

        if (configuration.GetSection("Agent:FileRoots").GetChildren().Any(root =>
                root.GetValue<bool?>("Enabled") ?? true))
        {
            Warn("Agent:FileRoots 非空：服务端形态不会把 local_* 文件工具暴露给 MCP，但内置聊天 Agent 仍能读写这些服务器目录。");
        }

        return findings;
    }

    /// <summary>把结论写进启动日志；存在错误时抛出，进程以非 0 退出。</summary>
    public static void Enforce(HostProfileInfo profile, IReadOnlyList<SecurityPostureFinding> findings, Action<string> log)
    {
        log($"宿主形态：{profile.Profile}（{profile.Reason}）");

        foreach (var finding in findings.Where(item => !item.IsError))
            log($"安全提示：{finding.Message}");

        var errors = findings.Where(item => item.IsError).ToList();
        if (errors.Count == 0) return;

        foreach (var error in errors)
            log($"安全检查未通过：{error.Message}");

        throw new InvalidOperationException(
            $"服务端形态安全检查未通过（{errors.Count} 项），已拒绝启动。详情见上方 [startup] 输出；" +
            $"仅在本机使用时可设 {HostProfileResolver.ConfigurationKey}=Desktop。");
    }
}
