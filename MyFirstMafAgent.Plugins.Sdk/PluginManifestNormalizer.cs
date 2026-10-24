using System.Text;
using System.Text.Json.Nodes;

namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>
/// 把"生态风格"的清单规范化成本项目的契约词汇。
///
/// <para>
/// 为什么要有这一层：插件作者不该为了上架一个本来就能跑的东西去改一堆字段名。
/// 现实里同一个概念在各家清单里叫法不同——工具入参官方叫 <c>inputSchema</c>（MCP）、
/// 权限叫 <c>net.outbound</c> / <c>fs</c> / <c>proc</c>、启动参数叫 <c>args</c>、
/// 启动命令写成数组也很常见（Claude Desktop 的 <c>mcpServers</c> 就是）。
/// 与其在文档里要求"必须写成我们的样子"，不如在<b>读入时</b>认下这些别名，
/// 内部只保留一套词汇。
/// </para>
///
/// <para>
/// 这一层只做<b>改名与展开</b>，不做"猜测语义"：认不出来的权限词原样留着，
/// 让校验器明确报错，作者才知道自己写错了什么。所有做过的改名都会作为 warning
/// 返回（<see cref="PluginManifestValidationResult.Warnings"/>），不静默生效。
/// </para>
/// </summary>
public static class PluginManifestNormalizer
{
    /// <summary>把原始 JSON 节点就地规范化。返回值中的 Notes 是要透出给作者的告警。</summary>
    public static (JsonObject? Root, IReadOnlyList<string> Notes) Normalize(JsonNode? raw)
    {
        if (raw is not JsonObject root) return (null, []);

        var notes = new List<string>();
        var mcp = NormalizeDialect(root, notes);

        NormalizeEntry(root, notes);
        NormalizeIdentity(root, notes, mcp);
        NormalizeCapabilities(root, notes);
        NormalizeConfig(root, notes);

        return (root, notes);
    }

    /// <summary>只做方言识别，便于外部（如安装器）先判断行为再决定怎么校验。</summary>
    public static bool IsMcp(JsonNode? raw) =>
        raw is JsonObject root
        && ResolveDialect(root) is { } dialect
        && PluginDialects.IsMcp(dialect);

    // ───────────────────────────── 方言 ─────────────────────────────

    private static string? ResolveDialect(JsonObject root)
    {
        // 两种写法都认：顶层 "dialect": "mcp"，或 "protocol": "mcp"（字符串形态）。
        if (root["dialect"] is JsonValue dialectValue && dialectValue.TryGetValue<string>(out var dialectText))
            return dialectText;

        if (root["protocol"] is JsonValue protocolValue && protocolValue.TryGetValue<string>(out var protocolText))
            return protocolText;

        if (root["protocol"] is JsonObject protocolObject
            && protocolObject["dialect"] is JsonValue nested
            && nested.TryGetValue<string>(out var nestedText))
            return nestedText;

        return null;
    }

    private static bool NormalizeDialect(JsonObject root, List<string> notes)
    {
        var declared = ResolveDialect(root);
        if (declared is null) return false;

        // "protocol": "mcp" 是字符串：它声明的是方言，不是版本区间。区间由核心补默认值。
        if (root["protocol"] is JsonValue or JsonObject { Count: 0 })
        {
            root.Remove("protocol");
            notes.Add($"protocol '{declared}' 按方言（dialect）解释，协议版本区间由核心补默认值");
        }
        else if (root["protocol"] is JsonObject protocolObject)
        {
            protocolObject.Remove("dialect");
        }

        var normalized = PluginDialects.Normalize(declared);
        if (normalized.Length == 0)
        {
            // 认不出来的方言原样写回，交给校验器报错（不要在这里假装成 native）。
            root["dialect"] = declared;
            return false;
        }

        root["dialect"] = normalized;

        if (root["protocol"] is null)
        {
            root["protocol"] = new JsonObject
            {
                ["minVersion"] = PluginProtocol.ProtocolVersion,
                ["maxVersion"] = PluginProtocol.ProtocolVersion
            };
        }

        return PluginDialects.IsMcp(normalized);
    }

