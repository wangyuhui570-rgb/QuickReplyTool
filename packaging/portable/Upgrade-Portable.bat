@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "PACKAGE_ROOT=%~dp0"
set "SOURCE_APP=%PACKAGE_ROOT%app"

echo 客服效率工具绿色版升级
echo.

if not exist "%SOURCE_APP%\QuickReplyTool.exe" (
    echo 当前升级包不完整，缺少 app\QuickReplyTool.exe
    pause
    exit /b 1
)

set /p "TARGET_ROOT=请输入你当前正在使用的绿色版根目录（目录内必须有 app 文件夹）： "
if "%TARGET_ROOT%"=="" (
    echo 未输入目录，已取消升级。
    pause
    exit /b 1
)

if not exist "%TARGET_ROOT%\app\QuickReplyTool.exe" (
    echo 目标目录无效，缺少 "%TARGET_ROOT%\app\QuickReplyTool.exe"
    pause
    exit /b 1
)

echo.
echo 正在关闭旧程序...
taskkill /IM QuickReplyTool.exe /F >nul 2>&1

echo 正在复制新程序文件...
robocopy "%SOURCE_APP%" "%TARGET_ROOT%\app" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
set "ROBOCOPY_EXIT=%ERRORLEVEL%"
if %ROBOCOPY_EXIT% GEQ 8 (
    echo 升级失败，Robocopy 返回码：%ROBOCOPY_EXIT%
    pause
    exit /b %ROBOCOPY_EXIT%
)

copy /Y "%PACKAGE_ROOT%Launch-QuickReplyTool.bat" "%TARGET_ROOT%\Launch-QuickReplyTool.bat" >nul
copy /Y "%PACKAGE_ROOT%Upgrade-Portable.bat" "%TARGET_ROOT%\Upgrade-Portable.bat" >nul
copy /Y "%PACKAGE_ROOT%使用说明.txt" "%TARGET_ROOT%\使用说明.txt" >nul
copy /Y "%PACKAGE_ROOT%版本信息.txt" "%TARGET_ROOT%\版本信息.txt" >nul
if exist "%TARGET_ROOT%\Portable-Readme.txt" del /Q "%TARGET_ROOT%\Portable-Readme.txt" >nul 2>&1
if exist "%TARGET_ROOT%\VERSION.txt" del /Q "%TARGET_ROOT%\VERSION.txt" >nul 2>&1

echo.
echo 升级完成。用户数据仍然保存在 %%AppData%%\QuickReplyTool
echo 如果已经开启开机启动或管理员启动，请保持原安装路径不变。
echo.

choice /M "是否立即启动升级后的程序"
if errorlevel 2 (
    exit /b 0
)

start "" "%TARGET_ROOT%\Launch-QuickReplyTool.bat"
