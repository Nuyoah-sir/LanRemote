// 只验证往返时延计量内核：可注入时钟下的精确 RTT、有界待确认、重复/未知回显不结算、过期清理；
// 不收发字节、不建立连接、不定时，不代表已接 ping loop 或已用于画质调控。

using System;
using System.Threading.Tasks;
using Xunit;

namespace LanRemote.Transport.Tests;

public sealed class PingRttMeterTests
{
    [Fact]
    public void Round_trip_is_measured_from_the_injected_clock()
    {
        ManualDeadlineClock clock = new();
        PingRttMeter meter = new(clock);

        Assert.True(meter.Arm(1));
        clock.Advance(TimeSpan.FromMilliseconds(42));

        Assert.True(meter.Settle(1, out TimeSpan rtt));
        Assert.Equal(TimeSpan.FromMilliseconds(42), rtt);
        Assert.Equal(TimeSpan.FromMilliseconds(42), meter.LatestRtt);
        Assert.Equal(1, meter.SampleCount);
        Assert.Equal(0, meter.PendingCount);
    }

    [Fact]
    public void No_sample_reports_null_instead_of_fabricated_zero()
    {
        PingRttMeter meter = new(new ManualDeadlineClock());

        Assert.Null(meter.LatestRtt);
        Assert.Equal(0, meter.SampleCount);
        Assert.Equal(0, meter.PendingCount);
    }

    [Fact]
    public void Unknown_or_duplicate_echo_does_not_settle_twice()
    {
        ManualDeadlineClock clock = new();
        PingRttMeter meter = new(clock);
        Assert.True(meter.Arm(5));

        Assert.False(meter.Settle(6, out TimeSpan unknown));
        Assert.Equal(TimeSpan.Zero, unknown);

        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(meter.Settle(5, out TimeSpan first));
        Assert.Equal(TimeSpan.FromMilliseconds(10), first);

        clock.Advance(TimeSpan.FromMilliseconds(90));
        Assert.False(meter.Settle(5, out _));
        Assert.Equal(1, meter.SampleCount);
        // 重复回显不得改写最近值。
        Assert.Equal(TimeSpan.FromMilliseconds(10), meter.LatestRtt);
    }

    [Fact]
    public void Negative_echo_from_the_peer_is_ignored_without_throwing()
    {
        PingRttMeter meter = new(new ManualDeadlineClock());
        Assert.True(meter.Arm(1));

        Assert.False(meter.Settle(-1, out _));
        Assert.Equal(1, meter.PendingCount);
        Assert.Equal(0, meter.SampleCount);
    }

    [Fact]
    public void Pending_samples_are_bounded_and_never_exceed_the_limit()
    {
        PingRttMeter meter = new(new ManualDeadlineClock());

        for (long sequence = 0; sequence < PingRttMeter.MaxPending; sequence++)
        {
            Assert.True(meter.Arm(sequence));
        }

        Assert.False(meter.Arm(PingRttMeter.MaxPending));
        Assert.Equal(PingRttMeter.MaxPending, meter.PendingCount);
    }

    [Fact]
    public void Duplicate_sequence_is_refused_instead_of_overwriting_the_start_time()
    {
        ManualDeadlineClock clock = new();
        PingRttMeter meter = new(clock);
        Assert.True(meter.Arm(3));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.False(meter.Arm(3));
        Assert.Equal(1, meter.PendingCount);
        Assert.True(meter.Settle(3, out TimeSpan rtt));
        Assert.Equal(TimeSpan.FromSeconds(5), rtt);
    }

    [Fact]
    public void Prune_drops_only_samples_older_than_the_age()
    {
        ManualDeadlineClock clock = new();
        PingRttMeter meter = new(clock);
        Assert.True(meter.Arm(1));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(meter.Arm(2));

        Assert.Equal(0, meter.Prune(TimeSpan.FromSeconds(60)));
        Assert.Equal(2, meter.PendingCount);

        Assert.Equal(1, meter.Prune(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, meter.PendingCount);
        Assert.False(meter.Settle(1, out _));
        Assert.True(meter.Settle(2, out _));
    }

    [Fact]
    public void Reset_clears_pending_and_history()
    {
        ManualDeadlineClock clock = new();
        PingRttMeter meter = new(clock);
        Assert.True(meter.Arm(1));
        clock.Advance(TimeSpan.FromMilliseconds(5));
        Assert.True(meter.Settle(1, out _));

        meter.Reset();

        Assert.Equal(0, meter.PendingCount);
        Assert.Equal(0, meter.SampleCount);
        Assert.Null(meter.LatestRtt);
    }

    [Fact]
    public void Latest_sample_wins_and_later_arms_are_independent()
    {
        ManualDeadlineClock clock = new();
        PingRttMeter meter = new(clock);

        Assert.True(meter.Arm(1));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(meter.Settle(1, out TimeSpan first));

        Assert.True(meter.Arm(2));
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.True(meter.Settle(2, out TimeSpan second));

        Assert.Equal(TimeSpan.FromMilliseconds(100), first);
        Assert.Equal(TimeSpan.FromMilliseconds(20), second);
        Assert.Equal(TimeSpan.FromMilliseconds(20), meter.LatestRtt);
        Assert.Equal(2, meter.SampleCount);
    }

    [Fact]
    public async Task Concurrent_arms_and_settles_never_exceed_the_bound()
    {
        PingRttMeter meter = new();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (long sequence = 0; sequence < 200; sequence++)
            {
                meter.Arm(sequence);
                meter.Settle(sequence, out TimeSpan rtt);
                Assert.True(rtt >= TimeSpan.Zero);
                meter.Prune(TimeSpan.FromMilliseconds(1));
            }

            Assert.True(worker >= 0);
        })));

        Assert.True(meter.PendingCount <= PingRttMeter.MaxPending);
    }

    [Fact]
    public void Real_system_clock_settles_a_non_negative_sample()
    {
        PingRttMeter meter = new();
        Assert.True(meter.Arm(1));

        Assert.True(meter.Settle(1, out TimeSpan rtt));
        Assert.True(rtt >= TimeSpan.Zero);
        Assert.Equal(rtt, meter.LatestRtt);
    }
}
