using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>manifest 校验结果。加载失败时插件不得进入"已启用"状态。</summary>
public sealed record PluginManifestValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    public static PluginManifestValidationResult Ok(IReadOnlyList<string>? warnings = null) =>
        new([], warnings ?? []);

    public static PluginManifestValidationResult Fail(params string[] errors) => new(errors, []);
}

/// <summary>
/// manifest 校验器。核心只信任通过校验的声明：
/// 能力 ID 必须唯一、协议区间必须与当前协议相交、资源限制必须在允许范围内。
/// </summary>
public static class PluginManifestValidator
{
    /// <summary>核心允许的最大资源上限（插件不能通过 manifest 放宽）。</summary>
    public const int MaxAllowedMemoryMb = 4096;
    public const int MaxAllowedInvocationTimeoutMs = 600_000;
    public const int MaxAllowedRequestBytes = 16 * 1024 * 1024;
    public const int MaxAllowedConcurrency = 16;

    private static readonly Regex IdPattern = new(
        @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>MCP 方言放宽 ID：不要求反向域名，但仍须能安全地当目录名。</summary>
    private static readonly Regex McpIdPattern = new(
        @"^[a-z0-9][a-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CapabilityIdPattern = new(
        @"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// 从 JSON 文本解析并校验。
    ///
    /// <para>
    /// 反序列化<b>之前</b>先过一遍 <see cref="PluginManifestNormalizer"/>：生态里的别名
    /// （<c>inputSchema</c>、<c>net.outbound</c>、<c>args</c>、<c>"protocol": "mcp"</c> …）
    /// 在这里被改名成契约词汇，并把改过名的字段作为 warning 透出——不静默生效。
    /// </para>
    /// </summary>
    public static (PluginManifest? Manifest, PluginManifestValidationResult Result) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, PluginManifestValidationResult.Fail("manifest is empty"));

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: DocumentOptions);
        }
        catch (JsonException exception)
        {
            return (null, PluginManifestValidationResult.Fail($"manifest is not valid JSON: {exception.Message}"));
        }

        if (node is not JsonObject)
            return (null, PluginManifestValidationResult.Fail("manifest root must be a JSON object"));

        var (normalized, notes) = PluginManifestNormalizer.Normalize(node);
        if (normalized is null)
            return (null, PluginManifestValidationResult.Fail("manifest root must be a JSON object"));

        PluginManifest? manifest;
        try
        {
            manifest = normalized.Deserialize<PluginManifest>(SerializerOptions);
        }
        catch (JsonException exception)
        {
            return (null, PluginManifestValidationResult.Fail($"manifest is not valid JSON: {exception.Message}"));
        }

        if (manifest is null)
            return (null, PluginManifestValidationResult.Fail("manifest deserialized to null"));

        var result = Validate(manifest);
        if (notes.Count > 0)
            result = new PluginManifestValidationResult(result.Errors, [.. notes, .. result.Warnings]);

        return (manifest, result);
    }

    /// <summary>校验已反序列化的 manifest。</summary>
    public static PluginManifestValidationResult Validate(PluginManifest manifest)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var isMcp = PluginDialects.IsMcp(manifest.Dialect);

        if (!string.IsNullOrWhiteSpace(manifest.Dialect) && !PluginDialects.IsKnown(manifest.Dialect))
            errors.Add($"dialect '{manifest.Dialect}' is not supported; use 'plugin' or 'mcp'");

        if (string.IsNullOrWhiteSpace(manifest.SchemaVersion))
            errors.Add("schemaVersion is required");
        else if (!string.Equals(manifest.SchemaVersion, PluginProtocol.ManifestSchemaVersion, StringComparison.Ordinal))
            errors.Add($"schemaVersion '{manifest.SchemaVersion}' is not supported; expected '{PluginProtocol.ManifestSchemaVersion}'");

        if (string.IsNullOrWhiteSpace(manifest.Id))
            errors.Add("id is required (reverse-domain form, e.g. com.example.notes)");
        else if (manifest.Id.Length > 128)
            errors.Add("id must be at most 128 characters");
        else if (isMcp ? !McpIdPattern.IsMatch(manifest.Id) : !IdPattern.IsMatch(manifest.Id))
            errors.Add(isMcp
                ? $"id '{manifest.Id}' must be lowercase alphanumeric with '.', '_' or '-'"
                : $"id '{manifest.Id}' must be lowercase reverse-domain (e.g. com.example.notes)");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors.Add("name is required");

        if (string.IsNullOrWhiteSpace(manifest.Version))
            errors.Add("version is required");
        else if (!SemanticVersion.TryParse(manifest.Version, out _))
            errors.Add($"version '{manifest.Version}' is not a valid semantic version");

        if (string.IsNullOrWhiteSpace(manifest.Publisher))
            errors.Add("publisher is required");

        if (manifest.Entry is null)
            errors.Add("entry is required");
        else if (string.IsNullOrWhiteSpace(manifest.Entry.Command))
            errors.Add("entry.command is required");

        if (manifest.Protocol is null)
        {
            errors.Add("protocol is required (minVersion/maxVersion)");
        }
        else if (isMcp)
        {
            // MCP 的版本由 initialize 的 protocolVersion 协商，语义与我们这套区间不同，不做区间判定。
            if (!SemanticVersion.TryParse(manifest.Protocol.MinVersion, out _)
                || !SemanticVersion.TryParse(manifest.Protocol.MaxVersion, out _))
                errors.Add("protocol.minVersion/maxVersion must be semantic versions");
        }
        else
        {
            if (!SemanticVersion.TryParse(manifest.Protocol.MinVersion, out _))
                errors.Add($"protocol.minVersion '{manifest.Protocol.MinVersion}' is not a valid semantic version");
            else if (!SemanticVersion.TryParse(manifest.Protocol.MaxVersion, out _))
                errors.Add($"protocol.maxVersion '{manifest.Protocol.MaxVersion}' is not a valid semantic version");
            else if (!manifest.Protocol.ContainsCurrentProtocol(out var reason))
                errors.Add($"protocol is incompatible with this core: {reason}");
        }

        ValidatePermissions(manifest.Permissions, "manifest", errors);

        // MCP 的能力来自 tools/list，清单里可以是空的；自有协议必须自己声明。
        ValidateCapabilities(manifest, errors, warnings, requireAtLeastOne: !isMcp);
        ValidateLimits(manifest, errors, warnings);

        if (manifest.Config is not null && !string.IsNullOrWhiteSpace(manifest.Config.Schema))
        {
            if (!IsValidJsonObjectSchema(manifest.Config.Schema, out var schemaError))
                errors.Add($"config.schema is not a valid JSON object schema: {schemaError}");
        }

        if (manifest.Integrity is { Sha256.Length: > 0 } integrity
            && !IsHexSha256(integrity.Sha256!))
            errors.Add("integrity.sha256 must be a 64 character hex string");

        if (manifest.Integrity is null || string.IsNullOrWhiteSpace(manifest.Integrity.Sha256))
            warnings.Add("integrity.sha256 is missing; package cannot be verified before install");

        return new PluginManifestValidationResult(errors, warnings);
    }

    private static void ValidateCapabilities(
        PluginManifest manifest,
        List<string> errors,
        List<string> warnings,
        bool requireAtLeastOne = true)
    {
        if (manifest.Capabilities.Count == 0)
        {
            if (requireAtLeastOne) errors.Add("capabilities must declare at least one capability");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in manifest.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability.Id))
            {
                errors.Add("capability id is required");
                continue;
            }

            if (!CapabilityIdPattern.IsMatch(capability.Id))
                errors.Add($"capability id '{capability.Id}' must be lowercase alphanumeric with '.', '_' or '-'");

            if (!seen.Add(capability.Id))
                errors.Add($"capability id '{capability.Id}' is duplicated");

            if (!PluginCapabilityKinds.IsKnown(capability.Kind))
                errors.Add($"capability '{capability.Id}' has unknown kind '{capability.Kind}'");
            else if (capability.Kind == PluginCapabilityKinds.AgentTool && string.IsNullOrWhiteSpace(capability.Parameters))
                warnings.Add($"agent-tool '{capability.Id}' has no parameters schema; the model will see an empty object");

            if (!PluginSideEffectLevels.IsKnown(capability.SideEffect))
                errors.Add($"capability '{capability.Id}' has unknown sideEffect '{capability.SideEffect}'");

            foreach (var permission in capability.Permissions)
            {
                if (!PluginPermissions.IsKnown(permission))
                    errors.Add($"capability '{capability.Id}' requests unknown permission '{permission}'");
            }

            if (!string.IsNullOrWhiteSpace(capability.Parameters)
                && !IsValidJsonSchema(capability.Parameters, out var error))
                errors.Add($"capability '{capability.Id}' parameters is not valid JSON Schema: {error}");
        }
    }

    /// <summary>顶层权限（MCP 方言下唯一能写权限的地方）。</summary>
    private static void ValidatePermissions(
        IReadOnlyList<string> permissions,
        string owner,
        List<string> errors)
    {
        foreach (var permission in permissions)
        {
            if (!PluginPermissions.IsKnown(permission))
                errors.Add($"{owner} requests unknown permission '{permission}'");
        }
    }

    private static void ValidateLimits(PluginManifest manifest, List<string> errors, List<string> warnings)
    {
        if (manifest.Limits is not { } limits) return;

        if (limits.MaxMemoryMb is < 64 or > MaxAllowedMemoryMb)
            errors.Add($"limits.maxMemoryMb must be between 64 and {MaxAllowedMemoryMb}");
        if (limits.MaxConcurrentInvocations is < 1 or > MaxAllowedConcurrency)
            errors.Add($"limits.maxConcurrentInvocations must be between 1 and {MaxAllowedConcurrency}");
        if (limits.InvocationTimeoutMs is < 1000 or > MaxAllowedInvocationTimeoutMs)
            errors.Add($"limits.invocationTimeoutMs must be between 1000 and {MaxAllowedInvocationTimeoutMs}");
        if (limits.MaxRequestBytes is < 1024 or > MaxAllowedRequestBytes)
            errors.Add($"limits.maxRequestBytes must be between 1024 and {MaxAllowedRequestBytes}");
        if (limits.MaxRetries is < 0 or > 5)
            errors.Add("limits.maxRetries must be between 0 and 5");

        if (limits.MaxRetries > 0)
            warnings.Add("limits.maxRetries > 0 requires every capability to be idempotent");
    }

    private static bool IsValidJsonSchema(string json, out string error)
    {
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "schema root must be an object";
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsValidJsonObjectSchema(string json, out string error)
    {
        if (!IsValidJsonSchema(json, out error)) return false;

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "object", StringComparison.Ordinal))
        {
            error = "config.schema type must be 'object'";
            return false;
        }

        return true;
    }

    private static bool IsHexSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }

        return true;
    }

    /// <summary>把 manifest 序列化为 JSON（写盘与诊断用，字段名保持契约拼写）。</summary>
    public static string ToJson(PluginManifest manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

    /// <summary>供测试与诊断：判断 JSON 是否可解析为 JSON Schema 对象。</summary>
    public static bool IsValidSchemaDocument(string json) => IsValidJsonSchema(json, out _);

    /// <summary>把 JSON 解析为可修改的节点（示例插件构造响应时使用）。</summary>
    public static JsonNode? ParseNode(string json) => JsonNode.Parse(json);
}
