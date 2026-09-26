<#
.SYNOPSIS
    Builds CSVM, exports the "Windows Desktop" release preset, and stages the whole
    friend-facing release payload in .scratch\export\. With -Linux it also exports the
    "Linux/X11" preset and packages the Linux tarball.

.DESCRIPTION
    The packaging entry point (see PROJECT_CONTEXT.md "Exporting a release build" and
    docs/tooling.md). Runs `dotnet build`, imports the project headless (needed once per
    fresh tree before an export can see every asset), exports the release preset defined
    in CSVM/export_presets.cfg, then copies the non-export pieces of the release listed
    in packaging/MANIFEST.md next to it, so .scratch\export\ is the zip's contents.

    The zip is CSVM-v<version>-win64.zip, named from CSVM/project.godot's
    application/config/version -- the same key the exported exe's file properties and the
    first line of every log state, so all three agree by construction.

    Copying is what keeps MANIFEST.md's "byte-identical to the repo source" rule true by
    construction: the READMEs and licences are taken from their one home in the
    repo on every export, never forked into a package variant that can drift.

    Two of the zip's files are about the build rather than part of it.
    LICENSE-thirdparty.txt is copied like any other payload row, but it names the Godot
    build, the .NET runtime version and the mech3ax commit it was assembled for, and this
    script re-checks all three against what it is packaging. BUILD-INFO.txt is the one
    generated file: it records the CSVM and mech3ax commits the two shipped binaries were
    built from, which is what lets a release page state the source each came from.

    Godot's export templates are user-global, not part of this repo, and there is no
    reliable way to install them unattended -- so this script checks for them first and
    throws a clear error naming the one-time setup step instead of letting Godot's own
    export fail cryptically partway through. unzbd.exe is checked the same way: it is a
    local fork build, not a repo artefact.

    -Linux is opt-in and leaves the Windows half untouched: the same run then exports the
    "Linux/X11" preset into .scratch\export-linux\, stages the Linux payload beside it and
    packs CSVM-v<version>-linux-x64.tar.gz inside WSL (Debian), because only a tar written
    on Linux carries the executable bit that CSVM.x86_64 and tools/unzbd need. A zip made
    here cannot, which is why the Linux download is a tarball.

.PARAMETER Linux
    Also export the Linux build and package the .tar.gz. Needs the Linux export templates
    and a WSL Debian distro with tar, plus, unless -LinuxUnzbd is given, the Rust musl
    toolchain in that distro (docs/tooling.md, "-Linux").

.PARAMETER LinuxUnzbd
    An existing Linux unzbd binary to ship as tools/unzbd, skipping the build. Without it,
    -Linux builds the mech3ax checkout for x86_64-unknown-linux-musl inside WSL (Debian;
    the cargo target dir is ~/cargo-target/mech3ax in the distro) and copies the binary
    to tools\mech3ax\target\x86_64-unknown-linux-musl\release\unzbd before shipping it.

.EXAMPLE
    .\ExportRelease.ps1
    Build, import, export to .scratch\export\CSVM.exe, and stage the release files.

.EXAMPLE
    .\ExportRelease.ps1 -Linux
    The same, then the Linux export in .scratch\export-linux\ and its tarball.
#>

param(
    [switch] $Linux,
    [string] $LinuxUnzbd = ""
)

$ErrorActionPreference = "Stop"

$RepoRoot   = $PSScriptRoot
$ProjectDir = Join-Path $RepoRoot "CSVM"
$Sln        = Join-Path $ProjectDir "CSVM.sln"

# tools/ is git-ignored, so a git worktree checkout has none of it. Fall back to the primary tree
# named by CSVM_DATA_ROOT, the same fallback RunTests.ps1 uses, so an export can be rehearsed from
# a worktree. A tree that has its own tools/ never reaches the fallback.
$ToolsRoot = $RepoRoot
if ((-not (Test-Path (Join-Path $RepoRoot "tools\godot"))) -and $env:CSVM_DATA_ROOT) {
    $ToolsRoot = $env:CSVM_DATA_ROOT
}
$GodotExe   = Join-Path $ToolsRoot "tools\godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe"

