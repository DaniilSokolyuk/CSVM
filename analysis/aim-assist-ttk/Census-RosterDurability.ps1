<#
.SYNOPSIS
    Read-only census of the AI roster durability overrides: slot 7 `init_health` and slot 66
    `armor`, per mission, split by team.

.DESCRIPTION
    The original's roster spawn applies `init_health` when greater than zero and `armor` when
    greater than or equal to zero, then the difficulty scale (docs/org/vehicleDamage.md). CSVM
    carries neither field, so a hostile authoring `armor 0` gets its full airframe armour instead.
    This script lists which blocks author them, so the claim can be checked at source rather than
    quoted.

    Field indices are docs/formats/ai-rosters.md's table: 3 team, 5 enabled, 7 init_health,
    20 title, 66 armor. Blocks are NOT fixed width (histogram 42/65/66/67/68/81), so every read is
    bounds-checked and a missing slot is unset, not zero.

    Reads extracted/*/*/zrdr/aiv.zrd.json. Writes nothing.

.PARAMETER Extracted
    The extraction workdir. Defaults to the repo's own `extracted/`.

.PARAMETER All
    List every override, not just the hostile `armor 0` cases.
#>
[CmdletBinding()]
param(
    [string] $Extracted = (Join-Path $PSScriptRoot '..\..\extracted'),
    [switch] $All
)

$ErrorActionPreference = 'Stop'

$files = Get-ChildItem -Path $Extracted -Recurse -Filter 'aiv.zrd.json' | Sort-Object FullName
if (-not $files) { throw "no aiv.zrd.json under $Extracted -- run Extract.ps1 -Unzip first" }

$rows = New-Object System.Collections.ArrayList
$blockCount = 0
$absentArmor = 0

foreach ($file in $files) {
    # chapter/mission from .../extracted/<chapter>/<mission>/zrdr/aiv.zrd.json
    $mission = Split-Path (Split-Path $file.FullName -Parent) -Parent
    $tag = '{0}/{1}' -f (Split-Path (Split-Path $mission -Parent) -Leaf), (Split-Path $mission -Leaf)

    $root = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8 | ConvertFrom-Json

    # The player's own block fixes which team id is friendly in this mission; the engine's rule is
    # "team differs from the player's", not a hardcoded id.
    $playerTeam = $null
    foreach ($block in $root[1..($root.Count - 1)]) {
        if ($block[0] -eq 'player' -and $block[1].Count -gt 3) { $playerTeam = $block[1][3]; break }
    }

    foreach ($block in $root[1..($root.Count - 1)]) {
        $name = $block[0]
        $f = $block[1]
        $blockCount++
        if ($f.Count -le 7) { continue }

        $team = if ($f.Count -gt 3) { $f[3] } else { $null }
        $enabled = if ($f.Count -gt 5) { $f[5] } else { $null }
        $title = if ($f.Count -gt 20) { $f[20] } else { '' }
        $health = $f[7]
        $armor = if ($f.Count -gt 66) { $f[66] } else { $null }
        $nonPlayer = ($null -ne $playerTeam) -and ($team -ne $playerTeam)
        if ($null -eq $armor -and $enabled -eq 1 -and $nonPlayer) { $absentArmor++ }

        $hasHealth = ($null -ne $health) -and ($health -gt 0)
        $hasArmor = ($null -ne $armor) -and ($armor -ge 0)
        if (-not ($hasHealth -or $hasArmor)) { continue }

        $null = $rows.Add([pscustomobject]@{
            Mission     = $tag
            Node        = $name
            Title       = $title
            Team        = $team
            Enabled     = $enabled
            Hostile     = $nonPlayer
            InitHealth  = if ($hasHealth) { $health } else { $null }
            Armor       = if ($hasArmor) { $armor } else { $null }
        })
    }
}

$hostileEnabled = $rows | Where-Object { $_.Hostile -and $_.Enabled -eq 1 }
$zeroArmor = $hostileEnabled | Where-Object { $_.Armor -eq 0 }

Write-Output ("roster blocks scanned   : {0} in {1} files" -f $blockCount, $files.Count)
Write-Output ("blocks with an override : {0}" -f $rows.Count)
Write-Output ("  enabled + non-player  : {0}" -f $hostileEnabled.Count)
Write-Output ("  of those, armor = 0   : {0}" -f $zeroArmor.Count)
Write-Output ("blocks with NO slot 66  : {0} (absent, not zero -- see the warning below)" -f $absentArmor)
Write-Output ''
Write-Output 'ABSENT IS NOT ZERO. Slot 66 exists only on a block wide enough to carry it, and 33 of'
Write-Output 'the 414 stop at 66 fields (indices 0-65). A reader that maps a missing slot to 0.0'
Write-Output 'invents an armour-stripped enemy where the data simply authors no override and the'
Write-Output 'airframe default applies. That mistake produces exactly 18 phantom hostiles.'
Write-Output ''
Write-Output 'Enabled non-player blocks authoring armor 0 (expected: none):'
$zeroArmor | Sort-Object Mission, Node | Format-Table Mission, Node, Title, Team, InitHealth -AutoSize

if ($All) {
    Write-Output ''
    Write-Output 'Every override, all teams:'
    $rows | Sort-Object Mission, Node | Format-Table Mission, Node, Title, Team, Enabled, Hostile, InitHealth, Armor -AutoSize
}
