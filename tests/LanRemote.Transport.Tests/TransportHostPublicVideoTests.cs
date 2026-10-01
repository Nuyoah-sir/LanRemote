using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class TransportHostPublicVideoTests
{
    [Fact]
    public void Public_Video_Factory_Has_Exactly_Five_Required_Inputs_And_Optional_Host_Options()
    {
        MethodInfo factory = Assert.Single(
            typeof(TransportHost).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.Name == nameof(TransportHost.CreateWithVideo));

        Assert.Equal(typeof(TransportHost), factory.ReturnType);
        ParameterInfo[] parameters = factory.GetParameters();
        Assert.Equal(
            new[]
            {
                typeof(IReadOnlyList<IPAddress>),
                typeof(ISubnetPolicy),
                typeof(X509Certificate2),
                typeof(ControlAuthContext),
                typeof(IVideoFrameProducerFactory),
                typeof(TransportHostOptions)
            },
            parameters.Select(parameter => parameter.ParameterType).ToArray());
        Assert.Equal(
            new[] { "localAddresses", "subnetPolicy", "serverCertificate", "authContext", "videoFactory", "options" },
            parameters.Select(parameter => parameter.Name).ToArray());
        Assert.All(parameters.Take(5), parameter => Assert.False(parameter.IsOptional));
        Assert.True(parameters[5].IsOptional);
        Assert.Null(parameters[5].DefaultValue);
        Assert.All(parameters, parameter => Assert.True(parameter.ParameterType.IsVisible));
    }

    [Fact]
    public void Public_Video_Entry_Does_Not_Expose_Low_Level_Credential_Inputs()
    {
        // 这里只校验新入口的公开形状；认证与视频 attach 门禁由运行时路由测试验证。
        MethodInfo factory = Assert.Single(
            typeof(TransportHost).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.Name.StartsWith("CreateWith", StringComparison.Ordinal));
        Assert.Equal(nameof(TransportHost.CreateWithVideo), factory.Name);
        Assert.DoesNotContain(factory.GetParameters(), parameter =>
            typeof(Stream).IsAssignableFrom(parameter.ParameterType)
            || parameter.ParameterType == typeof(byte[])
            || parameter.ParameterType == typeof(CancellationToken)
            || parameter.ParameterType == typeof(AccessSecret)
            || parameter.ParameterType == typeof(SessionRegistry));

        MethodInfo create = Assert.Single(typeof(IVideoFrameProducerFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(IVideoFrameProducerFactory.CreateAsync), create.Name);
        Assert.Equal(typeof(ValueTask<IVideoFrameProducer>), create.ReturnType);
        Assert.Equal(new[] { typeof(Guid), typeof(CancellationToken) },
            create.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public async Task Original_Public_Session_Handler_Constructor_Remains_Available()
    {
        ConstructorInfo constructor = Assert.Single(typeof(TransportHost).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        ParameterInfo[] parameters = constructor.GetParameters();
        Assert.Equal(
            new[]
            {
                typeof(IReadOnlyList<IPAddress>),
                typeof(ISubnetPolicy),
                typeof(X509Certificate2),
                typeof(Func<AcceptedConnection, CancellationToken, Task>),
                typeof(TransportHostOptions)
            },
            parameters.Select(parameter => parameter.ParameterType).ToArray());
        Assert.True(parameters[4].IsOptional);

        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using TransportHost host = new(
            new[] { IPAddress.Loopback }, new UnexpectedSubnetPolicy(), certificate,
            (_, _) => throw new InvalidOperationException("构造期间不得调用旧 handler。"),
            new TransportHostOptions { Port = -1 });
        Assert.False(host.IsRunning);
        Assert.Equal(0, host.ActiveConnections);
    }

    [Fact]
    public async Task Original_Internal_Channel_Router_Remains_Callable_But_Not_Public()
    {
        Assert.DoesNotContain(typeof(TransportHost)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.Name == nameof(TransportHost.CreateWithChannelRouter));

        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using TransportHost host = TransportHost.CreateWithChannelRouter(
            new[] { IPAddress.Loopback }, new UnexpectedSubnetPolicy(), certificate,
            NewContext(), new UnexpectedFrameSource(),
            options: new TransportHostOptions { Port = -1 });
        Assert.False(host.IsRunning);
        Assert.Equal(0, host.ActiveConnections);
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(5, 3)]
    public async Task CreateWithVideo_Is_Cold_And_Never_Invokes_Borrowed_Factory(int globalLimit, int perAddressLimit)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        CountingFactory factory = new();
        // 非法端口作为哨兵：即使实现误在构造期调用 Start，也不可能真正绑定端口。
        TransportHostOptions options = new()
        {
            Port = -1,
            MaxConnections = globalLimit,
            MaxConnectionsPerAddress = perAddressLimit
        };

        await using TransportHost host = TransportHost.CreateWithVideo(
            new[] { IPAddress.Loopback }, new UnexpectedSubnetPolicy(), certificate,
            NewContext(), factory, options);

        Assert.False(host.IsRunning);
        Assert.Equal(0, host.ActiveConnections);
        Assert.Equal(0, host.AdmittedConnections);
        Assert.Equal(globalLimit, host.Limiter.GlobalLimit);
        Assert.Equal(perAddressLimit, host.Limiter.PerAddressLimit);
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData("localAddresses")]
    [InlineData("subnetPolicy")]
    [InlineData("serverCertificate")]
    [InlineData("authContext")]
    [InlineData("videoFactory")]
    public void CreateWithVideo_Rejects_Null_Required_Input_Before_Start(string missing)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        CountingFactory factory = new();
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() =>
            TransportHost.CreateWithVideo(
                missing == "localAddresses" ? null! : new[] { IPAddress.Loopback },
                missing == "subnetPolicy" ? null! : new UnexpectedSubnetPolicy(),
                missing == "serverCertificate" ? null! : certificate,
                missing == "authContext" ? null! : NewContext(),
                missing == "videoFactory" ? null! : factory,
                new TransportHostOptions { Port = -1 }));

        Assert.Equal(missing, error.ParamName);
        Assert.Equal(0, factory.Calls);
    }

    private static ControlAuthContext NewContext()
    {
        UnexpectedControlServices services = new();
        return new ControlAuthContext
        {
            ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            AccessSecretStore = services,
            FailedAuthLimiter = new FailedAuthLimiter(),
            PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
            ApprovalGate = services,
            SessionRegistry = new SessionRegistry()
        };
    }

    private sealed class UnexpectedSubnetPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            throw new InvalidOperationException("未启动的 Host 不得检查网络地址。");
    }

    private sealed class UnexpectedControlServices : IAccessSecretStore, ILocalApprovalGate
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("构造期间不得加载认证密钥。");

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("构造期间不得重新生成认证密钥。");

        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(
            LocalApprovalRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("构造期间不得请求本机审批。");
    }

    private sealed class UnexpectedFrameSource : IVideoFrameSource
    {
        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("构造期间不得读取视频帧。");
    }

    private sealed class CountingFactory : IVideoFrameProducerFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<IVideoFrameProducer> CreateAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("构造期间不得创建视频生产者。");
        }
    }
}
