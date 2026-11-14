$ErrorActionPreference = 'Stop'
Write-Host '=================================================' -ForegroundColor Cyan
Write-Host '  Обновление и запуск Desktop Island (Admin)     ' -ForegroundColor Cyan
Write-Host '=================================================' -ForegroundColor Cyan

Get-Process DesktopIsland -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

Write-Host 'Публикация новой сборки...' -ForegroundColor Yellow
dotnet publish 'E:\Applications\DesktopIsland\Source\DesktopIsland.csproj' -c Release -o 'E:\Applications\DesktopIsland\App' --no-self-contained -r win-x64
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Ошибка публикации!' -ForegroundColor Red
    Read-Host 'Нажмите Enter для выхода'
    exit 1
}

$exe = 'E:\Applications\DesktopIsland\App\DesktopIsland.exe'
Write-Host 'Запуск обновлённого Desktop Island...' -ForegroundColor Green
Start-Process -FilePath $exe -WorkingDirectory 'E:\Applications\DesktopIsland\App'
Start-Sleep -Seconds 2
Get-Process DesktopIsland -ErrorAction SilentlyContinue | Select-Object Id, StartTime, Responding | Format-Table -AutoSize
