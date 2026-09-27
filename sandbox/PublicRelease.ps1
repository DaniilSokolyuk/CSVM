# Sandbox driver: the public README followed literally, by a machine that has never seen
# this project, on the zip a stranger downloads.
#
# Runs inside the sandbox, never on the host. The zip arrives in input\ carrying the
# mark of the web a browser download leaves (the Zone.Identifier stream), and the driver
# does what packaging/README.md tells a reader to do, in its order, recording at each
# step what that reader sees: the download lands in Downloads, Explorer's own extraction
# unzips it, CSVM.exe is double-clicked before any extraction (the screen that offers to
# extract), the extraction runs through CSVM.exe --headless --extract (the one step a script
# cannot take the way a reader does, since the in-game button needs a click), then CSVM.exe is
# run for the menu and for a flight. Every double-click is done twice: once through the
# shell, which is where SmartScreen and the attachment manager put their dialogs, and
# once as a plain process, which is what "Run anyway" leads to, so the run records the
# warning a stranger meets without being stopped by it.
#
# A retail install has to be mapped in read-only (RunSandbox.ps1 -MapReadOnly); the
# driver finds it as the desktop folder holding ZBD and GOSDATA.
param(
    [string]$Root = 'C:\Users\WDAGUtilityAccount\Desktop'
)

$ErrorActionPreference = 'Continue'
$In = Join-Path $Root 'input'
$Out = Join-Path $Root 'output'
$Downloads = Join-Path $env:USERPROFILE 'Downloads'
$WatchSeconds = 45

. (Join-Path $PSScriptRoot 'SandboxCommon.ps1')

Write-Step 'driver started'
New-Item -ItemType Directory -Path $Out -Force | Out-Null
$facts = Get-MachineFacts
Write-Step ('GPUs: {0}' -f (($facts.gpus | ForEach-Object { $_.name }) -join ', '))
Write-Step ('vulkan loader: {0}, ICDs: {1}, networked: {2}' -f $facts.vulkanLoader, $facts.vulkanIcds.Count, $facts.networked)

function Get-Motw([string]$Path) {
    # The mark of the web is the Zone.Identifier alternate stream. Read raw: a missing stream
    # is the answer "none", not an error.
    try {
        $text = Get-Content -LiteralPath $Path -Stream Zone.Identifier -Raw -ErrorAction Stop
        return ($text -replace "`r", '' -split "`n" | Where-Object { $_ }) -join '; '
    } catch { return $null }
}

# A new visible top-level window during a shell launch is what the person sees: the
# SmartScreen sheet, the attachment manager's security warning, a console, the game.
function Watch-ShellLaunch {
    param(
        [string]$Name,
        [string]$FilePath,
        [string]$Arguments,
        [string]$WorkingDirectory,
        [int]$Seconds
    )
    $dir = Join-Path $Out $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $before = @([Win32.Windows]::Visible() | ForEach-Object { $_.Handle })
    Write-Step ('shell-launching {0} {1}' -f $FilePath, $Arguments)
    # The double-click is Explorer's, so Explorer is asked to open the file: ShellExecute on a
    # file carrying the mark of the web does not return until the security prompt is
    # answered, and a prompt raised from a hidden helper process never reaches the screen.
    # Explorer returns at once and shows the prompt where a person would see it.
    $child = Start-Process -FilePath "$env:WINDIR\explorer.exe" -PassThru -ArgumentList ('"' + $FilePath + '"')
    $seen = @()
    $handles = @()
    $shot = $false
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 1000
        foreach ($w in [Win32.Windows]::Visible()) {
            if ($before -contains $w.Handle) { continue }
            $signature = Get-WindowSignature $w
            if ($seen -notcontains $signature) {
                $seen += $signature
                $handles += $w.Handle
                Write-Step ('new window: {0}' -f $signature)
            }
        }
        if (-not $shot -and $watch.Elapsed.TotalSeconds -ge 6) {
            Save-Screenshot (Join-Path $dir 'early.png')
            $shot = $true
        }
    }
    Save-Screenshot (Join-Path $dir 'final.png')
    # Tidy the desktop for the next step: close what appeared (a security prompt closes as
    # Cancel), then end the child and anything it managed to start.
    $childExited = $child.HasExited
    foreach ($h in $handles) { [Win32.Windows]::Close($h) }
    Start-Sleep -Seconds 3
    if (-not $child.HasExited) { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name CSVM, cmd -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    [ordered]@{
        name         = $Name
        file         = $FilePath
        arguments    = $Arguments
        launchReturned = $childExited
        windows      = $seen
        smartScreen  = [bool]($seen | Where-Object { $_ -match 'smartscreen|protected your PC|Security Warning|SmartScreen|Sicherheitswarnung' })
    }
}

