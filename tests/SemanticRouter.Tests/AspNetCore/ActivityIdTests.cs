using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Activity and feedback ids (#391): a log reserves its first block of ids from a shared store when it is created and
/// the next ones in the background, so handing out an id never waits for the store, and a reservation that fails is
/// read once, as none, rather than thrown on every later id.
/// </summary>
public sealed class ActivityIdTests
{
    [Fact]
    public void Ids_never_wait_for_a_reservation_that_takes_its_time()
    {
        using var store = new ReservingStore(first: 5000);
        var log = new RoutingActivityLog(store);
        store.Reservations.Should().Be(1, "the first block is reserved when the log is created");
        store.Block();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        long[] ids = [.. Enumerable.Range(0, 3 * RoutingActivityLog.IdBlock).Select(_ => log.Reserve())];
        timer.Stop();

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the next block is reserved in the background");
        ids[0].Should().Be(5000);
        ids.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        SpinWait.SpinUntil(() => store.Reservations >= 2, TimeSpan.FromSeconds(10));
        store.Reservations.Should().Be(2, "the next block is asked for once, in the background, and no id waits for it");

        store.Unblock();
        SpinWait.SpinUntil(() => store.Done == 2, TimeSpan.FromSeconds(10)).Should().BeTrue();
        long next = log.Reserve();
        next.Should().BeGreaterThan(ids[^1]);
        next.Should().BeGreaterThanOrEqualTo(store.LastFirst, "the block that arrived is taken");
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
    public void Feedback_never_waits_for_a_reservation_that_takes_its_time()
    {
        using var store = new ReservingStore(first: 70);
        var log = new RoutingFeedbackLog(store: store);
        store.Block();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1500; i++) // past the first block, within what the log keeps
        {
            log.Add(new RoutingFeedback { Timestamp = DateTimeOffset.UtcNow, EntryId = i, Kind = FeedbackKind.Retried }).Should().BeTrue();
        }

        timer.Stop();
        store.Unblock();

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        long[] ids = [.. log.Recent(int.MaxValue).Select(f => f.Id).Reverse()];
        ids[0].Should().Be(70);
        ids.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

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

        public long LastFirst { get; private set; }

        public void Block() => _open.Reset();

        public void Unblock() => _open.Set();

        public long ReserveIds(int count)
        {
            if (Interlocked.Increment(ref _reservations) > 1)
            {
                _open.Wait(TimeSpan.FromSeconds(30));
                if (FailAfterFirst)
                {
                    Interlocked.Increment(ref _done);
                    throw new InvalidOperationException("the file is locked");
                }
            }

            LastFirst = Interlocked.Add(ref _next, count) - count;
            Interlocked.Increment(ref _done);
            return LastFirst;
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
