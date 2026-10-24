using MyFirstMafAgent.Api.Hosting;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 安装器一次性参数的识别。这些参数会让 API 只做清理后退出，
/// 因此识别错误会导致「卸载时启动了整个 Web 主机」或「卸载没停掉数据库」。
/// </summary>
public sealed class InstallerArgumentsTests
{
    [Fact]
    public void IsStopPortablePostgresRequested_MatchesExactArgument()
    {
        Assert.True(InstallerArguments.IsStopPortablePostgresRequested(["--stop-portable-postgres"]));
    }

    [Fact]
    public void IsStopPortablePostgresRequested_IgnoresCase()
    {
        Assert.True(InstallerArguments.IsStopPortablePostgresRequested(["--STOP-Portable-Postgres"]));
    }

    [Fact]
    public void IsStopPortablePostgresRequested_FindsArgumentAmongOthers()
    {
        Assert.True(InstallerArguments.IsStopPortablePostgresRequested(
            ["--urls", "http://127.0.0.1:0", "--stop-portable-postgres"]));
    }

    [Fact]
    public void IsStopPortablePostgresRequested_IgnoresPrefixAndValueForms()
    {
        // 只认精确参数名：`=true` 形式与普通服务启动参数都不能被当成停止请求。
        Assert.False(InstallerArguments.IsStopPortablePostgresRequested(["--stop-portable-postgres=true"]));
        Assert.False(InstallerArguments.IsStopPortablePostgresRequested(["--stop-portable-postgres-x"]));
    }

    [Fact]
    public void IsStopPortablePostgresRequested_ReturnsFalseForNormalStartup()
    {
        Assert.False(InstallerArguments.IsStopPortablePostgresRequested([]));
        Assert.False(InstallerArguments.IsStopPortablePostgresRequested(
            ["--urls", "http://127.0.0.1:0", "--no-browser", "--desktop-data-root", @"C:\data"]));
    }
}
