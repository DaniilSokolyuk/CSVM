<#
.SYNOPSIS
    Regenerates packaging/LICENSE-thirdparty.txt from the payloads and the ported source
    it speaks for.

.DESCRIPTION
    The release zip ships CSVM.exe (the Godot engine statically linked), a self-contained
    .NET runtime beside it, and tools/unzbd.exe (a Rust binary). Each carries notice
    obligations that CSVM's own LICENSE and LICENSE-unzbd do not cover, so the assembled
    file is what discharges them. A fourth obligation is not a payload at all: CSVM's
    managed MPEG-1 decoder is a port of pl_mpeg, and MIT requires the notice to travel
    with the derived source. A fifth is the PromptFont file packed into the export, whose
    SIL Open Font License wants its copyright notice and licence carried with it.

    The three payload sources are read from the artefacts themselves rather than from a
    copy of upstream's website:

    - Godot. The Windows distribution ships NO LICENSE.txt or COPYRIGHT.txt on disk, and
      neither does the export-template archive: the engine keeps both texts inside the
      binary and hands them back through Engine.get_license_text(),
      get_copyright_info() and get_license_info(). So this script runs the pinned editor
      headless over a throwaway project and reads them out of the same binary the export
      templates were cut from. The dump project is built in TEMP, never in CSVM/, so no
      Godot run here can touch project.godot.
    - .NET. A self-contained publish copies the Microsoft.NETCore.App runtime pack into
      the payload, and that pack ships LICENSE.TXT (MIT) plus THIRD-PARTY-NOTICES.TXT.
      Those two files are the notice the publish requires, taken from the exact version
      in the NuGet cache rather than from the machine-wide dotnet install, which is a
      different (usually newer) build.
    - unzbd. EUPL-1.2 covers mech3ax's own code, not the crates statically linked into
      the binary, whose MIT and Apache terms want their own copyright notices carried.
      cargo resolves them for the target triple and each crate's licence text is read
      out of the registry checkout it was built from. Texts are deduplicated by content,
      so the identical Apache-2.0 sixty crates ship appears once.

    PromptFont follows the same rule. Its copyright statement is read out of the shipped
    font's own name table, and its licence is the LICENSE.txt from the PromptFont release,
    kept byte-identical as packaging/LICENSE-promptfont because that text carries no
    copyright line to read.

    pl_mpeg is the exception to that rule, and cannot be anything else: upstream declares
    MIT through an SPDX-License-Identifier line in its header and publishes no licence
    file, so there is no upstream text to read. The terms are therefore kept in this
    repository as packaging/LICENSE-plmpeg, and the section says in the shipped file where
    that text came from so a reader is not misled into taking it for a verbatim copy.

    ExportRelease.ps1 checks the three version stamps this script writes into the file's
    header against what it is actually packaging, and refuses an export whose notice was
    assembled for a different engine, runtime or fork commit. That check is what makes a
    stale notice a build failure rather than a silent shipping mistake. pl_mpeg and
    PromptFont carry no such stamp because both live in this repository rather than in a
    separately built binary: they move with CSVM's own history, which BUILD-INFO.txt
    already records.

.EXAMPLE
    .\packaging\BuildThirdPartyNotices.ps1
    Rewrite packaging/LICENSE-thirdparty.txt from the current tools/ and NuGet cache.
#>

[CmdletBinding()]
param(
    # Empty means "the newest win-x64 runtime pack in the NuGet cache", which is what a
    # self-contained publish on this machine picks up.
    [string] $RuntimeVersion = ""
)

$ErrorActionPreference = "Stop"

