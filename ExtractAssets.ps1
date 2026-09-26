<#
.SYNOPSIS
    Bulk-extracts every Crimson Skies ZBD archive into extracted/, mirroring the
    original CrimsonSkiesGame/ZBD folder structure (e.g. C1\IA1).

.DESCRIPTION
    Walks the retail game's ZBD tree, runs mech3ax `unzbd cs <mode>` on each file
    with the correct extraction mode for its type, and writes the output to the
    same relative path under extracted/ (keeping the basename, swapping the
    extension to .zip -- or .json for interp). Re-running only re-extracts files
    whose source is newer than the existing output, unless -Force is given.

    Mode mapping (game type `cs`):
        interp.zbd        -> interp    -> .json
        planes.zbd        -> gamez     -> .zip   (planes.zbd is a GameZ-format file)
        gamez.zbd         -> gamez     -> .zip
        soundsh/soundsl   -> sounds    -> .zip
        zrdr.zbd          -> reader    -> .zip
        rimage.zbd        -> textures  -> .zip
        texture.zbd       -> textures  -> .zip
        rtexture*.zbd     -> textures  -> .zip
        cam_anim.zbd      -> anim      -> .zip
        mis_anim.zbd      -> anim      -> .zip

    Anim archives (cam_anim/mis_anim) round-trip byte-identically in the fork since
    2026-07-21, are extracted like everything else, and are read by the Godot
    project's animation engine (CompiledAnim.cs -> AnimProgram.cs -> AnimRuntime.cs).

    A failure-free run stamps <Dest>\VERSION.json with what produced the tree (unzbd
    version line, exe SHA-256, fork commit when the checkout is present, date, schema
    integer). The engine compares the schema at boot (src\Session\Launch\ExtractionStamp.cs)
    and warns when it no longer matches.
    RULE: the schema integer is a hand-maintained promise -- bump $StampSchema here,
    in ExtractRof.ps1, and ExtractionStamp.Schema in the SAME commit as any reader
    change that invalidates old extractions.

.PARAMETER Source
    Root of the game's ZBD tree. Default: CrimsonSkiesGame\ZBD next to this script.

.PARAMETER Unzbd
    Path to the unzbd.exe to extract with. Default: the fork build at
    tools\mech3ax\target\release\unzbd.exe (`cargo build --release` in tools\mech3ax).
    Pass the pinned tools\mech3ax-v0.6.1-...\unzbd.exe to fall back to the old binary --
    the Godot loaders read either extraction shape (see GameZ.cs), so that rollback
    needs no code change.

.PARAMETER Dest
    Output root. Default: extracted\ next to this script.

.PARAMETER Unzip
    Optional. After producing each .zip, also expand it into a sibling folder
    named after the archive (e.g. extracted\C1\gamez.zip -> extracted\C1\gamez\).
    The interp .json output has nothing to unzip and is left as-is.

.PARAMETER Force
    Re-extract every archive even when its output is already up to date.

.EXAMPLE
    .\ExtractAssets.ps1
    Extract all ZBDs into extracted\, mirroring the C1\IA1\... structure.

.EXAMPLE
    .\ExtractAssets.ps1 -Unzip
    Same, and also unpack every produced .zip into a sibling folder.
#>

[CmdletBinding()]
param(
    [string] $Source,
    [string] $Dest,
    [string] $Unzbd,
    [switch] $Unzip,
    [switch] $Force
)

$ErrorActionPreference = "Stop"

$RepoRoot = $PSScriptRoot
if (-not $Source) { $Source = Join-Path $RepoRoot "CrimsonSkiesGame\ZBD" }
if (-not $Dest)   { $Dest   = Join-Path $RepoRoot "extracted" }
# The fork (tools\mech3ax) is the extraction source of record since 2026-07-21: it is the
# only build with CS gamez/planes support that round-trips byte-identically (the pinned
# v0.6.1 binary leaves a 72-byte planes.zbd diff), and it also supports cam_anim/mis_anim.
$ForkUnzbd = Join-Path $RepoRoot "tools\mech3ax\target\release\unzbd.exe"
if (-not $Unzbd) { $Unzbd = $ForkUnzbd }
$UnzbdExe = $Unzbd

