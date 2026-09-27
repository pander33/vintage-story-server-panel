@echo off
chcp 65001 >nul
rem Первый шаг установки панели на Windows — двойной щелчок или из командной строки:
rem   install.cmd                      установить или обновить
rem   install.cmd --no-server          не создавать первый игровой сервер
rem   install.cmd --servers-dir D:\vs-servers   каталог игровых серверов (иначе спросит при первой установке)
rem   install.cmd --access internet    панель открыта и из интернета (по умолчанию lan — локальная сеть)
rem   install.cmd --access localhost   панель только на этой машине
rem   install.cmd --port 9000          порт панели; также --bind, --allowed-hosts, --secure-cookie, --firewall
rem Всё остальное делает vs-panel install (Panel.Cli.exe) из этого же каталога.
setlocal

net session >nul 2>&1
if errorlevel 1 goto elevate

if not exist "%~dp0Panel.Cli.exe" (
    echo Рядом с install.cmd нет Panel.Cli.exe: это не каталог сборки панели.
    pause
    exit /b 1
)

"%~dp0Panel.Cli.exe" install --from "%~dp0." %*
set code=%errorlevel%
echo.
pause
exit /b %code%

:elevate
rem Нужны права администратора: службы, Program Files, права на ProgramData.
if "%~1"=="" (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
) else (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
)
exit /b
