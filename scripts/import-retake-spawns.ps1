# Imports the pre-made retake spawns from B3none/cs2-retakes (GPL-3.0, https://github.com/B3none/cs2-retakes)
# into data/retake-spawns/<map>.json, the plugin's built-in default spawns.
# Usage: powershell -File scripts/import-retake-spawns.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$downloadDir = Join-Path $env:TEMP 'retakes-spawns-dl'
$targetDir = Join-Path $root 'data\retake-spawns'
$utf8 = New-Object System.Text.UTF8Encoding $false
$culture = [Globalization.CultureInfo]::InvariantCulture

$tree = Invoke-RestMethod -UseBasicParsing 'https://api.github.com/repos/B3none/cs2-retakes/git/trees/master?recursive=1'
$maps = $tree.tree | Where-Object { $_.path -match '^RetakesPlugin/map_config/(.+)\.json$' } | ForEach-Object { $Matches[1] }

New-Item -ItemType Directory -Force $downloadDir, $targetDir | Out-Null

function Parse-Floats([string]$text) {
    # "1,234.56 -78.90 12.00" -> thousands separators removed
    $text.Replace(',', '').Split(' ', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [double]::Parse($_, $culture) }
}

foreach ($map in $maps) {
    $file = Join-Path $downloadDir "$map.json"
    Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/B3none/cs2-retakes/master/RetakesPlugin/map_config/$map.json" -OutFile $file
    $source = [IO.File]::ReadAllText($file, $utf8) | ConvertFrom-Json

    $spawns = foreach ($s in $source.Spawns) {
        $pos = @(Parse-Floats $s.Vector)
        $ang = @(Parse-Floats $s.QAngle)
        [ordered]@{
            Team      = if ($s.Team -eq 2) { 'Terrorist' } else { 'CounterTerrorist' }
            Site      = if ($s.Bombsite -eq 0) { 'A' } else { 'B' }
            IsPlanter = [bool]$s.CanBePlanter
            X         = [math]::Round($pos[0], 2)
            Y         = [math]::Round($pos[1], 2)
            Z         = [math]::Round($pos[2], 2)
            Yaw       = [math]::Round($ang[1], 2)
        }
    }

    $json = ConvertTo-Json @($spawns) -Depth 3
    [IO.File]::WriteAllText((Join-Path $targetDir "$map.json"), $json, $utf8)

    $summary = foreach ($site in 'A', 'B') {
        $t = @($spawns | Where-Object { $_.Site -eq $site -and $_.Team -eq 'Terrorist' })
        $ct = @($spawns | Where-Object { $_.Site -eq $site -and $_.Team -eq 'CounterTerrorist' })
        "$site`: $($t.Count) T ($(@($t | Where-Object IsPlanter).Count) planter) / $($ct.Count) CT"
    }
    Write-Output ("{0,-18} {1}" -f $map, ($summary -join ' | '))
}
