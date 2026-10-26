using System.Diagnostics;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 启动耗时打点。
/// <para>
/// 桌面版用户唯一的体感指标是「双击图标到看见界面」，而这条链路横跨 Electron、.NET 主机、
/// 便携 PostgreSQL 与数据库建表四段。没有打点时只能靠猜（曾经把 3.6 秒的建表检查当成"数据库慢"）。
/// 因此这里统一输出 <c>[startup] +1234ms …</c>：控制台直接看，<c>scripts\Measure-Startup.ps1</c>
/// 也能按这个前缀做前后对比。
/// </para>
/// <para>
/// 时间起点是进程启动（<see cref="ProcessStartTime"/>），不是第一次打点，
/// 否则会漏掉 JIT 与主机构建那段。
/// </para>
/// </summary>
public static class StartupTimeline
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>由 <see cref="ApiHost"/> 注入：同一行同时写控制台与可选的日志文件。</summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>从进程启动算起的毫秒数（进程内单调计时，不受系统时钟调整影响）。</summary>
    public static double ElapsedMilliseconds => Clock.Elapsed.TotalMilliseconds;

    /// <summary>打一个点，返回该点距进程启动的毫秒数。</summary>
    public static double Mark(string phase)
    {
        Sink?.Invoke(phase);
        return ElapsedMilliseconds;
    }

    /// <summary>打一个区间点：<paramref name="step"/> 从 <paramref name="since"/> 到现在的耗时。</summary>
    public static void Step(string step, double since)
    {
        var elapsed = ElapsedMilliseconds;
        Sink?.Invoke($"{step} [{elapsed - since:F0}ms]");
    }
}
