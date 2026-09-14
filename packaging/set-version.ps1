# Версия перед релизом: она лежит в двух местах, и разъехаться им нельзя.
#
# Из callsum\__init__.py её берёт сборка установщика — эта версия попадает
# в релиз, в имя пакета и в проверку обновлений. В Callsum.App.csproj она нужна
# при сборке из исходников: оттуда её читает вкладка «О программе». Если числа
# разойдутся, окно будет называть не ту версию, которую поставили.
#
#   packaging\set-version.ps1 1.1.0
#   packaging\set-version.ps1 1.1.0-rc.1
#
# Дальше версия никуда не подставляется руками: build-app.ps1 прочитает её сам.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    # Поставить версию не выше прежней. Обычно это ошибка: Velopack не умеет
    # возвращаться назад, и выпущенный номер уже не переиспользовать.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Версия должна выглядеть как 1.2.3 или 1.2.3-rc.1, а пришло: $Version"
}

# Где искать номер. Скобки в образце — это «что оставить как есть», между ними
# встанет новое число.
$targets = @(
    @{ Path = 'callsum\__init__.py'; Pattern = '(__version__\s*=\s*")([^"]+)(")' },
    @{ Path = 'app\Callsum.App\Callsum.App.csproj'; Pattern = '(<Version>)([^<]+)(</Version>)' }
)

# Сначала читаем все файлы и только потом пишем: остановиться на середине
# значило бы оставить версии разными — ровно то, от чего эта команда бережёт.
$changes = @()
foreach ($target in $targets) {
    $path = Join-Path $root $target.Path
    if (-not (Test-Path $path)) { throw "Не нашёл файл: $path" }

    $text = [System.IO.File]::ReadAllText($path)
    $found = [regex]::Matches($text, $target.Pattern)
    if ($found.Count -ne 1) {
        throw "В $($target.Path) строк с версией: $($found.Count), а нужна ровно одна"
    }

    $changes += @{
        Path = $path
        Name = $target.Path
        Was = $found[0].Groups[2].Value
        Text = [regex]::Replace($text, $target.Pattern, ('${1}' + $Version + '${3}'))
    }
}

# Сравниваются только числа: «1.1.0-rc.1» и «1.1.0» по ним равны, и это
# нормально — предрелиз выходит перед своим релизом.
function Get-Numbers([string]$text) { [version](($text -split '-')[0]) }

$was = $changes[0].Was
if (-not $Force -and (Get-Numbers $Version) -lt (Get-Numbers $was)) {
    throw "Версия $Version ниже нынешней $was. Если это намеренно — добавьте -Force"
}

foreach ($change in $changes) {
    # Пишем без метки кодировки: файлы её не имели, а лишний BOM — это правка
    # первой строки в каждом коммите с версией.
    [System.IO.File]::WriteAllText(
        $change.Path, $change.Text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ('{0,-40} {1} -> {2}' -f $change.Name, $change.Was, $Version)
}

Write-Host ""
if ($Version -match '-') {
    Write-Host "Это предрелиз. На GitHub его нужно пометить как pre-release —"
    Write-Host "иначе он уедет коллегам: обычные обновления берутся из обычных релизов."
} else {
    Write-Host "Дальше: коммит, слияние в main, тег v$Version и packaging\build-app.cmd"
}
