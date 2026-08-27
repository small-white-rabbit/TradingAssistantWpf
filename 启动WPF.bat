@echo off
chcp 65001 >nul
cd /d "%~dp0"
set PATH=%LOCALAPPDATA%\dotnet;%PATH%
echo 启动交易助手 WPF...
dotnet run --project "StockReviewWpf\StockReviewWpf.csproj" --configuration Debug
pause
