using System.Collections.Concurrent;
using System.Threading.Channels;
using MyFirstMafAgent.Api.Domain.Jobs;

namespace MyFirstMafAgent.Api.Application.Jobs;

/// <summary>
/// 任务状态的进程内广播，用于 SSE 推送与「有新任务了，别傻等轮询」的唤醒。
///
/// <para>
/// 每个订阅者一个独立的有界通道：慢客户端只会丢掉自己的旧事件（保留最新进度），
/// 绝不会拖慢其他订阅者，也不会因为「没人读」而阻塞任务本身。
/// </para>
/// </summary>
public sealed class JobEventStream
{
    private const int SubscriberCapacity = 64;

    private readonly ConcurrentDictionary<Guid, Channel<JobEvent>> _subscribers = new();

    /// <summary>订阅事件流。</summary>
    public JobSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<JobEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers[id] = channel;
        return new JobSubscription(id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel)) channel.Writer.TryComplete();
    }

    /// <summary>广播一条任务事件；没有订阅者时是零成本的空操作。</summary>
    public void Publish(JobEvent jobEvent)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(jobEvent);
    }

    /// <summary>广播多条（例如批量补投）。</summary>
    public void Publish(IEnumerable<JobEvent> events)
    {
        foreach (var channel in _subscribers.Values)
        {
            foreach (var jobEvent in events) channel.Writer.TryWrite(jobEvent);
        }
    }
}

/// <summary>一次订阅的句柄。</summary>
public sealed class JobSubscription(Guid id, ChannelReader<JobEvent> reader)
{
    public Guid Id { get; } = id;

    public ChannelReader<JobEvent> Reader { get; } = reader;
}

/// <summary>
/// 调度器的唤醒信号：入队成功后立刻唤醒空闲槽位，避免等到下一个轮询周期。
///
/// <para>
/// 信号量而不是事件：一批入队 5 个任务就要能唤醒最多 5 个槽位，
/// 单个「已重置」事件只能唤醒一个等待者，会退化成按轮询间隔逐个启动。
/// </para>
/// </summary>
public sealed class JobWakeSignal
{
    private const int Capacity = 512;

    private readonly SemaphoreSlim _signal = new(0, Capacity);

    /// <summary>通知有 <paramref name="count"/> 个新任务可领。</summary>
    public void Notify(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            try
            {
                if (_signal.CurrentCount >= Capacity) return;
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                return;
            }
        }
    }

    /// <summary>等待信号或超时；返回 true 表示是被信号唤醒的。</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _signal.WaitAsync(timeout, cancellationToken);
}
