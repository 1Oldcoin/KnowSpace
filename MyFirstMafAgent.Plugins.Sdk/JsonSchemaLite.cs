using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>
/// 极小的 JSON Schema 子集校验器，只覆盖插件契约实际需要的关键字。
///
/// <para>
/// 支持：<c>type</c>（含类型数组）、<c>properties</c>、<c>required</c>、
/// <c>additionalProperties</c>（布尔或 schema）、<c>enum</c>、<c>const</c>、
/// <c>items</c>、<c>minItems</c>、<c>maxItems</c>、<c>minLength</c>、<c>maxLength</c>、
/// <c>pattern</c>、<c>minimum</c>、<c>maximum</c>，以及 <c>allOf</c>/<c>anyOf</c>/<c>oneOf</c>。
/// </para>
///
/// <para>
/// 不实现 <c>$ref</c>、<c>format</c>、<c>if/then/else</c>、<c>dependencies</c>。
/// 遇到未知关键字一律忽略——校验是"尽力而为"，不替代插件自身的入参校验。
/// </para>
/// </summary>
public static class JsonSchemaLite
{
    /// <summary>递归深度上限，防止恶意 schema 造成栈溢出。</summary>
    private const int MaxDepth = 32;

    /// <summary>校验实例，返回人类可读的错误列表（空表示通过）。</summary>
    public static IReadOnlyList<string> Validate(JsonNode? schema, JsonNode? instance)
    {
        var errors = new List<string>();
        ValidateNode(schema, instance, "$", errors, 0);
        return errors;
    }

    /// <summary>校验 JSON 文本形式的 schema 与实例；schema 无法解析时返回错误。</summary>
    public static IReadOnlyList<string> Validate(string? schemaJson, JsonNode? instance)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return [];

        JsonNode? schema;
        try
        {
            schema = JsonNode.Parse(schemaJson);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            return ["schema 不是合法的 JSON"];
        }

        if (schema is not JsonObject)
            return ["schema 的根必须是 JSON 对象（object）"];

