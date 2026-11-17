@echo off
setlocal
cd /d "%~dp0"

echo.
echo   把新编译的浮光和球放到一起
echo   ==============================
echo.

set "SRC=app\src-tauri\target\release\fuguang.exe"
if not exist "%SRC%" (
    echo   [没有编译过] 找不到 %SRC%
    echo   先双击 app\2-重新编译.bat
    echo.
    pause
    exit /b 1
)

rem 先复制浮光，再复制球。任何一个失败都只有一个原因：
rem 对应的程序还开着，文件被锁住了。
rem 不去用 tasklist 判断 —— 中文进程名在不同代码页下的输出编码不一样，
rem 判错了会给出「没在运行」的假结论，然后用户看到一句莫名其妙的失败。
copy /y "%SRC%" "浮光面板.exe" >nul 2>&1
if errorlevel 1 goto busy

set "BALL=ball\dist\NetSpeedBall.exe"
if exist "%BALL%" (
    copy /y "%BALL%" "网络测速球.exe" >nul 2>&1
    if errorlevel 1 goto busy
    echo   [完成] 浮光和球都是最新的了。
) else (
    echo   [完成] 浮光是最新的了。球没有重新编译，保持原样。
)

echo.
echo   现在双击「网络测速球.exe」就能用了。
echo.
pause
exit /b 0

:busy
echo   [停一下] 还有程序开着，文件被锁住了，覆盖不了。
echo.
echo   请把这两个都退掉：
echo     1. 右下角托盘的浮光图标，右键，选「退出浮光」
echo     2. 右键悬浮球，选「退出」
echo.
echo   然后重新双击本文件。
echo.
pause
exit /b 1
