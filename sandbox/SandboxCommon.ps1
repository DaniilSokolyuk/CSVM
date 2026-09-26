# Shared by every sandbox driver: the step log, the window census, screenshots, the machine
# facts and the watched launch of CSVM.exe. Dot-sourced by the driver RunSandbox.ps1 copies
# in beside it; runs inside the sandbox, never on the host.
#
# The caller sets $Out (the writable output folder) and $App (where CSVM.exe is) before
# dot-sourcing this file.

# Every step is appended to its own file rather than left in the logon console's redirect,
# which is still buffered when the VM shuts down. Do not report progress with Write-Output:
# it joins the calling function's return value, and a run result read out of that array is
# a progress string whose every property is null.
function Write-Step([string]$Text) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $Text
    Write-Host $line
    # Opened with every share flag, because the host reads this file while it is being
    # written, and an append that refuses to share with that reader fails silently.
    try {
        $stream = New-Object IO.FileStream((Join-Path $Out 'steps.log'), 'Append', 'Write', 'ReadWrite, Delete')
        $bytes = [Text.Encoding]::ASCII.GetBytes($line + "`r`n")
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Dispose()
    } catch { }
}

if (-not ('Win32.Windows' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Win32
{
    public class WindowInfo
    {
        public IntPtr Handle;
        public int ProcessId;
        public string Class;
        public string Title;
        public string[] ChildText;
    }

    public static class Windows
    {
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static string TextOf(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(2048);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ClassOf(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassNameW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static WindowInfo[] Visible()
        {
            List<WindowInfo> found = new List<WindowInfo>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) { return true; }
                WindowInfo info = new WindowInfo();
                int processId;
                GetWindowThreadProcessId(hWnd, out processId);
                info.Handle = hWnd;
                info.ProcessId = processId;
                info.Class = ClassOf(hWnd);
                info.Title = TextOf(hWnd);
                List<string> kids = new List<string>();
                EnumChildWindows(hWnd, delegate(IntPtr child, IntPtr unused)
                {
                    string text = TextOf(child);
                    if (text.Length > 0) { kids.Add(ClassOf(child) + ": " + text); }
                    return true;
                }, IntPtr.Zero);
                info.ChildText = kids.ToArray();
                found.Add(info);
                return true;
            }, IntPtr.Zero);
            return found.ToArray();
        }

        public static void Close(IntPtr hWnd)
        {
            PostMessageW(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
        }
    }
}
'@
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-Lines([string]$Path) {
    if (-not (Test-Path $Path)) { return @() }
    @([IO.File]::ReadAllLines($Path))
}

function Save-Screenshot([string]$Path) {
    try {
        $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
    } catch {
        Write-Step ('screenshot failed: {0}' -f $_.Exception.Message)
    }
}

function Get-WindowSignature($w) {
    $owner = (Get-Process -Id $w.ProcessId -ErrorAction SilentlyContinue).ProcessName
    '{0} [{1}, {2}] {3}' -f $w.Title, $w.Class, $owner, ($w.ChildText -join ' | ')
}

# What the machine offers a renderer, read before anything is launched: the below-floor
# run is only evidence if the sandbox really has no Vulkan. WMI is access-denied to the
# sandbox account, so the display adapters are read out of the class registry key instead
# of Win32_VideoController, and the renderer the build actually got is taken off its own
# [perf] line.
function Get-MachineFacts {
    $classKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
    $gpus = @(Get-ChildItem $classKey -ErrorAction SilentlyContinue | ForEach-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if ($props.DriverDesc) {
                [ordered]@{
                    name   = $props.DriverDesc
                    driver = $props.DriverVersion
                }
            }
        })
    $icds = @()
    foreach ($key in 'HKLM:\SOFTWARE\Khronos\Vulkan\Drivers', 'HKLM:\SOFTWARE\WOW6432Node\Khronos\Vulkan\Drivers') {
        if (Test-Path $key) {
            $icds += @((Get-Item $key).GetValueNames())
        }
    }
    [ordered]@{
        os           = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue).ProductName
        build        = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue).CurrentBuild
        locale       = (Get-Culture).Name
        gpus         = $gpus
        vulkanLoader = [bool](Test-Path (Join-Path $env:WINDIR 'System32\vulkan-1.dll'))
        vulkanIcds   = $icds
        d3d12Core    = [bool](Test-Path (Join-Path $env:WINDIR 'System32\D3D12Core.dll'))
        # Not Get-NetAdapter: CIM is access-denied to the sandbox account, and the exception
        # it throws is terminating, which would leave every fact above empty.
        networked    = [Net.NetworkInformation.NetworkInterface]::GetIsNetworkAvailable()
    }
}

