# 项目初始化参考

> 命令仅为建议，施工 AI 应按实际 SDK/目录微调。

```powershell
dotnet new sln -n LanRemote

dotnet new wpf -n LanRemote.App -f net10.0-windows
dotnet new classlib -n LanRemote.Core -f net10.0
dotnet new classlib -n LanRemote.Discovery -f net10.0
dotnet new classlib -n LanRemote.Transport -f net10.0
dotnet new classlib -n LanRemote.Security -f net10.0-windows
dotnet new classlib -n LanRemote.Capture -f net10.0-windows
dotnet new classlib -n LanRemote.Input -f net10.0-windows
dotnet new classlib -n LanRemote.Sessions -f net10.0

dotnet new xunit -n LanRemote.Core.Tests -f net10.0
dotnet new xunit -n LanRemote.Security.Tests -f net10.0
dotnet new xunit -n LanRemote.Protocol.Tests -f net10.0
dotnet new xunit -n LanRemote.IntegrationTests -f net10.0
```

推荐全局属性 `Directory.Build.props`：

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
  </PropertyGroup>
</Project>
```

说明：
- 初期 `TreatWarningsAsErrors=false`，避免 Win32/WPF interop 警告阻塞施工；
- M9 前应把项目自身可修警告清掉；
- 不要用 suppress-all 方式隐藏安全警告。

## 推荐领域模型

```csharp
public sealed record DeviceIdentity(
    Guid DeviceId,
    string DeviceCode,
    string DeviceName,
    string CertificateSha256);

public sealed record DiscoveredDevice(
    Guid DeviceId,
    string DeviceCode,
    string DeviceName,
    IPAddress Address,
    int Port,
    string CertificateSha256,
    DateTimeOffset LastSeen,
    IReadOnlySet<string> Capabilities);

public enum SessionPermission
{
    ViewOnly = 0,
    Control = 1,
}

public sealed record VideoQualitySettings(
    int TargetFps,
    double Scale,
    int JpegQuality,
    bool Auto);
```

## Config 草案

```json
{
  "allowDiscovery": true,
  "allowViewing": true,
  "allowControl": true,
  "requireLocalApprovalForUnknownController": true,
  "autoStartOnLogin": false,
  "defaultQualityPreset": "Balanced",
  "discoveryPort": 45872,
  "transportPort": 45873,
  "maxViewSessions": 3,
  "maxControlSessions": 1
}
```

秘密不在这个 JSON。
