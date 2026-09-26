<#
.SYNOPSIS
    Launches ONE scripted Godot run (a --screenshot probe, a --dump-* report, an ad-hoc
    --run-tests) with its window on the hidden desktop and both output streams redirected
    to files -- so nothing flashes on screen and nothing prints over the calling terminal.

.DESCRIPTION
    The plain Godot binary is a Windows GUI-subsystem executable: started without std
    handles it calls AttachConsole(ATTACH_PARENT_PROCESS) and reopens stdout on CONOUT$,
    so its entire world-build chatter bypasses every pipe and lands on the console screen
    buffer of whatever terminal the run came from (verification SHELL-10). Its window also
    exists for the ~1 s boot before the engine can hide it (SHELL-13). RunTests.ps1 solves
    both internally through its private Invoke-Godot helper; this script is the same launch
    for everything else. Use it for ANY ad-hoc scripted run instead of invoking Godot
    directly.

    Does NOT build -- run `dotnet build CSVM/CSVM.sln` first if the code changed. Every
    argument is forwarded to CSVM verbatim (the part after `--` on a manual launch).
    Streams land beside the run's --log-file when one is passed, else under
    .scratch/logs/probe-<stamp>.out/.err; the script prints both paths and exits with
    Godot's exit code.

.PARAMETER Resolution
    Window size as WxH, forwarded as Godot's own `--resolution` BEFORE the `--` separator.
    Without it a probe renders at the project's 1280x720, and a saved option cannot raise it
    because --screenshot implies --det, which drops every saved option (DET-8). This is the
    only way a scripted capture lands at a size a player actually runs.

.PARAMETER TimeoutSec
    Kill the run if it has not exited after this many seconds and exit 124 (the GNU
    timeout convention, so "hung and killed" is distinguishable from "ran and failed").
    Defaults to 300 -- a probe is a scripted run that quits by itself, and an
    agent-driven session must never block forever on one that doesn't (a flag
    combination with no auto-quit, a stuck boot). Pass 0 for a deliberate open-ended
    run. The partial .out/.err streams survive the kill and are the evidence of where
    it hung.

.EXAMPLE
    .\RunProbe.ps1 --stage=empty --plane=player_bhawk --screenshot=Z:\CSVM\.scratch\probe.png

.EXAMPLE
    .\RunProbe.ps1 -TimeoutSec 60 --run-tests=weapons-fire
#>

# PositionalBinding=$false, or the first Godot flag would bind positionally to -TimeoutSec.
[CmdletBinding(PositionalBinding = $false)]
param(
    [int]$TimeoutSec = 300,
    [ValidatePattern('^\d+x\d+$')][string]$Resolution = "",
    # Everything else -- the Godot/CSVM arguments, forwarded verbatim. `--flag=value`
    # tokens never collide with the named parameter above (PowerShell only binds
    # single-dash names), so callers that pass no -TimeoutSec are untouched.
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$GodotArgs = @()
)

$ErrorActionPreference = "Stop"

$RepoRoot   = $PSScriptRoot
$ProjectDir = Join-Path $RepoRoot "CSVM"

# Same resolution as RunDev.ps1: tools/ is git-ignored, so a git worktree checkout has no
# Godot; CSVM_DATA_ROOT names the primary tree to borrow it from.
$GodotRel = "tools\godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe"
$GodotExe = Join-Path $RepoRoot $GodotRel
if ((-not (Test-Path $GodotExe)) -and $env:CSVM_DATA_ROOT) {
    $GodotExe = Join-Path $env:CSVM_DATA_ROOT $GodotRel
}
if (-not (Test-Path $GodotExe)) {
    throw "Godot not found at $GodotExe -- see PROJECT_CONTEXT.md for the tools/ setup."
}

$LogDir = Join-Path $RepoRoot ".scratch\logs"
if (-not (Test-Path $LogDir)) { $null = New-Item -ItemType Directory -Path $LogDir -Force }

