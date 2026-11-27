param(
    [ValidateSet('config','dryrun','updater')][string]$Mode = 'config',
    [string]$ResultsDirectory = (Join-Path $env:TEMP ('breakwall150-checks-' + [guid]::NewGuid().ToString('N'))),
    [string]$Package,
    [string]$PreviousExecutable
)
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'updater' -and (!$Package -or !$PreviousExecutable)) {
    throw 'Для проверки обновлятора укажите -Package и -PreviousExecutable.'
}
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
[IO.Directory]::CreateDirectory($ResultsDirectory) | Out-Null
$checks = Join-Path $ResultsDirectory 'Checks.exe'
& $compiler /nologo /target:exe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$checks" (Join-Path $PSScriptRoot 'checks/Checks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверки.' }
& $checks $root $Mode $ResultsDirectory $Package $PreviousExecutable
if ($LASTEXITCODE -ne 0) { throw 'Проверки завершились с ошибкой.' }
'Результаты: ' + $ResultsDirectory