$steps = [ordered]@{}

# Step 0: the download. A browser leaves the zip in Downloads with its Zone.Identifier;
# the input copy was stamped on the host, and whether the mark survives the mapped folder
# and the copy is recorded rather than assumed.
$zipIn = Get-ChildItem $In -Filter *.zip | Select-Object -First 1
$zip = Join-Path $Downloads $zipIn.Name
Copy-Item $zipIn.FullName -Destination $zip -Force
$motwIn = Get-Motw $zipIn.FullName
$motw = Get-Motw $zip
if (-not $motw) {
    # The share did not carry the stream; the browser would have. Write what a browser
    # writes and say so.
    Set-Content -LiteralPath $zip -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3`r`nReferrerUrl=https://github.com/Laeresh/CSVM/releases`r`nHostUrl=https://github.com/Laeresh/CSVM/releases" -Encoding Ascii
    $motw = Get-Motw $zip
    $motwSource = 'stamped inside the guest (the mapped folder dropped the stream)'
} else {
    $motwSource = 'carried in from the host copy'
}
Write-Step ('zip in Downloads: {0}; mark of the web: {1} ({2})' -f $zip, $motw, $motwSource)
$steps.download = [ordered]@{
    zip         = $zip
    sizeBytes   = (Get-Item $zip).Length
    sha256      = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    motwOnInput = $motwIn
    motw        = $motw
    motwSource  = $motwSource
}

# Step 1 of the README: unzip. Explorer's "Extract All" unzips beside the zip into a folder
# named after it, through the shell's copy engine, which is what propagates the mark to
# the files inside. Shell.Application's CopyHere is that engine.
$App = Join-Path $Downloads ([IO.Path]::GetFileNameWithoutExtension($zip))
if (Test-Path $App) { Remove-Item $App -Recurse -Force }
New-Item -ItemType Directory -Path $App -Force | Out-Null
$entries = [IO.Compression.ZipFile]::OpenRead($zip)
$expectedFiles = @($entries.Entries | Where-Object { -not $_.FullName.EndsWith('/') }).Count
$entries.Dispose()
Write-Step ('unzipping through the shell into {0} ({1} files)' -f $App, $expectedFiles)
$unzipWatch = [Diagnostics.Stopwatch]::StartNew()
$unzipError = $null
try {
    $shell = New-Object -ComObject Shell.Application
    $source = $shell.NameSpace($zip)
    $target = $shell.NameSpace($App)
    # 4: no progress dialog, 16: yes to all, 1024: no error UI.
    $target.CopyHere($source.Items(), 4 + 16 + 1024)
    while ($unzipWatch.Elapsed.TotalSeconds -lt 600) {
        $have = @(Get-ChildItem $App -Recurse -File -ErrorAction SilentlyContinue).Count
        if ($have -ge $expectedFiles) { break }
        Start-Sleep -Seconds 2
    }
} catch {
    $unzipError = $_.Exception.Message
    Write-Step ('shell unzip failed: {0}' -f $unzipError)
}
Start-Sleep -Seconds 3
$unzipped = @(Get-ChildItem $App -Recurse -File -ErrorAction SilentlyContinue).Count
if ($unzipped -lt $expectedFiles) {
    Write-Step ('shell unzip left {0} of {1} files; finishing with ZipFile so the run can go on' -f $unzipped, $expectedFiles)
    Remove-Item $App -Recurse -Force
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $App)
    $unzipMethod = 'ZipFile (shell copy incomplete)'
} else { $unzipMethod = 'shell' }
$steps.unzip = [ordered]@{
    folder        = $App
    method        = $unzipMethod
    seconds       = [int]$unzipWatch.Elapsed.TotalSeconds
    expectedFiles = $expectedFiles
    files         = @(Get-ChildItem $App -Recurse -File).Count
    rootListing   = @(Get-ChildItem $App | ForEach-Object { $_.Name })
    motwOnExe     = Get-Motw (Join-Path $App 'CSVM.exe')
    error         = $unzipError
}
Write-Step ('unzipped {0} files in {1}s by {2}; CSVM.exe mark: {3}' -f
    $steps.unzip.files, $steps.unzip.seconds, $unzipMethod, $steps.unzip.motwOnExe)

# What the README says happens when CSVM.exe is started before the extraction: the screen
# that offers to extract. Through the shell first, for the warning a stranger meets.
$steps.exeBeforeExtractShell = Watch-ShellLaunch -Name 'exe-before-extract-shell' -FilePath (Join-Path $App 'CSVM.exe') -WorkingDirectory $App -Seconds 25
$runs = @()
$runs += Invoke-Run -Name 'no-game-data' -Arguments @() -Seconds 30

