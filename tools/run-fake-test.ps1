# Интеграционная проверка WukongAutoBench без установленного Benchmark Tool:
# собирает имитатор (tools/FakeBenchmark) в папку с фиктивной установкой бенчмарка и прогоняет утилиту на ней.
# Во время прогона (~1 мин) утилита управляет мышью и клавиатурой.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fake = Join-Path $env:TEMP 'WukongAutoBench-fake'
$config = Join-Path $fake 'b1\Saved\Config\Windows'

Remove-Item $fake -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $config | Out-Null
Copy-Item (Join-Path $root 'tests\WukongAutoBench.Tests\TestData\GameUserSettings.sample.ini') (Join-Path $config 'GameUserSettings.ini')
dotnet publish (Join-Path $root 'tools\FakeBenchmark') -c Release -o $fake -v q
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать имитатор' }

$iniBefore = (Get-FileHash (Join-Path $config 'GameUserSettings.ini')).Hash
dotnet run --project (Join-Path $root 'src\WukongAutoBench') -c Release -- `
    --game-dir $fake --menu-delay 3 --cooldown 2 --timeout 4 --no-pause --out (Join-Path $fake 'results')
$exit = $LASTEXITCODE
$iniAfter = (Get-FileHash (Join-Path $config 'GameUserSettings.ini')).Hash

Write-Host ''
Write-Host "Код выхода утилиты: $exit"
Write-Host "GameUserSettings.ini восстановлен: $($iniBefore -eq $iniAfter)"
Write-Host "Лог имитатора (что он получил): $(Join-Path $fake 'fake.log')"
Write-Host "Отчёт: $(Join-Path $fake 'results')"