# A window belongs to the desktop its process was started on; falls back to a visible run
# rather than failing (same policy as RunTests.ps1).
. (Join-Path $PSScriptRoot "HiddenDesktop.ps1")
$HiddenDesktop = Open-HiddenDesktop -Name "csvm-probe"

# Engine options belong before the `--`; everything after it is the CSVM argument list.
$Engine = @("--path", $ProjectDir, "res://scenes/Main.tscn")
if ($Resolution) { $Engine += @("--resolution", $Resolution) }
$Launch = $Engine + @("--") + @($GodotArgs)

# SHELL-19: the argument string is re-split by the callee, and a path may carry a space, so
# any argument carrying one is quoted here or Godot receives it split. Same rule as
# RunTests.ps1's Invoke-Godot: --flag=value keeps the flag outside the quotes.
$quoted = @()
foreach ($a in $Launch) {
    if ($a -match '\s' -and $a -notmatch '^".*"$') {
        if ($a -match '^(--[^=]+)=(.*)$') { $quoted += ('{0}="{1}"' -f $Matches[1], $Matches[2]) }
        else { $quoted += ('"{0}"' -f $a) }
    } else {
        $quoted += $a
    }
}

# Streams park beside the run's own --log-file when one was passed, so its evidence stays
# together; otherwise a timestamped probe-* base keeps concurrent runs from clobbering.
$streamBase = Join-Path $LogDir ("probe-{0:yyyyMMdd-HHmmss}-{1}" -f (Get-Date), $PID)
for ($i = 0; $i -lt $Launch.Count - 1; $i++) {
    if ($Launch[$i] -eq "--log-file") { $streamBase = $Launch[$i + 1] }
}

Write-Host ("probe: {0}" -f ($GodotArgs -join " ")) -ForegroundColor Cyan
if ($HiddenDesktop) {
    Write-Host ("desktop: {0}   streams: {1}.out / .err" -f [CSVMHiddenDesktop]::Name, $streamBase) -ForegroundColor DarkGray
    # CreateProcess takes ONE command line and it must carry argv[0] itself.
    $cmdLine = ('"{0}" {1}' -f $GodotExe, ($quoted -join " "))
    $code = Invoke-OnHiddenDesktop -Exe $GodotExe -CommandLine $cmdLine `
                                   -WorkingDirectory $RepoRoot `
                                   -StdOut "$streamBase.out" -StdErr "$streamBase.err" `
                                   -TimeoutSec $TimeoutSec
} else {
    # Refused a desktop: run visibly, but STILL with real std handles -- the console
    # scribble is the part that must never degrade back (SHELL-10).
    Write-Host ("desktop: REFUSED (visible run)   streams: {0}.out / .err" -f $streamBase) -ForegroundColor Yellow
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $GodotExe
    $psi.Arguments              = ($quoted -join " ")
    $psi.WorkingDirectory       = $RepoRoot
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    # Both pipes drained asynchronously before the wait, or a chatty launch fills the
    # ~4 KB buffer and deadlocks.
    $outRead = $p.StandardOutput.ReadToEndAsync()
    $errRead = $p.StandardError.ReadToEndAsync()
    if ($TimeoutSec -gt 0) {
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            $p.Kill()
            $p.WaitForExit()
            $code = 124
        } else {
            $code = $p.ExitCode
        }
    } else {
        $p.WaitForExit()
        $code = $p.ExitCode
    }
    [System.IO.File]::WriteAllText("$streamBase.out", $outRead.Result)
    [System.IO.File]::WriteAllText("$streamBase.err", $errRead.Result)
}

if ($code -eq 124) {
    Write-Host ("TIMEOUT after {0}s -- killed; partial streams: {1}.out / .err" -f $TimeoutSec, $streamBase) -ForegroundColor Red
} else {
    Write-Host ("exit: {0}" -f $code) -ForegroundColor $(if ($code -eq 0) { "Green" } else { "Red" })
}
exit $code
