@echo off
chcp 65001 >nul
rem Initial Windows setup step: double-click or run from a command prompt:
rem   install.cmd                      install or update
rem   install.cmd --no-server          skip the first game server
rem   install.cmd --servers-dir D:\vs-servers   game server directory (otherwise prompted on first install)
rem   install.cmd --access internet    allow internet access (default: lan)
rem   install.cmd --access localhost   local access only
rem   install.cmd --port 9000          panel port; also --bind, --allowed-hosts, --secure-cookie, --firewall
rem vs-panel install (Panel.Cli.exe) in this directory performs the remaining steps.
setlocal

net session >nul 2>&1
if errorlevel 1 goto elevate

if not exist "%~dp0Panel.Cli.exe" (
    echo Panel.Cli.exe is missing next to install.cmd: this is not a panel build directory.
    pause
    exit /b 1
)

"%~dp0Panel.Cli.exe" install --from "%~dp0." %*
set code=%errorlevel%
echo.
pause
exit /b %code%

:elevate
rem Administrator privileges are required for services, Program Files and ProgramData.
rem The path and arguments reach PowerShell through the environment, not inside a quoted
rem literal: an apostrophe in either would end the literal and break the command.
set "VS_PANEL_SELF=%~f0"
set "VS_PANEL_ARGS=%*"
if "%~1"=="" (
    powershell -NoProfile -Command "Start-Process -FilePath $env:VS_PANEL_SELF -Verb RunAs"
) else (
    powershell -NoProfile -Command "Start-Process -FilePath $env:VS_PANEL_SELF -ArgumentList $env:VS_PANEL_ARGS -Verb RunAs"
)
exit /b