if (-not (Test-Path $UnzbdExe)) {
    if ($UnzbdExe -eq $ForkUnzbd) {
        throw "unzbd not found at $UnzbdExe -- build it with ``cargo build --release`` in tools\mech3ax, " +
              "or pass -Unzbd <path> (e.g. the pinned tools\mech3ax-v0.6.1-x86_64-pc-windows-msvc\unzbd.exe)."
    }
    throw "unzbd not found at $UnzbdExe"
}
if (-not (Test-Path $Source)) {
    throw "Source ZBD tree not found at $Source"
}

# Map a ZBD basename (no extension, lower-cased) to its unzbd mode + output
# extension. Returns $null for archives mech3ax can't extract for CS (skip them).
function Get-ExtractPlan([string] $BaseName) {
    switch -Regex ($BaseName.ToLowerInvariant()) {
        '^interp$'      { return @{ Mode = "interp";   Ext = ".json" } }
        '^planes$'      { return @{ Mode = "gamez";    Ext = ".zip"  } }
        '^gamez$'       { return @{ Mode = "gamez";    Ext = ".zip"  } }
        '^sounds[hl]$'  { return @{ Mode = "sounds";   Ext = ".zip"  } }
        '^zrdr$'        { return @{ Mode = "reader";   Ext = ".zip"  } }
        '^rimage$'      { return @{ Mode = "textures"; Ext = ".zip"  } }
        '^texture$'     { return @{ Mode = "textures"; Ext = ".zip"  } }
        '^rtexture\d+$' { return @{ Mode = "textures"; Ext = ".zip"  } }
        '^(cam_anim|mis_anim)$' { return @{ Mode = "anim"; Ext = ".zip"  } }
        default         { return "UNKNOWN" }
    }
}

$SourceFull = (Resolve-Path $Source).Path
$zbds = Get-ChildItem -Path $SourceFull -Recurse -File -Filter *.zbd | Sort-Object FullName

Write-Host "Extracting $($zbds.Count) ZBD file(s)" -ForegroundColor Cyan
Write-Host "  from: $SourceFull"
Write-Host "  to:   $Dest"
Write-Host "  with: $UnzbdExe"
if ($Unzip) { Write-Host "  (will also unzip produced archives)" }
Write-Host ""

$extracted = 0; $upToDate = 0; $skipped = 0; $unzipped = 0; $transformNotes = 0; $animNotes = 0
$failures = New-Object System.Collections.Generic.List[string]
$unknowns = New-Object System.Collections.Generic.List[string]

