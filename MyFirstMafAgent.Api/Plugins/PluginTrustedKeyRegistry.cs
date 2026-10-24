using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 受信任公钥的内存视图：把 appsettings 与数据库里的公钥合并成一张只读表。
///
/// <para>
/// 为什么需要这层缓存：签名校验发生在安装与启动的关键路径上，而且是<b>同步</b>调用
/// （<see cref="PluginIntegrityVerifier.VerifySignature"/>），不可能每次都去查库。
/// 因此这里维护一份快照，写操作后显式刷新。
/// </para>
///
/// <para>
/// 优先级：配置文件优先于数据库。运维用配置文件可以"强制信任/强制不信任"，
/// 而不必先去删库里的临时授权——这在排查"谁把这个发布者加进来的"时很重要。
/// </para>
/// </summary>
public sealed class PluginTrustedKeyRegistry(
    IOptions<PluginOptions> options,
    IPluginTrustedKeyStore store,
    ILogger<PluginTrustedKeyRegistry> logger)
{
    private readonly object _gate = new();
    private KeySnapshot _snapshot = KeySnapshot.Empty;

    /// <summary>从数据库加载一次快照。启动时调用；失败时退化为只用配置文件的公钥。</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value.TrustedPublisherKeys
            .Where(key => !string.IsNullOrWhiteSpace(key.PublicKeyPem) && !string.IsNullOrWhiteSpace(key.PluginId))
            .Select(key => new PluginTrustedKeyRecord(
                KeyId: "config:" + key.PluginId,
                PluginId: key.PluginId.Trim(),
                Algorithm: string.IsNullOrWhiteSpace(key.Algorithm) ? PluginTrustedKeys.AlgorithmEcdsa : key.Algorithm.Trim().ToLowerInvariant(),
                PublicKeyPem: key.PublicKeyPem.Trim(),
                Fingerprint: PluginTrustedKeys.ComputeFingerprint(key.PublicKeyPem.Replace("\r\n", "\n").Trim()),
                Note: "来自配置文件",
                CreatedAt: DateTimeOffset.MinValue,
                UpdatedAt: DateTimeOffset.MinValue))
            .ToList();

        List<PluginTrustedKeyRecord> stored;
        try
        {
            stored = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to load trusted plugin keys from the database; using configuration keys only");
            stored = [];
        }

        var merged = configured
            .Concat(stored.Where(row => configured.All(key => !string.Equals(key.KeyId, row.KeyId, StringComparison.Ordinal))))
            .ToList();

        lock (_gate) _snapshot = new KeySnapshot(merged);
    }

    /// <summary>当前快照（只读）。</summary>
    public IReadOnlyList<PluginTrustedKeyRecord> Keys
    {
        get { lock (_gate) return _snapshot.Keys; }
    }

    /// <summary>按域名段前缀找最长的匹配公钥；没有返回 null。</summary>
    public PluginTrustedKeyRecord? Find(string pluginId)
    {
        lock (_gate)
        {
            return _snapshot.OrderedByPrefixLength
                .FirstOrDefault(key => PluginTrustedKeys.Matches(key.PluginId, pluginId));
        }
    }

    private sealed class KeySnapshot(IReadOnlyList<PluginTrustedKeyRecord> keys)
    {
        public static readonly KeySnapshot Empty = new([]);

        public IReadOnlyList<PluginTrustedKeyRecord> Keys { get; } = keys;

        /// <summary>按前缀长度降序：最具体的公钥优先，<c>com.example.notes</c> 压过 <c>com.example</c>。</summary>
        public IReadOnlyList<PluginTrustedKeyRecord> OrderedByPrefixLength { get; } =
            keys.OrderByDescending(key => key.PluginId.Length).ToList();
    }
}
