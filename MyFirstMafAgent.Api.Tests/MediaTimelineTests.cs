using MyFirstMafAgent.Api.Documents;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 音视频时间轴切片：切片边界必须落在时间戳行的行首，否则「引用 → 跳到第几秒」会跳偏。
/// </summary>
public sealed class MediaTimelineTests
{
    [Fact]
    public void Chunks_start_at_a_timestamp()
    {
        var chunks = MediaTimeline.BuildChunks(
        [
            new MediaSegment(TimeSpan.Zero, "开场介绍"),
            new MediaSegment(TimeSpan.FromSeconds(30), "第一个论点"),
            new MediaSegment(TimeSpan.FromSeconds(75), "第二个论点")
        ]);

        Assert.All(chunks, chunk => Assert.StartsWith("[00:", chunk, StringComparison.Ordinal));
        Assert.Single(chunks);
    }

    [Fact]
    public void Segments_are_sorted_by_time()
    {
        var chunks = MediaTimeline.BuildChunks(
        [
            new MediaSegment(TimeSpan.FromSeconds(90), "第三段"),
            new MediaSegment(TimeSpan.Zero, "第一段"),
            new MediaSegment(TimeSpan.FromSeconds(45), "第二段")
        ]);

        var lines = chunks.SelectMany(chunk => chunk.Split('\n')).ToList();
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("[00:00:00] 第一段", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("[00:00:45] 第二段", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("[00:01:30] 第三段", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Chunks_are_split_on_segment_boundaries_and_never_mix_timestamps()
    {
        var segments = Enumerable.Range(0, 20)
            .Select(index => new MediaSegment(
                TimeSpan.FromSeconds(index * 15),
                $"第 {index} 段画面：白板上写着一些需要被压缩掉的比较长的说明文字"))
            .ToList();

        var chunks = MediaTimeline.BuildChunks(segments, maxChars: 200);

        Assert.True(chunks.Count > 1, "内容超过上限时必须切成多片");
        foreach (var chunk in chunks)
        {
            // 每一行的行首都必须有时间戳——这是"从切片读回起点"的前提。
            foreach (var line in chunk.Split('\n'))
                Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] ", line);
        }

        // 切片之间不能丢内容：把所有数字拼起来应覆盖 0..19。
        var joined = string.Join(' ', chunks);
        for (var index = 0; index < 20; index++)
            Assert.Contains($"第 {index} 段画面", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_segment_longer_than_the_limit_still_keeps_its_timestamp()
    {
        var chunks = MediaTimeline.BuildChunks(
            [new MediaSegment(TimeSpan.FromSeconds(7), new string('长', 3000))],
            maxChars: 100);

        Assert.Single(chunks);
        Assert.StartsWith("[00:00:07]", chunks[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_segments_are_dropped()
    {
        var chunks = MediaTimeline.BuildChunks(
        [
            new MediaSegment(TimeSpan.Zero, "   "),
            new MediaSegment(TimeSpan.FromSeconds(10), "有内容")
        ]);

        Assert.Single(chunks);
        Assert.DoesNotContain("[00:00:00]", chunks[0], StringComparison.Ordinal);
    }

    [Fact]
    public void No_segments_produces_no_chunks()
    {
        Assert.Empty(MediaTimeline.BuildChunks([]));
        Assert.Empty(MediaTimeline.BuildChunks([new MediaSegment(TimeSpan.Zero, "")]));
    }

    [Theory]
    [InlineData("[00:00:00] 开场", true, 0)]
    [InlineData("[00:01:30] 第二个论点", true, 90)]
    [InlineData("[00:12:34-00:12:40] 转写一行", true, 754)]
    [InlineData("[01:00:00] 一小时", true, 3600)]
    [InlineData("摘要：这段没有时间戳", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void Start_seconds_are_read_back_from_the_chunk(string? content, bool expectedFound, int expectedSeconds)
    {
        var found = MediaTimeline.TryReadStartSeconds(content, out var seconds);

        Assert.Equal(expectedFound, found);
        if (expectedFound) Assert.Equal(expectedSeconds, seconds);
    }

    [Fact]
    public void Zero_seconds_is_still_a_timestamp_so_the_chunk_stays_playable()
    {
        // 端点验证发现的回归：第一片必然以 [00:00:00] 开头，把它当成"没有时间戳"
        // 会让从 0 秒开始的那片引用整段失去跳转能力。null 只能表示"不是媒体切片"。
        Assert.Equal(0, MediaTimeline.ReadStartSeconds("[00:00:00] 开场"));
        Assert.Equal(90, MediaTimeline.ReadStartSeconds("[00:01:30] 第二个论点"));
        Assert.Null(MediaTimeline.ReadStartSeconds("纯文本切片"));
    }

    [Fact]
    public void Malformed_time_markers_are_rejected()
    {
        // 分/秒越界时宁可不给时间点，也不能算出错误秒数让前端跳错位置。
        Assert.False(MediaTimeline.TryReadStartSeconds("[00:99:00] 坏数据", out _));
        Assert.False(MediaTimeline.TryReadStartSeconds("[00:00:99] 坏数据", out _));
    }

    [Fact]
    public void Showinfo_timestamps_are_parsed_in_order()
    {
        // ffmpeg 9 的 showinfo 行格式（节选，真实存在多行噪声）。
        const string stderr = """
            ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers
            [Parsed_showinfo_1 @ 000001] config in time_base: 1/1000, frame_rate: 30/1
            [Parsed_showinfo_1 @ 000001] n:   0 pts:      0 pts_time:0       duration:33
            [Parsed_showinfo_1 @ 000001] n:   1 pts:  12500 pts_time:12.5    duration:33
            [Parsed_showinfo_1 @ 000001] n:   2 pts:  90000 pts_time:90      duration:33
            """;

        var times = KnowledgeIngestionService.ParseShowInfoTimes(stderr);

        Assert.Equal(3, times.Count);
        Assert.Equal(TimeSpan.Zero, times[0]);
        Assert.Equal(TimeSpan.FromSeconds(12.5), times[1]);
        Assert.Equal(TimeSpan.FromSeconds(90), times[2]);
    }

    [Fact]
    public void Showinfo_parsing_tolerates_empty_input()
    {
        Assert.Empty(KnowledgeIngestionService.ParseShowInfoTimes(null));
        Assert.Empty(KnowledgeIngestionService.ParseShowInfoTimes(""));
        Assert.Empty(KnowledgeIngestionService.ParseShowInfoTimes("no showinfo here"));
    }
}