        return Validate(schema, instance);
    }

    /// <summary>判断文档是否是对象形式（用于 manifest 的 schema 字段校验）。</summary>
    public static bool IsObjectSchema(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return false;

        try
        {
            return JsonNode.Parse(schemaJson) is JsonObject;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateNode(
        JsonNode? schema,
        JsonNode? instance,
        string path,
        List<string> errors,
        int depth)
    {
        if (schema is not JsonObject definition) return;
        if (depth > MaxDepth)
        {
            errors.Add($"{path}: schema 嵌套过深");
            return;
        }

        ValidateType(definition, instance, path, errors, depth);
        ValidateEnum(definition, instance, path, errors);
        ValidateCombinators(definition, instance, path, errors, depth);

        switch (instance)
        {
            case JsonObject value:
                ValidateObject(definition, value, path, errors, depth);
                break;
            case JsonArray array:
                ValidateArray(definition, array, path, errors, depth);
                break;
            case JsonValue scalar:
                ValidateScalar(definition, scalar, path, errors);
                break;
        }
    }

    private static void ValidateType(
        JsonObject definition,
        JsonNode? instance,
        string path,
        List<string> errors,
        int depth)
    {
        if (definition["type"] is not { } typeNode) return;

        var allowed = new List<string>();
        switch (typeNode)
        {
            case JsonValue value when value.TryGetValue<string>(out var single):
                allowed.Add(single);
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue entry && entry.TryGetValue<string>(out var name))
                        allowed.Add(name);
                }

                break;
            default:
                return;
        }

        if (allowed.Count == 0) return;
        if (allowed.Any(type => Matches(type, instance))) return;

        errors.Add($"{path}: 期望类型 {string.Join("|", allowed)}，实际为 {Describe(instance)}");
        _ = depth;
    }

    private static void ValidateEnum(
        JsonObject definition,
        JsonNode? instance,
        string path,
        List<string> errors)
    {
        if (definition["const"] is { } constant && !JsonNode.DeepEquals(constant, instance))
            errors.Add($"{path}: 必须等于常量 {constant.ToJsonString()}");

        if (definition["enum"] is JsonArray options && options.Count > 0)
        {
            var matched = options.Any(option =>
                JsonNode.DeepEquals(option, instance)
                || (option is JsonValue left && instance is JsonValue right && NumericEquals(left, right)));

            if (!matched)
                errors.Add($"{path}: 取值必须是 {string.Join(" / ", options.Select(option => option?.ToJsonString() ?? "null"))} 之一");
        }
    }

    private static void ValidateCombinators(
        JsonObject definition,
        JsonNode? instance,
        string path,
        List<string> errors,
        int depth)
    {
        if (definition["allOf"] is JsonArray allOf)
        {
            foreach (var branch in allOf)
                ValidateNode(branch, instance, path, errors, depth + 1);
        }

        if (definition["anyOf"] is JsonArray anyOf && anyOf.Count > 0)
        {
            var matched = anyOf.Any(branch => Collect(branch, instance, path, depth + 1).Count == 0);
            if (!matched) errors.Add($"{path}: 不满足 anyOf 中任何一个分支");
        }

        if (definition["oneOf"] is JsonArray oneOf && oneOf.Count > 0)
        {
            var matches = oneOf.Count(branch => Collect(branch, instance, path, depth + 1).Count == 0);
            if (matches != 1) errors.Add($"{path}: oneOf 需要恰好命中 1 个分支，实际命中 {matches} 个");
        }
    }

    private static List<string> Collect(JsonNode? schema, JsonNode? instance, string path, int depth)
    {
        var errors = new List<string>();
        ValidateNode(schema, instance, path, errors, depth);
        return errors;
    }

    private static void ValidateObject(
        JsonObject definition,
        JsonObject instance,
        string path,
        List<string> errors,
        int depth)
    {
        if (definition["required"] is JsonArray required)
        {
            foreach (var entry in required)
            {
                if (entry is not JsonValue value || !value.TryGetValue<string>(out var name)) continue;
                if (instance[name] is null && !instance.ContainsKey(name))
                    errors.Add($"{path}.{name}: 必填字段缺失");
            }
        }

        var properties = definition["properties"] as JsonObject;

        if (properties is not null)
        {
            foreach (var property in properties)
            {
                if (instance[property.Key] is not { } value) continue;
                ValidateNode(property.Value, value, $"{path}.{property.Key}", errors, depth + 1);
            }
        }

        if (definition["additionalProperties"] is { } additional)
        {
            var isClosed = additional is JsonValue flag
                && flag.TryGetValue<bool>(out var allowed)
                && !allowed;

            foreach (var property in instance)
            {
                if (properties is not null && properties.ContainsKey(property.Key)) continue;

                if (isClosed)
                    errors.Add($"{path}.{property.Key}: 不允许出现未声明的字段");
                else if (additional is JsonObject)
                    ValidateNode(additional, property.Value, $"{path}.{property.Key}", errors, depth + 1);
            }
        }
    }

    private static void ValidateArray(
        JsonObject definition,
        JsonArray instance,
        string path,
        List<string> errors,
        int depth)
    {
        if (ReadInt(definition["minItems"]) is { } minItems && instance.Count < minItems)
            errors.Add($"{path}: 至少需要 {minItems} 项，实际 {instance.Count} 项");

        if (ReadInt(definition["maxItems"]) is { } maxItems && instance.Count > maxItems)
            errors.Add($"{path}: 最多允许 {maxItems} 项，实际 {instance.Count} 项");

        if (definition["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < instance.Count; index++)
                ValidateNode(itemSchema, instance[index], $"{path}[{index}]", errors, depth + 1);
        }
    }

    private static void ValidateScalar(
        JsonObject definition,
        JsonValue instance,
        string path,
        List<string> errors)
    {
        if (instance.TryGetValue<string>(out var text))
        {
            if (ReadInt(definition["minLength"]) is { } minLength && text.Length < minLength)
                errors.Add($"{path}: 长度不能小于 {minLength}");

            if (ReadInt(definition["maxLength"]) is { } maxLength && text.Length > maxLength)
                errors.Add($"{path}: 长度不能大于 {maxLength}");

            if (definition["pattern"] is JsonValue patternValue
                && patternValue.TryGetValue<string>(out var pattern)
                && !string.IsNullOrEmpty(pattern))
            {
                try
                {
                    if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500)))
                        errors.Add($"{path}: 不匹配模式 {pattern}");
                }
                catch (ArgumentException)
                {
                    // schema 里的正则非法：属于插件自身缺陷，不作为用户配置错误处理。
                }
            }

            return;
        }

        if (!instance.TryGetValue<decimal>(out var number)) return;

        if (ReadDecimal(definition["minimum"]) is { } minimum && number < minimum)
            errors.Add($"{path}: 不能小于 {minimum}");

        if (ReadDecimal(definition["maximum"]) is { } maximum && number > maximum)
            errors.Add($"{path}: 不能大于 {maximum}");
    }

    private static bool Matches(string type, JsonNode? instance) => type switch
    {
        "object" => instance is JsonObject,
        "array" => instance is JsonArray,
        "string" => instance is JsonValue value && value.TryGetValue<string>(out _),
        "boolean" => instance is JsonValue flag && flag.TryGetValue<bool>(out _),
        "null" => instance is null,
        "number" => instance is JsonValue number && number.TryGetValue<decimal>(out _),
        "integer" => instance is JsonValue integer
            && integer.TryGetValue<decimal>(out var parsed)
            && parsed == decimal.Truncate(parsed),
        // 未识别的类型关键字不阻止校验，避免过度约束。
        _ => true
    };

    private static bool NumericEquals(JsonValue left, JsonValue right) =>
        left.TryGetValue<decimal>(out var leftNumber)
        && right.TryGetValue<decimal>(out var rightNumber)
        && leftNumber == rightNumber;

    private static string Describe(JsonNode? instance) => instance switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue value when value.TryGetValue<string>(out _) => "string",
        JsonValue value when value.TryGetValue<bool>(out _) => "boolean",
        JsonValue => "number",
        _ => "unknown"
    };

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : null;

    private static decimal? ReadDecimal(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<decimal>(out var parsed) ? parsed : null;
}
