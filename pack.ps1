# 一键打包发布脚本：dotnet publish + vpk pack
# 历史教训（参数漏传的三个坑，务必通过本脚本打包，勿手工敲 vpk 命令）：
#   1. packId 必须是 StockReviewWpf（与已装版本一致），敲错会导致安装器误显示"修复"、更新链断裂
#   2. --icon 必须指向 tray.ico（K+箭头图），漏传会让安装器/快捷方式用 Velopack 默认图标
#   3. --packTitle 必须是"交易助手"，漏传会让开始菜单/桌面快捷方式/控制面板显示英文 packId
param(
    [string]$Version
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
$Project = Join-Path $RepoRoot "StockReviewWpf\StockReviewWpf.csproj"
$PackId = "StockReviewWpf"
$PackTitle = "交易助手"
$MainExe = "StockReviewWpf.exe"
$Icon = "StockReviewWpf\Resources\Images\tray.ico"
$PublishDir = "StockReviewWpf\bin\Release\net10.0-windows\win-x64\publish"
$ReleasesDir = Join-Path $RepoRoot "Releases"

if (-not $Version) {
    [xml]$Csproj = Get-Content $Project -Encoding UTF8
    $Version = ($Csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if (-not $Version) { throw "未能从 csproj 读取版本号，请用 -Version 2.2.1 显式指定" }

Write-Host "==> 清理旧 publish 产物（防陈旧文件混入安装包）" -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

Write-Host "==> dotnet publish v$Version" -ForegroundColor Cyan
dotnet publish $Project -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

Write-Host "==> vpk pack v$Version (packId=$PackId, title=$PackTitle)" -ForegroundColor Cyan
vpk pack -u $PackId -v $Version --packTitle $PackTitle -p $PublishDir -e $MainExe --icon $Icon
if ($LASTEXITCODE -ne 0) { throw "vpk pack 失败" }

Write-Host "==> 打包完成，Releases 产物：" -ForegroundColor Green
Get-ChildItem $ReleasesDir -File | Sort-Object Length -Descending |
    Format-Table Name, @{N = 'SizeMB'; E = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