$RepoRoot     = Split-Path $PSScriptRoot -Parent
$GodotExe     = Join-Path $RepoRoot "tools\godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe"
$Mech3axRepo  = Join-Path $RepoRoot "tools\mech3ax"
$OutFile      = Join-Path $PSScriptRoot "LICENSE-thirdparty.txt"
$PlMpegFile   = Join-Path $PSScriptRoot "LICENSE-plmpeg"
$PromptFontLicense = Join-Path $PSScriptRoot "LICENSE-promptfont"
$PromptFontFile    = Join-Path $RepoRoot "CSVM\data\promptfont.ttf.bin"
$PackRoot     = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.netcore.app.runtime.win-x64"

$Rule = "=" * 78
$Thin = "-" * 78

# BOM-less UTF-8 everywhere: PowerShell 5.1's Get-Content/Set-Content default to ANSI and
# would double-encode every copyright sign in 800 KB of upstream text (verification.md
# SHELL-7, CLAUDE.md). Read and write through the .NET APIs, which detect and emit UTF-8.
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Read-Utf8([string] $Path) { return [System.IO.File]::ReadAllText($Path) }

foreach ($required in @($GodotExe, $Mech3axRepo, $PackRoot, $PromptFontFile, $PromptFontLicense)) {
    if (-not (Test-Path $required)) {
        throw "Not found: $required -- see PROJECT_CONTEXT.md for the tools/ setup."
    }
}

if (-not (Test-Path $PlMpegFile)) {
    throw "Not found: $PlMpegFile -- the MIT terms the pl_mpeg port ships under live in " +
        "the repository, because upstream publishes no licence file to read them from."
}

# ---------------------------------------------------------------- Godot

$godotBuild = (& $GodotExe --version | Select-Object -Last 1).Trim()
if (-not $godotBuild) {
    throw "Could not read a version string from $GodotExe."
}

$dumpDir = Join-Path $env:TEMP "csvm-notices-$PID"
if (Test-Path $dumpDir) { Remove-Item $dumpDir -Recurse -Force }
New-Item -ItemType Directory -Force $dumpDir | Out-Null
try {
    [System.IO.File]::WriteAllText(
        (Join-Path $dumpDir "project.godot"),
        "config_version=5`n`n[application]`n`nconfig/name=`"csvm-notices`"`n",
        $Utf8NoBom)

    # Space-indented on purpose: a tab that a copy-paste turns into spaces is a GDScript
    # parse error, and this script is edited far more often than it is read by Godot.
    $dumpScript = @'
extends SceneTree


func _init():
    var f := FileAccess.open("res://license.txt", FileAccess.WRITE)
    f.store_string(Engine.get_license_text())
    f.close()

    # COPYRIGHT.txt's machine-readable (DEP5) stanzas, rebuilt from the engine's tables.
    f = FileAccess.open("res://copyright.txt", FileAccess.WRITE)
    for component in Engine.get_copyright_info():
        f.store_string("Comment: " + str(component["name"]) + "\n")
        for part in component["parts"]:
            var files: Array = Array(part["files"])
            f.store_string("Files: " + "\n       ".join(files) + "\n")
            var holders: Array = Array(part["copyright"])
            f.store_string("Copyright: " + "\n           ".join(holders) + "\n")
            f.store_string("License: " + str(part["license"]) + "\n")
        f.store_string("\n")
    f.close()

    f = FileAccess.open("res://license_info.txt", FileAccess.WRITE)
    var info := Engine.get_license_info()
    for k in info:
        f.store_string("License: " + str(k) + "\n")
        f.store_string(str(info[k]))
        f.store_string("\n\n")
    f.close()

    quit()
'@
    [System.IO.File]::WriteAllText((Join-Path $dumpDir "dump.gd"), $dumpScript, $Utf8NoBom)

    Write-Host "Reading Godot $godotBuild licence tables out of the engine..." -ForegroundColor Cyan
    & $GodotExe --headless --path $dumpDir --script dump.gd | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Godot licence dump failed (exit $LASTEXITCODE)."
    }

    $godotLicense   = Read-Utf8 (Join-Path $dumpDir "license.txt")
    $godotCopyright = Read-Utf8 (Join-Path $dumpDir "copyright.txt")
    $godotLicenses  = Read-Utf8 (Join-Path $dumpDir "license_info.txt")
} finally {
    Remove-Item $dumpDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- .NET runtime

if (-not $RuntimeVersion) {
    # Sort as versions, not as strings: 8.0.9 sorts after 8.0.30 alphabetically.
    $RuntimeVersion = (Get-ChildItem $PackRoot -Directory |
        Sort-Object { [version] $_.Name } | Select-Object -Last 1).Name
}
$packDir = Join-Path $PackRoot $RuntimeVersion
if (-not (Test-Path $packDir)) {
    throw "Runtime pack $RuntimeVersion not in the NuGet cache ($PackRoot) -- publish once " +
        "so the pack is restored, or pass -RuntimeVersion for one that is there."
}
$dotnetLicense = Read-Utf8 (Join-Path $packDir "LICENSE.TXT")
$dotnetNotices = Read-Utf8 (Join-Path $packDir "THIRD-PARTY-NOTICES.TXT")

# ---------------------------------------------------------------- unzbd's crates

$forkCommit = (& git -C $Mech3axRepo rev-parse cs-anim).Trim()
if ($LASTEXITCODE -ne 0 -or $forkCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve cs-anim in $Mech3axRepo."
}

Write-Host "Resolving unzbd's crates at cs-anim $($forkCommit.Substring(0, 8))..." -ForegroundColor Cyan
Push-Location $Mech3axRepo
try {
    # --filter-platform keeps the Unix-only crates out; --offline keeps the enumeration a
    # read of the lockfile and the registry checkouts the binary was actually built from,
    # rather than a resolve that could pull something newer than the shipped exe.
    $metadataJson = & cargo metadata --format-version 1 --offline --filter-platform x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) {
        throw "cargo metadata failed (exit $LASTEXITCODE) in $Mech3axRepo."
    }
} finally {
    Pop-Location
}
$metadata = $metadataJson | ConvertFrom-Json

