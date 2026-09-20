using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Discovery.Networking;
using LanRemote.Discovery.Protocol;
using Microsoft.Extensions.Logging;

namespace LanRemote.Discovery;

/// <summary>
/// 基于 UDP 的局域网设备发现。
/// </summary>
/// <remarks>
/// <para>只做 IPv4；只使用 RFC1918 私有网卡；组播 <c>239.255.77.77:45872</c> TTL=1，
/// 外加每张网卡的 directed broadcast probe 作为兼容性兜底。
/// <b>没有任何云端、中继、STUN、UPnP、NAT 穿越</b>：在完全断网、只剩一个交换机的环境也必须工作。</para>
/// <para>报文处理顺序是安全要求：先看来源 IP（IPv4 + RFC1918 + 同子网），<b>通过后才进入 JSON parser</b>，
/// 绝不能把公网/跨子网的垃圾喂给反序列化器。</para>
/// <para>发现包<b>不是</b>认证依据：这里产出的设备只能用于「列表展示」与未来的证书 pinning，
/// 是否真的持有访问密钥必须由 M3/M4 的 HMAC 挑战证明。</para>
/// </remarks>
public sealed class LanDiscoveryService : IDiscoveryService
{
    private readonly INetworkBindingProvider _bindingProvider;
    private readonly DiscoveryRuntimeState _runtimeState;
    private readonly DiscoveryDeviceCache _cache;
    private readonly DiscoveryAnnouncementEvaluator _evaluator;
    private readonly ILogger<LanDiscoveryService> _logger;
    private readonly Channel<DiscoveredDevice> _updates;
    private readonly IPAddress _multicastAddress = IPAddress.Parse(DiscoveryConstants.MulticastGroupAddress);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task[] _loops = Array.Empty<Task>();
    private Socket? _receiver;
    private List<Socket> _senders = new();
    private IReadOnlyList<NetworkBinding> _activeBindings = Array.Empty<NetworkBinding>();
    private long _droppedPackets;

