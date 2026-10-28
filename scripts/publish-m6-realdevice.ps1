# LanRemote M6 · 真机双端验收包一键发布（PowerShell）
#
# 用法：在本机（非受限会话）的 PowerShell 里执行：
#   .\scripts\publish-m6-realdevice.ps1
#
# 说明：
#   · 需要能正常工作的 dotnet SDK（本机默认：C:\Users\Administrator\.dotnet\dotnet.exe；
#     若不在默认位置，用 -Dotnet 指定）。
#   · 产出：artifacts/m6-auto-quality-<日期>/ 与 outputs/LanRemote-M6-自动画质真机验收-<日期>-win-x64.zip
#   · 自包含 win-x64，目标机无需安装 .NET 运行时；两端请用同一个 ZIP（同哈希）。
#   · 若当前会话的 NuGet 还原报 "Value cannot be null (Parameter 'path1')"，
#     说明本机会话缺少 ProgramData / ProgramFiles / LOCALAPPDATA 等变量，
#     换一个普通桌面会话（或先在本脚本开头补齐环境变量）再执行。

param(
    [string]$Dotnet = "C:\Users\Administrator\.dotnet\dotnet.exe",
    [string]$Date = (Get-Date -Format "yyyyMMdd")
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$outDir = Join-Path $repo "artifacts\m6-auto-quality-$Date"
$zip    = Join-Path $repo "outputs\LanRemote-M6-自动画质真机验收-$Date-win-x64.zip"

Write-Host "== 1/3 publish (self-contained win-x64) =="
& $Dotnet publish "src\LanRemote.App\LanRemote.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

Write-Host "== 2/3 zip =="
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($outDir, $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "== 3/3 sha256 =="
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$shaFile = "$zip.sha256.txt"
Set-Content -Encoding utf8 $shaFile "$sha  $(Split-Path $zip -Leaf)"
Write-Host "ZIP : $zip"
Write-Host "SHA : $sha"
Write-Host "清单: $shaFile"
