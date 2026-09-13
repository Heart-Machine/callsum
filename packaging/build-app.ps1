# Сборка установщика приложения через Velopack.
#
# Установщик собирает вместе две части: само приложение на WinUI и ядро
# обработки, собранное packaging\build-core.cmd. Ядро кладётся в подпапку core —
# именно там приложение его и ищет рядом с собой.
#
# Версия берётся из callsum/__init__.py: она одна на ядро и на приложение,
# чтобы в релизе нельзя было перепутать, что с чем совместимо.

param(
    [string]$Version,
    [string]$Configuration = 'Release',
    # Забыть прошлые релизы и собрать ту же версию заново. Нужно при отладке:
    # обычно в dist\releases копятся версии, и по ним Velopack считает разницу
    # для обновлений, поэтому просто так их сносить нельзя.
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'dist\app'
$core = Join-Path $root 'dist\callsum-core'
$releases = Join-Path $root 'dist\releases'

if (-not $Version) {
    $source = Get-Content (Join-Path $root 'callsum\__init__.py') -Raw
    if ($source -notmatch '__version__\s*=\s*"([^"]+)"') {
        throw "Не нашёл __version__ в callsum\__init__.py"
    }
    $Version = $Matches[1]
}

if (-not (Test-Path (Join-Path $core 'callsum-core.exe'))) {
    throw "Сначала соберите ядро: packaging\build-core.cmd (жду его в $core)"
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "Не найден vpk. Поставьте его один раз: dotnet tool install -g vpk"
}

# В PATH может стоять dotnet без SDK — так бывает, когда рядом установлены
# системный и пользовательский. Тогда берём тот, что в профиле. Проверяем по
# выводу, а не по коду возврата: без SDK `--list-sdks` молча печатает пустоту
# и завершается успешно.
$dotnet = 'dotnet'
if (-not (& $dotnet --list-sdks 2>$null)) {
    $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet) -or -not (& $dotnet --list-sdks 2>$null)) {
        throw "Не найден .NET SDK. Поставьте его: https://dot.net"
    }
}

Write-Host "Собираю callsum $Version"

# Публикация начинается с чистой папки: иначе в установщик попадут файлы
# прошлых сборок, а найти потом лишний мегабайт будет негде.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if ($Clean -and (Test-Path $releases)) { Remove-Item $releases -Recurse -Force }

# Пакуется то, что собрала обычная сборка, а не `dotnet publish`: публикация
# неупакованного приложения WinUI не переносит скомпилированный XAML (.xbf) и
# таблицу ресурсов (.pri). Собранное из неё приложение молча закрывается сразу
# после запуска — без окна, без ошибки и без записи в журнале Windows.
& $dotnet build (Join-Path $root 'app\Callsum.App\Callsum.App.csproj') `
    -c $Configuration -p:Platform=x64 -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Не собралось приложение" }

$built = Join-Path $root "app\Callsum.App\bin\x64\$Configuration\net9.0-windows10.0.19041.0\win-x64"
if (-not (Test-Path (Join-Path $built 'Callsum.App.exe'))) {
    throw "Не нашёл собранное приложение в $built"
}

Copy-Item $built $publish -Recurse
# Отладочные символы пользователю не нужны — это десятки мегабайт в установщике.
Get-ChildItem $publish -Filter '*.pdb' -Recurse | Remove-Item -Force

Write-Host "Кладу ядро рядом с приложением"
Copy-Item $core (Join-Path $publish 'core') -Recurse

vpk pack `
    --packId callsum `
    --packVersion $Version `
    --packDir $publish `
    --mainExe Callsum.App.exe `
    --packTitle callsum `
    --packAuthors 'Heart-Machine' `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "Не собрался установщик" }

Write-Host ""
Write-Host "Готово: $releases"
Get-ChildItem $releases | ForEach-Object {
    '{0,-40} {1,8:N0} МБ' -f $_.Name, ($_.Length / 1MB)
}
