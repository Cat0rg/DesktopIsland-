@echo off
chcp 65001 >nul
title Полное удаление Desktop Island

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c \"\"%~f0\" \"%LOCALAPPDATA%\"\"' -WorkingDirectory '%~dp0' -Verb RunAs"
    exit /b
)

set "BASE_LOCAL=%~1"
if "%BASE_LOCAL%"=="" set "BASE_LOCAL=%LOCALAPPDATA%"
set "INSTALL_ROOT=%BASE_LOCAL%\DesktopIsland"

echo.
echo ===================================================
echo   Полное удаление Desktop Island
echo ===================================================
echo.

echo [1/5] Закрываю процесс Desktop Island...
taskkill /F /IM DesktopIsland.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo [2/5] Удаляю из автозапуска Windows...
schtasks /Delete /TN "DesktopIsland_AutoStart" /F >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "DesktopIsland" /f >nul 2>&1

echo [3/5] Удаляю ярлыки с Рабочего стола и из меню Пуск...
del /F /Q "%USERPROFILE%\Desktop\Desktop Island.lnk" >nul 2>&1
del /F /Q "%OneDrive%\Desktop\Desktop Island.lnk" >nul 2>&1
del /F /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Desktop Island.lnk" >nul 2>&1

echo [4/5] Очищаю папку пользователя %INSTALL_ROOT% ...
if exist "%INSTALL_ROOT%" (
    rmdir /S /Q "%INSTALL_ROOT%" >nul 2>&1
)

echo [5/5] Проверяю локальную папку запуска...
if exist "%~dp0App\DesktopIsland.exe" (
    rmdir /S /Q "%~dp0App" >nul 2>&1
)
if exist "%~dp0Config" (
    rmdir /S /Q "%~dp0Config" >nul 2>&1
)
if exist "%~dp0Logs" (
    rmdir /S /Q "%~dp0Logs" >nul 2>&1
)

echo.
echo ===================================================
echo   Desktop Island полностью удалён с компьютера!
echo ===================================================
echo.
pause
exit /b 0
