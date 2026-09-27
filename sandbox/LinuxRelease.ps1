<#
.SYNOPSIS
    The pre-release Linux check: the built tarball unpacked in WSL, extracted from the author's
    install with the exported build's own --extract, then the in-engine suites run headless from
    that export against that extraction. Any failure fails the check.

.DESCRIPTION
    The Linux sibling of sandbox\PublicRelease.ps1. That driver runs a zip inside Windows Sandbox;
    this one runs on the host and drives WSL (Debian), because a Linux tarball needs a Linux
    machine and WSL's Vulkan is too weak to render, so everything here is --headless.

    Stages, each reported PASS/FAIL in RunTests.ps1's style:
      payload      the archive's listing against packaging/MANIFEST.md's Linux table: every row
                   present, nothing at the root the table does not name, CSVM.x86_64 and
                   tools/unzbd marked -rwxr-xr-x, and LICENSE-thirdparty.txt stamped for the
                   linux-x64 runtime pack and the musl crate tree, naming nothing of Windows',
                   and no top-level text file (one without a NUL byte) carrying a CR
      extract      ./CSVM.x86_64 --headless -- --extract=<install> into a fresh data root, the
                   player's defaults (zips only): exit 0 and a stamped VERSION.json
      engine       (unless -NoSuites) --run-tests over that same zips-only data root, the shape
                   every player's install reads, in -Shards processes,
                   split by the harness's own shard:<i>/<n> term over
                   analysis/engine-suite-weights.json, so the suite list is the registry's and
                   never a copy of it

    A stage that fails does not stop the ones after it that can still run: a tarball whose unzbd
    lost its bit fails the payload stage AND shows what the extraction does with it.

    Everything in the distro lives under ~/csvm-linux-check (wiped at the start of each run, left
    behind for inspection), with XDG_DATA_HOME, XDG_CONFIG_HOME and XDG_CACHE_HOME pointed into it,
    one set per process, so the distro user's ~/.local/share/godot is never touched and parallel
    shards share no user:// state. The run's listing, logs and reports are copied to
    .scratch\linux-check\<timestamp>\.

.PARAMETER Tarball
    The tarball to check. Default: .scratch\CSVM-v<version>-linux-x64.tar.gz, the name
    ExportRelease.ps1 -Linux gives it, the version read from CSVM/project.godot.

.PARAMETER Install
    The original game's installed folder (Windows path). Default: CrimsonSkiesGame\ under this
    tree, or under the tree CSVM_DATA_ROOT names when this one has none (a worktree).

.PARAMETER Shards
    Concurrent engine processes. Default 6, RunTests.ps1's measured default for a full run.

.PARAMETER NoSuites
    Stop after the payload and extraction stages: the release check proper, without the engine
    suites.

.PARAMETER ShardTimeoutSec
    Per-process watchdog, in seconds. Default 900.

.EXAMPLE
    .\sandbox\LinuxRelease.ps1
    The tarball ExportRelease.ps1 -Linux just built, extracted and tested in WSL.

.EXAMPLE
    .\sandbox\LinuxRelease.ps1 -Tarball Z:\somewhere\CSVM-v0.2.0-linux-x64.tar.gz -NoSuites
#>
param(
    [string]$Tarball = "",
    [string]$Install = "",
    [int]$Shards = 6,
    [switch]$NoSuites,
    [int]$ShardTimeoutSec = 900,
    [string]$Distro = "Debian"
)

$ErrorActionPreference = "Stop"
$Inv = [Globalization.CultureInfo]::InvariantCulture
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Utf8 = New-Object System.Text.UTF8Encoding($false)

