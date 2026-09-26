<#
.SYNOPSIS
    Downloads the pinned official SDL2 Windows x64 runtime into tools\sdl2\, checked by SHA-256.

.DESCRIPTION
    CSVM reads DirectInput-only flight sticks through SDL2.dll (docs/tooling.md,
    "SDL2 for flight sticks"), because the SDL3 inside Godot 4.7 enumerates none of them. tools\ is
    git-ignored, so the DLL is fetched here rather than committed.

    Installs into <Root>\tools\sdl2\:
      SDL2.dll        from the libsdl-org release zip, SHA-256 checked twice (zip and DLL)
      README-SDL.txt  from the same zip, which asks to be distributed with the runtime
      LICENSE.txt     SDL's zlib licence, read from the release's own commit (the runtime zip
                      carries none), SHA-256 checked

    Nothing alters PATH: the game loads the DLL by absolute path (the search order is in
    docs/tooling.md, "SDL2 for flight sticks"). A missing DLL means no sticks, never a
    failed launch, so no launch script calls this.

    -Verify installs nothing. It checks the three files against the pins, throws naming this
    script as the fix, and otherwise returns the release facts ExportRelease.ps1 records in
    BUILD-INFO.txt. Keeping the pins in this one file is what lets the export check the DLL it
    ships without a second copy of the hash.

.PARAMETER Root
    The checkout whose tools\ receives SDL2. Defaults to this script's own tree. A worktree
    needs no copy of its own: the game falls back to CSVM_DATA_ROOT's tools\sdl2\.

.PARAMETER Force
    Re-download even when the installed files already match the pins.

.EXAMPLE
    .\InstallSdl2.ps1
    Fetch SDL2 2.32.10 into tools\sdl2\ of this tree, or report that it is already there.

.EXAMPLE
    .\InstallSdl2.ps1 -Root Z:\CSVM -Verify
    Throw unless Z:\CSVM\tools\sdl2\ holds the pinned files.
#>

[CmdletBinding()]
param(
    [string]$Root = $PSScriptRoot,
    [switch]$Force,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"

# The pins. 2.32.10 is the build that saw the VKB sticks on the user's machine (the plan's
# "What the data actually ships"); moving it means re-running that detection check, not only
# updating the hashes. ZipSha256 matches the digest GitHub publishes for the release asset.
$Version    = "2.32.10"
$Commit     = "5d249570393f7a37e037abf22cd6012a4cc56a71"
$ZipUrl     = "https://github.com/libsdl-org/SDL/releases/download/release-$Version/SDL2-$Version-win32-x64.zip"
$ZipSha256  = "6CF9706EEFD0A4A06DC764007934D428AFAF029FABDD408A9E646048C91E18FB"
$DllSha256  = "B37740A72A7A9706216DF9F0134894BB7A850B356FD149398C67D874CBCFACB4"
$ReadmeSha256  = "F17D8919136F9627468B4DFFBD7BDDD188EF2AAA8ED21914D2107D1C759E99D7"
$LicenseUrl    = "https://raw.githubusercontent.com/libsdl-org/SDL/$Commit/LICENSE.txt"
$LicenseSha256 = "97F35B302B361680EC1E891E95D2D52097BB95ABFF361434916D99DC1305F127"

$Sdl2Dir = Join-Path $Root "tools\sdl2"
$Pinned = @(
    @{ Name = "SDL2.dll";       Sha = $DllSha256 },
    @{ Name = "README-SDL.txt"; Sha = $ReadmeSha256 },
    @{ Name = "LICENSE.txt";    Sha = $LicenseSha256 }
)

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# Each pinned file that is missing or differs, by name; empty when the install is complete.
function Get-Mismatches {
    $bad = @()
    foreach ($file in $Pinned) {
        $path = Join-Path $Sdl2Dir $file.Name
        if (-not (Test-Path -LiteralPath $path)) {
            $bad += "$($file.Name) (missing)"
        } elseif ((Get-Sha256 $path) -ne $file.Sha) {
            $bad += "$($file.Name) (SHA-256 differs from the pin)"
        }
    }
    return , $bad
}

function Get-ReleaseFacts {
    return [pscustomobject]@{
        Version   = $Version
        Commit    = $Commit
        Directory = $Sdl2Dir
        DllSha256 = $DllSha256
        ZipUrl    = $ZipUrl
    }
}

if ($Verify) {
    $bad = Get-Mismatches
    if ($bad.Count -gt 0) {
        throw "SDL2 $Version is not installed at $Sdl2Dir ($($bad -join ', ')) -- run " +
            ".\InstallSdl2.ps1 -Root $Root."
    }
    return Get-ReleaseFacts
}

if ((-not $Force) -and (Get-Mismatches).Count -eq 0) {
    Write-Host "SDL2 $Version already installed at $Sdl2Dir" -ForegroundColor Green
    return
}

# tools\sdl2 is written into, never swept, but a link here would still put the files in
# another tree; CLAUDE.md forbids links from a worktree into the main checkout.
if ((Test-Path -LiteralPath $Sdl2Dir) -and
    ((Get-Item -LiteralPath $Sdl2Dir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "$Sdl2Dir is a junction or symlink -- remove it by hand and re-run."
}

# Windows PowerShell 5.1 does not offer TLS 1.2 by default, and GitHub refuses anything older.
[Net.ServicePointManager]::SecurityProtocol =
    [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Staging = Join-Path $env:TEMP "csvm-sdl2-$PID"
if (Test-Path -LiteralPath $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
New-Item -ItemType Directory -Force $Staging | Out-Null
try {
    $zip = Join-Path $Staging "SDL2-$Version-win32-x64.zip"
    Write-Host "Downloading $ZipUrl" -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $ZipUrl -OutFile $zip
    $zipHash = Get-Sha256 $zip
    if ($zipHash -ne $ZipSha256) {
        throw "SDL2 zip SHA-256 is $zipHash, expected $ZipSha256 -- refusing to install it."
    }

    Write-Host "Downloading $LicenseUrl" -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $LicenseUrl -OutFile (Join-Path $Staging "LICENSE.txt")

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($name in @("SDL2.dll", "README-SDL.txt")) {
            $entry = $archive.GetEntry($name)
            if ($null -eq $entry) { throw "The SDL2 zip holds no $name." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $Staging $name), $true)
        }
    } finally {
        $archive.Dispose()
    }

    # Every file is checked in staging before any of them replaces an installed one, so a
    # failed run leaves tools\sdl2 as it was.
    foreach ($file in $Pinned) {
        $hash = Get-Sha256 (Join-Path $Staging $file.Name)
        if ($hash -ne $file.Sha) {
            throw "$($file.Name) SHA-256 is $hash, expected $($file.Sha) -- refusing to install it."
        }
    }

    New-Item -ItemType Directory -Force $Sdl2Dir | Out-Null
    foreach ($file in $Pinned) {
        Copy-Item -LiteralPath (Join-Path $Staging $file.Name) -Destination (Join-Path $Sdl2Dir $file.Name) -Force
    }
} finally {
    Remove-Item -LiteralPath $Staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Installed SDL2 $Version at $Sdl2Dir" -ForegroundColor Green
