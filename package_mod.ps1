# Скрипт автоматической сборки и упаковки релиза OurSummerCar для отправки другу
$ErrorActionPreference = "Stop"

Write-Host "=== 1. Компиляция OurSummerCar.dll ===" -ForegroundColor Cyan
dotnet build "c:\me\Antigravity projects\OurSummerCar\OurSummerCar.csproj" -c Debug
if ($LASTEXITCODE -ne 0) {
    Write-Host "Ошибка компиляции!" -ForegroundColor Red
    exit 1
}

$releaseDir = "c:\me\Antigravity projects\OurSummerCar\Release\OurSummerCar_Release"
if (Test-Path $releaseDir) {
    Remove-Item -Recurse -Force $releaseDir
}

New-Item -ItemType Directory -Path "$releaseDir\Mods\Assets\OurSummerCar" -Force | Out-Null

Write-Host "=== 2. Копирование файлов ===" -ForegroundColor Cyan
Copy-Item "c:\me\Antigravity projects\OurSummerCar\bin\Debug\net35\OurSummerCar.dll" "$releaseDir\Mods\OurSummerCar.dll" -Force
Copy-Item "c:\me\Antigravity projects\OurSummerCar\Assets\clothes.unity3d" "$releaseDir\Mods\Assets\OurSummerCar\clothes.unity3d" -Force

# Создание батника автоустановки для друга
$installBat = @'
@echo off
chcp 65001 >nul
echo ===================================================
echo     Установка OurSummerCar (Кооператив для MSC)
echo ===================================================

set "TARGET_MODS=%USERPROFILE%\Documents\MySummerCar\Mods"

if not exist "%TARGET_MODS%" (
    echo [!] Папка %TARGET_MODS% не найдена.
    echo Попытка поиска в папке Steam...
    set "TARGET_MODS=C:\Program Files (x86)\Steam\steamapps\common\My Summer Car\Mods"
)

if not exist "%TARGET_MODS%" (
    echo [X] Папка Mods не найдена! Убедитесь, что установлен MSCLoader.
    pause
    exit /b 1
)

echo [*] Копирование OurSummerCar.dll в: %TARGET_MODS%
copy /Y "Mods\OurSummerCar.dll" "%TARGET_MODS%\" >nul

echo [*] Создание папки ассетов: %TARGET_MODS%\Assets\OurSummerCar
if not exist "%TARGET_MODS%\Assets\OurSummerCar" mkdir "%TARGET_MODS%\Assets\OurSummerCar"

echo [*] Копирование 3D-моделей clothes.unity3d...
copy /Y "Mods\Assets\OurSummerCar\clothes.unity3d" "%TARGET_MODS%\Assets\OurSummerCar\" >nul

echo ===================================================
echo  [V] Мод и 3D-модели успешно установлены!
echo  Теперь можно запускать игру и подключаться через Radmin VPN.
echo ===================================================
pause
'@

[System.IO.File]::WriteAllText("$releaseDir\install.bat", $installBat, [System.Text.Encoding]::UTF8)

# Readme для друга
$readme = @"
Our Summer Car - Инструкция по установке:
1. Запустите install.bat от имени пользователя (он сам скопирует DLL и 3D-модели в нужные папки MSCLoader).
   ИЛИ вручную скопируйте папку 'Mods' в свои Мои Документы:
   Documents\MySummerCar\Mods\

2. Запустите My Summer Car.
3. В игре нажмите F10, введите ваш Никнейм, IP друга из Radmin VPN (26.x.x.x) и нажмите 'Подключиться к другу'.
"@
[System.IO.File]::WriteAllText("$releaseDir\README.txt", $readme, [System.Text.Encoding]::UTF8)

Write-Host "=== 3. Создание ZIP-архива для отправки другу ===" -ForegroundColor Cyan
$zipPath = "c:\me\Antigravity projects\OurSummerCar\Release\OurSummerCar_Release.zip"
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

Compress-Archive -Path "$releaseDir\*" -DestinationPath $zipPath -Force

Write-Host "===================================================" -ForegroundColor Green
Write-Host "Готово! Архив для друга создан:" -ForegroundColor Green
Write-Host $zipPath -ForegroundColor Yellow
Write-Host "===================================================" -ForegroundColor Green
