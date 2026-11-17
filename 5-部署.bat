@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo.
echo   把新编译的浮光放到球旁边
echo   ============================
echo.

set "SRC=app\src-tauri\target\release\fuguang.exe"
if not exist "%SRC%" (
    echo   [没有编译过] 找不到 %SRC%
    echo   先双击 app\2-重新编译.bat
    echo.
    pause
    exit /b 1
)

rem 直接试着覆盖。成功就是成功，失败十有八九是浮光还开着、文件被锁住 ——
rem 不去用 tasklist 判断，是因为中文进程名在不同代码页下的输出编码不一样，
rem 判错了会给出"没在运行"的假结论，然后用户看到一句莫名其妙的复制失败。
copy /y "%SRC%" "浮光面板.exe" >nul 2>&1
if errorlevel 1 goto busy

echo   [完成] 浮光面板.exe 已经换成最新的了。
echo.
echo   现在双击「网络测速球.exe」，右键球 -^> 打开浮光面板。
echo.
pause
exit /b 0

:busy
echo   [停一下] 浮光正开着，文件被它锁住了，覆盖不了。
echo.
echo   1. 看右下角托盘，找到浮光的图标
echo   2. 右键它 -^> 退出
echo   3. 再双击一次本文件
echo.
pause
exit /b 1