$TemplateDir = Join-Path $env:APPDATA "Godot\export_templates\4.7.stable.mono"
$ExportDir   = Join-Path $RepoRoot ".scratch\export"
$ExportExe   = Join-Path $ExportDir "CSVM.exe"
$UnzbdExe    = Join-Path $ToolsRoot "tools\mech3ax\target\release\unzbd.exe"
$ProjectGodot = Join-Path $ProjectDir "project.godot"
$Mech3axRepo  = Join-Path $ToolsRoot "tools\mech3ax"
$ThirdPartyNotices = Join-Path $RepoRoot "packaging\LICENSE-thirdparty.txt"
$BuildInfo    = Join-Path $ExportDir "BUILD-INFO.txt"

$LinuxExportDir = Join-Path $RepoRoot ".scratch\export-linux"
$LinuxExportExe = Join-Path $LinuxExportDir "CSVM.x86_64"
$LinuxDistro    = "Debian"
# A named -LinuxUnzbd is shipped as given; without one the run builds the default path itself.
$BuildLinuxUnzbd = -not $LinuxUnzbd
if (-not $LinuxUnzbd) {
    $LinuxUnzbd = Join-Path $ToolsRoot "tools\mech3ax\target\x86_64-unknown-linux-musl\release\unzbd"
}
# The Linux README, shipped as README.md at the tarball root like the zip's own. A separate
# file because the Windows one describes SmartScreen and Direct3D 12.
$LinuxReadme = Join-Path $RepoRoot "packaging\README-linux.md"

# The zip payload beside the export output, from packaging/MANIFEST.md. Sources are the
# files' one home in the repo, so a copy is byte-identical to what the manifest names.
$ReleaseFiles = @(
    @{ Source = Join-Path $RepoRoot "packaging\README.md";      Dest = "README.md" },
    @{ Source = Join-Path $RepoRoot "packaging\LICENSE";        Dest = "LICENSE" },
    @{ Source = Join-Path $RepoRoot "packaging\LICENSE-unzbd";  Dest = "LICENSE-unzbd" },
    @{ Source = $ThirdPartyNotices;                             Dest = "LICENSE-thirdparty.txt" },
    @{ Source = $UnzbdExe;                                      Dest = "tools\unzbd.exe" }
)

# The tarball payload, packaging/MANIFEST.md's Linux table: the zip's list with the Linux README and
# the Linux unzbd. Both platforms extract from inside the game, so neither ships a script.
$LinuxReleaseFiles = @(
    @{ Source = $LinuxReadme;                                   Dest = "README.md" },
    @{ Source = Join-Path $RepoRoot "packaging\LICENSE";        Dest = "LICENSE" },
    @{ Source = Join-Path $RepoRoot "packaging\LICENSE-unzbd";  Dest = "LICENSE-unzbd" },
    @{ Source = $ThirdPartyNotices;                             Dest = "LICENSE-thirdparty.txt" },
    @{ Source = $LinuxUnzbd;                                    Dest = "tools\unzbd" }
)

if (-not (Test-Path $Sln)) {
    throw "Solution not found at $Sln"
}
if (-not (Test-Path $GodotExe)) {
    throw "Godot not found at $GodotExe -- see PROJECT_CONTEXT.md for the tools/ setup."
}

# Export templates are a one-time, user-global install (see README.md "Package a release
# build"): the inner templates/ FILES of tools/godot-4.7-mono-export-templates.tpz extracted
# directly into $TemplateDir. Godot's own export otherwise fails partway through with an
# error that doesn't say what's missing, so check up front instead.
if (-not (Test-Path $TemplateDir)) {
    throw "Godot export templates not found at $TemplateDir -- one-time setup: extract the " +
        "inner templates\ files of tools\godot-4.7-mono-export-templates.tpz directly into " +
        "that folder (create the version dir; do not keep the templates\ folder level)."
}