# Step 2 of the README: extract. The in-game screen takes a click no script may give
# (the Extract button, the folder picker), so the driver runs the same pipeline through the
# exported exe's headless flag, with the mapped install passed directly and the bundled
# tools\unzbd.exe found beside the exe as a player's run finds it.
$install = Get-ChildItem $Root -Directory | Where-Object {
    (Test-Path (Join-Path $_.FullName 'ZBD')) -and (Test-Path (Join-Path $_.FullName 'GOSDATA\ASSETS'))
} | Select-Object -First 1
$installPath = ''
if ($install) {
    $installPath = $install.FullName
    Write-Step ('retail install {0}' -f $installPath)
} else {
    Write-Step 'no retail install is mapped; the extraction will refuse the empty path'
}

$extractDir = Join-Path $Out 'extract'
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
Write-Step 'running CSVM.exe --headless -- --extract=<install>'
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = Join-Path $App 'CSVM.exe'
# --flag=value keeps the flag outside the quotes, or Godot reads the whole token as one path.
$info.Arguments = '--headless -- --extract="' + $installPath + '"'
$info.WorkingDirectory = $App
$info.UseShellExecute = $false
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$extractWatch = [Diagnostics.Stopwatch]::StartNew()
$proc = [Diagnostics.Process]::Start($info)
$outTask = $proc.StandardOutput.ReadToEndAsync()
$errTask = $proc.StandardError.ReadToEndAsync()
$finished = $proc.WaitForExit(900000)
if (-not $finished) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
$extractSeconds = [int]$extractWatch.Elapsed.TotalSeconds
[IO.File]::WriteAllText((Join-Path $extractDir 'stdout.txt'), $outTask.Result)
[IO.File]::WriteAllText((Join-Path $extractDir 'stderr.txt'), $errTask.Result)
$extractedDir = Join-Path $App 'extracted'
$extractedFiles = @(Get-ChildItem $extractedDir -Recurse -File -ErrorAction SilentlyContinue)
$extractedBytes = ($extractedFiles | Measure-Object Length -Sum).Sum
$steps.extract = [ordered]@{
    command       = $info.Arguments
    finished      = $finished
    exitCode      = $(if ($finished) { $proc.ExitCode } else { $null })
    seconds       = $extractSeconds
    files         = $extractedFiles.Count
    megabytes     = [math]::Round($extractedBytes / 1MB, 1)
    folder        = $extractedDir
    stamped       = (Test-Path (Join-Path $extractedDir 'VERSION.json'))
    stdoutHead    = @((Read-Lines (Join-Path $extractDir 'stdout.txt')) | Select-Object -First 40)
    stdoutTail    = @((Read-Lines (Join-Path $extractDir 'stdout.txt')) | Select-Object -Last 15)
    stderrLines   = @(Read-Lines (Join-Path $extractDir 'stderr.txt')).Count
}
Write-Step ('--extract exit={0} in {1}s; extracted {2} files, {3} MB, stamped={4}' -f $steps.extract.exitCode, $extractSeconds, $steps.extract.files, $steps.extract.megabytes, $steps.extract.stamped)

# Step 3 of the README: double-click CSVM.exe. Through the shell for the warning, then as a
# process for the menu itself, the log location, the version line and the save folder.
$steps.exeShell = Watch-ShellLaunch -Name 'exe-shell' -FilePath (Join-Path $App 'CSVM.exe') -WorkingDirectory $App -Seconds 25
$runs += Invoke-Run -Name 'menu' -Arguments @() -Seconds $WatchSeconds
$saveDir = Join-Path $env:APPDATA 'Godot\app_userdata\CSVM'
$steps.saves = [ordered]@{
    folder = $saveDir
    exists = (Test-Path $saveDir)
    files  = @(Get-ChildItem $saveDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Substring($saveDir.Length + 1) })
}
Write-Step ('save folder {0} exists={1} files={2}' -f $saveDir, $steps.saves.exists, $steps.saves.files.Count)

# And a flight on the data this machine extracted itself. A flight-mode run does not exit
# inside the sandbox, so it is watched and then closed; the frame rate is read from the
# log, not from how it ended. Below the floor this is the run the README says vanishes.
$runs += Invoke-Run -Name 'flight' -Seconds 75 -Arguments @('--', '--fly', '--chapter=C1', '--plane=player_fury', '--no-vsync')

Complete-Driver ([ordered]@{
    zip     = $zipIn.Name
    machine = $facts
    steps   = $steps
    runs    = $runs
})
