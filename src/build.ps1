param(
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?\z')]
    [string]$Version = '1.4.0'
)

$ErrorActionPreference = 'Stop'
if ($Version.Split('.').Length -eq 3) {
    $Version += '.0'
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path (Split-Path $PSScriptRoot -Parent) 'Omnissiah Breakwall.exe'
if (Test-Path -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'portable')) {
    $output = Join-Path (Split-Path $PSScriptRoot -Parent) 'portable\Omnissiah Breakwall.exe'
}
$sources = 'Program.cs','Engine.cs','DsStrategy.cs','Warp.cs','WindowsStartup.cs','ReleaseUpdate.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
$dsProfiles = Join-Path (Split-Path $output -Parent) 'profiles\ds-strategies.args'
$versionSource = Join-Path ([IO.Path]::GetTempPath()) ("breakwall-{0}.cs" -f [guid]::NewGuid())
try {
    @"
[assembly: System.Reflection.AssemblyVersion("$Version")]
[assembly: System.Reflection.AssemblyFileVersion("$Version")]
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
"@ | Set-Content -LiteralPath $versionSource -Encoding UTF8
    & $compiler /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$PSScriptRoot\assets\omnissiah.ico" "/resource:$PSScriptRoot\assets\omnissiah.png,omnissiah.png" "/resource:$dsProfiles,ds-strategies.args" "/out:$output" $sources $versionSource
    if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
} finally {
    if (Test-Path -LiteralPath $versionSource) {
        Remove-Item -LiteralPath $versionSource
    }
}
