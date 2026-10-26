using MyFirstMafAgent.Api.Infrastructure;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 便携数据库根目录的候选顺序是一份「活文档」：机器上用户目录整体不可写时
/// （火绒/360 的文件防护、Windows「受控文件夹访问」），必须还能回落到
/// 程序目录下的 <c>App_Data</c>，否则启动会直接抛
/// 「找不到可写且路径为纯英文的目录来运行便携 PostgreSQL」。
///
/// <para>
/// 注意候选返回的是**根目录**，数据目录与日志目录再拼在它下面
/// （<c>{根}\pgdata</c>、<c>{根}\pglogs</c>）。
/// </para>
/// </summary>
public class PortablePostgresRootTests
{
    [Fact]
    public void Candidates_Put_UserDataRoot_First()
    {
        var candidates = AppPaths.PortableDataRootCandidates(Path.GetTempPath());

        Assert.Equal(AppPaths.UserDataRoot, candidates[0]);
    }

    [Fact]
    public void Candidates_End_With_Application_Directory_Fallback()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "maf-content-root");

        var candidates = AppPaths.PortableDataRootCandidates(contentRoot);

        Assert.Equal(Path.Combine(Path.GetFullPath(contentRoot), "App_Data"), candidates[^1]);
    }

    [Fact]
    public void Data_and_log_directories_are_siblings_under_the_root()
    {
        // 日志必须与数据目录平级：pg_ctl -l 若指到 PGDATA 内部，崩溃恢复时的 fsync
        // 会撞上这个仍被占用的句柄（could not open file "./log/ctl.out": sharing violation）。
        Assert.Equal("pgdata", AppPaths.PostgresDataFolderName);
        Assert.Equal("pglogs", AppPaths.PostgresLogFolderName);
        Assert.NotEqual(AppPaths.PostgresDataFolderName, AppPaths.PostgresLogFolderName);
    }

    [Fact]
    public void Candidates_Are_Distinct_And_Never_Empty()
    {
        var candidates = AppPaths.PortableDataRootCandidates(Path.GetTempPath());

        Assert.All(candidates, candidate => Assert.False(string.IsNullOrWhiteSpace(candidate)));
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(@"C:\Apps\MyFirstMafAgent", true)]
    [InlineData(@"C:\Users\zhs\Desktop\Word\通用文件服务\MyFirstMafAgent", false)]
    [InlineData("/opt/myfirstmafagent", true)]
    [InlineData("/opt/中文目录", false)]
    public void IsAsciiPath_Detects_NonAscii_Characters(string path, bool expected)
    {
        Assert.Equal(expected, AppPaths.IsAsciiPath(path));
    }

    [Fact]
    public void DescribeWriteFailure_Points_At_Security_Software_When_Access_Denied()
    {
        var message = AppPaths.DescribeWriteFailure(
            new UnauthorizedAccessException(@"Access to the path 'C:\x\.write-probe' is denied."));

        Assert.Contains("写入被拒绝", message);
        Assert.Contains("安全软件", message);
        Assert.Contains("is denied", message);
    }

    [Fact]
    public void DescribeWriteFailure_Keeps_Raw_Message_For_Other_Errors()
    {
        var message = AppPaths.DescribeWriteFailure(new IOException("磁盘已满"));

        Assert.Contains("磁盘已满", message);
    }
}
