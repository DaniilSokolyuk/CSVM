<#
.SYNOPSIS
    Extracts a Crimson Skies install into extracted\ by running the engine's own
    --extract headless. Holds no extraction logic of its own.

.DESCRIPTION
    The developer's door to the one extraction implementation (CSVM\src\Extraction,
    docs\architecture\Extraction.md). It builds the solution, then runs
    `Godot --headless ... -- --extract=<install> --data-root=<root> --unzbd=<tool>`
    with the matching development flags, prints the engine's output as it arrives,
    and exits with the engine's exit code (0 on success, 1 on any failure).

    A player never needs this script: the game's own Extract screen runs the same
    pipeline. Options and their rules are in docs\cli.md under --extract.

    tools\ and CrimsonSkiesGame\ are git-ignored, so a git worktree has neither.
    The Godot binary, the default install and the default unzbd fall back to the
    tree named by CSVM_DATA_ROOT, the same fallback RunGame.ps1 and RunProbe.ps1
    use. The data root does NOT fall back: it defaults to this script's own folder,
    so a worktree run never writes the primary tree's extracted\ by accident.

.PARAMETER Install
    The Crimson Skies install folder (the one holding ZBD and GOSDATA side by side).
    Default: CrimsonSkiesGame next to this script, else under CSVM_DATA_ROOT.

.PARAMETER DataRoot
    The folder whose extracted\ is written. Default: this script's folder.

.PARAMETER Unzbd
    The unzbd executable. Default: the fork build at
    tools\mech3ax\target\release\unzbd.exe (`cargo build --release` in
    tools\mech3ax), next to this script, else under CSVM_DATA_ROOT. Pass the pinned
    tools\mech3ax-v0.6.1-...\unzbd.exe to fall back to the old binary; the loaders
    read either extraction shape (docs\formats\extraction.md).

.PARAMETER Unzip
    Also expand every extracted .zip into a sibling folder, the dev tree's shape
    (--extract-unzip).

.PARAMETER Force
    Re-extract archives the stamp calls up to date (--extract-force).

.PARAMETER NoBuild
    Skip the `dotnet build` of CSVM\CSVM.sln before the run.

.EXAMPLE
    .\Extract.ps1 -Unzip
    Extract CrimsonSkiesGame into extracted\ and unpack every zip beside it.

.EXAMPLE
    .\Extract.ps1 -DataRoot .scratch\data -Unzbd Z:\CSVM\tools\mech3ax\target\release\unzbd.exe
    A worktree run into a scratch tree.
#>

[CmdletBinding()]
param(
    [string] $Install,
    [string] $DataRoot,
    [string] $Unzbd,
    [switch] $Unzip,
    [switch] $Force,
    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"

$RepoRoot   = $PSScriptRoot
$ProjectDir = Join-Path $RepoRoot "CSVM"
$Sln        = Join-Path $ProjectDir "CSVM.sln"

# A git-ignored file is looked up next to this script first, then in the CSVM_DATA_ROOT tree.
function Resolve-Ignored([string] $Relative) {
    $here = Join-Path $RepoRoot $Relative
    if ((Test-Path $here) -or (-not $env:CSVM_DATA_ROOT)) { return $here }
    $there = Join-Path $env:CSVM_DATA_ROOT $Relative
    if (Test-Path $there) { return $there }
    return $here
}

$GodotExe = Resolve-Ignored "tools\godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe"
if (-not (Test-Path $GodotExe)) {
    throw "Godot not found at $GodotExe -- see PROJECT_CONTEXT.md for the tools/ setup. In a git worktree, set `$env:CSVM_DATA_ROOT to the primary tree."
}

if (-not $Install) { $Install = Resolve-Ignored "CrimsonSkiesGame" }
if (-not $Unzbd)   { $Unzbd   = Resolve-Ignored "tools\mech3ax\target\release\unzbd.exe" }
if (-not $DataRoot) { $DataRoot = $RepoRoot }

# The engine resolves relative paths against its own working directory, so hand it absolute
# ones. A missing install or tool is left for the engine to refuse, with its own message.
$here     = (Get-Location).ProviderPath
$Install  = [IO.Path]::GetFullPath([IO.Path]::Combine($here, $Install))
$Unzbd    = [IO.Path]::GetFullPath([IO.Path]::Combine($here, $Unzbd))
$DataRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($here, $DataRoot))
if (-not (Test-Path $DataRoot)) { $null = New-Item -ItemType Directory -Path $DataRoot -Force }

if (-not $NoBuild) {
    Write-Host "Building CSVM..." -ForegroundColor Cyan
    dotnet build $Sln --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
}

$CsvmArgs = @("--extract=$Install", "--data-root=$DataRoot", "--unzbd=$Unzbd")
if ($Unzip) { $CsvmArgs += "--extract-unzip" }
if ($Force) { $CsvmArgs += "--extract-force" }

# --headless opens no window at all, so no hidden desktop is needed. Real std handles are:
# without them the GUI-subsystem exe attaches to this console and prints over it (SHELL-10).
$Launch = @("--headless", "--path", $ProjectDir, "res://scenes/Main.tscn", "--") + $CsvmArgs

# SHELL-19: the callee re-splits the argument string, so an argument with a space is quoted;
# --flag=value keeps the flag outside the quotes. Same rule as RunProbe.ps1.
$quoted = @()
foreach ($a in $Launch) {
    if ($a -match '\s' -and $a -notmatch '^".*"$') {
        if ($a -match '^(--[^=]+)=(.*)$') { $quoted += ('{0}="{1}"' -f $Matches[1], $Matches[2]) }
        else { $quoted += ('"{0}"' -f $a) }
    } else {
        $quoted += $a
    }
}

Write-Host ("extract: {0}" -f ($CsvmArgs -join " ")) -ForegroundColor Cyan
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName               = $GodotExe
$psi.Arguments              = ($quoted -join " ")
$psi.WorkingDirectory       = $RepoRoot
$psi.UseShellExecute        = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$p = [System.Diagnostics.Process]::Start($psi)
# stderr drains asynchronously while stdout is echoed line by line, or a chatty stream fills
# its pipe buffer and the two ends deadlock.
$errRead = $p.StandardError.ReadToEndAsync()
while ($null -ne ($line = $p.StandardOutput.ReadLine())) { Write-Host $line }
$p.WaitForExit()
$err = $errRead.Result
if ($err) { Write-Host $err.TrimEnd() -ForegroundColor DarkYellow }

$code = $p.ExitCode
Write-Host ("exit: {0}" -f $code) -ForegroundColor $(if ($code -eq 0) { "Green" } else { "Red" })
exit $code