    // ───────────────────────────── 身份字段 ─────────────────────────────

    /// <summary>
    /// MCP server 本来没有"清单"，抄过来时通常只写 id/protocol/entry。
    /// 缺的 name/version/publisher 给出保守默认值并告警，避免为了跑起来被迫编造版本号。
    /// </summary>
    private static void NormalizeIdentity(JsonObject root, List<string> notes, bool mcp)
    {
        if (!mcp) return;

        if (root["name"] is null && TryGetString(root, "id", out var id))
        {
            root["name"] = id;
            notes.Add($"缺少 name，暂用 id '{id}' 作为显示名");
        }

        if (root["id"] is null && TryGetString(root, "name", out var name))
        {
            var derived = Slugify(name);
            if (derived.Length > 0)
            {
                root["id"] = derived;
                notes.Add($"缺少 id，已按 name 推导为 '{derived}'");
            }
        }

        if (root["version"] is null)
        {
            root["version"] = "0.0.0";
            notes.Add("缺少 version，按 0.0.0 处理");
        }

        if (root["publisher"] is null)
        {
            root["publisher"] = "unknown";
            notes.Add("缺少 publisher，按 unknown 处理");
        }

        // MCP 侧的 id 往往就是包名或工具名，不必强求反向域名；但必须能当目录名用。
        if (TryGetString(root, "id", out var current) && !IsSafeId(current))
        {
            var slug = Slugify(current);
            if (slug.Length > 0)
            {
                root["id"] = slug;
                notes.Add($"id '{current}' 不是合法的插件 ID，已规范化为 '{slug}'");
            }
        }
    }

    // ───────────────────────────── 入口 ─────────────────────────────

