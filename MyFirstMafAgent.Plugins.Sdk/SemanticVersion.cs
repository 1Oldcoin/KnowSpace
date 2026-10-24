using System.Globalization;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>
/// 最小 SemVer 2.0 实现（只做比较与格式校验，不支持 range 语法）。
/// 插件版本、协议版本与核心兼容区间都用它比较，避免字符串比较导致的
/// "1.10.0 &lt; 1.9.0" 这类错误。
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemanticVersion>
{
    private static readonly Regex VersionPattern = new(
        @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[0-9A-Za-z.\-]+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var match = VersionPattern.Match(value.Trim());
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;

        var pre = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        version = new SemanticVersion(major, minor, patch, pre);
        return true;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid semantic version (expected MAJOR.MINOR.PATCH[-prerelease]).");

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        // 有预发布标识的版本低于同号正式版（1.0.0-rc1 < 1.0.0）。
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public bool IsPreRelease => PreRelease is not null;

    public override string ToString() =>
        PreRelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
}

/// <summary>协议兼容区间：闭区间 [Min, Max]，两端都必须能解析。</summary>
public sealed record ProtocolRange(string MinVersion, string MaxVersion)
{
    public const string CurrentMin = PluginProtocol.ProtocolVersion;
    public const string CurrentMax = PluginProtocol.ProtocolVersion;

    public bool Contains(SemanticVersion version, out string reason)
    {
        reason = string.Empty;
        if (!SemanticVersion.TryParse(MinVersion, out var min))
        {
            reason = $"minVersion '{MinVersion}' is not a valid semantic version";
            return false;
        }

        if (!SemanticVersion.TryParse(MaxVersion, out var max))
        {
            reason = $"maxVersion '{MaxVersion}' is not a valid semantic version";
            return false;
        }

        if (min > max)
        {
            reason = $"minVersion '{MinVersion}' is greater than maxVersion '{MaxVersion}'";
            return false;
        }

        if (version < min || version > max)
        {
            reason = $"protocol {version} is outside the supported range [{MinVersion}, {MaxVersion}]";
            return false;
        }

        return true;
    }

    public bool ContainsCurrentProtocol(out string reason) =>
        SemanticVersion.TryParse(PluginProtocol.ProtocolVersion, out var current)
            ? Contains(current, out reason)
            : FailCurrent(out reason);

    private bool FailCurrent(out string reason)
    {
        reason = $"core protocol version '{PluginProtocol.ProtocolVersion}' is not a valid semantic version";
        return false;
    }
}
