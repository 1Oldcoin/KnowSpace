using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

/// <summary>
/// 受信任的发布者公钥。
///
/// <para>
/// 为什么入库而不只放在 appsettings：公钥会随着"信任某个新发布者"而变化，
/// 而 appsettings 是部署物——每次信任一个插件都要改配置、重启、再回滚配置，
/// 这在桌面应用里是不可接受的。入库后它可以像权限一样在运行期增删。
/// </para>
///
/// <para>
/// 只存公钥。私钥永远在发布者手里：核心一旦持有私钥，签名就失去意义
/// （谁能改库谁就能自签）。写入前会拒绝任何看起来像私钥的 PEM。
/// </para>
/// </summary>
[SugarTable("plugin_trusted_keys")]
public sealed class PluginTrustedKeySchemaEntity
{
    /// <summary>稳定的键标识，同时用于审计与日志。</summary>
    [SugarColumn(ColumnName = "key_id", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string KeyId { get; set; } = string.Empty;

    /// <summary>插件 ID 或 ID 前缀（按域名段匹配），例如 <c>com.example</c> 覆盖 <c>com.example.notes</c>。</summary>
    [SugarColumn(ColumnName = "plugin_id", ColumnDataType = "text", IsNullable = false)]
    public string PluginId { get; set; } = string.Empty;

    /// <summary><c>ecdsa</c> 或 <c>rsa</c>。空值时按 ECDSA 处理。</summary>
    [SugarColumn(ColumnName = "algorithm", ColumnDataType = "text", IsNullable = false, DefaultValue = "'ecdsa'")]
    public string Algorithm { get; set; } = "ecdsa";

    /// <summary>PEM 格式公钥（SPKI）。</summary>
    [SugarColumn(ColumnName = "public_key_pem", ColumnDataType = "text", IsNullable = false)]
    public string PublicKeyPem { get; set; } = string.Empty;

    /// <summary>公钥指纹（PEM 规范化后的 SHA-256 前 16 位十六进制），用于人工核对。</summary>
    [SugarColumn(ColumnName = "fingerprint", ColumnDataType = "text", IsNullable = false)]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>备注：谁在什么时候因为什么信任了它。</summary>
    [SugarColumn(ColumnName = "note", ColumnDataType = "text", IsNullable = true)]
    public string? Note { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}