    private static void NormalizeEntry(JsonObject root, List<string> notes)
    {
        if (root["entry"] is not JsonObject entry) return;

        // Claude Desktop 风格：args / cwd。先改名，后面的命令数组才能合并到同一个字段上。
        if (entry["arguments"] is null && entry["args"] is JsonNode args)
        {
            entry["arguments"] = args.DeepClone();
            entry.Remove("args");
            notes.Add("entry.args 已按 entry.arguments 读取");
        }

        // "command": ["npx", "-y", "pkg"] —— 命令 + 参数写成一个数组。
        if (entry["command"] is JsonArray commandArray)
        {
            var parts = ToStringList(commandArray);
            if (parts.Count > 0)
            {
                entry["command"] = parts[0];
                var rest = parts.Skip(1).ToList();

                if (rest.Count > 0)
                {
                    var existing = entry["arguments"] is JsonNode node ? ToStringList(node) : [];
                    entry["arguments"] = ToJsonArray(rest.Concat(existing));
                    notes.Add("entry.command 是数组，已拆成 command + arguments");
                }
            }
        }

        if (entry["workingDirectory"] is null && entry["cwd"] is JsonNode cwd)
        {
            entry["workingDirectory"] = cwd.DeepClone();
            entry.Remove("cwd");
            notes.Add("entry.cwd 已按 entry.workingDirectory 读取");
        }

        // 环境变量的值必须是字符串：JSON 里写数字/布尔很常见，这里统一转掉。
        if (entry["env"] is JsonObject env)
        {
            foreach (var pair in env.ToList())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out _)) continue;
                env[pair.Key] = pair.Value?.ToJsonString() ?? string.Empty;
            }
        }
    }

    // ───────────────────────────── 能力 ─────────────────────────────

    private static void NormalizeCapabilities(JsonObject root, List<string> notes)
    {
        // 顶层 requires / permissions 对所有能力生效（生态里常见"整个插件要什么"）。
        var topLevel = PluginPermissions.NormalizeAll(
            ToStringList(root["requires"]).Concat(ToStringList(root["permissions"])));

        var capabilities = NormalizeCapabilityContainer(root["capabilities"], notes);

        if (capabilities is null)
        {
            if (topLevel.Count > 0) root["permissions"] = ToJsonArray(topLevel);
            return;
        }

        foreach (var item in capabilities)
        {
            if (item is not JsonObject capability) continue;

            // id 缺失时用 name 兜底：模型看到的是 name，作者常常只写这个。
            if (!TryGetString(capability, "id", out _) && TryGetString(capability, "name", out var name))
            {
                var derived = Slugify(name);
                if (derived.Length > 0)
                {
                    capability["id"] = derived;
                    notes.Add($"能力缺少 id，已按 name 推导为 '{derived}'");
                }
            }

            NormalizeKind(capability, notes);
            NormalizeSideEffect(capability, notes);
            NormalizeParameters(capability, notes);

            var permissions = PluginPermissions.NormalizeAll(
                ToStringList(capability["permissions"])
                    .Concat(ToStringList(capability["requires"]))
                    .Concat(topLevel));

            capability.Remove("requires");
            if (permissions.Count > 0) capability["permissions"] = ToJsonArray(permissions);
        }

        root["capabilities"] = capabilities;
    }

    /// <summary>
    /// capabilities 允许三种形态：对象数组（本项目的形态）、字符串数组（只写工具名）、
    /// 以名称为键的对象（生态里也常见）。统一成对象数组。
    /// </summary>
    private static JsonArray? NormalizeCapabilityContainer(JsonNode? node, List<string> notes)
    {
        switch (node)
        {
            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array)
                {
                    if (item is JsonObject existing) result.Add(existing.DeepClone());
                    else if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(new JsonObject
                        {
                            ["id"] = Slugify(text),
                            ["name"] = text.Trim(),
                            ["kind"] = PluginCapabilityKinds.AgentTool,
                            ["sideEffect"] = PluginSideEffectLevels.Read
                        });
                    }
                }

                if (result.Count != array.Count)
                    notes.Add("capabilities 里的字符串项已按 agent-tool 能力展开");

                return result;
            }

            case JsonObject map:
            {
                var result = new JsonArray();
                foreach (var pair in map)
                {
                    if (pair.Value is not JsonObject capability) continue;

                    var clone = capability.DeepClone().AsObject();
                    if (!TryGetString(clone, "id", out _)) clone["id"] = Slugify(pair.Key);
                    if (!TryGetString(clone, "name", out _)) clone["name"] = pair.Key;
                    result.Add(clone);
                }

                notes.Add("capabilities 是对象映射，已按 id → 能力 展开成数组");
                return result;
            }

            default:
                return null;
        }
    }

    private static void NormalizeKind(JsonObject capability, List<string> notes)
    {
        if (!TryGetString(capability, "kind", out var kind))
        {
            // 没写 kind 的能力按工具处理：平台连接器必须显式声明（它会拿到额外的摄取通道，不能猜）。
            capability["kind"] = PluginCapabilityKinds.AgentTool;
            notes.Add("能力缺少 kind，已按 agent-tool 处理");
            return;
        }

        var normalized = kind.Trim().ToLowerInvariant() switch
        {
            "agent-tool" or "agenttool" or "tool" or "function" or "action" or "mcp-tool" => PluginCapabilityKinds.AgentTool,
            "platform-connector" or "platformconnector" or "connector" or "platform" or "sync" => PluginCapabilityKinds.PlatformConnector,
            _ => null
        };

        if (normalized is null || normalized == kind) return;

        capability["kind"] = normalized;
        notes.Add($"能力 kind '{kind}' 已按 '{normalized}' 读取");
    }

    private static void NormalizeSideEffect(JsonObject capability, List<string> notes)
    {
        if (!TryGetString(capability, "sideEffect", out var sideEffect)) return;

        var normalized = sideEffect.Trim().ToLowerInvariant() switch
        {
            "read" or "readonly" or "read-only" or "none" or "safe" => PluginSideEffectLevels.Read,
            "write-local" or "writelocal" or "write" or "local-write" => PluginSideEffectLevels.WriteLocal,
            "write-external" or "writeexternal" or "external" or "remote-write" => PluginSideEffectLevels.WriteExternal,
            "destructive" or "delete" or "danger" => PluginSideEffectLevels.Destructive,
            _ => null
        };

        if (normalized is null || normalized == sideEffect) return;

        capability["sideEffect"] = normalized;
        notes.Add($"能力 sideEffect '{sideEffect}' 已按 '{normalized}' 读取");
    }

    /// <summary>
    /// 入参 schema 有两种写法：本项目的字符串（内嵌 JSON Schema 文本）与 MCP 的对象。
    /// 内部统一成字符串，避免下游每次都要判断类型。
    /// </summary>
    private static void NormalizeParameters(JsonObject capability, List<string> notes)
    {
        foreach (var alias in (string[])["inputSchema", "input_schema", "parametersSchema", "argumentsSchema"])
        {
            if (capability["parameters"] is not null) break;
            if (capability[alias] is not JsonNode aliasNode) continue;

            capability["parameters"] = aliasNode is JsonValue value && value.TryGetValue<string>(out _)
                ? aliasNode.DeepClone()
                : aliasNode.ToJsonString();

            capability.Remove(alias);
            notes.Add($"能力 {alias} 已按 parameters 读取");
        }

        if (capability["parameters"] is JsonObject schema)
        {
            capability["parameters"] = schema.ToJsonString();
        }
    }

    private static void NormalizeConfig(JsonObject root, List<string> notes)
    {
        // 顶层 configSchema（只写 schema 的简写）→ config.schema。
        if (root["config"] is null && root["configSchema"] is JsonNode configSchema)
        {
            root["config"] = new JsonObject
            {
                ["schema"] = configSchema is JsonValue value && value.TryGetValue<string>(out _)
                    ? configSchema.DeepClone()
                    : configSchema.ToJsonString()
            };
            root.Remove("configSchema");
            notes.Add("顶层 configSchema 已按 config.schema 读取");
            return;
        }

        if (root["config"] is JsonValue configValue && configValue.TryGetValue<string>(out _))
        {
            var schema = configValue.DeepClone();
            root["config"] = new JsonObject { ["schema"] = schema };
            notes.Add("config 是字符串，已按 config.schema 读取");
            return;
        }

        if (root["config"] is JsonObject config && config["schema"] is JsonObject schemaObject)
        {
            config["schema"] = schemaObject.ToJsonString();
        }
    }

    // ───────────────────────────── 小工具 ─────────────────────────────

    private static bool TryGetString(JsonObject node, string property, out string value)
    {
        value = string.Empty;
        if (node[property] is JsonValue json && json.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            value = text.Trim();
            return true;
        }

        return false;
    }

    private static List<string> ToStringList(JsonNode? node)
    {
        var result = new List<string>();

        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                    if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                        result.Add(text.Trim());
                break;

            case JsonValue single when single.TryGetValue<string>(out var text):
                // 单个字符串也当列表读：`"permissions": "fs"` 在草稿里非常常见。
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
                break;
        }

        return result;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    /// <summary>把显示名压成可用作 ID / 目录名的形态：小写、非法字符折成 <c>-</c>。</summary>
    internal static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            // 只保留 ASCII：插件 ID 会变成目录名，中文/emoji 在这里只会带来麻烦。
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (character is '.' or '_' or '-')
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-', '.', '_');
    }

    /// <summary>MCP 侧的 ID 放宽到单段，但仍要能安全地当目录名。</summary>
    private static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 128
        && id[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}