    /// <summary>由 DI 容器构造。</summary>
    /// <param name="bindingProvider">本机合格网卡绑定。</param>
    /// <param name="runtimeState">运行时状态（身份 + 开关快照）。</param>
    /// <param name="logger">日志器。</param>
    public LanDiscoveryService(
        INetworkBindingProvider bindingProvider,
        DiscoveryRuntimeState runtimeState,
        ILogger<LanDiscoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(bindingProvider);
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentNullException.ThrowIfNull(logger);

        _bindingProvider = bindingProvider;
        _runtimeState = runtimeState;
        _logger = logger;
        _cache = new DiscoveryDeviceCache();
        _evaluator = new DiscoveryAnnouncementEvaluator();

        _updates = Channel.CreateBounded<DiscoveredDevice>(
            new BoundedChannelOptions(DiscoveryConstants.UpdateChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    /// <summary>启动时实际使用的网卡绑定（供诊断与日志）。</summary>
    public IReadOnlyList<NetworkBinding> CurrentBindings
    {
        get
        {
            lock (_gate)
            {
                return _activeBindings;
            }
        }
    }

    /// <summary>累计丢弃的非法/不可信报文数。</summary>
    public long DroppedPacketCount => Interlocked.Read(ref _droppedPackets);

    /// <inheritdoc />
    /// <remarks>
    /// <b>upsert-only 语义</b>：只推送「在线设备的插入或更新」，<b>不会</b>发送任何
    /// 「Removed」假设备。调用方必须自己按 <see cref="DiscoveredDevice.LastSeen"/> 做 TTL prune。
    /// 这是当前 Core API 的既定语义，M2 不推翻它（详见 ADR-023）。
    /// </remarks>
    public async IAsyncEnumerable<DiscoveredDevice> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (DiscoveredDevice device in _updates.Reader.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return device;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 幂等：已经启动时再次调用不会创建第二组 socket。
    /// 启动完成后会立即主动探测一次（<c>04_PROTOCOL_AND_SECURITY.md</c> 第 5 节的 probe 行为）。
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource cts;

        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            IReadOnlyList<NetworkBinding> bindings = _bindingProvider.GetBindings();
            _activeBindings = bindings;

            if (bindings.Count == 0)
            {
                _logger.LogWarning("没有找到任何合格的私有 IPv4 网卡，局域网发现不会发送也不接收。");
            }
            else
            {
                _logger.LogInformation(
                    "局域网发现启动。有效网卡={Count}，地址={Addresses}",
                    bindings.Count,
                    string.Join(", ", bindings.Select(b => $"{b.InterfaceName}:{b.Address}")));
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cts = cts;

            try
            {
                // 没有合格网卡时不开 socket：既拿不到可信来源，也不该白占端口。
                if (bindings.Count > 0)
                {
                    CreateReceiver(bindings);
                    CreateSenders(bindings);
                }
            }
            catch (Exception ex)
            {
                // socket 建不起来（端口被占用等）不能留下半截状态。
                CloseSocketsCore();
                _cts = null;
                _logger.LogError(ex, "创建 UDP socket 失败，局域网发现未启动。");
                throw;
            }

            _loops = new[]
            {
                Task.Run(() => ReceiveLoopAsync(cts.Token), CancellationToken.None),
                Task.Run(() => AnnounceLoopAsync(cts.Token), CancellationToken.None),
                Task.Run(() => CleanupLoopAsync(cts.Token), CancellationToken.None),
            };
        }

        // 启动后立即主动探测一次，避免必须等下一个 announce 周期才看到别人。
        try
        {
            await ProbeAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "启动后的首次探测发送失败；后续 announce 周期会继续尝试。");
        }
    }

    /// <inheritdoc />
    /// <remarks>幂等：重复调用不会抛异常，也不会重启 socket。停止后不支持再次启动（v1 只在退出时停止）。</remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task[] loops;

        lock (_gate)
        {
            if (_cts is null)
            {
                return;
            }

            cts = _cts;
            _cts = null;
            loops = _loops;
            _loops = Array.Empty<Task>();

            CloseSocketsCore();
        }

        cts.Cancel();

        // 完成 channel，让 WatchAsync 的消费者自然结束，不留悬挂等待。
        _updates.Writer.TryComplete();

        try
        {
            await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("停止局域网发现时，后台循环未在 5 秒内退出。");
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            // 必须观察后台任务异常，避免 unobserved exception。
            _logger.LogDebug(ex, "局域网发现后台循环退出时抛出异常。");
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 与「允许被发现」无关：即使 <c>AllowDiscovery=false</c>，
    /// 本机<b>仍然可以</b>扫描别人——「允许被发现」只控制 announce 与 probe 响应。
    /// </remarks>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        DiscoveryProbe probe = new() { Nonce = DiscoveryPacketCodec.NewNonce() };
        byte[] payload = DiscoveryPacketCodec.EncodeProbe(probe);

        IReadOnlyList<NetworkBinding> bindings;
        Socket[] senders;
        lock (_gate)
        {
            bindings = _activeBindings;
            senders = _senders.ToArray();
        }

        if (senders.Length == 0)
        {
            _logger.LogDebug("无可用发送 socket，跳过探测。");
            return;
        }

        for (int i = 0; i < bindings.Count && i < senders.Length; i++)
        {
            NetworkBinding binding = bindings[i];
            Socket sender = senders[i];

            await TrySendAsync(
                sender,
                payload,
                new IPEndPoint(_multicastAddress, DiscoveryConstants.Port),
                binding,
                "组播 probe",
                cancellationToken).ConfigureAwait(false);

            await TrySendAsync(
                sender,
                payload,
                new IPEndPoint(binding.DirectedBroadcast, DiscoveryConstants.Port),
                binding,
                "定向广播 probe",
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("已发送 probe：组播 + {Count} 个定向广播。", bindings.Count);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[DiscoveryConstants.ReceiverBufferBytes];
        EndPoint anyEndpoint = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? socket;
            lock (_gate)
            {
                socket = _receiver;
            }

            if (socket is null)
            {
                break;
            }

            try
            {
                SocketReceiveFromResult result = await socket
                    .ReceiveFromAsync(
                        new ArraySegment<byte>(buffer),
                        SocketFlags.None,
                        anyEndpoint,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.ReceivedBytes <= 0
                    || result.ReceivedBytes > DiscoveryConstants.MaxAnnouncementBytes)
                {
                    LogDrop("长度超出协议上限或为空");
                    continue;
                }

                if (result.RemoteEndPoint is not IPEndPoint remote)
                {
                    LogDrop("来源不是 IPv4 endpoint");
                    continue;
                }

                await HandleDatagramAsync(buffer, result.ReceivedBytes, remote, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                // 报文比缓冲区还大：丢弃并继续，绝不终止 discovery。
                LogDrop("datagram 超过接收缓冲区");
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogDebug(ex, "UDP 接收失败（SocketError={ErrorCode}）。", ex.SocketErrorCode);
                await SafeDelayAsync(50, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleDatagramAsync(
        byte[] buffer,
        int length,
        IPEndPoint remote,
        CancellationToken cancellationToken)
    {
        // ── 第一关：来源地址（JSON 之前） ──────────────────────────────
        IReadOnlyList<NetworkBinding> bindings;
        lock (_gate)
        {
            bindings = _activeBindings;
        }

        if (!SourceEndpointFilter.IsAcceptableSource(remote.Address, bindings))
        {
            LogDrop("来源地址非 IPv4 / 非 RFC1918 / 不在任何本机子网内");
            return;
        }

        // ── 第二关：只读出 type 再决定怎么解析 ────────────────────────
        byte[] payload = buffer.AsSpan(0, length).ToArray();

        if (!DiscoveryPacketCodec.TryPeekType(payload, out string? type))
        {
            LogDrop("无法识别报文 type");
            return;
        }

        if (string.Equals(type, DiscoveryConstants.ProbeType, StringComparison.Ordinal))
        {
            if (!DiscoveryPacketCodec.TryDecodeProbe(payload, out DiscoveryProbe? probe)
                || !IsValidProbe(probe))
            {
                LogDrop("probe 报文非法");
                return;
            }

            await ReplyToProbeAsync(remote, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(type, DiscoveryConstants.AnnounceType, StringComparison.Ordinal))
        {
            if (!DiscoveryPacketCodec.TryDecodeAnnouncement(payload, out DiscoveryAnnouncement? announcement))
            {
                LogDrop("announce 报文非法");
                return;
            }

            HandleAnnouncement(announcement!, remote);
            return;
        }

        LogDrop("未知报文 type");
    }

    private void HandleAnnouncement(DiscoveryAnnouncement announcement, IPEndPoint remote)
    {
        if (!_runtimeState.TryGetCurrent(out DiscoveryRuntimeSnapshot? local) || local is null)
        {
            LogDrop("本机身份尚未就绪");
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!_evaluator.TryEvaluate(
                announcement,
                remote.Address,
                local,
                now,
                out DiscoveredDevice? device,
                out string? reason)
            || device is null)
        {
            LogDrop(reason ?? "公告校验失败");
            return;
        }

        bool isNew = _cache.Upsert(device, now);

        // §54：第一次发现才 Information；重复的 announce 不刷日志。
        if (isNew)
        {
            _logger.LogInformation(
                "发现设备 {DeviceName}（{DeviceCode}）于 {RemoteAddress}，能力={Capabilities}。",
                device.DeviceName,
                device.DeviceCode,
                remote.Address,
                string.Join(',', device.Capabilities));
        }

        // 队列是有界的：UI 落后时丢旧状态，保留最近状态。
        _updates.Writer.TryWrite(device);
    }

    private async Task ReplyToProbeAsync(IPEndPoint remote, CancellationToken cancellationToken)
    {
        if (!_runtimeState.TryGetCurrent(out DiscoveryRuntimeSnapshot? local) || local is null)
        {
            return;
        }

        if (!local.CanReplyToProbe)
        {
            _logger.LogDebug("AllowDiscovery=false，不回应来自 {RemoteAddress} 的 probe。", remote.Address);
            return;
        }

        byte[] payload = BuildAnnouncement(local);

        IReadOnlyList<NetworkBinding> bindings;
        Socket[] senders;
        lock (_gate)
        {
            bindings = _activeBindings;
            senders = _senders.ToArray();
        }

        if (senders.Length == 0)
        {
            return;
        }

        // 选一个与远端同子网的 socket；找不到就用第一个。
        // 定向广播 probe 往往意味着对方收不到组播，因此这里用 unicast 回应兼容性最好。
        int index = 0;
        for (int i = 0; i < bindings.Count && i < senders.Length; i++)
        {
            if (Ipv4Math.IsInSameNetwork(bindings[i].Address, bindings[i].SubnetMask, remote.Address))
            {
                index = i;
                break;
            }
        }

        await TrySendAsync(
            senders[index],
            payload,
            remote,
            bindings[index < bindings.Count ? index : 0],
            "probe 的 unicast 回应",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        // 先立刻 announce 一次，让同网段的设备马上能看到本机。
        await AnnounceOnceAsync(cancellationToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(DiscoveryConstants.AnnounceIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await AnnounceOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private async Task AnnounceOnceAsync(CancellationToken cancellationToken)
    {
        if (!_runtimeState.TryGetCurrent(out DiscoveryRuntimeSnapshot? local) || local is null)
        {
            return;
        }

        if (!local.CanAnnounce)
        {
            return;
        }

        byte[] payload = BuildAnnouncement(local);

        IReadOnlyList<NetworkBinding> bindings;
        Socket[] senders;
        lock (_gate)
        {
            bindings = _activeBindings;
            senders = _senders.ToArray();
        }

        for (int i = 0; i < bindings.Count && i < senders.Length; i++)
        {
            await TrySendAsync(
                senders[i],
                payload,
                new IPEndPoint(_multicastAddress, DiscoveryConstants.Port),
                bindings[i],
                "组播 announce",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer =
            new(TimeSpan.FromMilliseconds(DiscoveryConstants.CacheCleanupIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                int removed = _cache.RemoveExpired(
                    DateTimeOffset.UtcNow,
                    out IReadOnlyList<DiscoveredDevice> expired);

                if (removed == 0)
                {
                    continue;
                }

                foreach (DiscoveredDevice device in expired)
                {
                    _logger.LogInformation(
                        "设备离线：{DeviceName}（{DeviceCode}）{Address}。",
                        device.DeviceName,
                        device.DeviceCode,
                        device.Address);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private static byte[] BuildAnnouncement(DiscoveryRuntimeSnapshot local) =>
        DiscoveryPacketCodec.EncodeAnnouncement(new DiscoveryAnnouncement
        {
            DeviceId = local.LocalDeviceId,
            DeviceCode = local.DeviceCode,
            DeviceName = local.DeviceName,
            AppVersion = local.AppVersion,
            TcpPort = DiscoveryConstants.ExpectedTransportPort,
            CertSha256 = local.CertificateSha256,
            Nonce = DiscoveryPacketCodec.NewNonce(),
            Capabilities = local.Capabilities.ToList(),
        });

    private static bool IsValidProbe(DiscoveryProbe? probe)
    {
        if (probe is null)
        {
            return false;
        }

        return string.Equals(probe.Magic, DiscoveryConstants.Magic, StringComparison.Ordinal)
            && probe.Protocol == DiscoveryConstants.ProtocolVersion
            && string.Equals(probe.Type, DiscoveryConstants.ProbeType, StringComparison.Ordinal)
            && DiscoveryPacketCodec.IsWellFormedNonce(probe.Nonce);
    }

    private async Task TrySendAsync(
        Socket sender,
        byte[] payload,
        IPEndPoint target,
        NetworkBinding binding,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            await sender
                .SendToAsync(new ArraySegment<byte>(payload), SocketFlags.None, target, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            // 一个网卡失败只影响这一个网卡，不能拖垮全部 discovery。
            _logger.LogDebug(
                ex,
                "发送{description}到 {Target} 失败（网卡={InterfaceName}，SocketError={ErrorCode}）。",
                description,
                target.Address,
                binding.InterfaceName,
                ex.SocketErrorCode);
        }
        catch (ObjectDisposedException)
        {
            // 正在停止，忽略。
        }
        catch (OperationCanceledException)
        {
            // 正在停止，忽略。
        }
    }

    private void CreateReceiver(IReadOnlyList<NetworkBinding> bindings)
    {
        Socket receiver = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            // Windows 上这两个选项必须在 Bind 之前设置，顺序反了会抛。
            receiver.ExclusiveAddressUse = false;
            receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            receiver.Bind(new IPEndPoint(IPAddress.Any, DiscoveryConstants.Port));

            foreach (NetworkBinding binding in bindings)
            {
                receiver.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(_multicastAddress, binding.Address));
            }

            _receiver = receiver;
        }
        catch
        {
            receiver.Dispose();
            throw;
        }
    }

    private void CreateSenders(IReadOnlyList<NetworkBinding> bindings)
    {
        List<Socket> senders = new();

        try
        {
            foreach (NetworkBinding binding in bindings)
            {
                Socket sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                senders.Add(sender);

                sender.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sender.Bind(new IPEndPoint(binding.Address, 0));
                sender.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                sender.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastTimeToLive,
                    DiscoveryConstants.MulticastTimeToLive);
            }

            _senders = senders;
        }
        catch
        {
            foreach (Socket sender in senders)
            {
                SafeDisposeSocket(sender);
            }

            throw;
        }
    }

    private void CloseSocketsCore()
    {
        if (_receiver is not null)
        {
            SafeDisposeSocket(_receiver);
            _receiver = null;
        }

        foreach (Socket sender in _senders)
        {
            SafeDisposeSocket(sender);
        }

        _senders = new List<Socket>();
        _activeBindings = Array.Empty<NetworkBinding>();
    }

    private void SafeDisposeSocket(Socket socket)
    {
        try
        {
            socket.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "释放 socket 时出错（已忽略）。");
        }
    }

    private void LogDrop(string reason)
    {
        long count = Interlocked.Increment(ref _droppedPackets);

        // 限制日志频率：恶意报文不能把日志刷爆（日志 DoS）。
        if (count <= 8 || count % 256 == 0)
        {
            _logger.LogDebug("丢弃发现报文：{Reason}（累计={Count}）", reason, count);
        }
    }

    private static async Task SafeDelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 忽略：延迟只是为了不在错误路径上空转。
        }
    }
}