# Suites that cannot pass in a headless process by construction, on any platform: each reads
# back something only a real renderer or a real display produces. Every entry was run headless
# from the Windows export of the same commit against a Windows extraction of the same shape and
# failed there too, so an entry here is never a Linux finding. They still run; a listed suite
# that passes is reported so a stale entry is seen. This is the one place the list lives.
$HeadlessOnly = [ordered]@{
    "cloud-field-fade"          = "reads the cloud cards' normals back out of the built mesh, which the headless dummy renderer does not store"
    "clutter-card-depth"        = "counts rendered pixels in a viewport; headless renders none"
    "crater-carve"              = "counts the clutter decorations the bowl destroys from the clutter MultiMesh, which the headless renderer keeps no instances for"
    "display-mode"              = "asserts on the window mode of a real window; the headless display server has none"
    "display-monitor"           = "enumerates screens; the headless display server reports none"
    "display-resolution"        = "reads the screen size; the headless display server reports 0x0"
    "display-vsync"             = "applies V-Sync to a real window's swap chain; headless has none"
    "menu-original-tracer"      = "reads the OS pointer's visibility and the monitor list from the display server, which headless does not provide"
    "menu-screenshot-key"       = "captures the viewport texture to a PNG; headless draws nothing, so every file is empty"
    "muzzle-flash-rides-muzzle" = "reads the flash quads' drawn instance transforms back, which the headless renderer does not keep"
    "trail-world-anchor"        = "reads the first drawn puff's instance transform back, which the headless renderer does not keep"
}
# Engine error lines a headless process prints and a windowed one does not, on either platform:
# the same run from the Windows export prints each of these, at the same counts. The harness's
# own allowlist is written for the windowed battery, so these are passed here, by pattern, and
# every other unexpected line still fails the stage.
$HeadlessEngineErrors = [ordered]@{
    'texture_2d_get \(\./servers/rendering/dummy/'  = "the dummy renderer holds no texture data to read back"
    '(_shaped_text_add_string|_ensure_cache_for_size|_font_get_(ascent|descent)) \(modules/text_server_adv/' ="a headless font has no rasterised size cache, so shaping at a measured size of 0 fails"
}