# Launches CSVM.exe as a process and watches it: the windows it draws, any dialog on the
# desktop, screenshots, both streams, the log it wrote and how it ended.
function Invoke-Run {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [int]$Seconds
    )

    $dir = Join-Path $Out $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    if (Test-Path (Join-Path $App 'logs')) { Remove-Item (Join-Path $App 'logs') -Recurse -Force }

    Write-Step ('launching CSVM.exe {0}' -f ($Arguments -join ' '))
    # Started through ProcessStartInfo rather than Start-Process: the object Start-Process
    # hands back cannot always be asked for an ExitCode, and how the build ended is half of
    # what this run is here to record.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $App 'CSVM.exe'
    # Quote what carries a space, or the process re-splits the argument before the build
    # sees it, which is how a data root under a user profile arrives in two pieces.
    $info.Arguments = (@($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
    $info.WorkingDirectory = $App
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $proc = [Diagnostics.Process]::Start($info)
    # Both streams are drained on their own threads; a full pipe buffer otherwise blocks the
    # build itself, and a failing renderer writes tens of thousands of lines.
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    $windows = @()
    $dialogs = @()
    $appDialogs = @()
    $sawGameWindow = $false
    $shotEarly = $false
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds -and -not $proc.HasExited) {
        Start-Sleep -Milliseconds 1000
        foreach ($w in [Win32.Windows]::Visible()) {
            $mine = ($w.ProcessId -eq $proc.Id)
            $isDialog = ($w.Class -eq '#32770')
            if (-not $mine -and -not $isDialog) { continue }
            $owner = (Get-Process -Id $w.ProcessId -ErrorAction SilentlyContinue).ProcessName
            $signature = Get-WindowSignature $w
            if ($windows -notcontains $signature) {
                $windows += $signature
                Write-Step ('window: {0}' -f $signature)
            }
            if ($isDialog -and ($dialogs -notcontains $signature)) {
                $dialogs += $signature
                Save-Screenshot (Join-Path $dir 'dialog.png')
                # A crash dialog belongs to WerFault, not to the build that died, so the
                # run is judged on dialogs the build is behind rather than on every
                # dialog the desktop happens to be showing.
                if ($mine -or $owner -eq 'WerFault' -or $w.Title -like '*CSVM*') { $appDialogs += $signature }
            }
            if ($mine -and -not $isDialog -and $w.Title.Length -gt 0) { $sawGameWindow = $true }
        }
        if (-not $shotEarly -and $watch.Elapsed.TotalSeconds -ge 8) {
            Save-Screenshot (Join-Path $dir 'early.png')
            $shotEarly = $true
        }
    }
    $aliveAtTimeout = -not $proc.HasExited
    $exitCode = $null
    if ($aliveAtTimeout) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 3
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    } else {
        # A build that died leaves its crash dialog behind a beat later, and the watch loop
        # above has already ended, so the desktop is read once more before the last shot.
        Start-Sleep -Seconds 6
        foreach ($w in [Win32.Windows]::Visible()) {
            if ($w.Class -ne '#32770') { continue }
            $owner = (Get-Process -Id $w.ProcessId -ErrorAction SilentlyContinue).ProcessName
            $signature = Get-WindowSignature $w
            if ($dialogs -notcontains $signature) { $dialogs += $signature }
            if ($owner -eq 'WerFault' -or $w.Title -like '*CSVM*') { $appDialogs += $signature }
        }
        $exitCode = $proc.ExitCode
    }
    Save-Screenshot (Join-Path $dir 'final.png')
    [IO.File]::WriteAllText((Join-Path $dir 'stdout.txt'), $stdoutTask.Result)
    [IO.File]::WriteAllText((Join-Path $dir 'stderr.txt'), $stderrTask.Result)

    $logLines = @()
    $logHead = @()
    $logFiles = @()
    if (Test-Path (Join-Path $App 'logs')) {
        Copy-Item (Join-Path $App 'logs') -Destination (Join-Path $dir 'logs') -Recurse -Force
        $logFiles = @(Get-ChildItem (Join-Path $App 'logs') -File | ForEach-Object { $_.Name })
        $newest = Get-ChildItem (Join-Path $dir 'logs') -Filter *.log -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if ($newest) {
            $all = @([IO.File]::ReadAllLines($newest.FullName))
            $logLines = @($all | Select-Object -Last 25)
            $logHead = @($all | Select-Object -First 3)
        }
    }

    # Read every captured stream through [IO.File], never Get-Content: its strings carry the
    # provider's note properties, and ConvertTo-Json walks those into a 300 MB summary.
    $stdout = @(Read-Lines (Join-Path $dir 'stdout.txt'))
    $stderr = @(Read-Lines (Join-Path $dir 'stderr.txt'))
    # The renderer the build actually got, in its own words.
    $rendererLine = [string](@($stdout + $logLines | Where-Object { $_ -match '\[perf\] gpu=' }) | Select-Object -First 1)

    $result = [ordered]@{
        name           = $Name
        arguments      = $Arguments
        watchedSeconds = [int]$watch.Elapsed.TotalSeconds
        aliveAtTimeout = $aliveAtTimeout
        exitCode       = $exitCode
        sawGameWindow  = $sawGameWindow
        dialogs        = $dialogs
        appDialogs     = $appDialogs
        windows        = $windows
        wroteLog       = (Test-Path (Join-Path $dir 'logs'))
        logFiles       = $logFiles
        logHead        = $logHead
        renderer       = $rendererLine
        logTail        = $logLines
        # Both streams are on disk in full; a failing renderer writes tens of thousands of
        # lines, and a summary nobody can open is not a record.
        stdoutLines    = $stdout.Count
        stderrLines    = $stderr.Count
        stdout         = @($stdout | Select-Object -First 60)
        stderrHead     = @($stderr | Select-Object -First 40)
        stderrDistinct = @($stderr | Where-Object { $_ -match '^(ERROR|WARNING|   at:)' } | Select-Object -Unique | Select-Object -First 40)
    }
    # A run counts as reaching the game only if it drew its own window, put no dialog on
    # screen, wrote a log and was still running when the watch ended.
    $result.perfRates = @($stdout | Where-Object { $_ -match '\[perf\] rate ' })
    $result.success = ($sawGameWindow -and $appDialogs.Count -eq 0 -and $result.wroteLog -and $aliveAtTimeout)
    Write-Step ('{0}: success={1} (window={2} alive={3} log={4} appDialogs={5}) exit={6}' -f
        $Name, $result.success, $sawGameWindow, $aliveAtTimeout, $result.wroteLog, $appDialogs.Count, $exitCode)
    if ($rendererLine) { Write-Step ('{0}: {1}' -f $Name, $rendererLine) }
    # The only thing this function writes to the output stream.
    $result
}

# Writes the summary and the done marker the host waits for. The mapped folder is a share
# the shutdown can cut off mid-flush, so the summary is read back before anything says the
# run finished.
function Complete-Driver([hashtable]$Summary) {
    $summaryPath = Join-Path $Out 'summary.json'
    $json = $Summary | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($summaryPath, $json, (New-Object Text.UTF8Encoding($false)))
    $written = 0
    for ($i = 0; $i -lt 30 -and $written -lt 1; $i++) {
        Start-Sleep -Seconds 1
        $written = (Get-Item $summaryPath -ErrorAction SilentlyContinue).Length
    }
    Write-Step ('summary.json is {0} bytes on the share' -f $written)
    'done' | Out-File (Join-Path $Out 'done.txt') -Encoding ascii
    Write-Step 'driver finished'
    # Ending the session is the host's job: it closes the sandbox window once it has read the
    # summary. A guest-side shutdown leaves the host window process running anyway, and
    # killing it wedges the Container Manager until an elevated service restart.
}
