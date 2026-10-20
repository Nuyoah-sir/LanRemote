using LanRemote.Core.Models;

namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    private ClientDisplayPresentation? _displayPresentation;

    /// <summary>内部 opt-in；只绑定已交付的真实 child，且必须早于首次读取与首次切屏请求。</summary>
    internal ClientDisplayPresentation BindDisplayPresentation(AuthenticatedVideoSession video)
    {
        ArgumentNullException.ThrowIfNull(video);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_connection is null, this);
            if (!_deliveryCommitted) throw new InvalidOperationException("控制认证尚未完成最终交付。");
            if (_displayPresentation is not null)
                throw new InvalidOperationException("显示提交桥只能绑定一次。");
            if (_displaySwitch is not { CanBindPresentationUnderGate: true })
                throw new InvalidOperationException("显示提交桥必须在已启用切屏且尚未接纳任何请求时绑定。");
            ClientVideoLifetime child = _videoLifetime
                ?? throw new InvalidOperationException("视频会话尚未交付。");
            if (!child.CanBindPresentationUnderGate || !ReferenceEquals(video, child.PublicSession))
                throw new InvalidOperationException("显示提交桥必须绑定尚未读取的当前已交付视频会话。");
            return _displayPresentation = new ClientDisplayPresentation(this, child);
        }
    }

    /// <summary>共享父 gate 的逻辑 UI 提交屏障；不解码、不证明物理 scanout，也不拥有 payload。</summary>
    internal sealed class ClientDisplayPresentation(AuthenticatedControlSession parent, ClientVideoLifetime child)
    {
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private object? _epoch = new();
        private DisplayChangedFrame? _receipt;
        private PresentedFrame? _lastPresented;
        private ulong? _lastReadFrameId;
        private ulong? _lastCommittedFrameId;
        private bool _pending;
        private bool _stopped;
        private bool _committing;

        internal object Gate => parent._gate;
        internal Task ChangedUnderGate => _changed.Task;
        internal bool IsStoppedUnderGate => _stopped || parent._connection is null || !parent._deliveryCommitted ||
            !ReferenceEquals(parent._displayPresentation, this) || !ReferenceEquals(parent._videoLifetime, child) ||
            parent._displaySwitch is null || !child.CanSwitchUnderGate;

        internal bool IsCurrentUnderGate(FrameStamp stamp) =>
            !IsStoppedUnderGate && _epoch is { } epoch && stamp is not null && stamp.Matches(this, epoch);

        /// <summary>尺寸必须来自 App 实际解码的 Bitmap 像素；仅核对数字不是解码成功证明。</summary>
        internal bool CanPresentUnderGate(FrameStamp stamp, int width, int height)
        {
            if (!IsCurrentUnderGate(stamp) || _pending) return false;
            if (_receipt is { } receipt)
            {
                if (stamp.FrameId < receipt.FirstFrameId) return false;
                if (stamp.Width != receipt.Width || stamp.Height != receipt.Height)
                    throw new FrameProtocolException("display-presentation-receipt-size-mismatch");
            }
            if (width != stamp.Width || height != stamp.Height)
                throw new FrameProtocolException("display-presentation-decoded-size-mismatch");
            return true;
        }

        /// <summary>
        /// commit 只能是固定短 UI 赋值，不得网络、Dispose、取消或等待；在同 gate 内复检 setter 重入撤销。
        /// 成功只记录逻辑提交历史，不证明显示器已扫描输出。
        /// </summary>
        internal bool TryCommit(FrameStamp stamp, int width, int height, Action commit)
        {
            ArgumentNullException.ThrowIfNull(commit);
            lock (Gate)
            {
                if (_committing || !CanPresentUnderGate(stamp, width, height) ||
                    (_lastCommittedFrameId is { } last && stamp.FrameId <= last)) return false;
                _committing = true;
                try
                {
                    commit();
                    if (!CanPresentUnderGate(stamp, width, height)) return false;
                    _lastPresented = new PresentedFrame(stamp.FrameId, width, height, _receipt?.RequestSequence);
                    _lastCommittedFrameId = stamp.FrameId;
                    return true;
                }
                finally { _committing = false; }
            }
        }

        internal PresentedFrame? LastPresented
        {
            get { lock (Gate) return !IsStoppedUnderGate && !_pending ? _lastPresented : null; }
        }

        internal Task<PresentationFrame?> ReadFrameAsync(CancellationToken token) =>
            child.ReadPresentationFrameAsync(this, token);

        /// <summary>调用方必须已退出 gate；仅请求父停止，不等待包含当前读取的 join。</summary>
        internal void RequestStop()
        {
            if (Monitor.IsEntered(Gate))
                throw new InvalidOperationException("显示提交桥必须在父 gate 外请求停止。");
            parent.StopDisplaySwitch();
        }

        internal FrameStamp StampUnderGate(EncodedFrame frame)
        {
            // 友元也不能用自造 EncodedFrame 签发资格：候选仅存在于原读取终检的无外部调用区间。
            if (!Monitor.IsEntered(Gate) || IsStoppedUnderGate || !child.IsPresentationCandidateUnderGate(frame))
                throw new InvalidOperationException("帧凭据只能在当前 child 的实际读取终检处签发。");
            if (_lastReadFrameId is { } last && frame.FrameId <= last)
                throw new FrameProtocolException("display-presentation-frame-id-not-increasing");
            FrameStamp stamp = new(this, _epoch!, frame.FrameId, frame.Width, frame.Height);
            _lastReadFrameId = frame.FrameId;
            return stamp;
        }

        internal void BeginRequestUnderGate()
        {
            // 只接受真实请求槽的接纳通知；重复调用不能制造新 epoch 或复用历史回执。
            if (IsStoppedUnderGate || _pending || parent._displaySwitch is not { HasPendingRequestUnderGate: true }) return;
            _epoch = new object();
            _pending = true;
            _receipt = null;
            _lastPresented = null;
            SignalUnderGate();
        }

        internal void AcceptReceiptUnderGate(DisplayChangedFrame receipt)
        {
            // state 已完成验证、原发送及 caller 注册退出；这里只接收其唯一发布点的实际对象。
            if (receipt is null || IsStoppedUnderGate || !_pending ||
                !ReferenceEquals(parent._displaySwitch?.ReceiptUnderGate, receipt)) return;
            _receipt = receipt;
            _pending = false;
            SignalUnderGate();
        }

        internal void RevokeUnderGate()
        {
            if (_stopped) return;
            _stopped = true;
            _epoch = null;
            _receipt = null;
            _lastPresented = null;
            _pending = false;
            SignalUnderGate();
        }

        private void SignalUnderGate()
        {
            TaskCompletionSource previous = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    /// <summary>仅携带真实帧头与不可导出的 owner/epoch；猜造的 epoch 不能通过当前性检查。</summary>
    internal sealed class FrameStamp
    {
        private readonly ClientDisplayPresentation _owner;
        private readonly object _epoch;

        internal FrameStamp(ClientDisplayPresentation owner, object epoch, ulong frameId, int width, int height)
        {
            _owner = owner;
            _epoch = epoch;
            FrameId = frameId;
            Width = width;
            Height = height;
        }

        internal ulong FrameId { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal bool Matches(ClientDisplayPresentation owner, object epoch) =>
            ReferenceEquals(_owner, owner) && ReferenceEquals(_epoch, epoch);
    }

    /// <summary>读取结果的非拥有型投影；调用方仅释放 Frame，不存在第二个 payload owner。</summary>
    internal sealed class PresentationFrame(EncodedFrame frame, FrameStamp stamp)
    {
        internal EncodedFrame Frame { get; } = frame;
        internal FrameStamp Stamp { get; } = stamp;
    }

    /// <summary>不可变的逻辑 UI 提交历史；保存过的对象不授予未来提交资格。</summary>
    internal sealed class PresentedFrame(ulong frameId, int width, int height, long? requestSequence)
    {
        internal ulong FrameId { get; } = frameId;
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal long? RequestSequence { get; } = requestSequence;
    }
}