# A null source is a workspace member (mech3ax's own crates), covered by LICENSE-unzbd.
$crates = @($metadata.packages | Where-Object { $_.source } | Sort-Object name, version)
if ($crates.Count -eq 0) {
    throw "cargo metadata resolved no external crates -- the filter or the lockfile is wrong."
}

$crateRows = New-Object System.Text.StringBuilder
$texts = @{}
foreach ($crate in $crates) {
    $spdx = if ($crate.license) { $crate.license } else { "(no license field; see $($crate.repository))" }
    [void] $crateRows.AppendLine(("  {0,-32} {1,-12} {2}" -f $crate.name, $crate.version, $spdx))

    $crateDir = Split-Path $crate.manifest_path -Parent
    $licenseFiles = @(Get-ChildItem $crateDir -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICEN[CS]E|COPYING|NOTICE|UNLICENSE)' } | Sort-Object Name)
    foreach ($licenseFile in $licenseFiles) {
        $text = Read-Utf8 $licenseFile.FullName
        # Keyed on the text, so the one Apache-2.0 body sixty crates ship is printed once
        # under all of their names instead of sixty times.
        if (-not $texts.ContainsKey($text)) { $texts[$text] = New-Object System.Collections.ArrayList }
        [void] $texts[$text].Add("$($crate.name) $($crate.version) ($($licenseFile.Name))")
    }
}
$crateBlocks = New-Object System.Text.StringBuilder
foreach ($text in ($texts.Keys | Sort-Object { $texts[$_][0] })) {
    [void] $crateBlocks.AppendLine($Thin)
    [void] $crateBlocks.AppendLine("As shipped by: " + ($texts[$text] -join ", "))
    [void] $crateBlocks.AppendLine($Thin)
    [void] $crateBlocks.AppendLine()
    [void] $crateBlocks.AppendLine($text.TrimEnd())
    [void] $crateBlocks.AppendLine()
}

