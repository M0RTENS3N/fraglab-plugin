# Regenerates data/skins.json from the ByMykel CSGO-API (https://github.com/ByMykel/CSGO-API).
# Run again after a CS2 update that adds new skins:  powershell -File scripts/generate-skins.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$downloadDir = Join-Path $env:TEMP 'ultimate-skins-dl'
$source = Join-Path $downloadDir 'skins.json'
$target = Join-Path $root 'data\skins.json'

New-Item -ItemType Directory -Force $downloadDir | Out-Null
Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/skins.json' -OutFile $source
$all = Get-Content $source -Raw -Encoding UTF8 | ConvertFrom-Json

$categories = @{
    'Pistols' = 'pistol'; 'Rifles' = 'rifle'; 'SMGs' = 'smg'; 'Heavy' = 'heavy'
    'Equipment' = 'equipment'; 'Knives' = 'knife'; 'Gloves' = 'gloves'
}

$items = @{}
$skins = New-Object System.Collections.Generic.List[object]

foreach ($s in $all) {
    $defindex = [int]$s.weapon.weapon_id
    $category = $categories[$s.category.name]
    if (-not $category) { continue }

    # Prefer the weapon_* class name (some knife entries use HUD names instead).
    if (-not $items.ContainsKey($defindex) -or ($s.weapon.id -like 'weapon_*' -and $items[$defindex].className -notlike 'weapon_*')) {
        $items[$defindex] = [ordered]@{
            defindex  = $defindex
            name      = $s.weapon.name
            className = $s.weapon.id
            category  = $category
        }
    }

    $paint = 0
    if ($s.paint_index) { $paint = [int]$s.paint_index }
    if ($paint -eq 0) { continue } # vanilla knives are handled by the plugin

    $skins.Add([ordered]@{
        defindex = $defindex
        paint    = $paint
        # Doppler-style finishes share one name; the phase (Ruby, Sapphire, Emerald, Phase 2...) tells them apart.
        name     = if ($s.phase) { "$($s.pattern.name) ($($s.phase))" } else { $s.pattern.name }
        rarity   = $s.rarity.color
        legacy   = [bool]$s.legacy_model
        minFloat = [double]$s.min_float
        maxFloat = [double]$s.max_float
    })
}

$output = [ordered]@{
    generated = (Get-Date -Format 'yyyy-MM-dd')
    items     = @($items.Values | Sort-Object { $_.category }, { $_.name })
    skins     = @($skins | Sort-Object { $_.defindex }, { $_.name })
}

New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
$json = $output | ConvertTo-Json -Depth 4 -Compress
[IO.File]::WriteAllText($target, $json, (New-Object System.Text.UTF8Encoding $false))
Write-Output "Wrote $($output.items.Count) items and $($skins.Count) skins to $target"
