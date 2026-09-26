<#
.SYNOPSIS
    Runs a release zip in a clean Windows Sandbox and collects what happened.

.DESCRIPTION
    Builds a .wsb config plus its mapped folders under .scratch\sandbox\, starts Windows
    Sandbox on it, and waits for the driver script to write done.txt into the writable
    output folder. -NoVGpu is the below-the-floor machine: a sandbox with <VGpu>Disable</VGpu>
    has no Vulkan at all, which is the only hardware the author can put the build on that
    the renderer floor excludes.

.PARAMETER Zip
    The release zip to test. Defaults to the newest .scratch\CSVM-v*-win64.zip.

.PARAMETER Driver
    The script the sandbox runs. It is copied in as input\Driver.ps1 and invoked once the
    mapped folders are up; it is responsible for writing output\done.txt when finished.

.PARAMETER MapReadOnly
    Extra host folders to map read-only onto the sandbox desktop under their own leaf name,
    for a driver that needs something the zip does not carry, such as a retail install.

.PARAMETER NoVGpu
    Disable the virtual GPU, putting the machine below the renderer floor.

.PARAMETER Networking
    Give the sandbox a network. Off by default; a run that records SmartScreen needs it,
    because the verdict on an unknown download is fetched rather than decided locally.

.PARAMETER MemoryMB
    Guest memory. It matters below the floor, where Direct3D 12's software rasterizer
    allocates its buffers out of system memory rather than out of a card.

.PARAMETER KeepOpen
    Leave the sandbox running after the driver finishes, to look at the screen yourself.

.EXAMPLE
    .\RunSandbox.ps1 -NoVGpu
#>
[CmdletBinding()]
param(
    [string]$Zip,
    [string]$Driver = (Join-Path $PSScriptRoot 'sandbox\RendererFloor.ps1'),
    [string[]]$MapReadOnly = @(),
    [switch]$NoVGpu,
    [switch]$Networking,
    [int]$MemoryMB = 8192,
    [int]$TimeoutMinutes = 25,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'

$sandboxExe = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
if (-not (Test-Path $sandboxExe)) {
    throw "Windows Sandbox is not installed: $sandboxExe is missing. Enable the 'Windows Sandbox' optional feature."
}
# A second session started while one is live comes up with no mapped folders and no driver,
# and the client process is named WindowsSandboxRemoteSession, not WindowsSandboxClient.
$sandboxProcesses = @('WindowsSandbox', 'WindowsSandboxClient', 'WindowsSandboxRemoteSession', 'WindowsSandboxServer')
if (Get-Process -Name $sandboxProcesses -ErrorAction SilentlyContinue) {
    throw 'A Windows Sandbox session is already running. Close its WINDOW (never kill its processes) and wait for it to drain.'
}
if (-not (Test-Path $Driver)) { throw "Driver script not found: $Driver" }

if (-not $Zip) {
    $Zip = (Get-ChildItem (Join-Path $PSScriptRoot '.scratch') -Filter 'CSVM-v*-win64.zip' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1).FullName
    if (-not $Zip) { throw 'No release zip found in .scratch\. Run .\ExportRelease.ps1 first, or pass -Zip.' }
}
$Zip = (Resolve-Path $Zip).Path

$vGpu = if ($NoVGpu) { 'Disable' } else { 'Enable' }
$runName = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), $(if ($NoVGpu) { 'novgpu' } else { 'vgpu' })
$runDir = Join-Path $PSScriptRoot ".scratch\sandbox\$runName"
$inDir = Join-Path $runDir 'input'
$outDir = Join-Path $runDir 'output'
New-Item -ItemType Directory -Path $inDir, $outDir -Force | Out-Null

Write-Host "Sandbox run $runName" -ForegroundColor Cyan
Write-Host "  zip    $Zip"
Write-Host "  driver $Driver"
Write-Host "  vGPU   $vGpu"
Write-Host "  network $network"

Copy-Item $Zip -Destination $inDir
Copy-Item $Driver -Destination (Join-Path $inDir 'Driver.ps1')
# The drivers share their helpers through a file beside them, dot-sourced by name.
Copy-Item (Join-Path $PSScriptRoot 'sandbox\SandboxCommon.ps1') -Destination $inDir
$spec = [ordered]@{
    vGpu              = $vGpu
    zip               = [IO.Path]::GetFileName($Zip)
    shutdownWhenDone  = (-not $KeepOpen)
}
$spec | ConvertTo-Json | Out-File (Join-Path $inDir 'run.json') -Encoding ascii

