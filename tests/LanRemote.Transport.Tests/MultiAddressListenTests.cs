using System.Net;
using System.Net.Sockets;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 实测：同一个 TCP 端口在多张本地 IPv4 地址上 bind 的行为，以及地址重叠复用问题。
/// </summary>
/// <remarks>
/// <para>M3 步骤 8 的前置问题（triage B-23）：Host 要给<b>每张</b>合格网卡起一个 45873 listener。</para>
/// <para>本机只有一张活跃网卡（<c>172.100.166.220</c>，且不是 RFC1918），没有两张真实网卡可用，
/// 所以用回环段的两个不同地址（<c>127.0.0.1</c> / <c>127.0.0.2</c>）测「同一端口 + 不同本地地址」。
/// <b>不覆盖</b>真实多网卡下的路由/防火墙差异，只回答 bind 层允许什么。</para>
/// <para><b>实测到的关键意外</b>：先 bind <c>0.0.0.0:P</c>，再 bind <c>127.0.0.1:P</c> —— <b>成功</b>。
/// 即 Windows 默认允许地址<b>重叠</b>复用（<c>TcpListener.ExclusiveAddressUse</c> 默认 <c>false</c>）。
/// 安全含义：若别的进程先占了 <c>0.0.0.0:45873</c>，我们的 per-NIC listener 会<b>静默启动成功</b>，
/// 但流量归属变得不确定——必须显式开 <c>ExclusiveAddressUse</c>，让重叠 bind <b>直接失败</b>。</para>
/// </remarks>
public sealed class MultiAddressListenTests
{
    [Fact]
    public void TcpListener_ExclusiveAddressUse_Defaults_To_False()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        // 记录默认值：这是「重叠 bind 会成功」的根因，不要凭印象认为默认是 true。
        Assert.False(listener.ExclusiveAddressUse);
    }

    [Fact]
    public void Same_Port_Can_Be_Bound_On_Two_Different_Local_Addresses()
    {
        int port = GetFreePort();

        using var first = new TcpListener(IPAddress.Loopback, port);
        using var second = new TcpListener(IPAddress.Parse("127.0.0.2"), port);

        first.Start();
        second.Start();

        Assert.Equal(port, ((IPEndPoint)first.LocalEndpoint).Port);
        Assert.Equal(port, ((IPEndPoint)second.LocalEndpoint).Port);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)first.LocalEndpoint).Address);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), ((IPEndPoint)second.LocalEndpoint).Address);
    }

    [Fact]
    public void Same_Port_On_Same_Local_Address_Cannot_Be_Bound_Twice()
    {
        int port = GetFreePort();

        using var first = new TcpListener(IPAddress.Loopback, port);
        first.Start();

        using var second = new TcpListener(IPAddress.Loopback, port);
        SocketException exception = Assert.Throws<SocketException>(() => second.Start());

        Assert.Equal(SocketError.AddressAlreadyInUse, exception.SocketErrorCode);
    }

    [Fact]
    public void Without_Exclusive_Wildcard_Bind_Does_Not_Block_Specific_Address_On_Same_Port()
    {
        int port = GetFreePort();

        using var wildcard = new TcpListener(IPAddress.Any, port);
        wildcard.Start();

        using var specific = new TcpListener(IPAddress.Loopback, port);

        // 实测：不抛。这正是要防的那件事——静默共享端口。
        specific.Start();
    }

    [Fact]
    public void With_Exclusive_Same_Port_Still_Works_On_Two_Different_Local_Addresses()
    {
        int port = GetFreePort();

        using var first = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        using var second = new TcpListener(IPAddress.Parse("127.0.0.2"), port) { ExclusiveAddressUse = true };

        // 关键：ExclusiveAddressUse 不能把「多网卡同端口」一起禁掉，否则 M3 步骤 8 就做不成。
        first.Start();
        second.Start();

        Assert.Equal(port, ((IPEndPoint)second.LocalEndpoint).Port);
    }

    [Fact]
    public void With_Exclusive_Overlapping_Wildcard_Bind_Is_Refused()
    {
        int port = GetFreePort();

        using var wildcard = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
        wildcard.Start();

        using var specific = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        SocketException exception = Assert.Throws<SocketException>(() => specific.Start());

        // 实测是 AccessDenied(10013)，**不是** AddressAlreadyInUse(10048)。
        // 差别很重要：Host 启动若只把 10048 当作"端口被占"，就会漏判这种情况，
        // 于是把一个"端口被别人以更宽的地址占了"的严重情形当成未知错误吞掉。
        Assert.Equal(SocketError.AccessDenied, exception.SocketErrorCode);
    }

    /// <summary>
    /// 完整 2×2 矩阵：先 bind 的一方是否 exclusive × 后 bind 的一方是否 exclusive。
    /// </summary>
    /// <remarks>
    /// 这一组是被一次失败逼出来的：我原本假设「我们自己开 <c>ExclusiveAddressUse</c>
    /// 就能发现别人先占了更宽的地址」，实测<b>不成立</b>。记录真实矩阵，别再猜。
    /// </remarks>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void Overlap_Matrix_Wildcard_Then_Specific(
        bool firstExclusive,
        bool secondExclusive,
        bool secondBindSucceeds)
    {
        int port = GetFreePort();

        using var wildcard = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = firstExclusive };
        wildcard.Start();

        using var specific = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = secondExclusive };

        bool succeeded = true;
        SocketError? error = null;
        try
        {
            specific.Start();
        }
        catch (SocketException ex)
        {
            succeeded = false;
            error = ex.SocketErrorCode;
        }

        Assert.Equal(secondBindSucceeds, succeeded);

        if (!succeeded)
        {
            Assert.NotNull(error);
        }
    }

    /// <summary>
    /// 带 <c>SO_REUSEADDR</c> 的后来者能否抢走我们已经 exclusive 占住的端口。
    /// </summary>
    [Fact]
    public void ReuseAddress_Takeover_Is_Refused_Against_An_Exclusive_Bind()
    {
        int port = GetFreePort();

        using Socket ours = CreateListenerSocket(port, exclusiveAddressUse: true);

        using Socket intruder = CreateReuseAddressSocket();
        SocketException exception = Assert.Throws<SocketException>(
            () => intruder.Bind(new IPEndPoint(IPAddress.Loopback, port)));

        // 实测 AccessDenied(10013)，不是 AddressAlreadyInUse(10048)。
        Assert.Equal(SocketError.AccessDenied, exception.SocketErrorCode);
    }

    /// <summary>
    /// 对照组：不开 <c>ExclusiveAddressUse</c> 时，带 <c>SO_REUSEADDR</c> 的后来者同样被拒。
    /// </summary>
    /// <remarks>
    /// <b>这条是本次测量里最重要的诚实记录</b>：我原本以为
    /// 「<c>ExclusiveAddressUse</c> 买到的是挡住 <c>SO_REUSEADDR</c> 抢端口」，
    /// 但对照组显示——本机（Win11 25H2 / 26200）上<b>开不开都一样是被拒</b>。
    /// 也就是说在<b>本机可观测的范围内，<c>ExclusiveAddressUse</c> 没有带来差别</b>。
    /// <para>保留 <c>ExclusiveAddressUse = true</c> 的理由因此只能写成：
    /// 防御 <c>SO_REUSEADDR</c> 语义更宽松的旧版 Windows；
    /// <b>不要</b>声称它能发现「别人先占了更宽的地址」——实测不能（见矩阵）。</para>
    /// </remarks>
    [Fact]
    public void ReuseAddress_Takeover_Is_Also_Refused_Against_A_Non_Exclusive_Bind()
    {
        int port = GetFreePort();

        using Socket ours = CreateListenerSocket(port, exclusiveAddressUse: false);

        using Socket intruder = CreateReuseAddressSocket();
        SocketException exception = Assert.Throws<SocketException>(
            () => intruder.Bind(new IPEndPoint(IPAddress.Loopback, port)));

        Assert.Equal(SocketError.AccessDenied, exception.SocketErrorCode);
    }

    /// <summary>
    /// 当更宽的 bind 已经存在时，连到**具体地址**的连接归谁。
    /// </summary>
    /// <remarks>
    /// 上面几组说明「我们的 bind 会静默成功」。这还没回答最关键的问题：
    /// 那种共享会不会把流量送到别人那里。这一组就是回答它。
    /// </remarks>
    [Fact(Timeout = 30_000)]
    public async Task Connection_To_Specific_Address_Goes_To_The_More_Specific_Listener()
    {
        int port = GetFreePort();

        using var wildcard = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = false };
        wildcard.Start();

        using var specific = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        specific.Start();

        Task<TcpClient> wildcardAccept = wildcard.AcceptTcpClientAsync();
        Task<TcpClient> specificAccept = specific.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        Task finished = await Task.WhenAny(specificAccept, Task.Delay(TimeSpan.FromSeconds(3)));

        // 实测：最具体的那个 listener 收到连接。
        Assert.Same(specificAccept, finished);
        Assert.False(wildcardAccept.IsCompleted);

        using TcpClient accepted = await specificAccept;
        Assert.True(accepted.Client.RemoteEndPoint is IPEndPoint { Address: { } } endpoint
                    && endpoint.Address.Equals(IPAddress.Loopback));
    }

    private static Socket CreateListenerSocket(int port, bool exclusiveAddressUse)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ExclusiveAddressUse,
                exclusiveAddressUse);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Listen(8);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Socket CreateReuseAddressSocket()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
