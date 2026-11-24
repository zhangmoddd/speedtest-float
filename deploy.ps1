# 测速浮光 · 部署：把编译好的两个程序复制到项目根目录，之后双击「网络测速球.exe」就能用。
#
# 入口是同目录的 5-部署.bat（双击那个），这个文件是它调用的。
#
# 为什么逻辑放在 .ps1 而不是直接写在 .bat 里：
#   cmd 按**系统代码页**（中文 Windows 是 GBK）读取 .bat 的字节，而 Git 和
#   现代编辑器保存的是 UTF-8。于是 .bat 里一旦出现中文，双击运行时提示全是乱码；
#   更糟的是 "浮光面板.exe" 这个文件名也会被解错 —— 实测在代码页 936 的控制台里，
#   UTF-8 的「浮光面板.exe」会被 copy 成「娴哩厜闈㈡澘.exe」，球按名字找不到面板，
#   右键「打开浮光面板」就失效了。
#   所以 .bat 只留纯 ASCII 的几行，真正的事交给 PowerShell。
#   （本文件必须保存为 **UTF-8 带 BOM**：Windows 自带的 PowerShell 5.1 只认 BOM，
#    没有 BOM 它会按 GBK 读，中文又会乱一次。）
#
# 同一条规矩的来源：app/开发笔记.md「踩过的坑」第 9 条。
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$panelSrc = Join-Path $root "app\src-tauri\target\release\fuguang.exe"
$ballSrc  = Join-Path $root "ball\dist\NetSpeedBall.exe"
$panelDst = Join-Path $root "浮光面板.exe"
$ballDst  = Join-Path $root "网络测速球.exe"

function Stop-Deploy([string]$message) {
    Write-Host ""
    Write-Host $message
    Write-Host ""
    [void](Read-Host "  按回车键关闭这个窗口")
    exit 1
}

Write-Host ""
Write-Host "  正在把编译好的两个程序放到一起"
Write-Host "  =============================="
Write-Host ""

if (-not (Test-Path -LiteralPath $panelSrc)) {
    Stop-Deploy "  [没有编译过] 找不到 $panelSrc`r`n  请先双击 app\2-重新编译.bat"
}

# 先复制面板，再复制球：任何一步失败都只动到一个原文件
# （两者互不依赖，面板被占着时球照样能更新）。
#
# 不用 tasklist 判断「程序是否在运行」：进程名在不同代码页下编码不一样，
# 写死的判断反而会误判。直接复制，被占住就报错，再告诉用户怎么解除。
if ($PSCmdlet.ShouldProcess($panelDst, "复制面板程序")) {
    try {
        Copy-Item -LiteralPath $panelSrc -Destination $panelDst -Force
    } catch {
        Stop-Deploy "  [停一下] 面板程序正在运行，文件被占着，不能覆盖。`r`n`r`n  请先退出面板：`r`n    1. 右键屏幕上的测速球，菜单里选「退出」`r`n    2. 右键任务栏托盘里的图标，选「退出」`r`n`r`n  然后再双击 5-部署.bat。`r`n`r`n  详细信息：$($_.Exception.Message)"
    }
}

if (Test-Path -LiteralPath $ballSrc) {
    if ($PSCmdlet.ShouldProcess($ballDst, "复制测速球")) {
        try {
            Copy-Item -LiteralPath $ballSrc -Destination $ballDst -Force
            Write-Host "  [完成] 面板和测速球都已更新。"
        } catch {
            Stop-Deploy "  [停一下] 测速球正在运行，文件被占着，不能覆盖。`r`n`r`n  请右键任务栏托盘里的图标，选「退出」，然后再双击 5-部署.bat。`r`n`r`n  详细信息：$($_.Exception.Message)"
        }
    }
} else {
    Write-Host "  [完成] 面板已更新。测速球没有新编译过，沿用原文件。"
}

Write-Host ""
Write-Host "  现在可以双击 网络测速球.exe，启动测速球和面板。"
Write-Host ""
[void](Read-Host "  按回车键关闭这个窗口")
exit 0
