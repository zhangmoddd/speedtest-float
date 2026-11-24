@echo off
rem Deploy launcher: copies the built panel + ball into the project root.
rem
rem KEEP THIS FILE PURE ASCII AND CRLF. cmd decodes .bat bytes with the system
rem code page (GBK on zh-CN), so any Chinese text here shows up as mojibake --
rem and a Chinese file name passed to copy would be created under a wrong name.
rem The real logic lives in deploy.ps1 (UTF-8 with BOM); see its header for why.
rem Full path to PowerShell: it is always there, while PATH may be stripped.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1"
exit /b %ERRORLEVEL%
