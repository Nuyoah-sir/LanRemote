using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Sessions;

/// <summary>拥有专用依赖的内部适配器；成功构造时接管所有权，不改变原管线的启动及停止语义。</summary>
internal sealed class FramePipelineVideoProducer : IVideoFrameProducer
{
    private readonly Guid _sessionId;
    private readonly FramePipeline _pipeline;
    private readonly IScreenCaptureBackend _capture;
    private readonly IFrameEncoder _encoder;
    private readonly Task _completion;

    internal FramePipelineVideoProducer(
        Guid sessionId, FramePipeline pipeline, IScreenCaptureBackend capture, IFrameEncoder encoder)
    {
        _sessionId = sessionId;
        _pipeline = pipeline;
        _capture = capture;
        _encoder = encoder;
        // 立即观察原 Completion，生产自行故障也会清理，不依赖用户再次调用 Stop/Dispose。
        _completion = CompleteAsync();
    }

    public Task Completion => _completion;

    public void Start() => _pipeline.Start();

    public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default) =>
        ReadCoreAsync(_pipeline.ReadNextAsync(cancellationToken));

    public Task StopAsync()
    {
        _ = _pipeline.StopAsync();
        return _completion;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async ValueTask<EncodedFrame?> ReadCoreAsync(ValueTask<EncodedFrame?> read)
    {
        try
        {
            EncodedFrame? frame = await read.ConfigureAwait(false);
            if (frame is not null)
            {
                return frame;
            }
        }
        catch (AggregateException)
        {
            // 原管线故障已由 CompleteAsync 收集，不能只转抛它而漏掉随后发生的依赖清理错误。
        }

        // 正常 EOF 和管线故障都等待完整清理；普通读取取消不进入这里，也不停止生产。
        await _completion.ConfigureAwait(false);
        return null;
    }

    private async Task CompleteAsync()
    {
        List<Exception> errors = [];
        await FramePipelineVideoProducerCleanup.ObserveAsync(_pipeline.Completion, errors).ConfigureAwait(false);
        await FramePipelineVideoProducerCleanup.DisposeDependenciesAsync(_capture, _encoder, errors).ConfigureAwait(false);
        // 不 join Read：读者在 EOF/故障路径反过来等待本任务。
        if (errors.Count != 0)
        {
            throw new AggregateException($"会话 {_sessionId} 的视频生产者停止时发生错误。", errors);
        }
    }
}

/// <summary>成功停止与创建失败共用的专用依赖清理；不释放工厂、委托目标或时钟。</summary>
internal static class FramePipelineVideoProducerCleanup
{
    internal static async Task ObserveAsync(Task operation, List<Exception> errors)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (operation.IsFaulted)
            {
                // await 只抛第一根；逐根保留原对象，不展平嵌套聚合，也不去重。
                errors.AddRange(operation.Exception!.InnerExceptions);
            }
            else
            {
                errors.Add(exception);
            }
        }
    }

    internal static async Task DisposeDependenciesAsync(object? capture, object? encoder, List<Exception> errors)
    {
        // 两路先独立调度再全 join，任一 DisposeAsync 的同步前缀阻塞都不阻止另一方进入。
        Task captureCleanup = RunCleanup(capture);
        Task encoderCleanup = ReferenceEquals(capture, encoder) ? Task.CompletedTask : RunCleanup(encoder);
        await ObserveAsync(Task.WhenAll(captureCleanup, encoderCleanup), errors).ConfigureAwait(false);
    }

    private static Task RunCleanup(object? dependency)
    {
        if (dependency is not IAsyncDisposable && dependency is not IDisposable)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            Task? operation = null;
            try
            {
                if (dependency is IAsyncDisposable asyncDisposable)
                {
                    // 原 ValueTask 只转一次 Task，并完整等待；异步释放失败也不再尝试同步释放。
                    operation = asyncDisposable.DisposeAsync().AsTask();
                    await operation.ConfigureAwait(false);
                }
                else if (dependency is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception exception)
            {
                if (operation is { IsFaulted: true })
                {
                    throw new AggregateException("清理视频生产专用依赖失败。", operation.Exception!.InnerExceptions);
                }

                // 同步抛出的 OCE 或 Canceled 原任务也是清理失败，不得伪装成正常取消。
                throw new AggregateException("清理视频生产专用依赖失败。", exception);
            }
        });
    }
}
