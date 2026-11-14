@echo off
chcp 65001 >nul
title Обновление Desktop Island

:: =====================================================================
:: Прямая ссылка на последний релиз DesktopIsland.zip в GitHub Releases:
:: Если рядом с этим батником уже лежит папка App\DesktopIsland.exe,
:: обновление установится из неё автоматически даже без скачивания.
:: =====================================================================
set "UPDATE_URL=https://github.com/Cat0rg/DesktopIsland-/releases/latest/download/DesktopIsland.zip"

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c \"\"%~f0\" \"%LOCALAPPDATA%\" \"%USERDOMAIN%\%USERNAME%\"\"' -WorkingDirectory '%~dp0' -Verb RunAs"
    exit /b
)

set "BASE_LOCAL=%~1"
if "%BASE_LOCAL%"=="" set "BASE_LOCAL=%LOCALAPPDATA%"
set "TARGET_USER=%~2"
if "%TARGET_USER%"=="" set "TARGET_USER=%USERDOMAIN%\%USERNAME%"

set "INSTALL_ROOT=%BASE_LOCAL%\DesktopIsland"
set "APP_DIR=%INSTALL_ROOT%\App"
set "EXE_PATH=%APP_DIR%\DesktopIsland.exe"
set "WORK_TMP=%TEMP%\DesktopIsland_Update"

echo.
echo ===================================================
echo   Desktop Island - Автообновление
echo ===================================================
echo.

set "SRC_APP="
if exist "%~dp0App\DesktopIsland.exe" (
    set "SRC_APP=%~dp0App"
    echo [1/5] Найдена локальная сборка рядом со скриптом.
    goto :apply_update
)

if exist "%~dp0DesktopIsland.exe" (
    if not exist "%WORK_TMP%" mkdir "%WORK_TMP%"
    copy /Y "%~dp0DesktopIsland.exe" "%WORK_TMP%\DesktopIsland.exe" >nul
    set "SRC_APP=%WORK_TMP%"
    echo [1/5] Найден локальный файл DesktopIsland.exe рядом со скриптом.
    goto :apply_update
)

if "%UPDATE_URL%"=="" (
    echo [ОШИБКА] Не указана ссылка UPDATE_URL внутри батника и нет локальной папки App\.
    echo.
    echo Как отправлять обновление другу одним этим файлом:
    echo   1. Загрузите новый архив DesktopIsland_Installer_v1.3.zip ^(или .exe^) в GitHub Releases / облако.
    echo   2. Откройте Update_DesktopIsland.bat в Блокноте и впишите ссылку в строку:
    echo      set "UPDATE_URL=https://..."
    echo   3. Скиньте этот .bat другу — при запуске он сам скачает обнову,
    echo      сохранит все настройки и запустит островок от администратора.
    echo.
    pause
    exit /b 1
)

echo [1/5] Скачиваю обновление...
if exist "%WORK_TMP%" rmdir /S /Q "%WORK_TMP%" >nul 2>&1
mkdir "%WORK_TMP%" >nul 2>&1

set "DL_FILE=%WORK_TMP%\update_payload"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $url = '%UPDATE_URL%'; $out = '%DL_FILE%'; Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing; if ((Get-Item $out).Length -lt 1048576) { throw 'Скачанный файл слишком мал (проверьте прямую ссылку).' }; $bytes = [System.IO.File]::ReadAllBytes($out) | Select-Object -First 2; $sig = [System.Text.Encoding]::ASCII.GetString($bytes); if ($sig -eq 'PK') { Rename-Item $out ($out + '.zip'); Expand-Archive ($out + '.zip') -DestinationPath '%WORK_TMP%\unzipped' -Force } else { Rename-Item $out '%WORK_TMP%\DesktopIsland.exe' }"
if %errorlevel% neq 0 (
    echo [ОШИБКА] Не удалось скачать или распаковать обновление. Ваши текущие файлы не затронуты.
    rmdir /S /Q "%WORK_TMP%" >nul 2>&1
    pause
    exit /b 1
)

if exist "%WORK_TMP%\unzipped\App\DesktopIsland.exe" (
    set "SRC_APP=%WORK_TMP%\unzipped\App"
) else if exist "%WORK_TMP%\unzipped\DesktopIsland.exe" (
    set "SRC_APP=%WORK_TMP%\unzipped"
) else if exist "%WORK_TMP%\DesktopIsland.exe" (
    set "SRC_APP=%WORK_TMP%"
) else (
    for /R "%WORK_TMP%\unzipped" %%F in (DesktopIsland.exe) do (
        set "SRC_APP=%%~dpF"
        goto :found_nested
    )
)
:found_nested
if "%SRC_APP%"=="" (
    echo [ОШИБКА] В скачанном обновлении не найден DesktopIsland.exe.
    rmdir /S /Q "%WORK_TMP%" >nul 2>&1
    pause
    exit /b 1
)

:apply_update
echo [2/5] Закрываю текущий Desktop Island...
taskkill /F /IM DesktopIsland.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo [3/5] Обновляю программу (настройки Config\ сохраняются)...
if not exist "%APP_DIR%" mkdir "%APP_DIR%"
xcopy /E /Y /I /Q "%SRC_APP%\*" "%APP_DIR%\" >nul

echo [4/5] Проверяю автозапуск от администратора (без подтверждения UAC)...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "DesktopIsland" /f >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$exe = '%EXE_PATH%'; $dir = '%APP_DIR%'; $user = '%TARGET_USER%'; $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dir; $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user; $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest; $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; Register-ScheduledTask -TaskName 'DesktopIsland_AutoStart' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null" >nul 2>&1
if %errorlevel% neq 0 (
    schtasks /Create /TN "DesktopIsland_AutoStart" /TR "\"%EXE_PATH%\"" /SC ONLOGON /RL HIGHEST /F >nul
)

echo [5/5] Запускаю обновлённый Desktop Island с правами администратора...
schtasks /Run /TN "DesktopIsland_AutoStart" >nul 2>&1
timeout /t 2 /nobreak >nul
tasklist /FI "IMAGENAME eq DesktopIsland.exe" 2>nul | find /I "DesktopIsland.exe" >nul
if %errorlevel% neq 0 (
    start "" /D "%APP_DIR%" "%EXE_PATH%"
)

if exist "%WORK_TMP%" rmdir /S /Q "%WORK_TMP%" >nul 2>&1

echo.
echo ===================================================
echo   Обновление успешно установлено!
echo ===================================================
echo.
timeout /t 3 /nobreak >nul
exit /b 0
