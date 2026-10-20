# Builds the plugin and creates dist/FragLabPlugin.zip, laid out to be extracted into game/csgo
# (e.g. DatHost file manager).  Usage: powershell -File scripts/package.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & dotnet build -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

    $plugin = 'addons/counterstrikesharp/plugins/FragLabPlugin'
    $files = [ordered]@{
        "$plugin/FragLabPlugin.dll" = 'build/FragLabPlugin.dll'
        "$plugin/FragLabPlugin.pdb" = 'build/FragLabPlugin.pdb'
        "$plugin/data/skins.json"    = 'data/skins.json'
    }
    Get-ChildItem cfg/fraglab -File | ForEach-Object { $files["cfg/fraglab/$($_.Name)"] = $_.FullName }
    Get-ChildItem data/retake-spawns -File | ForEach-Object { $files["$plugin/data/retake-spawns/$($_.Name)"] = $_.FullName }

    New-Item -ItemType Directory -Force dist | Out-Null
    $zip = Join-Path $root 'dist/FragLabPlugin.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }

    # Entries use forward slashes so they extract correctly on Linux hosts.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files.Keys) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Resolve-Path $files[$entry]).Path, $entry) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Output "Created $zip"
    $files.Keys | ForEach-Object { "  $_" }
}
finally {
    Pop-Location
}
