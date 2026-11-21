using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Activity and feedback ids (#391): a log reserves its first block of ids from a shared store when it is created and
/// the next one in the background as soon as it takes a block, so handing out an id does not wait for the store in the
/// normal case; a reservation that fails is read once, as none, rather than thrown on every later id; and a log whose
/// block ran out before the next one arrived waits for it rather than hand out ids another log reserved.
/// </summary>
public sealed class ActivityIdTests
{
    [Fact]
    public async Task Ids_do_not_wait_for_a_reservation_that_takes_its_time_while_the_block_lasts()
    {
        using var store = new ReservingStore(first: 5000);
        var log = new RoutingActivityLog(store);
        store.Reservations.Should().Be(1, "the first block is reserved when the log is created");
        store.Block();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        long[] ids = [.. Enumerable.Range(0, RoutingActivityLog.IdBlock).Select(_ => log.Reserve())];
        timer.Stop();

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the next block is reserved in the background");
        ids[0].Should().Be(5000);
        ids.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        SpinWait.SpinUntil(() => store.Reservations >= 2, TimeSpan.FromSeconds(10));
        store.Reservations.Should().Be(2, "the next block is asked for once, in the background, as soon as the first is taken");

        Task<long> next = Task.Run(log.Reserve);
        (await Finishes(next, TimeSpan.FromMilliseconds(300))).Should().BeFalse("past the block, an id waits for the next one");
        log.Recent().Should().BeEmpty("reading the log does not wait for the store");

        store.Unblock();
        await next.WaitAsync(TimeSpan.FromSeconds(10));
        long taken = await next;
        taken.Should().BeGreaterThan(ids[^1]);
        taken.Should().Be(store.FirstOf(2), "the block that arrived is taken");
    }

    [Fact]
    public async Task Logs_sharing_a_store_never_hand_out_the_same_id_when_a_reservation_takes_its_time()
    {
        // Two routers on one state file: while one of them writes, the other's reservation waits for the file.
        using var store = new ReservingStore(first: 1);
        var first = new RoutingActivityLog(store);
        var second = new RoutingActivityLog(store);
        store.Block();

        Task<long[]> past = Task.Run(() => Enumerable.Range(0, RoutingActivityLog.IdBlock + 10).Select(_ => first.Reserve()).ToArray());
        (await Finishes(past, TimeSpan.FromMilliseconds(300))).Should().BeFalse("the first log waits for its next block");
        long[] others = [.. Enumerable.Range(0, RoutingActivityLog.IdBlock).Select(_ => second.Reserve())];
        store.Unblock();

        await past.WaitAsync(TimeSpan.FromSeconds(10));
        long[] all = [.. await past, .. others];
        all.Should().OnlyHaveUniqueItems("an id one log hands out is in a block no other log was given");
    }

    [Fact]
    public async Task Feedback_logs_sharing_a_store_never_hand_out_the_same_id_when_a_reservation_takes_its_time()
    {
        using var store = new ReservingStore(first: 1);
        var first = new RoutingFeedbackLog(store: store);
        var second = new RoutingFeedbackLog(store: store);
        store.Block();

        Task past = Task.Run(() =>
        {
            for (int i = 0; i < RoutingActivityLog.IdBlock + 10; i++)
            {
                first.Add(new RoutingFeedback { Timestamp = DateTimeOffset.UtcNow, EntryId = i, Kind = FeedbackKind.Retried });
            }
        });
        (await Finishes(past, TimeSpan.FromMilliseconds(300))).Should().BeFalse("the first log waits for its next block");
        for (int i = 0; i < RoutingActivityLog.IdBlock; i++)
        {
            second.Add(new RoutingFeedback { Timestamp = DateTimeOffset.UtcNow, EntryId = i, Kind = FeedbackKind.Retried });
        }

        first.Recent().Should().NotBeEmpty("reading the log does not wait for the store");
        store.Unblock();

        await past.WaitAsync(TimeSpan.FromSeconds(10));
        long[] all = [.. first.Recent(int.MaxValue).Select(f => f.Id), .. second.Recent(int.MaxValue).Select(f => f.Id)];
        all.Should().HaveCount((2 * RoutingActivityLog.IdBlock) + 10).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_reservation_that_fails_is_read_once_and_ids_count_on()
    {
        using var store = new ReservingStore(first: 100) { FailAfterFirst = true };
        var log = new RoutingActivityLog(store);

        long[] ids = [.. Enumerable.Range(0, 4 * RoutingActivityLog.IdBlock).Select(_ => log.Reserve())];

        ids[0].Should().Be(100);
        ids.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        ids[^1].Should().Be(100 + (4 * RoutingActivityLog.IdBlock) - 1, "a failed reservation counts on from the last id");
    }

    [Fact]
    public void A_reservation_that_never_returns_is_waited_for_a_bounded_time()
    {
        using var store = new ReservingStore(first: 10);
        var ids = new IdBlocks(store.ReserveIds, 0, TimeSpan.FromMilliseconds(200));
        store.Block();
        for (int i = 0; i < RoutingActivityLog.IdBlock; i++)
        {
            ids.TryNext(out _, out _).Should().BeTrue();
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        long id;
        IdWait wait;
        while (!ids.TryNext(out id, out wait))
        {
            wait.Wait();
        }

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        id.Should().Be(10 + RoutingActivityLog.IdBlock, "after the wait the ids count on, as for a store that failed");
    }

    [Fact]
    public void Feedback_does_not_wait_for_a_reservation_that_takes_its_time_while_the_block_lasts()
    {
        using var store = new ReservingStore(first: 70);
        var log = new RoutingFeedbackLog(store: store);
        store.Block();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < RoutingActivityLog.IdBlock; i++)
        {
            log.Add(new RoutingFeedback { Timestamp = DateTimeOffset.UtcNow, EntryId = i, Kind = FeedbackKind.Retried }).Should().BeTrue();
        }

        timer.Stop();
        store.Unblock();

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        log.Add(new RoutingFeedback { Timestamp = DateTimeOffset.UtcNow, EntryId = -1, Kind = FeedbackKind.Retried }).Should().BeTrue();
        long[] ids = [.. log.Recent(int.MaxValue).Select(f => f.Id).Reverse()];
        ids[0].Should().Be(70);
        ids.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    private static async Task<bool> Finishes(Task task, TimeSpan within) => await Task.WhenAny(task, Task.Delay(within)) == task;

    /// <summary>A store that hands out blocks of ids; while blocked, a reservation waits (another router holds the file).</summary>
    private sealed class ReservingStore(long first) : IRoutingActivityStore, IRoutingFeedbackStore, IDisposable
    {
        private readonly ManualResetEventSlim _open = new(true);
        private long _next = first;
        private int _reservations;
        private int _done;

        public bool FailAfterFirst { get; init; }

        public int Reservations => Volatile.Read(ref _reservations);

        public int Done => Volatile.Read(ref _done);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _firsts = new();

        /// <summary>The first id of the <paramref name="reservation"/>th reservation (from 1).</summary>
        public long FirstOf(int reservation) => _firsts[reservation];

        public void Block() => _open.Reset();

        public void Unblock() => _open.Set();

        public long ReserveIds(int count)
        {
            int reservation = Interlocked.Increment(ref _reservations);
            if (reservation > 1)
            {
                _open.Wait(TimeSpan.FromSeconds(30));
                if (FailAfterFirst)
                {
                    Interlocked.Increment(ref _done);
                    throw new InvalidOperationException("the file is locked");
                }
            }

            long firstId = Interlocked.Add(ref _next, count) - count;
            _firsts[reservation] = firstId;
            Interlocked.Increment(ref _done);
            return firstId;
        }

        public void Append(RoutingActivityEntry entry)
        {
        }

        public void Add(RoutingFeedback feedback)
        {
        }

        IReadOnlyList<RoutingActivityEntry> IRoutingActivityStore.Recent(int limit) => [];

        IReadOnlyList<RoutingActivityEntry> IRoutingActivityStore.Between(DateTimeOffset since, DateTimeOffset until, int limit) => [];

        IReadOnlyList<RoutingFeedback> IRoutingFeedbackStore.Recent(int limit) => [];

        IReadOnlyList<RoutingFeedback> IRoutingFeedbackStore.Between(DateTimeOffset since, DateTimeOffset until, int limit) => [];

        public void Dispose()
        {
            _open.Set();
            _open.Dispose();
        }
    }
}