foreach ($zbd in $zbds) {
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($zbd.Name)
    $rel = $zbd.FullName.Substring($SourceFull.Length).TrimStart('\', '/')
    $plan = Get-ExtractPlan $baseName

    if ($null -eq $plan) {
        Write-Host "  SKIP (unsupported) $rel" -ForegroundColor DarkGray
        $skipped++
        continue
    }
    if ($plan -eq "UNKNOWN") {
        Write-Host "  SKIP (unknown type) $rel" -ForegroundColor Yellow
        $unknowns.Add($rel)
        $skipped++
        continue
    }

    # Mirror the relative path under $Dest, swapping .zbd for the mode's extension.
    $outRel = [System.IO.Path]::ChangeExtension($rel, $plan.Ext)
    $outPath = Join-Path $Dest $outRel
    $outDir = Split-Path -Parent $outPath
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

    $upToDateAlready = (Test-Path $outPath) -and
                       ((Get-Item $outPath).LastWriteTime -ge $zbd.LastWriteTime)

    if ($upToDateAlready -and -not $Force) {
        Write-Host "  ok   $outRel (up to date)" -ForegroundColor DarkGray
        $upToDate++
    }
    else {
        Write-Host "  ->   $outRel  [$($plan.Mode)]" -ForegroundColor Green
        # unzbd writes diagnostics to stderr, and $ErrorActionPreference = "Stop" would
        # turn any of them into a terminating error (PowerShell 5.1 wraps a native exe's
        # stderr in ErrorRecords). Capture instead, and judge by the exit code.
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $output = & $UnzbdExe cs $plan.Mode $zbd.FullName $outPath 2>&1
        $exit = $LASTEXITCODE
        $ErrorActionPreference = $prevEap

        $stderrLines = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        # "object3d transform fail" is upstream mech3ax noting that recomposing a node's
        # transform from its euler angles doesn't reproduce the stored matrix bit-for-bit.
        # It is informational, not a failure: the original matrix is preserved verbatim
        # (that is what makes the gamez round-trip byte-identical), and the Godot loader
        # reads it in preference to the angles. Counted, not printed -- a few hundred
        # across a full run would bury real errors.
        # "INTERVAL VAL FAIL" / "DELTA VAL FAIL" is unzbd's anim reader noting that an
        # event's interval or delta fields do not validate against its flags; the raw
        # values are kept as garbage in the JSON and the event still extracts. Thousands
        # per full run, on a run that succeeds, so also counted rather than printed.
        # "anim def duplicate anim ref" is the same reader seeing a call-object-connector
        # ref name twice in one anim def, which the original data does; both refs are kept.
        $benignPattern = "object3d transform fail|VAL FAIL|anim def duplicate anim ref"
        $benign = @($stderrLines | Where-Object { "$_" -match $benignPattern })
        $unexpected = @($stderrLines | Where-Object { "$_" -notmatch $benignPattern })
        $transformFails = @($benign | Where-Object { "$_" -match "object3d transform fail" }).Count
        $valFails = $benign.Count - $transformFails

        if ($exit -ne 0) {
            Write-Host "       FAILED (unzbd exit $exit)" -ForegroundColor Red
            $stderrLines | ForEach-Object { Write-Host "         $_" -ForegroundColor Red }
            $failures.Add("$rel (exit $exit)")
            continue
        }
        foreach ($line in $unexpected) { Write-Host "       $line" -ForegroundColor Yellow }
        if ($transformFails -gt 0) {
            Write-Host "       ($transformFails transform-precision notes)" -ForegroundColor DarkGray
            $transformNotes += $transformFails
        }
        if ($valFails -gt 0) {
            Write-Host "       ($valFails anim-validation notes)" -ForegroundColor DarkGray
            $animNotes += $valFails
        }
        $extracted++
    }

    # Optional: expand the produced .zip into a sibling folder (skip .json).
    if ($Unzip -and $plan.Ext -eq ".zip" -and (Test-Path $outPath)) {
        $expandDir = Join-Path $outDir ([System.IO.Path]::GetFileNameWithoutExtension($outPath))
        if ((Test-Path $expandDir) -and -not $Force) {
            $srcNewer = (Get-Item $outPath).LastWriteTime -gt (Get-Item $expandDir).LastWriteTime
            if (-not $srcNewer) { continue }
        }
        if (Test-Path $expandDir) { Remove-Item $expandDir -Recurse -Force }
        Expand-Archive -Path $outPath -DestinationPath $expandDir -Force
        $unzipped++
    }
}

# ---- messages.json --------------------------------------------------------
# The HUD/briefing string table lives in strings.dll at the install root -- the
# ZBD tree's PARENT -- so the walk above never sees it. Without messages.json the
# engine falls back to raw MSG_* keys on the HUD and briefings. Same idempotence
# and failure accounting as the loop.
$StringsDll = Join-Path (Split-Path $SourceFull -Parent) "strings.dll"
$MessagesOut = Join-Path $Dest "messages.json"
if (Test-Path $StringsDll) {
    $msgUpToDate = (Test-Path $MessagesOut) -and
                   ((Get-Item $MessagesOut).LastWriteTime -ge (Get-Item $StringsDll).LastWriteTime)
    if ($msgUpToDate -and -not $Force) {
        Write-Host "  ok   messages.json (up to date)" -ForegroundColor DarkGray
        $upToDate++
    }
    else {
        Write-Host "  ->   messages.json  [messages]" -ForegroundColor Green
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $output = & $UnzbdExe cs messages $StringsDll $MessagesOut 2>&1
        $exit = $LASTEXITCODE
        $ErrorActionPreference = $prevEap
        if ($exit -ne 0) {
            Write-Host "       FAILED (unzbd exit $exit)" -ForegroundColor Red
            @($output) | ForEach-Object { Write-Host "         $_" -ForegroundColor Red }
            $failures.Add("strings.dll -> messages.json (exit $exit)")
        }
        else { $extracted++ }
    }
}
else {
    Write-Host "  SKIP messages.json (no strings.dll at $StringsDll)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
Write-Host "  extracted:  $extracted"
Write-Host "  up to date: $upToDate"
Write-Host "  skipped:    $skipped"
if ($Unzip)              { Write-Host "  unzipped:   $unzipped" }
if ($transformNotes -gt 0) {
    Write-Host "  transform-precision notes: $transformNotes (informational -- see the comment in this script)" -ForegroundColor DarkGray
}
if ($animNotes -gt 0) {
    Write-Host "  anim-validation notes: $animNotes (informational -- see the comment in this script)" -ForegroundColor DarkGray
}
if ($unknowns.Count -gt 0) {
    Write-Host "  UNKNOWN types (no mode mapped -- check the script):" -ForegroundColor Yellow
    $unknowns | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
}

# ---- version stamp --------------------------------------------------------
# A failure-free run (including the all-up-to-date one) stamps the tree with what
# produced it, so the engine (src\Session\Launch\ExtractionStamp.cs) can tell a boot-time
# "it looks wrong" report which extractor vintage it is looking at. Read-merge-write:
# ExtractRof.ps1 owns the "rof" field of the same file and must survive this write.
# $StampSchema bumps together with ExtractRof.ps1's and ExtractionStamp.Schema, in the
# same commit as any reader change that invalidates old extractions (see the header).
if ($failures.Count -eq 0) {
    $StampSchema = 3

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    # The fork self-reports a frozen version number; the build timestamp in the same
    # line is what actually distinguishes binaries, so the line is kept verbatim.
    $unzbdVersion = ((& $UnzbdExe --version 2>&1 | Select-Object -First 1) | Out-String).Trim()
    # Dev layout: the exe sits at <checkout>\target\release\unzbd.exe inside a git
    # checkout. A bare exe (a friend's machine) has no checkout -- omit the field.
    $forkCommit = $null
    $forkDir = Split-Path (Split-Path (Split-Path $UnzbdExe -Parent) -Parent) -Parent
    if ($forkDir -and (Test-Path (Join-Path $forkDir ".git")) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        $head = git -C $forkDir rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $head) { $forkCommit = "$head".Trim() }
    }
    $ErrorActionPreference = $prevEap

    $stampPath = Join-Path $Dest "VERSION.json"
    $stamp = [ordered]@{}
    if (Test-Path $stampPath) {
        try {
            $existing = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
            foreach ($p in $existing.PSObject.Properties) { $stamp[$p.Name] = $p.Value }
        } catch {
            # Unreadable stamp: rewrite from scratch (the engine already warns about it).
        }
    }
    $assets = [ordered]@{
        script       = "ExtractAssets.ps1"
        date         = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        unzbdVersion = $unzbdVersion
        unzbdSha256  = (Get-FileHash -LiteralPath $UnzbdExe -Algorithm SHA256).Hash
    }
    if ($forkCommit) { $assets["unzbdCommit"] = $forkCommit }
    $stamp["schema"] = $StampSchema
    $stamp["assets"] = $assets
    $stamp | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $stampPath -Encoding UTF8
    Write-Host "  stamped:    $stampPath (schema $StampSchema)"
}

if ($failures.Count -gt 0) {
    Write-Host "  FAILURES:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    exit 1
}
