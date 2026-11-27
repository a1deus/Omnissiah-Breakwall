param(
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+\z')]
    [string]$Version = '1.5.1',
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1') -Version $Version
if (Test-Path -LiteralPath (Join-Path $root 'engine/winws.exe')) { throw 'В сборке остался прежний движок winws.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$archivePath = Join-Path $OutputDirectory "Omnissiah-Breakwall-v$Version-win-x64.zip"
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
$stream = [IO.File]::Create($archivePath)
$archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @('Omnissiah Breakwall.exe', 'README.md', 'Инструкция.txt') | ForEach-Object { Get-Item -LiteralPath (Join-Path $root $_) }
    foreach ($directory in @('engine','profiles','warp','src','licenses')) {
        $files += Get-ChildItem -File -Recurse -LiteralPath (Join-Path $root $directory)
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose(); $stream.Dispose() }
[pscustomobject]@{
    Archive = $archivePath
    Bytes = (Get-Item -LiteralPath $archivePath).Length
    SHA256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
}
