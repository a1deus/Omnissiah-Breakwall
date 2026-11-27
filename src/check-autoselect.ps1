param([string]$ResultsDirectory = (Join-Path $env:TEMP ('breakwall-autoselect-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[IO.Directory]::CreateDirectory($ResultsDirectory) | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $ResultsDirectory 'AutoChecks.exe'
$fixture = Join-Path $ResultsDirectory 'CurlFixture.exe'
& $compiler /nologo /target:exe "/out:$fixture" (Join-Path $PSScriptRoot 'checks/CurlFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Не удалось собрать локальную имитацию curl.'}
$sources = 'Program.cs','Engine.cs','DsStrategy.cs','Zapret2.cs','Warp.cs','WindowsStartup.cs','ReleaseUpdate.cs','AutoSelect.cs','AutoSelectPage.cs','DarktideUdp.cs','SpaceMarineUdp.cs','SpaceMarineDiscovery.cs','SpaceMarineUdpOwnership.cs','checks/AutoChecks.cs','checks/UdpChecks.cs','checks/SpaceMarineChecks.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe /platform:x64 /main:AutoCheckMain /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/resource:$PSScriptRoot/assets/omnissiah.png,omnissiah.png" "/resource:$root/profiles/ds-strategies.args,ds-strategies.args" "/out:$output" $sources
if($LASTEXITCODE -ne 0){throw 'Не удалось собрать проверки автоподбора.'}
& $output $root $ResultsDirectory
if($LASTEXITCODE -ne 0){throw 'Проверки автоподбора завершились с ошибкой.'}
'Результаты: ' + $ResultsDirectory
