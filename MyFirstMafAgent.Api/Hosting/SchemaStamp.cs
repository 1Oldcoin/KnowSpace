using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SqlSugar;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 数据库结构"指纹"：用来跳过已经做过的建表检查。
/// <para>
/// <b>为什么需要</b>：启动时每个 store 都要跑一轮 SqlSugar <c>CodeFirst.InitTables</c> 与
/// <c>DbMaintenance.IsAnyColumn</c>。这些调用每次都会去查 catalog（表在不在、有哪些列），
/// 空库与满库的耗时一样，纯属固定开销。实测（<c>scripts\Measure-Startup.ps1</c>）：
/// 知识库 11 张表 + 分身 20 张表合计约 2.8 秒，占整个后端启动的四分之三。
/// 而这段工作的结果在两次启动之间**完全一样**——表早就建好了。
/// </para>
/// <para>
/// <b>做法</b>：把「当前程序的表结构」算成一个指纹，与上次初始化成功后写进库里的指纹比对。
/// 一致就跳过 DDL；不一致（升级/降级/手工改库）就老老实实跑完整初始化，跑完再写指纹。
/// </para>
/// <para>
/// <b>指纹取什么</b>：所有 <c>*SchemaEntity</c> 类型的表名/列名/类型/可空/默认值，
/// 加上程序集 MVID。加入 MVID 是刻意的保守选择：只要代码重新编译过就重跑一次完整初始化，
/// 这样"有人绕过实体、直接手写 ALTER"也不会被漏掉。代价只是每次升级后第一次启动慢一点。
/// </para>
/// <para>
/// <b>应急开关</b>：设环境变量 <c>MAF_FORCE_SCHEMA_INIT=1</c> 可强制走完整初始化
/// （怀疑库结构被手工改坏、或某个补列逻辑没生效时用）。
/// </para>
/// </summary>
public static class SchemaStamp
{
    /// <summary>指纹表名。单独一张小表，不参与任何业务查询。</summary>
    public const string TableName = "maf_schema_stamp";

    private static readonly Lazy<string> Cached = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>当前程序期望的指纹（缓存在进程内，重复调用无成本）。</summary>
    public static string Current => Cached.Value;

    /// <summary>是否被要求强制完整初始化。</summary>
    public static bool ForceFullInitialization =>
        string.Equals(Environment.GetEnvironmentVariable("MAF_FORCE_SCHEMA_INIT"), "1", StringComparison.Ordinal);

    private static string Compute()
    {
        var assembly = typeof(SchemaStamp).Assembly;
        var builder = new StringBuilder();

        // MVID：任何一次重新编译都会变，等于"程序变了就重查一遍结构"。
        builder.Append(assembly.ManifestModule.ModuleVersionId).Append('\n');

        var entityTypes = assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.Name.EndsWith("SchemaEntity", StringComparison.Ordinal))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var type in entityTypes)
        {
            builder.Append(type.FullName).Append('|');

            var table = type.GetCustomAttribute<SugarTable>();
            builder.Append(table?.TableName ?? string.Empty).Append('|');

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                var column = property.GetCustomAttribute<SugarColumn>();

                // 只记录"表结构"相关的部分：Ignore 的列与导航属性不进指纹。
                if (column is { IsIgnore: true }) continue;

                builder.Append(property.Name).Append(':')
                    .Append(column?.ColumnName ?? string.Empty).Append(',')
                    .Append(column?.ColumnDataType ?? string.Empty).Append(',')
                    .Append(column?.IsNullable ?? false).Append(',')
                    .Append(column?.DefaultValue ?? string.Empty).Append(',')
                    .Append(column?.Length ?? 0).Append(',')
                    .Append(column?.IsPrimaryKey ?? false).Append(',')
                    .Append(column?.IsIdentity ?? false).Append(';');            }

            builder.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 读出库里记录的指纹。表不存在或读失败都返回 null（等于"不知道"，走完整初始化）。
    /// </summary>
    public static async Task<string?> TryReadAsync(SqlSugarScope database, CancellationToken cancellationToken)
    {
        try
        {
            var exists = await database.Ado.GetIntAsync(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @name",
                new { name = TableName }, cancellationToken);

            if (exists == 0) return null;

            var value = await database.Ado.GetStringAsync(
                $"SELECT fingerprint FROM {TableName} ORDER BY updated_at DESC LIMIT 1",
                null, cancellationToken);

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            // 任何读取问题都退化成"完整初始化"：宁可慢，不可缺表。
            return null;
        }
    }

    /// <summary>完整初始化成功后写入指纹（单行，覆盖式）。</summary>
    public static async Task SaveAsync(SqlSugarScope database, string fingerprint, CancellationToken cancellationToken)
    {
        await database.Ado.ExecuteCommandAsync(
            $"""
             CREATE TABLE IF NOT EXISTS {TableName} (
                 id integer PRIMARY KEY,
                 fingerprint text NOT NULL,
                 updated_at timestamptz NOT NULL DEFAULT now()
             );
             """, null, cancellationToken);

        await database.Ado.ExecuteCommandAsync(
            $"""
             INSERT INTO {TableName} (id, fingerprint, updated_at)
             VALUES (1, @fingerprint, now())
             ON CONFLICT (id) DO UPDATE SET fingerprint = EXCLUDED.fingerprint, updated_at = now();
             """, new { fingerprint }, cancellationToken);
    }
}