# ---------------------------------------------------------------- pl_mpeg

$plmpegLicense = Read-Utf8 $PlMpegFile

$plmpegIntro = @"
CSVM's MPEG-1 video and MP2 audio decoding is a port of pl_mpeg, a single-header
C library by Dominic Szablewski (https://phoboslab.org), published at
https://github.com/phoboslab/pl_mpeg. The C source is not in this archive and no
pl_mpeg binary is shipped: the code that runs is C# derived from it, so this
notice is carried for the source CSVM's own decoder is written from rather than
for a component inside a binary here.

pl_mpeg declares its terms as the line SPDX-License-Identifier: MIT in its
header. It publishes no LICENSE file and reproduces no licence block in the
header itself, so there is no upstream text to copy. What follows is therefore
the standard MIT licence with pl_mpeg's author named as the copyright holder,
kept in this project as packaging\LICENSE-plmpeg, and not a verbatim copy of a
file upstream distributes. No copyright year is given because upstream states
none.

$($plmpegLicense.TrimEnd())
"@

# ---------------------------------------------------------------- PromptFont

# WPF's GlyphTypeface throws on the shipped .bin name and reads the same bytes under .ttf,
# so the name table is read from a TEMP copy.
Add-Type -AssemblyName PresentationCore
$fontCopy = Join-Path $env:TEMP "csvm-notices-promptfont-$PID.ttf"
Copy-Item $PromptFontFile $fontCopy -Force
try {
    $typeface = New-Object System.Windows.Media.GlyphTypeface([Uri] $fontCopy)
    $fontCopyright = $typeface.Copyrights[[System.Globalization.CultureInfo] "en-US"]
} finally {
    Remove-Item $fontCopy -Force -ErrorAction SilentlyContinue
}
if (-not $fontCopyright) {
    throw "$PromptFontFile carries no en-US copyright string in its name table."
}
$fontCopyrightLines = (($fontCopyright -split "`r?`n") | Where-Object { $_.Trim() } |
    ForEach-Object { "  " + $_.Trim() }) -join "`n"
$promptFontLicenseText = Read-Utf8 $PromptFontLicense

$promptFontIntro = @"
The controller button pictures CSVM draws in its prompts are characters of
PromptFont by Yukari "Shinmera" Hafner, published at
https://github.com/Shinmera/promptfont. The font file is packed unmodified inside
the exported .pck as data\promptfont.ttf.bin; only the file name differs from
upstream's promptfont.ttf.

The font's copyright statement, as its own name table carries it:

$fontCopyrightLines

The licence text follows, byte-identical to the LICENSE.txt in the PromptFont
release and kept in this project as packaging\LICENSE-promptfont.

$($promptFontLicenseText.TrimEnd())
"@

# ---------------------------------------------------------------- assembly

function Section([string] $Number, [string] $Title, [string] $Body) {
    return @(
        $Rule
        "$Number. $Title"
        $Rule
        ""
        $Body.TrimEnd()
        ""
        ""
    ) -join "`n"
}

$header = @"
CSVM third-party notices
========================

This file carries the notices that the third-party software inside this build is
licensed on condition of carrying, and one notice for third-party source CSVM's own
code is ported from. It is not CSVM's own licence: CSVM is under the GNU General
Public License v3, whose text is in LICENSE beside this file, and the extractor
tools\unzbd.exe is under the EUPL-1.2, whose text is in LICENSE-unzbd.

This archive contains no Crimson Skies code, data or artwork. The game files a
player extracts with CSVM stay on their own machine.

Assembled from these exact payload versions, which ExportRelease.ps1 re-checks
against what it packages before it will build a zip:

  Godot Engine build: $godotBuild
  .NET runtime version: $RuntimeVersion
  mech3ax cs-anim commit: $forkCommit

Regenerate with packaging\BuildThirdPartyNotices.ps1, which reads every text in
sections 1 to 6 and 8 out of the shipped artefacts themselves. Section 7 is the
one exception and says so in its own text: pl_mpeg publishes no licence file to
read.

Sections
--------

  1. Godot Engine, the engine linked into CSVM.exe
  2. Godot Engine third-party components
  3. Godot Engine third-party licence texts
  4. .NET runtime, published self-contained into data_CSVM_windows_x86_64\
  5. .NET runtime third-party notices
  6. Rust crates linked into tools\unzbd.exe
  7. pl_mpeg, the MPEG-1 decoder CSVM's video code is ported from
  8. PromptFont, the font the controller button pictures are drawn from

"@

$crateIntro = @"
tools\unzbd.exe is built from the mech3ax fork, branch cs-anim, at commit
$forkCommit.
The fork's own code is EUPL-1.2 and its text is in LICENSE-unzbd, not repeated
here; the crates statically linked into the binary are under their own terms and
are listed below with the SPDX expression each crate's Cargo.toml declares.

The list is every non-workspace package cargo resolves for
x86_64-pc-windows-msvc, which is a superset of what any single binary links, so
nothing linked is missing from it. The licence texts follow, deduplicated by
content: a text several crates ship identically appears once, under all of their
names.

$($crateRows.ToString().TrimEnd())

$($crateBlocks.ToString().TrimEnd())
"@

$document = @(
    $header
    (Section "1" "Godot Engine, the engine linked into CSVM.exe" $godotLicense)
    (Section "2" "Godot Engine third-party components" $godotCopyright)
    (Section "3" "Godot Engine third-party licence texts" $godotLicenses)
    (Section "4" "The .NET runtime published into data_CSVM_windows_x86_64\" $dotnetLicense)
    (Section "5" ".NET runtime third-party notices" $dotnetNotices)
    (Section "6" "Rust crates linked into tools\unzbd.exe" $crateIntro)
    (Section "7" "pl_mpeg, the MPEG-1 decoder CSVM's video code is ported from" $plmpegIntro)
    (Section "8" "PromptFont, the font the controller button pictures are drawn from" $promptFontIntro)
) -join "`n"

# Godot 4.7 embeds the FreeType licence with its copyright sign ALREADY double-encoded --
# the engine hands back U+00C2 U+00A9 where one copyright sign was meant -- and the repo's own
# CheckEncoding.ps1 is right to refuse to commit that. Undoing the sequence restores the
# character upstream meant and changes no term of any licence, but it is still a deliberate
# edit to a verbatim text, so it is confined to that one pattern and counted out loud: a
# second occurrence turning up here is something to go and look at, not to wave through.
# Written as regex escapes, not as the characters themselves: a BOM-less .ps1 carrying
# non-ASCII is mangled by PowerShell 5.1's own interpreter before it runs (CLAUDE.md).
$repairPattern = 'Â([ -¿])'
$repaired = [regex]::Matches($document, $repairPattern).Count
$document = [regex]::Replace($document, $repairPattern, '$1')

# CRLF throughout, matching the other files in the zip: a recipient opens this in whatever
# Windows hands them. Normalise from LF rather than replacing blindly, or the upstream
# files that already use CRLF gain a second carriage return per line.
$document = ($document -replace "`r`n", "`n") -replace "`n", "`r`n"
[System.IO.File]::WriteAllText($OutFile, $document, $Utf8NoBom)

Write-Host "Wrote $OutFile" -ForegroundColor Green
Write-Host "  Godot $godotBuild, .NET runtime $RuntimeVersion, $($crates.Count) crates, $($texts.Count) distinct crate licence texts"
Write-Host "  Repaired $repaired double-encoded character(s) in upstream text (expected 1, Godot's FreeType notice)"
Write-Host "  pl_mpeg's MIT terms taken from packaging\LICENSE-plmpeg (upstream ships no licence file)"
Write-Host "  PromptFont's copyright read from its name table, its OFL text from packaging\LICENSE-promptfont"