# unzbd is built locally from the mech3ax fork (branch cs-anim, Decision 5) and is not a repo
# artefact, so a fresh tree can reach the export step without having it. Check before the long
# build rather than after, and never fall back to the pinned v0.6.1 binary: the fork's output is
# what the engine reads.
if (-not (Test-Path $UnzbdExe)) {
    throw "unzbd.exe not found at $UnzbdExe -- build the mech3ax fork (branch cs-anim) first; " +
        "see packaging\MANIFEST.md. The pinned v0.6.1 binary is not a substitute."
}

foreach ($file in $ReleaseFiles) {
    if (-not (Test-Path $file.Source)) {
        throw "Release payload file not found at $($file.Source) -- see packaging\MANIFEST.md."
    }
}

# The Linux half's three prerequisites live outside the repo, like the templates and unzbd.exe
# above, and each would otherwise surface only after the Windows export has spent its minutes.
if ($Linux) {
    $linuxTemplate = Join-Path $TemplateDir "linux_release.x86_64"
    if (-not (Test-Path $linuxTemplate)) {
        throw "Linux export template not found at $linuxTemplate -- the one-time template " +
            "install above must include the linux_release.x86_64 file of the .tpz."
    }
    if ((-not $BuildLinuxUnzbd) -and (-not (Test-Path $LinuxUnzbd))) {
        throw "Linux unzbd not found at $LinuxUnzbd -- pass an existing file to -LinuxUnzbd, or " +
            "leave it out to build the musl unzbd in WSL. unzbd.exe is not a substitute."
    }
    try {
        & wsl.exe -d $LinuxDistro --exec tar --version | Out-Null
        $wslExit = $LASTEXITCODE
    } catch {
        $wslExit = -1
    }
    if ($wslExit -ne 0) {
        throw "WSL distro '$LinuxDistro' with tar is not reachable (wsl.exe -d $LinuxDistro) -- " +
            "one-time setup: 'wsl --install -d Debian'. The tarball is packed inside it so the " +
            "executable bits survive."
    }
}

