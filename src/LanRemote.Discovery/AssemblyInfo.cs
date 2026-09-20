using System.Runtime.CompilerServices;

// Discovery 把「probe 回应的目标端口」「组播出口网卡选项值」这两条不变量
// 做成了 internal 的纯函数，好让单元测试直接断言它们，而不是为了测试新增公共 API。
// 只开放给测试工程，产品代码对外的公共表面积不受影响。
[assembly: InternalsVisibleTo("LanRemote.Protocol.Tests")]
