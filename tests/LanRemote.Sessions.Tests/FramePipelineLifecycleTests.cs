using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Xunit;
using Xunit.Sdk;

namespace LanRemote.Sessions.Tests;

public sealed class FramePipelineLifecycleTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData("capture", true)]
    [InlineData("capture", false)]
    [InlineData("encode", true)]
    [InlineData("encode", false)]
    public async Task Stop_joins_synchronous_prefix_and_original_async_tail_before_EOF(
        string stage, bool stopInPrefix)
    {
        var f = new Fixture();
        f.PrefixStage = stage;
        f.TailStage = stage;
        f.SpecialIndex = 1;
        try
        {
            f.Pipeline.Start();
            Task coordinator = await f.Coordinator();
            Call<CapturedFrame> firstCapture = await f.Captures.At(0);
            Task captureLoop = await f.Loop(firstCapture, "CaptureLoopAsync");
            firstCapture.Reply();
            Call<EncodedFrame> firstEncode = await f.Encodes.At(0);
            Task encodeLoop = await f.Loop(firstEncode, "EncodeLoopAsync");
            firstEncode.Reply();
            using (EncodedFrame? warmup = await Success(f.Read())) Assert.NotNull(warmup);

            Call<CapturedFrame> nextCapture = await f.Captures.At(1);
            CallBase held;
            if (stage == "capture") held = nextCapture;
            else
            {
                nextCapture.Reply();
                held = await f.Encodes.At(1);
            }
            await Success(held.Prefix.Entered.Task);
            Task loop = stage == "capture" ? captureLoop : encodeLoop;
            Assert.False(held.Prefix.IsOpen);
            Assert.False(held.InvocationExited.Task.IsCompleted);

            if (!stopInPrefix)
            {
                held.Prefix.Open();
                held.ReplyDefault();
                await Success(held.Tail.Entered.Task);
                // RunTail 的同步前缀可能先发出 Tail.Entered，Invoke 尚未写回 Worker。
                await Success(held.InvocationExited.Task);
                await AssertAwaiting(loop, held.Worker!);
            }

            Task<EncodedFrame?> reader = f.Read();
            Task queueRead = await RegisteredAwait(reader, f.Pipeline, "ReadCoreAsync");
            Task completion = f.Pipeline.Completion;
            Assert.Same(completion, f.Pipeline.StopAsync());
            Assert.Same(completion, f.Pipeline.StopAsync());
            Assert.Same(completion, f.Pipeline.DisposeAsync().AsTask());
            f.ReleaseCallsExcept(held);
            await Success(held.CancellationSeen.Task);
            await AssertLoopJoined(f, coordinator, loop, stage);

            if (stopInPrefix)
            {
                // 已知真实 loop 的 Task.Run 解包链仍在 join 中；不是只看一个未完成快照。
                Assert.False(held.Prefix.IsOpen);
                Assert.False(held.InvocationExited.Task.IsCompleted);
                held.AssertInputAlive();
                held.Prefix.Open();
                held.ReplyDefault();
                await Success(held.Tail.Entered.Task);
            }

            // 原结果已产生，但原异步尾部尚未退出，raw 仍不能回收。
            await Success(held.InvocationExited.Task);
            await AssertAwaiting(loop, held.Worker!);
            await AssertLoopJoined(f, coordinator, loop, stage);
            held.AssertInputAlive();
            Assert.Equal(0, held.ResultOwner!.DisposeCount);
            await Observe(queueRead);
            await AssertAwaiting(reader, completion);
            Assert.Throws<InvalidOperationException>(f.Pipeline.Start);
            // 不用 Task.Run(Func<Task>)，否则会自动解包，把验证返回身份变成等待停止完成。
            Task<Task>[] requests = Enumerable.Range(0, 8).Select(index => Task.Factory.StartNew(
                () => index % 2 == 0 ? f.Pipeline.StopAsync() : f.Pipeline.DisposeAsync().AsTask(),
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default)).ToArray();
            f.Track(requests);
            Assert.All(await Success(Task.WhenAll(requests)), task => Assert.Same(completion, task));

            held.Tail.Open();
            await Success(completion);
            Assert.Null(await Success(reader));
            Assert.True(held.Worker!.IsCompletedSuccessfully);
            Assert.Equal(0, held.InputDisposalsAtPrefixExit);
            Assert.Equal(0, held.InputDisposalsAtTailExit);
            Assert.Null(held.InputReadErrorAtTailExit);
            Assert.Equal(1, held.ResultOwner.DisposeCount);
            Assert.Same(completion, f.Pipeline.StopAsync());
            Assert.Same(completion, f.Pipeline.DisposeAsync().AsTask());
            f.AssertAllReleased();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Reader_cancellation_releases_read_slot_but_not_original_capture_or_pipeline()
    {
        var f = new Fixture();
        using var readerCancellation = new CancellationTokenSource();
        try
        {
            f.Pipeline.Start();
            Call<CapturedFrame> capture = await f.Captures.At(0);
            Task loop = await f.Loop(capture, "CaptureLoopAsync");
            Task<EncodedFrame?> canceledRead = f.Read(readerCancellation.Token);
            await RegisteredAwait(canceledRead, f.Pipeline, "ReadCoreAsync");
            readerCancellation.Cancel();
            var canceled = Assert.IsAssignableFrom<OperationCanceledException>(await Error(canceledRead));
            Assert.True(readerCancellation.IsCancellationRequested);
            Assert.Equal(f.ReadToken(canceledRead), canceled.CancellationToken);
            Assert.True(canceledRead.IsCanceled);
            await AssertAwaiting(loop, capture.Worker!);
            Assert.False(capture.Token.IsCancellationRequested);

            Task<EncodedFrame?> nextRead = f.Read();
            capture.Reply();
            Call<EncodedFrame> encode = await f.Encodes.At(0);
            EncodedFrame expected = encode.Reply();
            using (EncodedFrame? actual = await Success(nextRead)) Assert.Same(expected, actual);
            // 实际完成后续往返，而不是以 Completion 的瞬时状态推断管线未被停止。
            Call<CapturedFrame> second = await f.Captures.At(1);
            Assert.False(second.Token.IsCancellationRequested);
            second.Reply();
            Call<EncodedFrame> secondEncode = await f.Encodes.At(1);
            EncodedFrame secondExpected = secondEncode.Reply();
            using (EncodedFrame? actual = await Success(f.Read())) Assert.Same(secondExpected, actual);
            Task stop = f.Pipeline.StopAsync();
            f.ReleaseCallsExcept(null);
            await Success(stop);
            f.AssertAllReleased();
        }
        finally
        {
            readerCancellation.Cancel();
            await f.Finish();
        }
    }

    [Theory]
    [InlineData("capture", "single", false)]
    [InlineData("capture", "nested", false)]
    [InlineData("capture", "multiple", false)]
    [InlineData("capture", "own-oce", false)]
    [InlineData("capture", "single", true)]
    [InlineData("capture", "nested", true)]
    [InlineData("capture", "multiple", true)]
    [InlineData("capture", "own-oce", true)]
    [InlineData("encode", "single", false)]
    [InlineData("encode", "nested", false)]
    [InlineData("encode", "multiple", false)]
    [InlineData("encode", "own-oce", false)]
    [InlineData("encode", "single", true)]
    [InlineData("encode", "nested", true)]
    [InlineData("encode", "multiple", true)]
    [InlineData("encode", "own-oce", true)]
    public async Task Faulted_original_preserves_all_ordered_roots_and_nested_instances_and_never_becomes_EOF(
        string stage, string shape, bool stopFirst)
    {
        var f = new Fixture();
        try
        {
            f.Pipeline.Start();
            CallBase call = await f.FirstOperation(stage);
            Task loop = await f.Loop(call, Method(stage));
            var leaf = new IOException("原操作叶异常。");
            var inner = new AggregateException("原嵌套节点，不能展平。", leaf);
            var branch = new AggregateException("原分支，不能重建。", new InvalidOperationException("兄弟叶。"), inner);
            var ownOce = new OperationCanceledException("Faulted 中的本 token OCE，不是正常取消。", call.Token);
            Exception[] roots = shape switch
            {
                "single" => [leaf],
                "nested" => [new AggregateException("单子容器也必须保留。", branch)],
                "multiple" => [branch, ownOce, branch],
                _ => [ownOce]
            };
            Task<EncodedFrame?> reader = f.Read();
            if (stopFirst)
            {
                _ = f.Pipeline.StopAsync();
                await Success(call.CancellationSeen.Task);
                await AssertAwaiting(loop, call.Worker!);
            }
            call.Fail(roots);
            // 吞错后循环可能正常退出却不请求停止，先由原循环故障断言判错。
            await Observe(f.PublicLoop(stage));
            Task failedLoop = AssertLoopFailure(f, stage, roots);
            await Success(call.CancellationSeen.Task);
            f.ReleaseCallsExcept(call);
            var failure = Assert.IsType<AggregateException>(await Error(f.Pipeline.Completion));
            Assert.True(call.Worker!.IsFaulted);
            Assert.True(f.Pipeline.Completion.IsFaulted);
            AssertChildren(call.Worker.Exception!, roots);
            AssertSubtreeWithChildren(failure, roots);
            AssertCompletionRoots(f, failure, failedLoop);
            Assert.Same(inner, branch.InnerExceptions[1]);
            Assert.Same(leaf, Assert.Single(inner.InnerExceptions));
            Assert.Same(failure, await Error(reader));
            Assert.Same(failure, await Error(f.Read()));
            Assert.Same(f.Pipeline.Completion, f.Pipeline.StopAsync());
            Assert.Same(f.Pipeline.Completion, f.Pipeline.DisposeAsync().AsTask());
            Assert.Same(failure, await Error(f.Pipeline.DisposeAsync().AsTask()));
            f.AssertAllReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("capture", false)]
    [InlineData("capture", true)]
    [InlineData("encode", false)]
    [InlineData("encode", true)]
    public async Task Canceled_original_task_is_benign_only_for_requested_pipeline_token(string stage, bool foreign)
    {
        var f = new Fixture();
        using var foreignSource = new CancellationTokenSource();
        foreignSource.Cancel();
        try
        {
            f.Pipeline.Start();
            CallBase call = await f.FirstOperation(stage);
            Task loop = await f.Loop(call, Method(stage));
            Task<EncodedFrame?> reader = f.Read();
            Task stop = f.Pipeline.StopAsync();
            await Success(call.CancellationSeen.Task);
            await AssertAwaiting(loop, call.Worker!);
            call.Cancel(foreign ? foreignSource.Token : call.Token);
            f.ReleaseCallsExcept(call);
            Exception original = await Error(call.Worker!);
            Assert.True(call.Worker!.IsCanceled);
            var canceled = Assert.IsAssignableFrom<OperationCanceledException>(original);
            Assert.Equal(foreign ? foreignSource.Token : call.Token, canceled.CancellationToken);
            if (foreign)
            {
                var failure = Assert.IsType<AggregateException>(await Error(stop));
                // TrySetCanceled 没有保存异常实例；不同 await 可以各自创建 TaskCanceledException。
                Assert.Contains(Tree(failure).OfType<OperationCanceledException>(),
                    error => error.CancellationToken == foreignSource.Token);
                Task failedLoop = f.PublicLoop(stage);
                var loopRoot = Assert.IsType<AggregateException>(Assert.Single(failedLoop.Exception!.InnerExceptions));
                var loopCancellation = Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(loopRoot.InnerExceptions));
                Assert.Equal(foreignSource.Token, loopCancellation.CancellationToken);
                AssertCompletionRoots(f, failure, failedLoop);
                Assert.Same(failure, await Error(reader));
                Assert.True(stop.IsFaulted);
            }
            else
            {
                await Success(stop);
                Assert.Null(await Success(reader));
                Assert.True(stop.IsCompletedSuccessfully);
            }
            f.AssertAllReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("capture", false, false)]
    [InlineData("capture", false, true)]
    [InlineData("capture", true, false)]
    [InlineData("capture", true, true)]
    [InlineData("encode", false, false)]
    [InlineData("encode", false, true)]
    [InlineData("encode", true, false)]
    [InlineData("encode", true, true)]
    public async Task Synchronous_OCE_requires_both_own_token_and_an_actual_stop_request(
        string stage, bool foreign, bool stopFirst)
    {
        var f = new Fixture { PrefixStage = stage, SpecialIndex = 0 };
        using var foreignSource = new CancellationTokenSource();
        foreignSource.Cancel();
        try
        {
            f.Pipeline.Start();
            CallBase call = await f.FirstOperation(stage);
            await Success(call.Prefix.Entered.Task);
            var original = new OperationCanceledException("同步前缀原 OCE。", foreign ? foreignSource.Token : call.Token);
            call.PrefixFailure = original;
            if (stopFirst)
            {
                _ = f.Pipeline.StopAsync();
                await Success(call.CancellationSeen.Task);
            }
            call.Prefix.Open();
            await Success(call.InvocationExited.Task);
            await Observe(f.PublicLoop(stage));
            if (!stopFirst || foreign) _ = AssertLoopFailure(f, stage, original);
            await Success(call.CancellationSeen.Task);
            f.ReleaseCallsExcept(call);
            Assert.Null(call.Worker);
            if (stopFirst && !foreign)
            {
                await Success(f.Pipeline.Completion);
                Assert.Null(await Success(f.Read()));
            }
            else
            {
                var failure = Assert.IsType<AggregateException>(await Error(f.Pipeline.Completion));
                AssertContainsSame(failure, original);
                AssertCompletionRoots(f, failure, AssertLoopFailure(f, stage, original));
                Assert.Same(failure, await Error(f.Read()));
            }
            f.AssertAllReleased();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Concurrent_worker_faults_and_raw_cleanup_fault_are_all_preserved()
    {
        var f = new Fixture();
        try
        {
            f.Pipeline.Start();
            Call<CapturedFrame> first = await f.Captures.At(0);
            var cleanup = new OperationCanceledException("raw 的 Dispose OCE 不能视为业务取消。");
            first.Reply(failure: cleanup);
            Call<EncodedFrame> encode = await f.Encodes.At(0);
            Call<CapturedFrame> capture = await f.Captures.At(1);
            await f.Loop(encode, "EncodeLoopAsync");
            await f.Loop(capture, "CaptureLoopAsync");
            var captureLeaf = new IOException("采集根一。");
            var captureTree = new AggregateException("采集根二。", new InvalidOperationException("采集内叶。"));
            var encodeLeaf = new IOException("编码根一。");
            var encodeTree = new AggregateException("编码根二。", new AggregateException("编码内节点。", new Exception("编码内叶。")));
            Task<EncodedFrame?> reader = f.Read();
            capture.Fail([captureLeaf, captureTree, captureTree]);
            await Observe(f.PublicLoop("capture"));
            Task failedCapture = AssertLoopFailure(f, "capture", captureLeaf, captureTree, captureTree);
            await Success(encode.CancellationSeen.Task);
            // 已收到首错停止后，再让另一条真实原操作失败，不能只保留第一个 await 抛出的异常。
            encode.Fail([encodeLeaf, encodeTree]);
            f.ReleaseCallsExcept(null);
            var failure = Assert.IsType<AggregateException>(await Error(f.Pipeline.Completion));
            AssertSubtreeWithChildren(failure, [captureLeaf, captureTree, captureTree]);
            AssertSubtreeWithChildren(failure, [encodeLeaf, encodeTree, cleanup]);
            AssertCompletionRoots(f, failure, failedCapture,
                AssertLoopFailure(f, "encode", encodeLeaf, encodeTree, cleanup));
            Assert.Same(failure, await Error(reader));
            Assert.Equal(1, first.ResultOwner!.DisposeCount);
            f.AssertAllReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("encode")]
    public async Task Late_result_cleanup_OCE_faults_completion_instead_of_EOF(string stage)
    {
        var f = new Fixture();
        try
        {
            f.Pipeline.Start();
            CallBase call = await f.FirstOperation(stage);
            Task loop = await f.Loop(call, Method(stage));
            Task<EncodedFrame?> reader = f.Read();
            _ = f.Pipeline.StopAsync();
            await Success(call.CancellationSeen.Task);
            await AssertAwaiting(loop, call.Worker!);
            var cleanup = new OperationCanceledException("迟到 owner 的本 token 清理 OCE。", call.Token);
            call.ReplyWithFailure(cleanup);
            f.ReleaseCallsExcept(call);
            var failure = Assert.IsType<AggregateException>(await Error(f.Pipeline.Completion));
            AssertContainsSame(failure, cleanup);
            AssertCompletionRoots(f, failure, AssertLoopFailure(f, stage, cleanup));
            Assert.Same(failure, await Error(reader));
            Assert.True(call.Worker!.IsCompletedSuccessfully);
            Assert.Equal(1, call.ResultOwner!.DisposeCount);
            f.AssertAllReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("raw", false, false)]
    [InlineData("raw", false, true)]
    [InlineData("raw", true, false)]
    [InlineData("raw", true, true)]
    [InlineData("encoded", false, false)]
    [InlineData("encoded", false, true)]
    [InlineData("encoded", true, false)]
    [InlineData("encoded", true, true)]
    public async Task DropOldest_disposer_failure_keeps_new_owner_and_blocked_drop_is_joined(
        string queue, bool block, bool oce)
    {
        var f = new Fixture(rawCapacity: 1, encodedCapacity: 1);
        Gate drop = f.NewGate();
        if (!block) drop.Open();
        Exception original = oce
            ? new OperationCanceledException("DropOldest 原清理 OCE。")
            : new IOException("DropOldest 原清理错误。");
        try
        {
            f.Pipeline.Start();
            Task coordinator = await f.Coordinator();
            Call<CapturedFrame> c0 = await f.Captures.At(0);
            Task captureLoop = await f.Loop(c0, "CaptureLoopAsync");
            c0.Reply();
            Call<EncodedFrame> e0 = await f.Encodes.At(0);
            Task encodeLoop = await f.Loop(e0, "EncodeLoopAsync");
            CountingOwner evicted;
            CountingOwner accepted;
            CallBase producer;
            if (queue == "raw")
            {
                Call<CapturedFrame> c1 = await f.Captures.At(1);
                c1.Reply(drop, original);
                evicted = c1.ResultOwner!;
                Call<CapturedFrame> c2 = await f.Captures.At(2);
                c2.Reply();
                accepted = c2.ResultOwner!;
                producer = c2;
            }
            else
            {
                e0.Reply(drop, original);
                Call<CapturedFrame> c1 = await f.Captures.At(1);
                c1.Reply();
                Call<EncodedFrame> e1 = await f.Encodes.At(1);
                e1.Reply();
                evicted = e0.ResultOwner!;
                accepted = e1.ResultOwner!;
                producer = e1;
            }
            await Success(drop.Entered.Task);
            if (block)
            {
                _ = f.Pipeline.StopAsync();
                await Success(producer.CancellationSeen.Task);
                await AssertLoopJoined(f, coordinator, queue == "raw" ? captureLoop : encodeLoop,
                    queue == "raw" ? "capture" : "encode");
                // 新帧已在 TryWrite 锁内移交；Stop 不等锁外旧帧 disposer，也不重复释放新帧。
                await Success(accepted.Disposed.Task);
                Assert.Equal(1, accepted.DisposeCount);
                Assert.False(drop.IsOpen);
                Assert.False(drop.Exited.Task.IsCompleted);
                drop.Open();
            }
            string producerStage = queue == "raw" ? "capture" : "encode";
            await Observe(f.PublicLoop(producerStage));
            Task failedProducer = AssertLoopFailure(f, producerStage, original);
            await Success(producer.CancellationSeen.Task);
            f.ReleaseCallsExcept(null);
            var failure = Assert.IsType<AggregateException>(await Error(f.Pipeline.Completion));
            AssertContainsSame(failure, original);
            AssertCompletionRoots(f, failure, failedProducer);
            Assert.Equal(1, evicted.DisposeCount);
            Assert.Equal(1, accepted.DisposeCount);
            Assert.Same(failure, await Error(f.Read()));
            f.AssertAllReleased();
        }
        finally
        {
            drop.Open();
            await f.Finish();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queue_stops_and_cancel_callbacks_run_independently_and_every_cleanup_is_joined(bool throws)
    {
        var f = new Fixture();
        Gate rawGate = f.NewGate();
        Gate encodedGate = f.NewGate();
        Gate cancelGate = f.NewGate();
        CancellationTokenRegistration registration = default;
        try
        {
            f.Pipeline.Start();
            Task coordinator = await f.Coordinator();
            var rawFirstError = new IOException("raw Stop 第一项。");
            var rawSecondError = new OperationCanceledException("raw Stop 第二项 OCE。");
            var encodedFirstError = new OperationCanceledException("encoded Stop 第一项 OCE。");
            var encodedSecondError = new IOException("encoded Stop 第二项。");
            var callbackError = new AggregateException("Cancel callback 原嵌套错误。", new IOException("callback 叶。"));

            Call<CapturedFrame> c0 = await f.Captures.At(0);
            c0.Reply();
            Call<EncodedFrame> e0 = await f.Encodes.At(0);
            e0.Reply(encodedGate, throws ? encodedFirstError : null);
            Call<CapturedFrame> c1 = await f.Captures.At(1);
            c1.Reply();
            Call<EncodedFrame> e1 = await f.Encodes.At(1);
            e1.Reply(failure: throws ? encodedSecondError : null);
            Call<CapturedFrame> c2 = await f.Captures.At(2);
            c2.Reply();
            Call<EncodedFrame> activeEncode = await f.Encodes.At(2);
            // 第三次 Encode 已进入，证明前两个 encoded 已完成入队。
            await f.Loop(activeEncode, "EncodeLoopAsync");
            Call<CapturedFrame> c3 = await f.Captures.At(3);
            c3.Reply(rawGate, throws ? rawFirstError : null);
            Call<CapturedFrame> c4 = await f.Captures.At(4);
            c4.Reply(failure: throws ? rawSecondError : null);
            Call<CapturedFrame> activeCapture = await f.Captures.At(5);
            // 第六次 Capture 已进入，证明两个排队 raw 的 TryWrite 都已返回。
            await f.Loop(activeCapture, "CaptureLoopAsync");
            registration = activeCapture.Token.Register(() =>
            {
                cancelGate.Block();
                if (throws) throw callbackError;
            });

            Task completion = f.Pipeline.Completion;
            Task<Task> stopRequest = Task.Factory.StartNew(f.Pipeline.StopAsync, CancellationToken.None,
                TaskCreationOptions.None, TaskScheduler.Default);
            f.Track(stopRequest);
            // 就绪条件是“任意清理进入”，不要求三路都已成功独立进入。
            Task firstEntered = await Success(Task.WhenAny(rawGate.Entered.Task, encodedGate.Entered.Task, cancelGate.Entered.Task));
            Gate firstGate = new[] { rawGate, encodedGate, cancelGate }
                .Single(gate => ReferenceEquals(gate.Entered.Task, firstEntered));
            // 若把 disposer/Cancel 直接放在协调器栈上，协调器根本来不及发布 await。
            // 先用已进入的真实调用栈直接否定这种串行实现，不能让下面的 await 探针等到 Guard。
            Assert.False(firstGate.OnCoordinatorStack, "阻塞清理不能占用真实协调状态机的同步调用栈。");
            Assert.NotSame(stopRequest, firstGate.ExecutingTask);
            Task join = await RegisteredAwait(coordinator, f.Pipeline, "CoordinateStopAsync");
            AssertWhenAll(join);
            // 两个原操作和三个清理门全部关闭，五个输入不可能合法完成。
            // 若改成一个串行清理任务，直接由真实 WhenAll 的输入数失败，而非等 Guard 杀错。
            // 在放行循环之前观察，避免把“await 循环结束”误当作 WhenAll 的递减回调已执行。
            Assert.Equal(5, Assert.IsType<int>(RequiredField(join.GetType(), "_remainingToComplete").GetValue(join)));
            f.ReleaseCallsExcept(null);
            await Observe(f.PublicLoop("capture"));
            await Observe(f.PublicLoop("encode"));
            await Success(Task.WhenAll(rawGate.Entered.Task, encodedGate.Entered.Task, cancelGate.Entered.Task));
            Task rawStop = Assert.IsAssignableFrom<Task>(rawGate.ExecutingTask);
            Task encodedStop = Assert.IsAssignableFrom<Task>(encodedGate.ExecutingTask);
            Task cancel = Assert.IsAssignableFrom<Task>(cancelGate.ExecutingTask);
            f.Track(rawStop, encodedStop, cancel);
            Assert.NotSame(rawStop, encodedStop);
            Assert.NotSame(rawStop, cancel);
            Assert.NotSame(encodedStop, cancel);
            Assert.NotSame(stopRequest, cancel);
            Assert.Same(completion, await Success(stopRequest));
            Assert.True(IsRegistered(rawStop, join));
            Assert.True(IsRegistered(encodedStop, join));
            Assert.True(IsRegistered(cancel, join));

            Task<EncodedFrame?> reader = f.Read();
            await AssertAwaiting(reader, completion);
            Assert.Same(completion, f.Pipeline.DisposeAsync().AsTask());
            Assert.Same(completion, f.Pipeline.StopAsync());
            rawGate.Open();
            await Observe(rawStop);
            Assert.Equal(1, c3.ResultOwner!.DisposeCount);
            Assert.Equal(1, c4.ResultOwner!.DisposeCount);
            Assert.True(IsRegistered(encodedStop, join));
            Assert.True(IsRegistered(cancel, join));
            encodedGate.Open();
            await Observe(encodedStop);
            Assert.Equal(1, e0.ResultOwner!.DisposeCount);
            Assert.Equal(1, e1.ResultOwner!.DisposeCount);
            await AssertAwaiting(coordinator, join);
            Assert.True(IsRegistered(cancel, join));
            await AssertAwaiting(reader, completion);
            cancelGate.Open();

            if (throws)
            {
                var failure = Assert.IsType<AggregateException>(await Error(completion));
                foreach (Exception expected in new Exception[]
                    { rawFirstError, rawSecondError, encodedFirstError, encodedSecondError, callbackError })
                    AssertContainsSame(failure, expected);
                AssertSubtreeWithChildren(failure, [rawFirstError, rawSecondError]);
                AssertSubtreeWithChildren(failure, [encodedFirstError, encodedSecondError]);
                AssertCleanupFailure(rawStop, rawFirstError, rawSecondError);
                AssertCleanupFailure(encodedStop, encodedFirstError, encodedSecondError);
                AssertCleanupFailure(cancel, callbackError);
                AssertCompletionRoots(f, failure, rawStop, encodedStop, cancel);
                Assert.Same(failure, await Error(reader));
            }
            else
            {
                await Success(completion);
                Assert.Null(await Success(reader));
            }
            Assert.True(cancel.IsCompleted);
            Assert.True(cancelGate.Exited.Task.IsCompletedSuccessfully);
            f.AssertAllReleased();
        }
        finally
        {
            rawGate.Open();
            encodedGate.Open();
            cancelGate.Open();
            try { await f.Finish(); }
            finally { registration.Dispose(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EOF_wait_ignores_later_reader_cancellation_and_releases_slot_for_another_EOF_wait(bool fail)
    {
        var f = new Fixture();
        using var firstCaller = new CancellationTokenSource();
        using var secondCaller = new CancellationTokenSource();
        try
        {
            f.Pipeline.Start();
            Call<CapturedFrame> capture = await f.Captures.At(0);
            Task loop = await f.Loop(capture, "CaptureLoopAsync");
            Task<EncodedFrame?> first = f.Read(firstCaller.Token);
            Task queueRead = await RegisteredAwait(first, f.Pipeline, "ReadCoreAsync");
            Task completion = f.Pipeline.StopAsync();
            await Success(capture.CancellationSeen.Task);
            await Observe(queueRead);
            // 只观察任意已登记 await；不能把“await Completion”本身当成就绪条件。
            Task firstAwaited = await RegisteredAwait(first, f.Pipeline, "ReadCoreAsync");
            Assert.Same(completion, firstAwaited);
            firstCaller.Cancel();
            Assert.True(f.ReadToken(first).IsCancellationRequested);
            Task afterCancellation = await RegisteredAwait(first, f.Pipeline, "ReadCoreAsync");
            Assert.Same(completion, afterCancellation);

            // 首读仍等 EOF，但队列读槽已经释放；第二个原读取也必须能进入完整收尾等待。
            Task<EncodedFrame?> second = f.Read(secondCaller.Token);
            Task secondAwaited = await RegisteredAwait(second, f.Pipeline, "ReadCoreAsync");
            Assert.Same(completion, secondAwaited);
            secondCaller.Cancel();
            Assert.True(f.ReadToken(second).IsCancellationRequested);
            Task secondAfterCancellation = await RegisteredAwait(second, f.Pipeline, "ReadCoreAsync");
            Assert.Same(completion, secondAfterCancellation);
            await AssertAwaiting(loop, capture.Worker!);
            await AssertAwaiting(first, completion);

            var original = new IOException("EOF 收尾阶段才到达的原采集失败。");
            if (fail) capture.Fail([original]);
            else capture.Reply();
            f.ReleaseCallsExcept(capture);
            if (fail)
            {
                var failure = Assert.IsType<AggregateException>(await Error(completion));
                AssertCompletionRoots(f, failure, AssertLoopFailure(f, "capture", original));
                Assert.Same(failure, await Error(first));
                Assert.Same(failure, await Error(second));
                Assert.True(first.IsFaulted);
                Assert.True(second.IsFaulted);
            }
            else
            {
                await Success(completion);
                Assert.Null(await Success(first));
                Assert.Null(await Success(second));
                Assert.True(first.IsCompletedSuccessfully);
                Assert.True(second.IsCompletedSuccessfully);
            }
            f.AssertAllReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("forwarded")]
    [InlineData("discover")]
    public async Task Await_probes_report_completed_publication_as_contract_failure_not_Guard(string probe)
    {
        var f = new Fixture();
        var original = Signal();
        try
        {
            // 受控原任务没有任何 continuation；发布任务已结束就是断链事实，不等待身份匹配。
            Task checking = probe == "forwarded"
                ? AssertForwarded(original.Task, Task.CompletedTask)
                : DiscoverMachine(original.Task, Task.CompletedTask, f.Pipeline, "CaptureLoopAsync");
            f.Track(checking);
            Exception failure = await Error(checking);
            Assert.IsAssignableFrom<XunitException>(failure);
            Assert.Contains("发布任务提前完成", failure.Message);
            Assert.DoesNotContain("Guard", failure.Message);
            Assert.False(original.Task.IsCompleted);
        }
        finally
        {
            original.TrySetResult();
            try { await Success(original.Task); }
            finally { await f.Finish(); }
        }
    }

    [Theory]
    [InlineData("start-timestamp", false, false)]
    [InlineData("start-timestamp", true, false)]
    [InlineData("start-timestamp", true, true)]
    [InlineData("elapsed-timestamp", false, false)]
    [InlineData("elapsed-timestamp", true, false)]
    [InlineData("elapsed-timestamp", true, true)]
    [InlineData("timer", false, false)]
    [InlineData("timer", true, false)]
    [InlineData("timer", false, true)]
    public async Task Clock_failures_preserve_original_exception_and_ownership(string site, bool ownOce, bool stopFirst)
    {
        var clock = new FaultClock(site);
        var f = new Fixture(timeProvider: clock);
        try
        {
            f.Pipeline.Start();
            Task coordinator = await f.Coordinator();
            Call<CapturedFrame> capture = await f.Captures.At(0);
            Task captureLoop = await f.Loop(capture, "CaptureLoopAsync");
            capture.Reply();
            await Success(clock.Hold.Entered.Task);
            Call<EncodedFrame> encode = await f.Encodes.At(0);
            await f.Loop(encode, "EncodeLoopAsync");
            Task<EncodedFrame?> reader = f.Read();
            Exception original = ownOce
                ? new OperationCanceledException("TimeProvider 原异常，使用管线本 token。", capture.Token)
                : new IOException("TimeProvider 原异常。");
            clock.Failure = original;
            if (stopFirst)
            {
                _ = f.Pipeline.StopAsync();
                await Success(capture.CancellationSeen.Task);
                await AssertLoopJoined(f, coordinator, captureLoop, "capture");
            }
            else Assert.False(capture.Token.IsCancellationRequested);

            // GetTimestamp 不是取消点，即使已 Stop 的同 token OCE 也必须故障；
            // CreateTimer 的同 token OCE 在 token 尚未取消时同样不能被吞掉。
            clock.Hold.Open();
            // 吞掉时钟异常会让循环成功退出；先检查原循环故障，不等缺失的自动停止信号到 Guard。
            await Observe(f.PublicLoop("capture"));
            Task failedLoop = AssertLoopFailure(f, "capture", original);
            await Success(capture.CancellationSeen.Task);
            f.ReleaseCallsExcept(null);
            var failure = Assert.IsType<AggregateException>(await Error(f.Pipeline.Completion));
            AssertCompletionRoots(f, failure, failedLoop);
            Assert.Same(failure, await Error(reader));
            Assert.Equal(1, f.Captures.Count);
            Assert.Equal(site == "timer" ? 1 : 0, clock.TimerCalls);
            Assert.Equal(site == "start-timestamp" ? 3 : 2, clock.TimestampCalls);
            f.AssertAllReleased();
        }
        finally
        {
            clock.Hold.Open();
            try { await f.Finish(); }
            finally { if (clock.Hold.Entered.Task.IsCompleted) await clock.Hold.Join(); }
        }
    }

    [Fact]
    public async Task Stop_during_next_start_timestamp_joins_clock_and_never_invokes_another_capture()
    {
        var clock = new FaultClock("start-timestamp");
        var f = new Fixture(timeProvider: clock);
        try
        {
            f.Pipeline.Start();
            Task coordinator = await f.Coordinator();
            Call<CapturedFrame> capture = await f.Captures.At(0);
            Task loop = await f.Loop(capture, "CaptureLoopAsync");
            capture.Reply();
            await Success(clock.Hold.Entered.Task);
            Task stop = f.Pipeline.StopAsync();
            await Success(capture.CancellationSeen.Task);
            await AssertLoopJoined(f, coordinator, loop, "capture");
            Assert.False(clock.Hold.IsOpen);
            // 先保证错误实现多调 Capture 时也能返回，再放时钟门；以最终精确调用数判错。
            f.ReleaseCallsExcept(null);
            clock.Hold.Open();
            await Success(stop);
            Assert.Equal(3, clock.TimestampCalls);
            Assert.Equal(1, f.Captures.Count);
            Assert.Equal(0, clock.TimerCalls);
            Assert.Null(await Success(f.Read()));
            f.AssertAllReleased();
        }
        finally
        {
            clock.Hold.Open();
            try { await f.Finish(); }
            finally { if (clock.Hold.Entered.Task.IsCompleted) await clock.Hold.Join(); }
        }
    }

    private static string Method(string stage) => stage == "capture" ? "CaptureLoopAsync" : "EncodeLoopAsync";

    private static async Task AssertLoopJoined(Fixture f, Task coordinator, Task loop, string stage)
    {
        Task join = await RegisteredAwait(coordinator, f.Pipeline, "CoordinateStopAsync");
        AssertWhenAll(join);
        Task publicLoop = f.PublicLoop(stage);
        Assert.False(loop.IsCompleted);
        Assert.False(publicLoop.IsCompleted);
        // Task.Run(Func<Task>) 返回的是解包代理，不是状态机本体，必须核对这条中间边。
        Assert.True(IsRegistered(loop, publicLoop), "真实 loop 必须仍向已发布的 Task.Run 解包任务报告完成。");
        Assert.True(IsRegistered(publicLoop, join), "完整 join 缺失了仍在执行同步前缀/原尾部的循环。");
        Assert.True(IsRegistered(coordinator, f.Pipeline.Completion), "Completion 必须仍由真实协调状态机解包完成。");
    }

    private static void AssertWhenAll(Task task) =>
        Assert.StartsWith("WhenAllPromise", task.GetType().Name);

    private static void AssertChildren(AggregateException actual, IReadOnlyList<Exception> expected)
    {
        Assert.Equal(expected.Count, actual.InnerExceptions.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Same(expected[i], actual.InnerExceptions[i]);
    }

    private static Task AssertLoopFailure(Fixture f, string stage, params Exception[] expected)
    {
        Task loop = f.PublicLoop(stage);
        Assert.True(loop.IsFaulted);
        var root = Assert.IsType<AggregateException>(Assert.Single(loop.Exception!.InnerExceptions));
        AssertChildren(root, expected);
        return loop;
    }

    private static void AssertCompletionRoots(Fixture f, AggregateException failure, params Task[] failedSources)
    {
        Assert.Same(failure, Assert.Single(f.Pipeline.Completion.Exception!.InnerExceptions));
        // WhenAll 不保证跨 worker 的错误顺序；精确比较各来源的原根多重集，不去重、不展平。
        var expected = new List<Exception>();
        foreach (Task source in failedSources)
        {
            Assert.True(source.IsFaulted);
            expected.AddRange(source.Exception!.InnerExceptions);
        }
        var remaining = failure.InnerExceptions.ToList();
        Assert.Equal(expected.Count, remaining.Count);
        foreach (Exception root in expected)
        {
            int index = remaining.FindIndex(actual => ReferenceEquals(actual, root));
            Assert.True(index >= 0, "Completion 的顶层错误根不属于预期原循环/清理任务。");
            remaining.RemoveAt(index);
        }
        Assert.Empty(remaining);
    }

    private static void AssertCleanupFailure(Task cleanup, params Exception[] expected)
    {
        var wrapper = Assert.IsType<AggregateException>(Assert.Single(cleanup.Exception!.InnerExceptions));
        var original = Assert.IsType<AggregateException>(Assert.Single(wrapper.InnerExceptions));
        AssertChildren(original, expected);
    }

    private static IEnumerable<Exception> Tree(Exception error)
    {
        yield return error;
        IEnumerable<Exception> children = error is AggregateException aggregate
            ? aggregate.InnerExceptions : error.InnerException is { } inner ? [inner] : [];
        foreach (Exception child in children)
            foreach (Exception descendant in Tree(child)) yield return descendant;
    }

    private static void AssertContainsSame(Exception tree, Exception expected) =>
        Assert.Contains(Tree(tree), item => ReferenceEquals(item, expected));

    private static void AssertSubtreeWithChildren(Exception tree, Exception[] expected)
    {
        AggregateException[] matches = Tree(tree).OfType<AggregateException>()
            .Where(node => node.InnerExceptions.Count == expected.Length &&
                node.InnerExceptions.Select((child, i) => ReferenceEquals(child, expected[i])).All(equal => equal))
            .ToArray();
        Assert.NotEmpty(matches);
        AssertChildren(matches[0], expected);
    }

    // Watchdog 只约束失败等待；业务异常只从获胜的原任务读取，不把 Guard 超时当被测错误。
    private static async Task Finished(Task original)
    {
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try { Assert.Same(original, await Task.WhenAny(original, guard)); }
        finally { cancellation.Cancel(); }
    }

    private static async Task Success(Task original) { await Finished(original); await original; }
    private static async Task<T> Success<T>(Task<T> original) { await Finished(original); return await original; }
    private static async Task<Exception> Error(Task original)
    {
        await Finished(original);
        Exception? error = await Record.ExceptionAsync(() => original);
        Assert.NotNull(error);
        return error;
    }
    private static async Task Observe(Task original)
    {
        await Finished(original);
        _ = await Record.ExceptionAsync(() => original);
        _ = original.Exception;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }
    private static FieldInfo RequiredField(Type type, string name) => RuntimeField(type, name)
        ?? throw new XunitException($"运行时缺少 {type.Name}.{name}；必须适配真实链证明，不能跳过测试。");

    private static object[] Continuations(Task task)
    {
        object? value = RequiredField(typeof(Task), "m_continuationObject").GetValue(task);
        if (value is IList list)
        {
            lock (list) return list.Cast<object?>().OfType<object>().ToArray();
        }
        return value is null ? [] : [value];
    }

    private static object ContinuationTarget(object continuation)
    {
        if (continuation is Delegate action) return action.Target ?? continuation;
        if (RuntimeField(continuation.GetType(), "m_action")?.GetValue(continuation) is Delegate scheduled)
            return scheduled.Target ?? continuation;
        return continuation;
    }
    private static bool IsRegistered(Task antecedent, Task continuation) => Continuations(antecedent)
        .Any(item => ReferenceEquals(ContinuationTarget(item), continuation));

    private static async Task AssertForwarded(Task machine, Task published)
    {
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try
        {
            // 首次 await 的登记可能先于 Task.Run 外层返回；只等任意登记，再单独核对身份。
            while (true)
            {
                Assert.False(published.IsCompleted, "发布任务提前完成，原状态机的等待关系已经断开。");
                Assert.False(machine.IsCompleted, "原状态机在建立解包关系前已退出。");
                if (Continuations(machine).Length != 0) break;
                if (guard.IsCompleted) throw new XunitException("Guard：原状态机的外层任务尚未登记。");
                await Task.Yield();
            }
            Assert.False(published.IsCompleted, "发布任务提前完成，不能用旧 continuation 冒充等待关系。");
            Assert.True(IsRegistered(machine, published), "发布的循环/Completion 没有承接真实状态机。");
        }
        finally { cancellation.Cancel(); }
    }

    private static Type MachineType(string method) => typeof(FramePipeline).GetMethod(method, Fields)?
        .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
        ?? throw new XunitException($"缺少 {method} 的真实状态机元数据。");

    private static object? StateMachine(Task task) => RuntimeField(task.GetType(), "StateMachine")?.GetValue(task);

    private static IEnumerable<Task> StoredTasks(object value, int depth = 0)
    {
        // 只走 builder/awaiter 的值类型字段，不遍历普通状态机局部变量，避免认错 operation 槽。
        if (depth > 4) yield break;
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        {
            object? child = field.GetValue(value);
            if (child is Task task) yield return task;
            else if (child is not null && field.FieldType.IsValueType && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum)
                foreach (Task nested in StoredTasks(child, depth + 1)) yield return nested;
        }
    }

    private static Task? FindRegisteredAwait(Task owner, FramePipeline pipeline, Type type)
    {
        object? state = StateMachine(owner);
        if (state is null) return null;
        Assert.IsType(type, state);
        Assert.Same(pipeline, RequiredField(type, "<>4__this").GetValue(state));
        object builder = RequiredField(type, "<>t__builder").GetValue(state)!;
        Assert.Contains(StoredTasks(builder), task => ReferenceEquals(owner, task));
        int number = Assert.IsType<int>(RequiredField(type, "<>1__state").GetValue(state));
        if (number < 0) return null;
        foreach (FieldInfo field in type.GetFields(Fields).Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal)))
        {
            if (field.GetValue(state) is not { } awaiter) continue;
            foreach (Task awaited in StoredTasks(awaiter))
                if (!awaited.IsCompleted && IsRegistered(awaited, owner)) return awaited;
        }
        return null;
    }

    private static async Task<Task> RegisteredAwait(Task owner, FramePipeline pipeline, string method, Task? published = null)
    {
        Type type = MachineType(method);
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try
        {
            while (true)
            {
                Assert.False(owner.IsCompleted, $"{method} 提前退出，不能冒充完整等待。");
                if (published is not null)
                    Assert.False(published.IsCompleted, "发布任务提前完成，不能继续等待断开的 await 登记。");
                Task? actual = FindRegisteredAwait(owner, pipeline, type);
                if (actual is not null)
                {
                    Assert.False(owner.IsCompleted, $"{method} 在观察期间提前退出。");
                    if (published is not null)
                        Assert.False(published.IsCompleted, "发布任务提前完成，不能接受残留 await 登记。");
                    return actual;
                }
                if (guard.IsCompleted) throw new XunitException($"Guard：{method} 的真实 await 登记不可观察。");
                await Task.Yield();
            }
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task AssertAwaiting(Task owner, Task original)
    {
        object state = StateMachine(owner) ?? throw new XunitException("必须使用状态机本体，不能拿 Task.Run 代理当 worker。");
        var pipeline = Assert.IsType<FramePipeline>(RequiredField(state.GetType(), "<>4__this").GetValue(state));
        string method = new[] { "CaptureLoopAsync", "EncodeLoopAsync", "ReadCoreAsync", "CoordinateStopAsync" }
            .Single(name => MachineType(name) == state.GetType());
        // 就绪只要求实际 await+反向登记；代理和原任务均可返回，身份比较必须在等待之外。
        Task actual = await RegisteredAwait(owner, pipeline, method);
        Assert.Same(original, actual);
        Assert.False(original.IsCompleted);
        Assert.False(owner.IsCompleted);
    }

    private static async Task<Task> DiscoverMachine(Task anchor, Task published, FramePipeline pipeline, string method)
    {
        Type type = MachineType(method);
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try
        {
            while (true)
            {
                Assert.False(published.IsCompleted, "发布任务提前完成，原 worker 的状态机等待关系已经断开。");
                var pending = new Queue<Task>();
                var seen = new HashSet<Task>(ReferenceEqualityComparer.Instance);
                pending.Enqueue(anchor);
                while (pending.TryDequeue(out Task? candidate))
                {
                    if (!seen.Add(candidate)) continue;
                    if (StateMachine(candidate) is { } state && state.GetType() == type)
                    {
                        // 穿过实际 continuation 图发现状态机，允许图中存在取消代理；不要求正确答案才能就绪。
                        await RegisteredAwait(candidate, pipeline, method, published);
                        return candidate;
                    }
                    foreach (object continuation in Continuations(candidate))
                        if (ContinuationTarget(continuation) is Task next) pending.Enqueue(next);
                }
                Assert.False(anchor.IsCompleted, "用于发现原状态机的受控操作已经退出。");
                Assert.False(published.IsCompleted, "发布任务提前完成，不能以 Guard 代替等待关系断言。");
                if (guard.IsCompleted) throw new XunitException($"Guard：无法从 continuation 图找到 {method}。");
                await Task.Yield();
            }
        }
        finally { cancellation.Cancel(); }
    }

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Gate> _gates = [];
        private readonly List<CountingOwner> _owners = [];
        private readonly List<Task> _realMachines = [];
        private readonly List<(Task<EncodedFrame?> Original, CancellationTokenSource Cancellation)> _reads = [];
        private bool _releaseCalls;
        private bool _finishing;
        private CallBase? _held;
        private int _captureDisposes;
        private int _encoderDisposes;
        internal string? PrefixStage;
        internal string? TailStage;
        internal int SpecialIndex;
        internal CallLog<CapturedFrame> Captures { get; } = new();
        internal CallLog<EncodedFrame> Encodes { get; } = new();
        internal FramePipeline Pipeline { get; }

        internal Fixture(int rawCapacity = 2, int encodedCapacity = 2, TimeProvider? timeProvider = null)
        {
            Pipeline = new FramePipeline(new Capture(this), new Encoder(this), new DisplayId(7),
                rawCapacity, encodedCapacity, timeProvider ?? new OverBudgetClock());
        }

        internal Gate NewGate()
        {
            lock (_gate)
            {
                var gate = new Gate();
                _gates.Add(gate);
                if (_finishing) gate.Open();
                return gate;
            }
        }
        internal CountingOwner Owner(Gate? gate, Exception? error)
        {
            var owner = new CountingOwner(gate, error);
            lock (_gate) _owners.Add(owner);
            return owner;
        }
        internal Task<EncodedFrame?> Read(CancellationToken token = default)
        {
            // 每个原读取都有测试持有的取消源；调用者取消通过链接传播，清理取消不反向影响调用者。
            var cancellation = token.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(token)
                : new CancellationTokenSource();
            try
            {
                Task<EncodedFrame?> task = Pipeline.ReadNextAsync(cancellation.Token).AsTask();
                _reads.Add((task, cancellation));
                return task;
            }
            catch
            {
                cancellation.Dispose();
                throw;
            }
        }
        internal CancellationToken ReadToken(Task<EncodedFrame?> original) =>
            _reads.Single(read => ReferenceEquals(read.Original, original)).Cancellation.Token;
        internal void Track(params Task[] tasks) => _realMachines.AddRange(tasks);
        internal Task PublicLoop(string stage) => Assert.IsAssignableFrom<Task>(
            RequiredField(typeof(FramePipeline), stage == "capture" ? "_captureLoop" : "_encodeLoop").GetValue(Pipeline));
        internal async Task<Task> Coordinator()
        {
            var signal = Assert.IsType<TaskCompletionSource>(RequiredField(typeof(FramePipeline), "_stopSignal").GetValue(Pipeline));
            Task coordinator = await DiscoverMachine(signal.Task, Pipeline.Completion, Pipeline, "CoordinateStopAsync");
            _realMachines.Add(coordinator);
            await AssertForwarded(coordinator, Pipeline.Completion);
            return coordinator;
        }
        internal async Task<Task> Loop(CallBase call, string method)
        {
            await Success(call.InvocationExited.Task);
            Task worker = Assert.IsAssignableFrom<Task>(call.Worker);
            Task published = PublicLoop(method == "CaptureLoopAsync" ? "capture" : "encode");
            Task loop = await DiscoverMachine(worker, published, Pipeline, method);
            _realMachines.Add(loop);
            await AssertAwaiting(loop, worker);
            await AssertForwarded(loop, published);
            return loop;
        }
        internal async Task<CallBase> FirstOperation(string stage)
        {
            Call<CapturedFrame> capture = await Captures.At(0);
            if (stage == "capture") return capture;
            capture.Reply();
            return await Encodes.At(0);
        }
        internal void ReleaseCallsExcept(CallBase? held)
        {
            CallBase[] calls;
            lock (_gate)
            {
                _releaseCalls = true;
                _held = held;
                calls = Captures.Snapshot().Cast<CallBase>().Concat(Encodes.Snapshot()).ToArray();
            }
            foreach (CallBase call in calls) if (!ReferenceEquals(call, held)) call.Release();
        }
        private void Published(CallBase call)
        {
            bool release;
            lock (_gate) release = _releaseCalls && !ReferenceEquals(call, _held);
            if (release) call.Release();
        }
        internal void AssertAllReleased()
        {
            lock (_gate) Assert.All(_owners, owner => Assert.Equal(1, owner.DisposeCount));
            Assert.Equal(0, Volatile.Read(ref _captureDisposes));
            Assert.Equal(0, Volatile.Read(ref _encoderDisposes));
        }
        internal async Task Finish()
        {
            Task<Task>? stopRequest = null;
            try
            {
                // 停止调用本身即使同步阻塞/抛错，也不能挡住测试线程的放闸和读取取消。
                stopRequest = Task.Factory.StartNew(Pipeline.StopAsync, CancellationToken.None,
                    TaskCreationOptions.None, TaskScheduler.Default);
            }
            finally
            {
                Gate[] gates;
                Task[] readCancellations = [];
                lock (_gate) { _finishing = true; gates = _gates.ToArray(); }
                try
                {
                    try
                    {
                        foreach (Gate gate in gates) gate.Open();
                        ReleaseCallsExcept(null);
                    }
                    finally
                    {
                        // 独立于 encodedStop：遗漏队列 Stop 的变异也能结束真实队列读取。
                        readCancellations = _reads.Select(read => read.Cancellation.CancelAsync()).ToArray();
                    }
                    IEnumerable<Task> originals = new[] { Pipeline.Completion, PublicLoop("capture"), PublicLoop("encode") }
                        .Concat(_realMachines).Concat(stopRequest is null ? Array.Empty<Task>() : new Task[] { stopRequest });
                    await Task.WhenAll(originals.Select(Observe)
                        .Concat(readCancellations.Select(Observe))
                        .Concat(_reads.Select(read => JoinRead(read.Original))));
                }
                finally
                {
                    CallBase[] calls = Captures.Snapshot().Cast<CallBase>().Concat(Encodes.Snapshot()).ToArray();
                    lock (_gate) gates = _gates.ToArray();
                    try
                    {
                        // Completion 错误提前完成也不替代原 API、清理任务和原读取的独立 join。
                        await Task.WhenAll(calls.Select(call => call.Join())
                            .Concat(gates.Where(gate => gate.Entered.Task.IsCompleted).Select(gate => gate.Join()))
                            .Concat(readCancellations.Select(Observe))
                            .Concat(_reads.Select(read => JoinRead(read.Original))));
                    }
                    finally
                    {
                        try { foreach (CallBase call in calls) call.Unregister(); }
                        finally
                        {
                            // 先取消并 await 原读取，最后才释放链接 CTS，不释放借用的调用者 CTS。
                            foreach (var read in _reads) read.Cancellation.Dispose();
                        }
                    }
                }
            }
        }
        private static async Task JoinRead(Task<EncodedFrame?> task)
        {
            await Finished(task);
            if (task.IsCompletedSuccessfully) (await task)?.Dispose();
            else await Observe(task);
        }

        private sealed class Capture(Fixture f) : IScreenCaptureBackend, IDisposable, IAsyncDisposable
        {
            public IReadOnlyList<DisplayInfo> GetDisplays() => Array.Empty<DisplayInfo>();
            public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken)
            {
                int index = f.Captures.Count;
                var call = new Call<CapturedFrame>(f, cancellationToken, null,
                    f.PrefixStage == "capture" && index == f.SpecialIndex,
                    f.TailStage == "capture" && index == f.SpecialIndex,
                    owner => new CapturedFrame(displayId, 1, 1, 4, FramePixelFormat.Bgra32, owner, index + 1));
                f.Captures.Add(call);
                f.Published(call);
                return call.Invoke();
            }
            public void Dispose() => Interlocked.Increment(ref f._captureDisposes);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class Encoder(Fixture f) : IFrameEncoder, IDisposable, IAsyncDisposable
        {
            public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, VideoQualitySettings settings, CancellationToken cancellationToken)
            {
                int index = f.Encodes.Count;
                var call = new Call<EncodedFrame>(f, cancellationToken, frame,
                    f.PrefixStage == "encode" && index == f.SpecialIndex,
                    f.TailStage == "encode" && index == f.SpecialIndex,
                    owner => new EncodedFrame(VideoCodec.Jpeg, 1, 1, (ulong)index + 1, frame.TimestampUs, 60, owner));
                f.Encodes.Add(call);
                f.Published(call);
                return call.Invoke();
            }
            public void Dispose() => Interlocked.Increment(ref f._encoderDisposes);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private abstract class CallBase
    {
        private readonly CancellationTokenRegistration _registration;
        protected readonly Fixture Fixture;
        protected readonly CapturedFrame? Input;
        private readonly CountingOwner? _inputOwner;
        protected readonly bool HasPrefix;
        protected readonly bool HasTail;
        internal Gate Prefix { get; }
        internal Gate Tail { get; }
        internal CancellationToken Token { get; }
        internal TaskCompletionSource InvocationExited { get; } = Signal();
        internal TaskCompletionSource CancellationSeen { get; } = Signal();
        internal Task? Worker;
        internal CountingOwner? ResultOwner;
        internal Exception? PrefixFailure;
        internal int InputDisposalsAtPrefixExit;
        internal int InputDisposalsAtTailExit;
        internal Exception? InputReadErrorAtTailExit;

        protected CallBase(Fixture fixture, CancellationToken token, CapturedFrame? input, bool prefix, bool tail)
        {
            Fixture = fixture;
            Input = input;
            _inputOwner = input is null ? null : Assert.IsType<CountingOwner>(
                RequiredField(typeof(CapturedFrame), "_owner").GetValue(input));
            Token = token;
            HasPrefix = prefix;
            HasTail = tail;
            Prefix = fixture.NewGate();
            Tail = fixture.NewGate();
            if (!prefix) Prefix.Open();
            if (!tail) Tail.Open();
            _registration = token.Register(() => CancellationSeen.TrySetResult());
        }
        protected int InputDisposals => _inputOwner?.DisposeCount ?? 0;
        internal void AssertInputAlive()
        {
            if (Input is null) return;
            Assert.Equal(0, InputDisposals);
            Assert.Equal(4, Input.Pixels.Length);
        }
        internal abstract void ReplyDefault();
        internal abstract void ReplyWithFailure(Exception error);
        internal abstract void Fail(Exception[] errors);
        internal abstract void Cancel(CancellationToken token);
        internal abstract void Release();
        internal abstract Task Join();
        internal void Unregister() => _registration.Dispose();
    }

    private sealed class Call<T> : CallBase where T : class, IDisposable
    {
        private readonly object _gate = new();
        private readonly Func<CountingOwner, T> _produce;
        private readonly TaskCompletionSource<T> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T? _frame;
        internal Call(Fixture fixture, CancellationToken token, CapturedFrame? input, bool prefix, bool tail,
            Func<CountingOwner, T> produce) : base(fixture, token, input, prefix, tail) => _produce = produce;

        internal ValueTask<T> Invoke()
        {
            try
            {
                if (HasPrefix) Prefix.Block();
                if (PrefixFailure is { } failure) throw failure;
                InputDisposalsAtPrefixExit = InputDisposals;
                Task<T> worker = HasTail ? RunTail() : _result.Task;
                Worker = worker;
                return new ValueTask<T>(worker);
            }
            catch
            {
                _result.TrySetCanceled();
                throw;
            }
            finally { InvocationExited.TrySetResult(); }
        }
        private async Task<T> RunTail()
        {
            T result = await _result.Task.ConfigureAwait(false);
            await Tail.WaitAsync().ConfigureAwait(false);
            InputReadErrorAtTailExit = Record.Exception(() =>
            {
                InputDisposalsAtTailExit = InputDisposals;
                if (Input is not null) _ = Input.Pixels.Length;
            });
            return result;
        }
        internal T Reply(Gate? gate = null, Exception? failure = null)
        {
            lock (_gate)
            {
                if (_frame is not null) return _frame;
                if (_result.Task.IsCompleted) throw new XunitException("已经失败或取消的原操作不能再次产生帧。");
                ResultOwner = Fixture.Owner(gate, failure);
                _frame = _produce(ResultOwner);
                _result.SetResult(_frame);
                return _frame;
            }
        }
        internal override void ReplyDefault() => Reply();
        internal override void ReplyWithFailure(Exception error) => Reply(failure: error);
        internal override void Fail(Exception[] errors) { lock (_gate) _result.TrySetException(errors); }
        internal override void Cancel(CancellationToken token) { lock (_gate) _result.TrySetCanceled(token); }
        internal override void Release()
        {
            // 先准备同步 throw 或成功结果，再放门；已失败/取消的原任务绝不制造未移交 owner。
            lock (_gate)
            {
                if (!_result.Task.IsCompleted)
                {
                    if (PrefixFailure is not null) _result.TrySetCanceled();
                    else Reply();
                }
            }
            Prefix.Open();
            Tail.Open();
        }
        internal override async Task Join()
        {
            await Success(InvocationExited.Task);
            await Observe(_result.Task);
            if (Worker is { } worker) await Observe(worker);
        }
    }

    private sealed class CallLog<T> where T : class, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Call<T>> _calls = [];
        private readonly Dictionary<int, TaskCompletionSource<Call<T>>> _waiting = [];
        internal int Count { get { lock (_gate) return _calls.Count; } }
        internal Call<T>[] Snapshot() { lock (_gate) return _calls.ToArray(); }
        internal void Add(Call<T> call)
        {
            lock (_gate)
            {
                int index = _calls.Count;
                _calls.Add(call);
                if (_waiting.Remove(index, out TaskCompletionSource<Call<T>>? waiter)) waiter.TrySetResult(call);
            }
        }
        internal Task<Call<T>> At(int index)
        {
            lock (_gate)
            {
                if (index < _calls.Count) return Task.FromResult(_calls[index]);
                if (!_waiting.TryGetValue(index, out TaskCompletionSource<Call<T>>? waiter))
                    _waiting.Add(index, waiter = new(TaskCreationOptions.RunContinuationsAsynchronously));
                return Success(waiter.Task);
            }
        }
    }

    private sealed class CountingOwner(Gate? gate, Exception? failure) : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = [1, 2, 3, 4];
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal TaskCompletionSource Disposed { get; } = Signal();
        public Memory<byte> Memory
        {
            get { ObjectDisposedException.ThrowIf(DisposeCount != 0, this); return _bytes; }
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            try
            {
                gate?.Block();
                if (failure is not null) throw failure;
            }
            finally { Disposed.TrySetResult(); }
        }
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Exited { get; } = Signal();
        internal Task? ExecutingTask { get; private set; }
        internal bool OnCoordinatorStack { get; private set; }
        internal bool IsOpen => _release.Task.IsCompleted;
        internal void Open() => _release.TrySetResult();
        internal void Block()
        {
            // RunCleanup 是 Task.Run(Action)，其真实执行任务来自线程本地槽，绝不构造替身任务。
            ExecutingTask = typeof(Task).GetField("t_currentTask", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as Task;
            Type coordinator = MachineType("CoordinateStopAsync");
            OnCoordinatorStack = new System.Diagnostics.StackTrace().GetFrames()
                .Any(frame => frame.GetMethod()?.DeclaringType == coordinator);
            Entered.TrySetResult();
            try { Success(_release.Task).GetAwaiter().GetResult(); }
            finally { Exited.TrySetResult(); }
        }
        internal async Task WaitAsync()
        {
            Entered.TrySetResult();
            try { await Success(_release.Task).ConfigureAwait(false); }
            finally { Exited.TrySetResult(); }
        }
        internal async Task Join()
        {
            await Success(Exited.Task);
            if (ExecutingTask is { } task) await Observe(task);
        }
    }

    private sealed class FaultClock(string site) : TimeProvider
    {
        private int _timestamps;
        private int _timers;
        internal Gate Hold { get; } = new();
        internal Exception? Failure;
        internal int TimestampCalls => Volatile.Read(ref _timestamps);
        internal int TimerCalls => Volatile.Read(ref _timers);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            int call = Interlocked.Increment(ref _timestamps);
            // 首次原 Capture 已挂起后才进入目标取时点，不涉及 Start/Stop 发布窗。
            if ((site == "elapsed-timestamp" && call == 2) || (site == "start-timestamp" && call == 3))
            {
                Hold.Block();
                if (Failure is { } error) throw error;
            }
            return site == "timer" ? 0 : (call - 1L) * TimeSpan.TicksPerSecond;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            if (site != "timer") throw new XunitException("非计时器故障场景不应创建计时器。");
            Hold.Block();
            throw Failure ?? new XunitException("计时器故障场景尚未发布原异常。");
        }
    }

    private sealed class OverBudgetClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        // 生命周期测试不测试节拍；每次取时超过预算，后续采集只由原操作门推进，不使用墙钟延迟。
        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new XunitException("生命周期测试不应创建节拍计时器。");
    }
}