# PowerShell 5.1 mangles embedded double quotes in a native command's arguments, so every WSL step
# is a script file with LF endings, run with its paths passed as arguments.
function ConvertTo-WslPath([string] $Path) {
    $wslPath = (& wsl.exe -d $script:LinuxDistro --exec wslpath -u $Path)
    if ($LASTEXITCODE -ne 0 -or -not $wslPath) {
        throw "wslpath could not translate $Path for WSL."
    }
    return $wslPath.Trim()
}
function Write-WslScript([string] $Name, [string] $Text) {
    $path = Join-Path $script:RepoRoot ".scratch\$Name-$PID.sh"
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    [System.IO.File]::WriteAllText($path, ($Text -replace "`r`n", "`n"),
        (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

# The Linux unzbd is built from the same mech3ax checkout as unzbd.exe, so the two cannot drift
# apart between releases. The build runs before the Windows export because a missing toolchain
# would otherwise surface only after minutes of exporting. The cargo target dir sits in the
# distro's own filesystem: a build through /mnt/z is slow and would leave a Linux target tree
# inside the checkout. The toolchain checks exit with their own codes so each failure names the
# setup step that fixes it; rust-toolchain.toml pins the Rust version, which is why the musl
# target has to be added to that toolchain rather than to stable. cargo's stderr is folded into
# stdout because a caller that redirects this script's streams turns every native stderr line
# into a terminating error under "Stop", ending the run at cargo's first warning.
if ($Linux -and $BuildLinuxUnzbd) {
    $buildScript = Write-WslScript "build-unzbd" @'
set -eu
exec 2>&1
repo="$1"; out="$2"
target=x86_64-unknown-linux-musl
cargo="$HOME/.cargo/bin/cargo"; rustup="$HOME/.cargo/bin/rustup"
[ -x "$cargo" ] && [ -x "$rustup" ] || exit 10
command -v cc >/dev/null 2>&1 && command -v musl-gcc >/dev/null 2>&1 || exit 11
cd "$repo"
"$rustup" target list --installed 2>/dev/null | grep -qx "$target" || exit 12
export CARGO_TARGET_DIR="$HOME/cargo-target/mech3ax"
"$cargo" build --release --locked --target "$target" --bin unzbd
mkdir -p "$(dirname "$out")"
cp -- "$CARGO_TARGET_DIR/$target/release/unzbd" "$out"
"$out" --version
'@
    $pinnedRust = "the version tools\mech3ax\rust-toolchain.toml pins"
    $pinMatch = Select-String -Path (Join-Path $Mech3axRepo "rust-toolchain.toml") `
        -Pattern '^channel\s*=\s*"([^"]+)"' -ErrorAction SilentlyContinue
    if ($pinMatch) {
        $pinnedRust = $pinMatch.Matches[0].Groups[1].Value
    }
    Write-Host "Building the Linux unzbd in WSL ($LinuxDistro)..." -ForegroundColor Cyan
    try {
        & wsl.exe -d $LinuxDistro --exec sh (ConvertTo-WslPath $buildScript) `
            (ConvertTo-WslPath $Mech3axRepo) (ConvertTo-WslPath $LinuxUnzbd)
        $buildExit = $LASTEXITCODE
    } finally {
        Remove-Item $buildScript -Force -ErrorAction SilentlyContinue
    }
    $setup = "one-time setup in WSL $LinuxDistro, see docs/tooling.md '-Linux'"
    switch ($buildExit) {
        0 { }
        10 {
            throw "rustup/cargo not found in ~/.cargo/bin in WSL $LinuxDistro -- $setup`: " +
                "install rustup from https://rustup.rs, or pass -LinuxUnzbd <path>."
        }
        11 {
            throw "No C compiler or musl-gcc in WSL $LinuxDistro -- $setup`: " +
                "'sudo apt install build-essential musl-tools'."
        }
        12 {
            throw "Rust $pinnedRust has no x86_64-unknown-linux-musl target in WSL $LinuxDistro " +
                "-- $setup`: 'rustup target add --toolchain $pinnedRust x86_64-unknown-linux-musl'."
        }
        default {
            throw "cargo build of the Linux unzbd failed in WSL $LinuxDistro (exit $buildExit)."
        }
    }
}

if ($Linux) {
    foreach ($file in $LinuxReleaseFiles) {
        if (-not (Test-Path $file.Source)) {
            throw "Linux payload file not found at $($file.Source) -- see packaging\MANIFEST.md."
        }
    }
}

# LICENSE-thirdparty.txt speaks for three payloads whose versions it names in its own header
# (packaging\BuildThirdPartyNotices.ps1 assembles it). A notice assembled for a different
# engine, runtime or fork commit is worse than none: it states, in the zip, obligations that
# belong to software the zip does not contain. So the header is read back and re-checked
# against what this run is actually packaging. Read through ReadAllText because the file is
# BOM-less UTF-8 and 5.1's own readers would decode it as ANSI (verification.md SHELL-7).
$noticeText = [System.IO.File]::ReadAllText($ThirdPartyNotices)
function Get-NoticeStamp([string] $Label, [string] $Pattern) {
    $match = [regex]::Match($script:noticeText, $Pattern)
    if (-not $match.Success) {
        throw "packaging\LICENSE-thirdparty.txt states no $Label -- regenerate it with " +
            "packaging\BuildThirdPartyNotices.ps1."
    }
    return $match.Groups[1].Value
}
$noticeGodot   = Get-NoticeStamp "Godot Engine build" 'Godot Engine build: (\S+)'
$noticeRuntime = Get-NoticeStamp ".NET runtime version" '\.NET runtime version: (\S+)'
$noticeFork    = Get-NoticeStamp "mech3ax cs-anim commit" 'mech3ax cs-anim commit: ([0-9a-f]{40})'

$godotBuild = (& $GodotExe --version | Select-Object -Last 1).Trim()
if ($godotBuild -ne $noticeGodot) {
    throw "packaging\LICENSE-thirdparty.txt was assembled for Godot $noticeGodot but this " +
        "export runs $godotBuild -- regenerate it with packaging\BuildThirdPartyNotices.ps1."
}

# The fork commit is checked, not assumed, for the reason the notice exists: the crate list in
# its section 6 is an enumeration of one commit's dependency tree, and a moved cs-anim can add
# a crate the notice does not name. Whether that commit is PUSHED is a separate question, and
# it is recorded rather than enforced here -- the fork is iterated on locally all the time and
# an unpushed commit only becomes a false claim at publish time, where PublishRelease.ps1
# refuses it.
$forkCommit = (& git -C $Mech3axRepo rev-parse cs-anim 2>$null)
if ($LASTEXITCODE -ne 0 -or $forkCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve cs-anim in $Mech3axRepo -- the bundled unzbd.exe's source commit " +
        "is part of the release, so the export will not guess it."
}
if ($forkCommit -ne $noticeFork) {
    throw "packaging\LICENSE-thirdparty.txt enumerates cs-anim $noticeFork but the fork is at " +
        "$forkCommit -- regenerate it with packaging\BuildThirdPartyNotices.ps1."
}
$forkPushed = ((& git -C $Mech3axRepo rev-parse origin/cs-anim 2>$null) -eq $forkCommit)
$forkDirty  = [bool] (& git -C $Mech3axRepo status --porcelain)
$csvmCommit = (& git -C $RepoRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $csvmCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve HEAD in $RepoRoot -- the exe's source commit is part of the release."
}
$csvmDirty = [bool] (& git -C $RepoRoot status --porcelain)

# The version has one home: project.godot's application/config/version, which the engine reads at
# startup for the log's first line and the launchscreen's corner, and which the export stamps into
# the exe. Read back here so the zip's name cannot disagree with what is inside it. -Encoding utf8
# because 5.1 decodes a BOM-less file as ANSI (CLAUDE.md); the key itself is ASCII, the file is not
# necessarily. Exactly one match, so a second definition is an error rather than a coin toss.
$versionMatch = @(Get-Content $ProjectGodot -Encoding utf8 | Select-String -Pattern '^config/version="([^"]+)"')
if ($versionMatch.Count -ne 1) {
    throw "Expected exactly one config/version in $ProjectGodot, found $($versionMatch.Count) -- " +
        "the release's version number lives there and nowhere else (docs/tooling.md)."
}
$Version = $versionMatch[0].Matches[0].Groups[1].Value
$ZipPath = Join-Path $RepoRoot ".scratch\CSVM-v$Version-win64.zip"
$TarPath = Join-Path $RepoRoot ".scratch\CSVM-v$Version-linux-x64.tar.gz"
Write-Host "Version $Version (CSVM\project.godot)" -ForegroundColor Cyan

# The staging folder is rebuilt from nothing each run, because everything in it is copied into
# the archive: a file left by an earlier export or a hand assembly would otherwise ship forever.
# PowerShell 5.1's recursive delete FOLLOWS directory junctions into their target (see
# CleanScratch.ps1), so refuse to sweep a folder someone has linked something into rather than
# deleting whatever is on the far side of the link.
function Clear-StagingDir([string] $Dir) {
    if (Test-Path $Dir) {
        $links = Get-ChildItem $Dir -Recurse -Directory -Force |
            Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }
        if ($links) {
            throw "$Dir contains a junction or symlink ($($links[0].FullName)) -- remove it " +
                "by hand; a recursive delete here would delete the link's target."
        }
        Write-Host "Clearing $Dir..." -ForegroundColor Cyan
        # Emptied, not deleted: a shell or a running build sitting in the folder holds the
        # directory itself open, and removing its contents works where removing the folder fails.
        # A running exported build still holds its own exe, which Godot reports much later and far
        # less clearly, as a failure to rename its temporary file after the whole pack is done.
        try {
            Get-ChildItem $Dir -Force | Remove-Item -Recurse -Force -ErrorAction Stop
        } catch {
            throw "Could not clear $Dir -- close the exported build if it is still running. " +
                "($($_.Exception.Message))"
        }
    } else {
        New-Item -ItemType Directory -Force $Dir | Out-Null
    }
}
Clear-StagingDir $ExportDir
if ($Linux) {
    Clear-StagingDir $LinuxExportDir
}

Write-Host "Building CSVM..." -ForegroundColor Cyan
dotnet build $Sln
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed (exit $LASTEXITCODE)."
}

