# Sandbox driver: what the exported build does on the machine it was dropped into.
#
# Runs inside the sandbox, never on the host. It unzips the release, launches CSVM.exe
# the way a recipient double-clicks it, and records what a person at that machine would
# see: the windows that appeared, the text of any dialog, screenshots, the log the build
# wrote, and its exit code. When the plain launch does not reach a running game it tries
# the renderer flags in turn, so a working fallback becomes a documented troubleshooting
# line rather than a guess.
param(
    [string]$Root = 'C:\Users\WDAGUtilityAccount\Desktop'
)

$ErrorActionPreference = 'Continue'
$In = Join-Path $Root 'input'
$Out = Join-Path $Root 'output'
$App = 'C:\CSVM'
$WatchSeconds = 45

. (Join-Path $PSScriptRoot 'SandboxCommon.ps1')

Write-Step 'driver started'
New-Item -ItemType Directory -Path $Out -Force | Out-Null
$facts = Get-MachineFacts
Write-Step ('GPUs: {0}' -f (($facts.gpus | ForEach-Object { $_.name }) -join ', '))
Write-Step ('vulkan loader: {0}, ICDs: {1}' -f $facts.vulkanLoader, $facts.vulkanIcds.Count)

$zip = Get-ChildItem $In -Filter *.zip | Select-Object -First 1
Write-Step ('unzipping {0}' -f $zip.Name)
if (Test-Path $App) { Remove-Item $App -Recurse -Force }
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip.FullName, $App)

$runs = @()
$runs += Invoke-Run -Name 'default' -Arguments @() -Seconds $WatchSeconds
if (-not $runs[0].success) {
    Write-Step 'plain launch did not reach the game; trying the renderer flags'
    $runs += Invoke-Run -Name 'd3d12' -Arguments @('--rendering-driver', 'd3d12') -Seconds $WatchSeconds
    $runs += Invoke-Run -Name 'gl-compatibility' -Arguments @('--rendering-method', 'gl_compatibility') -Seconds $WatchSeconds
    $runs += Invoke-Run -Name 'opengl3' -Arguments @('--rendering-driver', 'opengl3', '--rendering-method', 'gl_compatibility') -Seconds $WatchSeconds
}

# Whether the machine renders the menu says nothing about whether it can fly, so a mapped
# extraction tree buys the second question an answer: the flight's own [perf] rate lines.
# A flight-mode run does not exit inside the sandbox, so it is watched and then closed like
# any other; the frame rate is read from the log, not from how it ended.
if (Test-Path (Join-Path $Root 'extracted')) {
    Write-Step 'an extraction tree is mapped; flying a chapter to measure the frame rate'
    # The engine reads its own flags out of the user args, so they go after the bare `--`;
    # passed before it they reach Godot, which ignores them, and the run silently becomes a
    # plain menu launch. --no-vsync is what makes the rate mean anything: the sandbox
    # presents at 32 Hz and every capped run reports that number whatever it could do.
    $runs += Invoke-Run -Name 'flight' -Seconds 75 -Arguments @(
        '--', "--data-root=$Root", '--fly', '--chapter=C1', '--plane=player_fury', '--no-vsync')
}

Complete-Driver ([ordered]@{
    zip     = $zip.Name
    machine = $facts
    runs    = $runs
})
