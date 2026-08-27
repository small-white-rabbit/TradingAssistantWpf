@echo off
chcp 65001 >nul
echo ===================================
echo  交易助手 WPF - 构建脚本
echo ===================================
echo.

set PATH=%LOCALAPPDATA%\dotnet;%PATH%

echo [1/3] 还原 NuGet 包...
dotnet restore "stock-review-wpf\StockReviewWpf.sln"
if %errorlevel% neq 0 (
    echo [错误] NuGet 包还原失败
    pause
    exit /b 1
)

echo.
echo [2/3] 编译项目...
dotnet build "stock-review-wpf\StockReviewWpf.sln" --configuration Release
if %errorlevel% neq 0 (
    echo [错误] 编译失败
    pause
    exit /b 1
)

echo.
echo [3/3] 编译成功！
echo 输出目录: stock-review-wpf\StockReviewWpf\bin\Release\net10.0-windows\
echo.
echo 使用 启动WPF.bat 运行程序
pause
