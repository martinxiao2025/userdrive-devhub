using DevHubd.Core.Data;
using DevHubd.Core.Execution;
using DevHubd.Core.Results;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// P4 通知游标验收：<c>seq</c> 单调递增、<c>acked_at</c> 由数据库保证"已提醒过的不再提醒"，
/// 因此守护进程崩溃重启或扩展窗口重载后都不会重复弹出同一条通知。
/// </summary>
public sealed class NoticeStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly ExecutionStore _execution;

    /// <summary>准备临时数据库。</summary>
    public NoticeStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        _execution = new ExecutionStore(_factory);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>seq 严格单调，且按 sinceSeq 只返回更新的通知。</summary>
    [Fact]
    public async Task ListSinceAsync_ReturnsOnlyNewerNotices_InAscendingSeqAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow.ToString("O");

        await _execution.InsertNoticeAsync("result:T-001:awaiting_review:1", "result", "T-001", "任务 T-001 完成", "正文一", now, ct);
        await _execution.InsertNoticeAsync("result:T-002:awaiting_review:1", "result", "T-002", "任务 T-002 完成", "正文二", now, ct);
        await _execution.InsertNoticeAsync("result:T-003:awaiting_review:1", "result", "T-003", "任务 T-003 完成", "正文三", now, ct);

        var notices = new NoticeStore(_factory);
        var page = await notices.ListSinceAsync(0, 10, ct);

        Assert.Equal(3, page.Items.Count);
        Assert.True(page.Items[0].Seq < page.Items[1].Seq && page.Items[1].Seq < page.Items[2].Seq);
        Assert.Equal(page.Items[^1].Seq, page.LatestSeq);

        var newer = await notices.ListSinceAsync(page.Items[1].Seq, 10, ct);
        Assert.Single(newer.Items);
        Assert.Equal("T-003", newer.Items[0].TaskId);
    }

    /// <summary>
    /// 崩溃恢复：确认过的通知在新连接（模拟重启后的进程）中再次查询也不会返回；
    /// 新写入的通知只有更大的 seq，游标推进后只提醒新条目。
    /// </summary>
    [Fact]
    public async Task AckAsync_SurvivesRestart_AndNeverNotifiesSameSeqTwiceAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow.ToString("O");

        await _execution.InsertNoticeAsync("result:T-001:awaiting_review:1", "result", "T-001", "第一条", "正文", now, ct);
        await _execution.InsertNoticeAsync("result:T-002:awaiting_review:1", "result", "T-002", "第二条", "正文", now, ct);

        // 第一次轮询：拿到两条并全部确认。
        var beforeRestart = new NoticeStore(_factory);
        var firstPage = await beforeRestart.ListSinceAsync(0, 10, ct);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(2, await beforeRestart.AckAsync(firstPage.Items.Select(item => item.Seq).ToArray(), now, ct));

        // 模拟进程重启：新的连接工厂与 NoticeStore 实例，数据库文件不变。
        var restartedFactory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        var afterRestart = new NoticeStore(restartedFactory);
        var replay = await afterRestart.ListSinceAsync(0, 10, ct);
        Assert.Empty(replay.Items);
        Assert.Equal(firstPage.LatestSeq, replay.LatestSeq);

        // 重启后新产生的通知：seq 更大，游标推进后只返回这一条。
        await new ExecutionStore(restartedFactory).InsertNoticeAsync(
            "result:T-003:awaiting_review:1", "result", "T-003", "第三条", "正文", now, ct);

        var secondPage = await afterRestart.ListSinceAsync(replay.LatestSeq, 10, ct);
        Assert.Single(secondPage.Items);
        Assert.Equal("T-003", secondPage.Items[0].TaskId);
        Assert.True(secondPage.Items[0].Seq > replay.LatestSeq);
    }

    /// <summary>同一 dedupe_key 重复写入不新增通知（数据库层去重）。</summary>
    [Fact]
    public async Task InsertNoticeAsync_DuplicateDedupeKey_DoesNotCreateSecondNoticeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow.ToString("O");

        await _execution.InsertNoticeAsync("breaker:openai:quota", "breaker", "T-001", "配额不足", "正文", now, ct);
        await _execution.InsertNoticeAsync("breaker:openai:quota", "breaker", "T-001", "配额不足（重复）", "正文", now, ct);

        var page = await new NoticeStore(_factory).ListSinceAsync(0, 10, ct);
        Assert.Single(page.Items);
    }
}
