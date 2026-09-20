# 为当前 PowerShell 会话加载本机级的 .NET SDK。
# 用法：. .\scripts\env.ps1
#
# 说明：SDK 装在用户级目录而不是 Program Files，所以不在系统 PATH 里。
# 本脚本只影响当前会话，不修改任何持久化环境变量。

$dotnetDir = Join-Path $env:USERPROFILE '.dotnet'
$dotnetExe = Join-Path $dotnetDir 'dotnet.exe'

if (-not (Test-Path $dotnetExe)) {
    Write-Error "未找到 SDK: $dotnetExe`n请重新运行安装步骤，或手工指定 dotnet 路径。"
    return
}

$env:DOTNET_ROOT = $dotnetDir
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

Write-Host "已加载 .NET SDK: $((dotnet --version))"