Write-Host "Importing project..." -ForegroundColor Cyan
& $GodotExe --path $ProjectDir --headless --import
if ($LASTEXITCODE -ne 0) {
    throw "Godot import failed (exit $LASTEXITCODE)."
}

# NOT --headless, unlike the import above: the preset's Shader Baker compiles the pipeline
# variants into the pack, and it needs a live rendering device on the renderer the target will
# use. Headless has a dummy one, so the bake is skipped in silence and the export still reports
# success -- the payload just ships without it and every player pays the compile at first draw.
# The driver and method are pinned rather than left to the editor's own setting for the same
# reason: baking on a different renderer than the target cannot include the core shaders.
# A real editor rewrites project.godot on startup: same values, but its own key order and NONE
# of the comments, so an export would silently strip every decode the file carries. Snapshot and
# restore it byte-for-byte around the run. Copy-Item both ways rather than a text round-trip,
# which is what keeps PowerShell 5.1's ANSI default away from the file (see CLAUDE.md). This is
# the file's only writer: the version above is READ from it, never stamped into it.
function Invoke-PresetExport([string] $Preset, [string] $OutFile) {
    $stagingDir = Split-Path $OutFile -Parent
    Write-Host "Exporting release build to $OutFile..." -ForegroundColor Cyan
    $backup = Join-Path $env:TEMP "csvm-project-godot-$PID.bak"
    Copy-Item $script:ProjectGodot $backup -Force

    $log = Join-Path $stagingDir "export.log"
    try {
        & $script:GodotExe --path $script:ProjectDir --rendering-driver vulkan `
            --rendering-method forward_plus --export-release $Preset $OutFile |
            Tee-Object -FilePath $log
        $exportExit = $LASTEXITCODE
    } finally {
        # In a finally so a failed or interrupted export cannot leave the stripped file behind.
        Copy-Item $backup $script:ProjectGodot -Force
        Remove-Item $backup -Force
    }
    if ($exportExit -ne 0) {
        throw "Godot export of '$Preset' failed (exit $exportExit)."
    }

    # A skipped bake is the failure this script cannot see any other way: it costs no exit code,
    # no warning and no missing file, only a slower first draw on someone else's machine. Assert
    # the stage ran rather than trusting the flag, since the preset key and the renderer have to
    # agree for it to do anything.
    if (-not (Select-String -Path $log -Pattern "baking_shaders" -Quiet)) {
        throw "Export of '$Preset' finished but baked no shaders -- check shader_baker/enabled " +
            "in CSVM\export_presets.cfg and that this export ran with a real rendering device."
    }
    Remove-Item $log -Force
}

# The self-contained publish's runtime version is only knowable after the export has produced
# it, which is why this is the one notice stamp checked after the export rather than up front.
# It moves whenever the SDK does, silently, and it is what sections 4 and 5 of the notice quote.
function Assert-ExportRuntime([string] $DataDir) {
    $runtimeConfig = Join-Path $DataDir "CSVM.runtimeconfig.json"
    if (-not (Test-Path $runtimeConfig)) {
        throw "Export produced no $runtimeConfig -- the preset's .NET publish did not run."
    }
    $exportRuntime = ([System.IO.File]::ReadAllText($runtimeConfig) |
        ConvertFrom-Json).runtimeOptions.includedFrameworks[0].version
    if ($exportRuntime -ne $script:noticeRuntime) {
        throw "packaging\LICENSE-thirdparty.txt was assembled for .NET runtime " +
            "$($script:noticeRuntime) but the export bundles $exportRuntime -- regenerate it " +
            "with packaging\BuildThirdPartyNotices.ps1."
    }
}

function Copy-ReleaseFiles($Files, [string] $Dir) {
    Write-Host "Staging release files..." -ForegroundColor Cyan
    foreach ($file in $Files) {
        $dest = Join-Path $Dir $file.Dest
        $destDir = Split-Path $dest -Parent
        if (-not (Test-Path $destDir)) {
            New-Item -ItemType Directory -Force $destDir | Out-Null
        }
        Copy-Item $file.Source $dest -Force
        Write-Host "  $($file.Dest)"
    }
}

# BUILD-INFO.txt is the one payload file that is GENERATED rather than copied from packaging\,
# because the fact it states -- which commit each shipped binary was built from -- is different
# on every run and has no repo home that could hold it. It states the two qualifiers honestly
# instead of refusing: an export off a dirty tree is the normal development case, and
# PublishRelease.ps1 is where a qualifier becomes a refusal, since only a published binary
# makes a false source-correspondence claim to anybody. UTF8Encoding($false) rather than
# Set-Content, whose 5.1 default is ANSI (CLAUDE.md). The names and line endings are the
# target platform's, so the file reads as native in the archive it ships in.
function Write-BuildInfo([string] $Path, [string] $ExeLine, [string] $UnzbdName, [string] $Newline) {
    $text = @"
CSVM build provenance
=====================

Every binary in this archive is built from public source. These are the commits.

$ExeLine
  version:  $script:Version
  source:   https://github.com/Laeresh/CSVM
  commit:   $script:csvmCommit
  worktree: $(if ($script:csvmDirty) { "MODIFIED -- this build does not match the commit above" } else { "clean" })

$UnzbdName
  source:   https://github.com/Laeresh/mech3ax  (branch cs-anim)
  commit:   $script:forkCommit
  pushed:   $(if ($script:forkPushed) { "yes, origin/cs-anim is at this commit" } else { "NO -- this commit is not on origin/cs-anim" })
  worktree: $(if ($script:forkDirty) { "MODIFIED -- this build does not match the commit above" } else { "clean" })

CSVM's own licence is LICENSE (GPL-3) and unzbd's is LICENSE-unzbd (EUPL-1.2).
The notices for the third-party software inside both binaries, including the
Godot engine and the .NET runtime, are in LICENSE-thirdparty.txt.
"@
    $text = ($text -replace "`r`n", "`n") -replace "`n", $Newline
    [System.IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  BUILD-INFO.txt (generated)"
    if ($script:csvmDirty)   { Write-Host "  ! CSVM worktree is dirty; BUILD-INFO.txt says so" -ForegroundColor Yellow }
    if ($script:forkDirty)   { Write-Host "  ! mech3ax worktree is dirty; BUILD-INFO.txt says so" -ForegroundColor Yellow }
    if (-not $script:forkPushed) { Write-Host "  ! cs-anim is not pushed; BUILD-INFO.txt says so" -ForegroundColor Yellow }
}

# The .NET publish leaves scanner shadow copies (name~RFxxxxxxx.TMP) in the data folder, and
# Godot's own CSVM.tmp survives a failed embed. Both are junk a recipient must not receive, and
# a locked one aborts the whole archive midway, so drop them and skip them when archiving.
function Remove-ExportJunk([string] $Dir) {
    Get-ChildItem $Dir -Recurse -File -Include "*.TMP", "*.tmp" |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

Invoke-PresetExport "Windows Desktop" $ExportExe

# Whether the version reached the exe is not something the exit code can say: with the preset's
# application/modify_resources off, the export succeeds and ships an exe whose properties still
# name Godot's own export template. Read the stamp back instead of trusting the flag.
$exeInfo = (Get-Item $ExportExe).VersionInfo
if ($exeInfo.FileVersion -notlike "$Version*" -or $exeInfo.ProductVersion -notlike "$Version*") {
    throw "Exported exe states file version '$($exeInfo.FileVersion)' and product version " +
        "'$($exeInfo.ProductVersion)', neither of them $Version -- check application/modify_resources " +
        "and the application/*_version keys in CSVM\export_presets.cfg."
}

Assert-ExportRuntime (Join-Path $ExportDir "data_CSVM_windows_x86_64")
Copy-ReleaseFiles $ReleaseFiles $ExportDir
Write-BuildInfo $BuildInfo "CSVM.exe, and data_CSVM_windows_x86_64\ beside it" "tools\unzbd.exe" "`r`n"
Remove-ExportJunk $ExportDir

# The zip lands beside the staging folder, not inside it: an archiver walking a directory it is
# writing into is how a release zip ends up containing a truncated copy of itself. Built through
# ZipFile rather than Compress-Archive, whose per-file errors are non-terminating: it reports
# success having written nothing, and the missing zip is only noticed on the next hand-off.
Write-Host "Packaging $ZipPath..." -ForegroundColor Cyan
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$prefix = (Get-Item $ExportDir).FullName.TrimEnd("\") + "\"
$entries = Get-ChildItem $ExportDir -Recurse -File |
    Where-Object { $_.Extension -notin @(".tmp", ".TMP") }
$zip = [System.IO.Compression.ZipFile]::Open($ZipPath, "Create")
try {
    foreach ($entry in $entries) {
        $relative = $entry.FullName.Substring($prefix.Length)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $entry.FullName, $relative, "Optimal") | Out-Null
    }
} finally {
    $zip.Dispose()
}

Write-Host "Exported to $ExportExe" -ForegroundColor Green
Write-Host "Packaged  $ZipPath ($($entries.Count) files)" -ForegroundColor Green

if (-not $Linux) {
    return
}

# The Linux half. Same export, checks and payload discipline as above; what differs is the
# preset, the payload list, and that the archive is written inside WSL.
Invoke-PresetExport "Linux/X11" $LinuxExportExe
Assert-ExportRuntime (Join-Path $LinuxExportDir "data_CSVM_linuxbsd_x86_64")
Copy-ReleaseFiles $LinuxReleaseFiles $LinuxExportDir
Write-BuildInfo (Join-Path $LinuxExportDir "BUILD-INFO.txt") `
    "CSVM.x86_64, and data_CSVM_linuxbsd_x86_64/ beside it" "tools/unzbd" "`n"
Remove-ExportJunk $LinuxExportDir

# Files on a Windows drive have no Unix mode of their own (WSL reports every one as 0777), so the
# modes are set on a copy in the distro's own filesystem and the tar is written from there: 0755
# for the two executables and every directory, 0644 for everything else, root-owned so an unpack
# by any user does not try to restore this machine's uid. Entries sit at the archive root, as they
# do in the zip.
$packScript = Write-WslScript "pack-linux" @'
set -eu
src="$1"; out="$2"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
cp -R -- "$src/." "$tmp/"
find "$tmp" -type d -exec chmod 0755 {} +
find "$tmp" -type f -exec chmod 0644 {} +
chmod 0755 "$tmp/CSVM.x86_64" "$tmp/tools/unzbd"
rm -f -- "$out"
cd "$tmp"
tar --create --gzip --file="$out" --owner=0 --group=0 --numeric-owner --sort=name -- *
tar --list --verbose --gzip --file="$out"
'@

Write-Host "Packaging $TarPath in WSL ($LinuxDistro)..." -ForegroundColor Cyan
try {
    $listing = @(& wsl.exe -d $LinuxDistro --exec sh (ConvertTo-WslPath $packScript) `
        (ConvertTo-WslPath $LinuxExportDir) (ConvertTo-WslPath $TarPath))
    $packExit = $LASTEXITCODE
} finally {
    Remove-Item $packScript -Force -ErrorAction SilentlyContinue
}
if ($packExit -ne 0 -or -not (Test-Path $TarPath)) {
    throw "Packing $TarPath in WSL failed (exit $packExit)."
}

# Read the modes back out of the archive rather than trusting the chmod: a tarball whose
# executable lost its bit unpacks without complaint and fails only when a player runs it.
foreach ($exe in @("CSVM.x86_64", "tools/unzbd")) {
    $line = $listing | Where-Object { $_ -match "\s$([regex]::Escape($exe))$" }
    if (-not $line -or $line -notmatch '^-rwxr-xr-x ') {
        throw "$TarPath does not mark $exe executable (listing: '$line')."
    }
}
$tarFiles = @($listing | Where-Object { $_ -match '^-' }).Count
$TarSha256 = (Get-FileHash $TarPath -Algorithm SHA256).Hash.ToLower()

Write-Host "Exported to $LinuxExportExe" -ForegroundColor Green
Write-Host "Packaged  $TarPath ($tarFiles files)" -ForegroundColor Green
Write-Host "SHA-256   $TarSha256" -ForegroundColor Green
