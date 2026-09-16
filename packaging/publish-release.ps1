# Собрать и опубликовать релиз на GitHub.
#
# Запускается только после слияния PR с номером версии в main. Проверки ниже
# намеренно не дают выпустить то, что лежит в чужой ветке или ещё не сохранено.
#
#   packaging\publish-release.ps1 1.1.0
#   packaging\publish-release.ps1 1.2.0-rc.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# vpk обычно ставится как пользовательский dotnet tool, а SDK — рядом. Не
# заставляем перед каждым выпуском вручную вспоминать оба пути.
$env:Path = "$env:USERPROFILE\.dotnet;$env:USERPROFILE\.dotnet\tools;$env:Path"

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Версия должна выглядеть как 1.2.3 или 1.2.3-rc.1, а пришло: $Version"
}

function Invoke-Checked([scriptblock]$Command, [string]$Failure) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw $Failure
    }
}

Push-Location $root
try {
    if ((git branch --show-current) -ne 'main') {
        throw "Выпускать можно только из main. Переключитесь: git switch main"
    }

    if (git status --porcelain) {
        throw "Рабочая папка содержит незакоммиченные изменения. Сначала сохраните или уберите их."
    }

    Invoke-Checked { git pull --ff-only origin main } "Не удалось обновить main"

    $engineSource = Get-Content 'callsum\__init__.py' -Raw
    $appSource = Get-Content 'app\Callsum.App\Callsum.App.csproj' -Raw
    $engineVersion = [regex]::Match($engineSource, '__version__\s*=\s*"([^"]+)"').Groups[1].Value
    $appVersion = [regex]::Match($appSource, '<Version>([^<]+)</Version>').Groups[1].Value
    if ($engineVersion -ne $Version -or $appVersion -ne $Version) {
        throw "main не подготовлен к ${Version}: ядро $engineVersion, приложение $appVersion. Сначала слейте PR с номером версии."
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "Не найден GitHub CLI. Установите его и войдите: gh auth login"
    }

    $tag = "v$Version"
    Invoke-Checked { git fetch origin --tags } "Не удалось получить теги из GitHub"
    $remoteTag = git ls-remote --tags origin "refs/tags/$tag"
    if ($remoteTag) {
        throw "Тег $tag уже опубликован. Номер версии нельзя использовать повторно."
    }

    $releaseDir = Join-Path $root 'dist\releases'
    # В папке остаются старые nupkg: по ним Velopack строит delta-пакет. Убираем
    # только пробную сборку именно этой версии и общие имена её установщика.
    $stale = @(
        "callsum-$Version-full.nupkg",
        "callsum-$Version-delta.nupkg",
        'callsum-win-Portable.zip',
        'callsum-win-Setup.exe',
        'RELEASES',
        'assets.win.json',
        'releases.win.json'
    )
    foreach ($name in $stale) {
        $path = Join-Path $releaseDir $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    Invoke-Checked { cmd /c packaging\build-core.cmd } "Не собралось ядро"
    Invoke-Checked { cmd /c packaging\build-app.cmd } "Не собрался установщик"

    $assets = @(
        (Join-Path $releaseDir 'callsum-win-Setup.exe'),
        (Join-Path $releaseDir 'callsum-win-Portable.zip'),
        (Join-Path $releaseDir "callsum-$Version-full.nupkg"),
        (Join-Path $releaseDir 'RELEASES'),
        (Join-Path $releaseDir 'assets.win.json'),
        (Join-Path $releaseDir 'releases.win.json')
    )
    # Для первого выпуска Velopack не с чем сравнивать и delta-пакет честно
    # не создаёт. Обычные последующие релизы добавляют его автоматически.
    $delta = Join-Path $releaseDir "callsum-$Version-delta.nupkg"
    if (Test-Path -LiteralPath $delta) {
        $assets += $delta
    }
    foreach ($asset in $assets) {
        if (-not (Test-Path -LiteralPath $asset)) {
            throw "Не создан файл релиза: $asset"
        }
    }

    Invoke-Checked { git tag -a $tag -m "callsum $Version" } "Не удалось создать тег $tag"
    Invoke-Checked { git push origin $tag } "Не удалось отправить тег $tag"

    $release = @('release', 'create', $tag) + $assets + @('--title', "callsum $Version", '--generate-notes')
    if ($Version -match '-') {
        $release += '--prerelease'
    }
    Invoke-Checked { & gh @release } "Не удалось опубликовать релиз $tag"
}
finally {
    Pop-Location
}