# The mapped folders can still be mounting when LogonCommand fires, so the logon command
# is a poll rather than a call, and it redirects the driver's streams to the output folder
# because the logon console is invisible.
$desktop = 'C:\Users\WDAGUtilityAccount\Desktop'
$inner = "`$driver='$desktop\input\Driver.ps1'; `$log='$desktop\output\driver.log'; " +
    "for(`$i=0; `$i -lt 180 -and -not ((Test-Path `$driver) -and (Test-Path '$desktop\output')); `$i++){Start-Sleep -Seconds 1}; " +
    ". `$driver *> `$log"
$logon = [Security.SecurityElement]::Escape(
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"$inner`"")

$extraFolders = ''
foreach ($folder in $MapReadOnly) {
    $hostPath = (Resolve-Path $folder).Path
    $leaf = Split-Path $hostPath -Leaf
    $extraFolders += @"

    <MappedFolder>
      <HostFolder>$hostPath</HostFolder>
      <SandboxFolder>$desktop\$leaf</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
"@
}

$network = if ($Networking) { 'Enable' } else { 'Disable' }
$wsb = @"
<Configuration>
  <vGPU>$vGpu</vGPU>
  <Networking>$network</Networking>
  <MemoryInMB>$MemoryMB</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$inDir</HostFolder>
      <SandboxFolder>$desktop\input</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$outDir</HostFolder>
      <SandboxFolder>$desktop\output</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>$extraFolders
  </MappedFolders>
  <LogonCommand>
    <Command>$logon</Command>
  </LogonCommand>
</Configuration>
"@
$wsbPath = Join-Path $runDir "$runName.wsb"
[IO.File]::WriteAllText($wsbPath, $wsb, (New-Object Text.UTF8Encoding($false)))

Write-Host "Starting Windows Sandbox on $wsbPath..." -ForegroundColor Cyan
Start-Process -FilePath $sandboxExe -ArgumentList "`"$wsbPath`""

$donePath = Join-Path $outDir 'done.txt'
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path $donePath) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
}

if (-not (Test-Path $donePath)) {
    Write-Host "Driver did not finish within $TimeoutMinutes minutes." -ForegroundColor Yellow
    Write-Host '  Close the sandbox WINDOW by hand; never kill its processes, which wedges the Container Manager.' -ForegroundColor Yellow
    Write-Host "  Partial output: $outDir"
    exit 1
}

$summaryPath = Join-Path $outDir 'summary.json'
$waitBy = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $waitBy -and -not ((Get-Item $summaryPath -ErrorAction SilentlyContinue).Length -gt 0)) {
    Start-Sleep -Seconds 5
}
if (-not ((Get-Item $summaryPath -ErrorAction SilentlyContinue).Length -gt 0)) {
    Write-Host 'The driver wrote no usable summary.json; its step log is the record:' -ForegroundColor Yellow
    Get-Content (Join-Path $outDir 'steps.log') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
}
$summary = $null
if ((Get-Item $summaryPath -ErrorAction SilentlyContinue).Length -gt 0) {
    # A driver of the caller's own may write whatever summary suits it; only the shape this
    # script prints is JSON.
    try { $summary = Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}
if ($summary) {
    Write-Host ''
    Write-Host "Machine: $($summary.machine.gpus.name -join ', ')" -ForegroundColor Cyan
    Write-Host "  vulkan loader present: $($summary.machine.vulkanLoader), ICDs registered: $(@($summary.machine.vulkanIcds).Count)"
    foreach ($run in $summary.runs) {
        $colour = if ($run.success) { 'Green' } else { 'Yellow' }
        Write-Host ("  {0,-18} success={1} alive={2} exit={3} log={4}" -f $run.name, $run.success, $run.aliveAtTimeout, $run.exitCode, $run.wroteLog) -ForegroundColor $colour
        if ($run.renderer) { Write-Host "      $($run.renderer)" }
        foreach ($dialog in $run.dialogs) { Write-Host "      dialog: $dialog" -ForegroundColor Yellow }
    }
}
Write-Host ''
Write-Host "Output: $outDir" -ForegroundColor Green

if (-not $KeepOpen) {
    # Ask the person to close the window, and do not touch it from here. Killing the
    # processes wedges the Container Manager until an elevated service restart, and the
    # window ignores a posted close. Alt+F4 is not a way around that: it is delivered into
    # the guest, or into whatever window the person was working in when the raise this
    # process asked for was refused.
    Write-Host 'Close the Windows Sandbox window now; nothing else is waiting on it.' -ForegroundColor Yellow
    $drainBy = (Get-Date).AddMinutes(10)
    while ((Get-Process -Name $sandboxProcesses -ErrorAction SilentlyContinue) -and (Get-Date) -lt $drainBy) {
        Start-Sleep -Seconds 5
    }
}