function Write-Banner([string]$Text) {
    Write-Host ""
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Get-StatusColor([string]$Status) {
    if ($Status -eq "PASS") { return "Green" }
    if ($Status -eq "FAIL") { return "Red" }
    return "Yellow"
}

$Stages = New-Object System.Collections.ArrayList
$Unchecked = New-Object System.Collections.ArrayList
function Add-Stage([string]$Name, [string]$Status, [double]$Seconds, [string]$Detail) {
    $null = $Stages.Add([pscustomobject]@{ Name = $Name; Status = $Status; Seconds = $Seconds; Detail = $Detail })
}

# --- inputs -------------------------------------------------------------------------------------

if (-not $Tarball) {
    $godot = [IO.File]::ReadAllText((Join-Path $RepoRoot "CSVM\project.godot"))
    if ($godot -notmatch '(?m)^config/version="([^"]+)"') {
        throw "No application/config/version in CSVM\project.godot; pass -Tarball."
    }
    $Tarball = Join-Path $RepoRoot ".scratch\CSVM-v$($Matches[1])-linux-x64.tar.gz"
}
if (-not (Test-Path $Tarball)) {
    throw "Tarball not found at $Tarball -- build it with .\ExportRelease.ps1 -Linux, or pass -Tarball."
}
$Tarball = (Resolve-Path $Tarball).Path

if (-not $Install) {
    $candidates = @(Join-Path $RepoRoot "CrimsonSkiesGame")
    if ($env:CSVM_DATA_ROOT) { $candidates += Join-Path $env:CSVM_DATA_ROOT "CrimsonSkiesGame" }
    $Install = $candidates | Where-Object { Test-Path (Join-Path $_ "ZBD") } | Select-Object -First 1
    if (-not $Install) {
        throw "No original install found at $($candidates -join ' or ') -- pass -Install."
    }
}
$Install = (Resolve-Path $Install).Path

$Weights = Join-Path $RepoRoot "analysis\engine-suite-weights.json"
if (-not $NoSuites -and -not (Test-Path $Weights)) {
    throw "No $Weights; the shards are planned from it."
}

# packaging/MANIFEST.md's Linux table is the statement of what the tarball holds, so the
# expected listing is read from it rather than restated here. Each backticked name in a row's
# first cell is one entry; a name ending in / is a folder that must hold at least one file.
$manifestText = [IO.File]::ReadAllText((Join-Path $RepoRoot "packaging\MANIFEST.md"), $Utf8)
$linuxPart = $manifestText.Substring($manifestText.IndexOf("## Linux tarball"))
$Expected = @()
foreach ($row in ($linuxPart -split "`n" | Where-Object { $_ -match '^\|' })) {
    $first = ($row -split '\|')[1]
    if ($first -match '^\s*-+\s*$' -or $first -match 'Tarball path') { continue }
    foreach ($m in [regex]::Matches($first, '`([^`]+)`')) { $Expected += $m.Groups[1].Value }
}
if ($Expected.Count -lt 5) {
    throw "Read only $($Expected.Count) payload names from packaging\MANIFEST.md's Linux table; its layout changed."
}

try {
    & wsl.exe -d $Distro --exec true
    $wslExit = $LASTEXITCODE
} catch { $wslExit = -1 }
if ($wslExit -ne 0) {
    throw "WSL distro '$Distro' is not reachable (wsl.exe -d $Distro) -- one-time setup: 'wsl --install -d Debian'."
}

# Godot's Linux build loads fontconfig for system fonts and logs an engine error on every lookup
# without it. Every desktop distribution and SteamOS ship it; a minimal WSL Debian does not.
$ldconfig = @(& wsl.exe -d $Distro --exec /sbin/ldconfig -p)
if (-not ($ldconfig | Where-Object { $_ -match 'libfontconfig\.so\.1' })) {
    throw "WSL distro '$Distro' has no libfontconfig.so.1 -- one-time setup: 'sudo apt install libfontconfig1' " +
        "inside it (a player's system has it; without it every text lookup logs an engine error)."
}

function ConvertTo-WslPath([string]$Path) {
    $wslPath = (& wsl.exe -d $Distro --exec wslpath -u $Path)
    if ($LASTEXITCODE -ne 0 -or -not $wslPath) { throw "wslpath could not translate $Path for WSL." }
    return $wslPath.Trim()
}

$Stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$OutDir = Join-Path $RepoRoot ".scratch\linux-check\$Stamp"
New-Item -ItemType Directory -Force $OutDir | Out-Null

# PowerShell 5.1 mangles embedded double quotes in a native command's arguments, so the WSL side
# is one script file with LF endings and its inputs passed as plain arguments, as ExportRelease.ps1
# does for its pack step. Each stage writes its own files into the host folder and its exit code
# into <stage>.exit; the verdicts are all drawn here, from those files.
$checkScript = Join-Path $OutDir "linux-check.sh"
$sh = @'
set -u
tarball="$1"; install="$2"; out="$3"; shards="$4"; suites="$5"; weights="$6"; timeout_s="$7"
# bash's own notices (a child's "Segmentation fault") go to a file: native stderr reaching a
# PowerShell caller that redirects it is a terminating error there.
exec 2>"$out/bash.stderr"
root="$HOME/csvm-linux-check"
game="$root/game"; data="$root/data"
# The previous run is kept for inspection until this one starts.
rm -rf -- "$root"
mkdir -p "$game" "$data"

xdg() {
  export XDG_DATA_HOME="$root/xdg/$1/data" XDG_CONFIG_HOME="$root/xdg/$1/config" XDG_CACHE_HOME="$root/xdg/$1/cache"
  mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME"
}

tar --list --verbose --gzip --file="$tarball" > "$out/listing.txt" 2>&1
echo $? > "$out/listing.exit"
tar --extract --gzip --file="$tarball" --directory="$game" > "$out/unpack.log" 2>&1
echo $? > "$out/unpack.exit"
[ -f "$game/LICENSE-thirdparty.txt" ] && cp -- "$game/LICENSE-thirdparty.txt" "$out/LICENSE-thirdparty.txt"
# Each top-level file with no NUL byte is text; its carriage-return count goes to textfiles.txt.
: > "$out/textfiles.txt"
for f in "$game"/*; do
  [ -f "$f" ] || continue
  [ "$(tr -dc '\000' < "$f" | head -c 1 | wc -c)" = "0" ] || continue
  printf '%s %s\n' "$(basename -- "$f")" "$(tr -dc '\r' < "$f" | wc -c)" >> "$out/textfiles.txt"
done
cd "$game" || exit 0

xdg extract
start=$(date +%s)
timeout -k 10 "$timeout_s" ./CSVM.x86_64 --headless -- --extract="$install" --data-root="$data" > "$out/extract.log" 2>&1
echo $? > "$out/extract.exit"
echo $(( $(date +%s) - start )) > "$out/extract.seconds"
if [ -f "$data/extracted/VERSION.json" ]; then cp "$data/extracted/VERSION.json" "$out/VERSION.json"; fi
find "$data/extracted" -type f 2>/dev/null | wc -l > "$out/extract.files"
du -sm "$data/extracted" 2>/dev/null | cut -f1 > "$out/extract.mb"

[ "$suites" = "1" ] || exit 0
[ "$(cat "$out/extract.exit")" = "0" ] || exit 0

mkdir -p analysis
cp "$weights" analysis/engine-suite-weights.json
start=$(date +%s)
k=1
while [ "$k" -le "$shards" ]; do
  (
    xdg "shard$k"
    # No --log-file: an exported build cannot see Godot's own flags from managed code, so the
    # harness screens the engine log at user://logs/godot.log, which this process owns alone.
    timeout -k 10 "$timeout_s" ./CSVM.x86_64 --headless -- --run-tests="shard:$k/$shards" --data-root="$data" > "$out/shard$k.out" 2>&1
    echo $? > "$out/shard$k.exit"
  ) &
  k=$((k + 1))
done
wait
echo $(( $(date +%s) - start )) > "$out/engine.seconds"
k=1
while [ "$k" -le "$shards" ]; do
  report=$(find "$root/xdg/shard$k" "$game/.scratch" -name test-report.json -path "*shard${k}of${shards}*" 2>/dev/null | head -n 1)
  if [ -z "$report" ] && [ "$shards" = "1" ] && [ -f "$game/.scratch/test-report.json" ]; then
    report="$game/.scratch/test-report.json"
  fi
  if [ -n "$report" ]; then cp "$report" "$out/shard$k.report.json"; fi
  log="$root/xdg/shard$k/data/godot/app_userdata/CSVM/logs/godot.log"
  if [ -f "$log" ]; then cp "$log" "$out/shard$k.godot.log"; fi
  k=$((k + 1))
done
exit 0
'@
[IO.File]::WriteAllText($checkScript, ($sh -replace "`r`n", "`n"), $Utf8)

Write-Banner "Linux release check in WSL ($Distro)"
Write-Host "  tarball  $Tarball"
Write-Host "  install  $Install"
Write-Host "  output   $OutDir"
$watch = [Diagnostics.Stopwatch]::StartNew()
$ErrorActionPreference = "Continue"
& wsl.exe -d $Distro --exec bash (ConvertTo-WslPath $checkScript) (ConvertTo-WslPath $Tarball) `
    (ConvertTo-WslPath $Install) (ConvertTo-WslPath $OutDir) $Shards $(if ($NoSuites) { "0" } else { "1" }) `
    (ConvertTo-WslPath $Weights) $ShardTimeoutSec
$wslRun = $LASTEXITCODE
$ErrorActionPreference = "Stop"
$totalSeconds = $watch.Elapsed.TotalSeconds
if ($wslRun -ne 0) {
    throw "The WSL side of the check exited $wslRun before writing its results; see $OutDir."
}

function Read-Exit([string]$Name) {
    $path = Join-Path $OutDir "$Name.exit"
    if (-not (Test-Path $path)) { return $null }
    return [int]([IO.File]::ReadAllText($path).Trim())
}
function Read-Number([string]$Name) {
    $path = Join-Path $OutDir $Name
    if (-not (Test-Path $path)) { return 0 }
    $text = [IO.File]::ReadAllText($path).Trim()
    if ($text -match '^\d+$') { return [int]$text }
    return 0
}

# --- payload ------------------------------------------------------------------------------------

Write-Banner "payload"
$problems = @()
$listing = @()
if ((Read-Exit "listing") -ne 0) {
    $problems += "tar could not list the archive: $([IO.File]::ReadAllText((Join-Path $OutDir 'listing.txt')).Trim())"
} else {
    # GNU tar's verbose line: mode, owner/group, size, date, time, name. Names carry no spaces.
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $OutDir "listing.txt"))) {
        $parts = $line -split '\s+'
        if ($parts.Count -lt 6) { continue }
        $listing += [pscustomobject]@{ Mode = $parts[0]; Name = $parts[-1].TrimEnd('/') ; IsDir = $parts[0].StartsWith('d') }
    }
    $files = @($listing | Where-Object { -not $_.IsDir })
    foreach ($name in $Expected) {
        if ($name.EndsWith('/')) {
            $prefix = $name
            if (-not ($files | Where-Object { $_.Name.StartsWith($prefix) })) {
                $problems += "MANIFEST.md names the folder $name, and the archive holds no file under it"
            }
        } elseif (-not ($files | Where-Object { $_.Name -eq $name })) {
            $problems += "MANIFEST.md names $name, and the archive does not carry it"
        }
    }
    foreach ($entry in $files) {
        $covered = $Expected | Where-Object {
            ($_ -eq $entry.Name) -or ($_.EndsWith('/') -and $entry.Name.StartsWith($_))
        }
        if (-not $covered) {
            $problems += "the archive carries $($entry.Name), which MANIFEST.md's Linux table does not name"
        }
    }
    foreach ($exe in @("CSVM.x86_64", "tools/unzbd")) {
        $entry = $files | Where-Object { $_.Name -eq $exe } | Select-Object -First 1
        if ($entry -and $entry.Mode -ne '-rwxr-xr-x') {
            $problems += "$exe is $($entry.Mode) in the archive, not -rwxr-xr-x: a player's unpack cannot run it"
        }
    }
}
if ((Read-Exit "unpack") -ne 0) {
    $problems += "the archive did not unpack: $([IO.File]::ReadAllText((Join-Path $OutDir 'unpack.log')).Trim())"
}
# The notice must speak for what this archive ships: the linux-x64 runtime pack and the musl crate
# tree, stated as the header stamps packaging\BuildThirdPartyNotices.ps1 -Linux writes, and none of
# the Windows payload's names. The zip's notice, shipped here by mistake, fails on both counts.
$noticePath = Join-Path $OutDir "LICENSE-thirdparty.txt"
if (Test-Path $noticePath) {
    $notice = [IO.File]::ReadAllText($noticePath, $Utf8)
    foreach ($stamp in @(".NET runtime pack: Microsoft.NETCore.App.Runtime.linux-x64",
                         "unzbd crate target: x86_64-unknown-linux-musl")) {
        if ($notice.IndexOf($stamp, [StringComparison]::Ordinal) -lt 0) {
            $problems += "LICENSE-thirdparty.txt does not state '$stamp': it was not assembled for the Linux payload"
        }
    }
    foreach ($marker in @("win-x64", "x86_64-pc-windows-msvc", "data_CSVM_windows_x86_64", "unzbd.exe", "CSVM.exe")) {
        if ($notice.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $problems += "LICENSE-thirdparty.txt names '$marker', which is the Windows payload's"
        }
    }
}
# Linux text is LF. A CRLF file reads with a stray ^M on every line in a terminal pager or editor,
# and is what the autocrlf checkout hands the export unless it stages the committed bytes.
$textFiles = Join-Path $OutDir "textfiles.txt"
$textCount = 0
if (Test-Path $textFiles) {
    foreach ($line in [IO.File]::ReadAllLines($textFiles)) {
        if ($line -notmatch '^(\S+) (\d+)$') { continue }
        $textCount++
        if ([int]$Matches[2] -gt 0) {
            $problems += "$($Matches[1]) carries $($Matches[2]) carriage return(s): a Linux text file must be LF only"
        }
    }
}
if ($textCount -eq 0 -and (Read-Exit "unpack") -eq 0) {
    $problems += "found no top-level text file to check for carriage returns (textfiles.txt is empty)"
}
$fileCount = @($listing | Where-Object { -not $_.IsDir }).Count
foreach ($p in $problems) { Write-Host "  !! $p" -ForegroundColor Red }
Add-Stage "payload" $(if ($problems.Count -eq 0) { "PASS" } else { "FAIL" }) 0 `
    ("{0} file(s), {1} manifest name(s), {2} top-level text file(s){3}" -f $fileCount, $Expected.Count, $textCount, $(if ($problems.Count) { "; $($problems.Count) problem(s)" } else { "" }))

# --- extract ------------------------------------------------------------------------------------

Write-Banner "extract"
$extractExit = Read-Exit "extract"
$extractLog = Join-Path $OutDir "extract.log"
$stamped = Test-Path (Join-Path $OutDir "VERSION.json")
$extractSeconds = Read-Number "extract.seconds"
if ($null -eq $extractExit) {
    Add-Stage "extract" "FAIL" 0 "did not run: the archive did not unpack"
} else {
    $summary = @()
    if (Test-Path $extractLog) {
        $summary = @([IO.File]::ReadAllLines($extractLog) | Where-Object {
            $_ -match '^\s+(extracted|up to date|skipped|files extracted|movies copied):'
        } | ForEach-Object { $_.Trim() })
    }
    foreach ($s in $summary) { Write-Host "  $s" -ForegroundColor DarkGray }
    $ok = ($extractExit -eq 0) -and $stamped
    if (-not $ok) {
        Write-Host "  !! --extract exited $extractExit, VERSION.json stamped: $stamped; the log's last lines:" -ForegroundColor Red
        if (Test-Path $extractLog) {
            [IO.File]::ReadAllLines($extractLog) | Select-Object -Last 12 | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
        }
    }
    Add-Stage "extract" $(if ($ok) { "PASS" } else { "FAIL" }) $extractSeconds `
        ("exit {0}, stamped={1}, {2} files, {3} MB" -f $extractExit, $stamped, (Read-Number "extract.files"), (Read-Number "extract.mb"))
}

# --- engine -------------------------------------------------------------------------------------

$suiteRows = @()
if ($NoSuites) {
    $null = $Unchecked.Add("the in-engine suites did not run (-NoSuites)")
} elseif ($extractExit -ne 0 -or -not $stamped) {
    Add-Stage "engine" "FAIL" 0 "did not run: the extraction failed"
} else {
    Write-Banner "engine ($Shards shard(s), headless)"
    $problems = @()
    $seen = @{}
    $totals = @{}
    $allowSeen = @{}
    $allowMax = @{}
    $allowWhy = @{}
    $unexpected = [ordered]@{}
    $headlessErrors = [ordered]@{}
    foreach ($k in 1..$Shards) {
        $label = "shard $k/$Shards"
        $exit = Read-Exit "shard$k"
        $reportPath = Join-Path $OutDir "shard$k.report.json"
        if ($exit -eq 124) {
            $problems += "${label}: timed out after ${ShardTimeoutSec}s"
        }
        if (-not (Test-Path $reportPath)) {
            $problems += "${label}: exited $exit with no report; see shard$k.out"
            continue
        }
        $json = [IO.File]::ReadAllText($reportPath, $Utf8) | ConvertFrom-Json
        if ($exit -ne 0 -and $exit -ne 1 -and $exit -ne 124) {
            # A signal after the report is written is a crash in the export's teardown, the one a
            # render instance still alive at exit causes. A player's quit takes the same teardown,
            # so it fails the stage rather than trusting the report.
            $problems += "${label}: crashed at exit (exit $exit) after writing its report; see shard$k.out"
        }
        $totals[[string][int]$json.shard.selectedTotal] = 1
        foreach ($suite in @($json.suites)) {
            if ($seen.ContainsKey($suite.name)) { $problems += "suite '$($suite.name)' ran in more than one shard" }
            $seen[$suite.name] = 1
            $suiteRows += $suite
        }
        if (-not $json.engineErrors.screened) {
            $problems += "${label}: the engine log was not screened ($($json.engineErrors.logFile))"
        }
        foreach ($u in @($json.engineErrors.unexpected)) {
            $line = $u -replace '\s+', ' '
            $headless = $HeadlessEngineErrors.Keys | Where-Object { $line -match $_ } | Select-Object -First 1
            if ($headless) { $headlessErrors[$headless] = [int]$headlessErrors[$headless] + 1 }
            else { $unexpected[$line] = [int]$unexpected[$line] + 1 }
        }
        foreach ($a in @($json.engineErrors.allowlist)) {
            $allowSeen[$a.pattern] = [int]$allowSeen[$a.pattern] + [int]$a.seen
            $allowMax[$a.pattern] = [int]$a.max
            $allowWhy[$a.pattern] = $a.why
        }
    }
    # Run-wide caps, as RunTests.ps1 sums them: each shard sees only its share of an allowance.
    foreach ($pattern in $allowSeen.Keys) {
        if ($allowSeen[$pattern] -gt $allowMax[$pattern]) {
            $problems += "allowlisted engine error over its cap: $pattern seen $($allowSeen[$pattern])x, allowed $($allowMax[$pattern])x, $($allowWhy[$pattern])"
        }
    }
    foreach ($line in $unexpected.Keys) {
        $problems += "unexpected engine error ($($unexpected[$line])x): $line"
    }
    if ($totals.Count -gt 1) {
        $problems += "the shards disagree on the selection size ($($totals.Keys -join ', '))"
    } elseif ($totals.Count -eq 1) {
        $expectedTotal = [int]@($totals.Keys)[0]
        if ($seen.Count -ne $expectedTotal) {
            $problems += "the shards covered $($seen.Count) suite(s) of the registry's $expectedTotal"
        }
    }
    foreach ($name in $HeadlessOnly.Keys) {
        if ($seen.Count -gt 0 -and -not $seen.ContainsKey($name)) {
            $problems += "the headless-only list names '$name', which no shard ran: the suite was renamed or removed, so the list is stale"
        }
    }

    $pass = 0; $fail = 0; $harnessSkip = 0; $headlessSkip = 0; $stale = @()
    foreach ($suite in ($suiteRows | Sort-Object { [int]$_.index })) {
        $name = $suite.name
        if ($HeadlessOnly.Contains($name)) {
            $headlessSkip++
            if ($suite.status -eq "pass") { $stale += $name }
            continue
        }
        if ($suite.status -eq "pass") { $pass++; continue }
        if ($suite.status -eq "skip") {
            $harnessSkip++
            Write-Host ("  SKIP  {0}  {1}" -f $name, $suite.detail) -ForegroundColor Yellow
            continue
        }
        $fail++
        Write-Host ("  FAIL  {0}" -f $name) -ForegroundColor Red
        foreach ($f in @($suite.failures)) { Write-Host "          !! $f" -ForegroundColor Red }
    }
    foreach ($p in $problems) { Write-Host "  !! $p" -ForegroundColor Red }
    Write-Host ("  {0} passed, {1} failed, {2} skipped headless by construction, {3} skipped by the harness" -f $pass, $fail, $headlessSkip, $harnessSkip)
    foreach ($name in $HeadlessOnly.Keys) {
        $row = $suiteRows | Where-Object { $_.name -eq $name } | Select-Object -First 1
        $status = if ($row) { $row.status.ToUpperInvariant() } else { "NOT RUN" }
        Write-Host ("  skip  {0}  ({1} here) {2}" -f $name, $status, $HeadlessOnly[$name]) -ForegroundColor DarkGray
    }
    foreach ($pattern in $HeadlessEngineErrors.Keys) {
        Write-Host ("  skip  engine error /{0}/ seen {1}x: {2}" -f $pattern, [int]$headlessErrors[$pattern], $HeadlessEngineErrors[$pattern]) -ForegroundColor DarkGray
    }
    foreach ($name in $stale) {
        Write-Host "  note: '$name' is on the headless-only list and passed; if it passes on a second run, take it off the list" -ForegroundColor Yellow
    }
    $status = if ($fail -eq 0 -and $problems.Count -eq 0 -and $suiteRows.Count -gt 0) { "PASS" } else { "FAIL" }
    Add-Stage "engine" $status (Read-Number "engine.seconds") `
        ("{0} passed, {1} failed, {2} headless-only, {3} harness skips of {4}; {5} shard(s)" -f $pass, $fail, $headlessSkip, $harnessSkip, $suiteRows.Count, $Shards)
    if ($harnessSkip -gt 0) {
        $null = $Unchecked.Add("$harnessSkip in-engine suite(s) SKIPPED by the harness, normally for missing extracted data")
    }
    $null = $Unchecked.Add("$headlessSkip suite(s) that need a renderer or a display did not count (the headless-only list in this script)")
    $null = $Unchecked.Add("the goldens: WSL cannot render, so they stay Windows-only")
}

# --- summary ------------------------------------------------------------------------------------

Write-Banner "summary"
foreach ($s in $Stages) {
    Write-Host ("  {0,-8} {1,-5} {2,6}s  {3}" -f $s.Name, $s.Status, $s.Seconds.ToString("0", $Inv), $s.Detail) -ForegroundColor (Get-StatusColor $s.Status)
}
foreach ($u in $Unchecked) { Write-Host "  not checked: $u" -ForegroundColor Yellow }
Write-Host ("  total {0}s; results in {1}" -f $totalSeconds.ToString("0", $Inv), $OutDir)
$failed = @($Stages | Where-Object { $_.Status -ne "PASS" }).Count
if ($failed -gt 0) {
    Write-Host "LINUX CHECK FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "LINUX CHECK PASSED" -ForegroundColor Green
exit 0
