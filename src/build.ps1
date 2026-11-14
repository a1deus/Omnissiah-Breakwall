$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path (Split-Path $PSScriptRoot -Parent) 'Omnissiah Breakwall.exe'
if (Test-Path -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'portable')) {
    $output = Join-Path (Split-Path $PSScriptRoot -Parent) 'portable\Omnissiah Breakwall.exe'
}
$sources = 'Program.cs','Engine.cs','Warp.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$PSScriptRoot\assets\omnissiah.ico" "/resource:$PSScriptRoot\assets\omnissiah.png,omnissiah.png" "/out:$output" $sources
if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
