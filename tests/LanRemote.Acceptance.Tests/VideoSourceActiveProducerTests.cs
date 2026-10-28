using System;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport;
using Xunit;

namespace LanRemote.Acceptance.Tests;

/// <summary>当前代际只读接缝：只暴露已发布的 active 生产者，不把尚未发布的候选或最近创建对象冒充当前代。</summary>
public sealed class VideoSourceActiveProducerTests
{
    private static readonly Guid Session = new("3f2f8f1a-9a6f-4a67-9a12-6cf0d18a2b31");

    [Fact(Timeout = 30_000)]
    public async Task Active_producer_is_the_published_generation_and_replacement_is_visible()
    {
        ReplaceableVideoFrameSource source = new(Session);
        StubProducer first = new();
        StubProducer second = new(start: true);
        try
        {
            Assert.Null(source.ActiveProducer);
            Assert.False(source.HasReplacementInFlight);
            await source.ReplaceAsync(first);
            Assert.Same(first, source.ActiveProducer);
            Assert.False(source.HasReplacementInFlight);

            Task replacing = source.ReplaceAsync(second);
            Assert.True(source.HasReplacementInFlight);
            Assert.Null(source.ActiveProducer); // 换代空窗不是 EOF，也不提前冒充新代已发布。
            Assert.False(replacing.IsCompleted);

            second.Release();
            await replacing;
            Assert.Same(second, source.ActiveProducer);
            Assert.False(source.HasReplacementInFlight);
            Assert.Equal(1, first.StopCalls);
            Assert.Equal(0, second.StopCalls);
        }
        finally
        {
            second.Release();
            await source.DisposeAsync();
        }
        Assert.Equal(1, second.StopCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task Second_replacement_is_rejected_while_one_is_in_flight()
    {
        ReplaceableVideoFrameSource source = new(Session);
        StubProducer first = new();
        StubProducer held = new(start: true);
        StubProducer third = new();
        try
        {
            await source.ReplaceAsync(first);
            Task replacing = source.ReplaceAsync(held);
            Assert.True(source.HasReplacementInFlight);
            Assert.Throws<InvalidOperationException>(() => { _ = source.ReplaceAsync(third); });
            held.Release();
            await replacing;
            Assert.Same(held, source.ActiveProducer);
        }
        finally
        {
            held.Release();
            await source.DisposeAsync();
        }
    }

    private sealed class StubProducer : IVideoFrameProducer
    {
        private readonly TaskCompletionSource? _start;
        private int _stopCalls, _startCalls, _disposeCalls;
        internal StubProducer(bool start = false) => _start = start ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal void Release() => _start?.TrySetResult();
        public Task Completion { get; } = Task.CompletedTask;
        public void Start()
        {
            Interlocked.Increment(ref _startCalls);
            _start?.Task.GetAwaiter().GetResult();
        }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default) =>
            new((EncodedFrame?)null);
        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCalls);
            return Completion;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }
}
