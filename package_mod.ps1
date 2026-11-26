# Скрипт автоматической сборки и упаковки модульного релиза OurSummerCar
param(
    [string]$VersionTag = "latest",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = "c:\me\Antigravity projects\OurSummerCar"
$slnPath = Join-Path $repoRoot "src\OurSummerCar.sln"

if (-not $SkipBuild) {
    Write-Host "=== 1. Сборка модульного решения OurSummerCar.sln (Release) ===" -ForegroundColor Cyan
    dotnet build $slnPath -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Ошибка компиляции!" -ForegroundColor Red
        exit 1
    }
}

$releaseDir = Join-Path $repoRoot "Release\OurSummerCar_Release"
if (Test-Path $releaseDir) {
    Remove-Item -Recurse -Force $releaseDir
}

$modsOut = Join-Path $releaseDir "Mods"
$coreOut = Join-Path $modsOut "Assets\OurSummerCar\Core"
$modulesOut = Join-Path $modsOut "Assets\OurSummerCar\Modules"
$assetsOut = Join-Path $modsOut "Assets\OurSummerCar"

New-Item -ItemType Directory -Path $coreOut -Force | Out-Null
New-Item -ItemType Directory -Path $modulesOut -Force | Out-Null

Write-Host "=== 2. Копирование Bootstrap, OSC.Core, модулей и 3D-ассетов ===" -ForegroundColor Cyan
$bootstrapDll = Join-Path $repoRoot "src\OSC.Bootstrap\bin\Release\net35\OurSummerCar.dll"
$coreDll = Join-Path $repoRoot "src\OSC.Core\bin\Release\net35\OSC.Core.dll"
Copy-Item -LiteralPath $bootstrapDll -Destination (Join-Path $modsOut "OurSummerCar.dll") -Force
Copy-Item -LiteralPath $coreDll -Destination (Join-Path $coreOut "OSC.Core.dll") -Force

$moduleDlls = Get-ChildItem -LiteralPath (Join-Path $repoRoot "src") -Directory -Filter "OSC.Module.*" | ForEach-Object {
    Get-ChildItem -LiteralPath (Join-Path $_.FullName "bin\Release\net35") -Filter "OSC.Module.*.dll" -ErrorAction SilentlyContinue
}
if (-not $moduleDlls -or $moduleDlls.Count -eq 0) {
    Write-Host "Не найдены скомпилированные модули OSC.Module.*.dll!" -ForegroundColor Red
    exit 1
}
foreach ($dll in $moduleDlls) {
    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $modulesOut $dll.Name) -Force
    Write-Host "  + $($dll.Name)" -ForegroundColor Gray
}

foreach ($assetName in @("clothes.unity3d", "wreckmp.unity3d")) {
    $assetSrc = Join-Path $repoRoot "Assets\$assetName"
    if (Test-Path -LiteralPath $assetSrc) {
        Copy-Item -LiteralPath $assetSrc -Destination (Join-Path $assetsOut $assetName) -Force
        Write-Host "  + $assetName" -ForegroundColor Gray
    }
}

$installBat = @'
@echo off
chcp 65001 >nul
echo ===================================================
echo     Установка OurSummerCar (%VERSION_LABEL%)
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
copy /Y "Mods\OurSummerCar.dll" "%TARGET_MODS%\OurSummerCar.dll" >nul

if not exist "%TARGET_MODS%\Assets\OurSummerCar\Core" mkdir "%TARGET_MODS%\Assets\OurSummerCar\Core"
if not exist "%TARGET_MODS%\Assets\OurSummerCar\Modules" mkdir "%TARGET_MODS%\Assets\OurSummerCar\Modules"

echo [*] Копирование OSC.Core.dll...
copy /Y "Mods\Assets\OurSummerCar\Core\OSC.Core.dll" "%TARGET_MODS%\Assets\OurSummerCar\Core\OSC.Core.dll" >nul

echo [*] Копирование модулей синхронизации (OSC.Module.*.dll)...
copy /Y "Mods\Assets\OurSummerCar\Modules\*.dll" "%TARGET_MODS%\Assets\OurSummerCar\Modules\" >nul

echo [*] Копирование 3D-ассетов...
if exist "Mods\Assets\OurSummerCar\clothes.unity3d" copy /Y "Mods\Assets\OurSummerCar\clothes.unity3d" "%TARGET_MODS%\Assets\OurSummerCar\clothes.unity3d" >nul
if exist "Mods\Assets\OurSummerCar\wreckmp.unity3d" copy /Y "Mods\Assets\OurSummerCar\wreckmp.unity3d" "%TARGET_MODS%\Assets\OurSummerCar\wreckmp.unity3d" >nul

echo ===================================================
echo  [V] OurSummerCar успешно установлен!
echo  Убедитесь, что у хоста и клиента одинаковая версия.
echo ===================================================
pause
'@
$installBat = $installBat.Replace("%VERSION_LABEL%", $VersionTag)
[System.IO.File]::WriteAllText((Join-Path $releaseDir "install.bat"), $installBat, [System.Text.Encoding]::UTF8)

$readme = @"
OurSummerCar ($VersionTag) - Инструкция по установке:
1. Запустите install.bat — скрипт автоматически скопирует:
   - Mods\OurSummerCar.dll
   - Mods\Assets\OurSummerCar\Core\OSC.Core.dll
   - Mods\Assets\OurSummerCar\Modules\OSC.Module.*.dll
   - Mods\Assets\OurSummerCar\clothes.unity3d (и wreckmp.unity3d при наличии)
   Либо вручную скопируйте папку 'Mods' в Documents\MySummerCar\.

2. Запустите My Summer Car (MSCLoader).
3. В игре нажмите F10, введите никнейм и IP друга (например, Radmin VPN 26.x.x.x), затем запустите сервер (Хост) или подключитесь (Клиент).
"@
[System.IO.File]::WriteAllText((Join-Path $releaseDir "README.txt"), $readme, [System.Text.Encoding]::UTF8)

Write-Host "=== 3. Создание ZIP-архивов ($VersionTag и OurSummerCar-Mod.zip) ===" -ForegroundColor Cyan
$backupsDir = Join-Path $repoRoot "Release\backups"
New-Item -ItemType Directory -Path $backupsDir -Force | Out-Null

$backupZip = Join-Path $backupsDir "OurSummerCar-$VersionTag.zip"
$rootZip = Join-Path $repoRoot "OurSummerCar-Mod.zip"

if (Test-Path -LiteralPath $backupZip) { Remove-Item -LiteralPath $backupZip -Force }
if (Test-Path -LiteralPath $rootZip) { Remove-Item -LiteralPath $rootZip -Force }

Compress-Archive -Path "$releaseDir\*" -DestinationPath $backupZip -Force
Copy-Item -LiteralPath $backupZip -Destination $rootZip -Force

Write-Host "Готово:" -ForegroundColor Green
Write-Host "  Backup : $backupZip ($((Get-Item $backupZip).Length) bytes)" -ForegroundColor Yellow
Write-Host "  Current: $rootZip ($((Get-Item $rootZip).Length) bytes)" -ForegroundColor Yellow
