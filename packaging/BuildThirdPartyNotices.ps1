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

    The payload sources are read from the artefacts themselves, or from the toolchain that
    built them, rather than from a copy of upstream's website:

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

    - The Rust standard library. Every Rust binary links it, and it is licensed apart from
      the crates cargo resolves. Section 9 is the pinned toolchain's own
      share/doc/rust/COPYRIGHT-library.html as plain text, the licence texts it names from
      share/doc/rust/licenses/, and the crates the target's rust-std rlibs name as their
      sources that the file does not list (on musl, std's backtrace crates), read from their
      crates.io releases.

    PromptFont follows the same rule. Its copyright statement is read out of the shipped
    font's own name table, and its licence is the LICENSE.txt from the PromptFont release,
    kept byte-identical as packaging/LICENSE-promptfont because that text carries no
    copyright line to read.

    pl_mpeg is the exception to that rule, and cannot be anything else: upstream declares
    MIT through an SPDX-License-Identifier line in its header and publishes no licence
    file, so there is no upstream text to read. The terms are therefore kept in this
    repository as packaging/LICENSE-plmpeg, and the section says in the shipped file where
    that text came from so a reader is not misled into taking it for a verbatim copy.

    ExportRelease.ps1 checks the version stamps this script writes into the file's header
    against what it is actually packaging, and refuses an export whose notice was
    assembled for a different engine, runtime, runtime pack, crate target or fork commit.
    That check is what makes a stale notice a build failure rather than a silent shipping
    mistake. pl_mpeg and PromptFont carry no such stamp because both live in this
    repository rather than in a separately built binary: they move with CSVM's own
    history, which BUILD-INFO.txt already records.

    -Linux also writes packaging/LICENSE-thirdparty-linux.txt, which the Linux tarball
    ships as LICENSE-thirdparty.txt. Its sources are the Linux payload's own: the
    linux-x64 runtime pack the Linux export bundles, and the crate tree cargo resolves
    for x86_64-unknown-linux-musl, the target tools/unzbd is built for. The two crate
    trees differ (libc and the backtrace crates on Linux, windows-sys and its companions
    on Windows). Godot's texts are the same for both: the engine compiles them from one
    COPYRIGHT.txt on every platform, and the run checks through WSL that the Linux
    export template reports the same build string as the editor they are read from.
    The tarball carries no SDL2, so neither notice has an SDL section; the zip's
    LICENSE-SDL2.txt is SDL's own file beside it.

.EXAMPLE
    .\packaging\BuildThirdPartyNotices.ps1
    Rewrite packaging/LICENSE-thirdparty.txt from the current tools/ and NuGet cache.

.EXAMPLE
    .\packaging\BuildThirdPartyNotices.ps1 -Linux
    The same, then packaging/LICENSE-thirdparty-linux.txt beside it.
#>

[CmdletBinding()]
param(
    # Empty means "the newest win-x64 runtime pack in the NuGet cache", which is what a
    # self-contained publish on this machine picks up. -Linux reads the linux-x64 pack of
    # the same version, since both exports publish from the one SDK.
    [string] $RuntimeVersion = "",

    # The checkout whose git-ignored tools\ holds Godot and the mech3ax fork, as for
    # ExportRelease.ps1's -ToolsRoot; a worktree has none of its own.
    [string] $ToolsRoot = "",

    # Also write the Linux tarball's notice. Needs WSL (Debian) for the template check.
    [switch] $Linux
)

$ErrorActionPreference = "Stop"

$RepoRoot     = Split-Path $PSScriptRoot -Parent
# tools/ is git-ignored, so a worktree with no -ToolsRoot takes it from CSVM_DATA_ROOT, as
# ExportRelease.ps1 does.
if (-not $ToolsRoot) {
    $ToolsRoot = $RepoRoot
    if ((-not (Test-Path (Join-Path $RepoRoot "tools\godot"))) -and $env:CSVM_DATA_ROOT) {
        $ToolsRoot = $env:CSVM_DATA_ROOT
    }
}
$ToolsRoot    = (Resolve-Path $ToolsRoot).Path
$GodotExe     = Join-Path $ToolsRoot "tools\godot\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe"
$Mech3axRepo  = Join-Path $ToolsRoot "tools\mech3ax"
$PlMpegFile   = Join-Path $PSScriptRoot "LICENSE-plmpeg"
$PromptFontLicense = Join-Path $PSScriptRoot "LICENSE-promptfont"
$PromptFontFile    = Join-Path $RepoRoot "CSVM\data\promptfont.ttf.bin"
$PackCache    = Join-Path $env:USERPROFILE ".nuget\packages"
$PackRoot     = Join-Path $PackCache "microsoft.netcore.app.runtime.win-x64"
$LinuxTemplate = Join-Path $env:APPDATA "Godot\export_templates\4.7.stable.mono\linux_release.x86_64"
$LinuxDistro  = "Debian"

# What differs between the two notices. Pack and Target are also written into the header as
# stamps, which ExportRelease.ps1 and sandbox\LinuxRelease.ps1 read back, so a notice built for
# one platform cannot ship in the other's archive. Sep is the path separator the recipient's
# system uses, so the file names read as they appear in that archive.
$Platforms = @(
    [pscustomobject]@{
        Name = "Windows"; OutFile = Join-Path $PSScriptRoot "LICENSE-thirdparty.txt"
        Pack = "win-x64"; Target = "x86_64-pc-windows-msvc"; Archive = "zip"; Sep = "\"
        Exe = "CSVM.exe"; DataDir = "data_CSVM_windows_x86_64"; Unzbd = "tools\unzbd.exe"
    }
)
if ($Linux) {
    $Platforms += [pscustomobject]@{
        Name = "Linux"; OutFile = Join-Path $PSScriptRoot "LICENSE-thirdparty-linux.txt"
        Pack = "linux-x64"; Target = "x86_64-unknown-linux-musl"; Archive = "tarball"; Sep = "/"
        Exe = "CSVM.x86_64"; DataDir = "data_CSVM_linuxbsd_x86_64"; Unzbd = "tools/unzbd"
    }
}

$Rule = "=" * 78
$Thin = "-" * 78

# BOM-less UTF-8 everywhere: PowerShell 5.1's Get-Content/Set-Content default to ANSI and
# would double-encode every copyright sign in 800 KB of upstream text (verification.md
# SHELL-7, CLAUDE.md). Read and write through the .NET APIs, which detect and emit UTF-8.
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Read-Utf8([string] $Path) { return [System.IO.File]::ReadAllText($Path) }

$requiredPaths = @($GodotExe, $Mech3axRepo, $PackRoot, $PromptFontFile, $PromptFontLicense)
if ($Linux) { $requiredPaths += $LinuxTemplate }
foreach ($required in $requiredPaths) {
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

# The texts above come out of the Windows editor. The Linux export template is a different
# binary of the same build, compiled from the same COPYRIGHT.txt, so its tables are the same
# ones; what can drift is which build the installed template is. Its own --version answers
# that, and only a Linux process can run it.
if ($Linux) {
    $templateWsl = (& wsl.exe -d $LinuxDistro --exec wslpath -u $LinuxTemplate)
    if ($LASTEXITCODE -ne 0 -or -not $templateWsl) {
        throw "WSL distro '$LinuxDistro' is not reachable to run $LinuxTemplate -- one-time " +
            "setup: 'wsl --install -d Debian' (ExportRelease.ps1 -Linux needs it too)."
    }
    $templateBuild = (& wsl.exe -d $LinuxDistro --exec $templateWsl.Trim() --version |
        Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or "$templateBuild".Trim() -ne $godotBuild) {
        throw "The Linux export template $LinuxTemplate reports build '$templateBuild', not the " +
            "editor's $godotBuild -- reinstall the templates from the pinned .tpz."
    }
}

# ---------------------------------------------------------------- .NET runtime

if (-not $RuntimeVersion) {
    # Sort as versions, not as strings: 8.0.9 sorts after 8.0.30 alphabetically.
    $RuntimeVersion = (Get-ChildItem $PackRoot -Directory |
        Sort-Object { [version] $_.Name } | Select-Object -Last 1).Name
}
foreach ($platform in $Platforms) {
    $packRootHere = Join-Path $PackCache "microsoft.netcore.app.runtime.$($platform.Pack)"
    $packDir = Join-Path $packRootHere $RuntimeVersion
    if (-not (Test-Path $packDir)) {
        throw "Runtime pack $($platform.Pack) $RuntimeVersion not in the NuGet cache " +
            "($packRootHere) -- export the $($platform.Name) build once so the pack is " +
            "restored, or pass -RuntimeVersion for one that is there."
    }
    $platform | Add-Member PackName "Microsoft.NETCore.App.Runtime.$($platform.Pack)"
    $platform | Add-Member DotnetLicense (Read-Utf8 (Join-Path $packDir "LICENSE.TXT"))
    $platform | Add-Member DotnetNotices (Read-Utf8 (Join-Path $packDir "THIRD-PARTY-NOTICES.TXT"))
}

# ---------------------------------------------------------------- unzbd's crates

$forkCommit = (& git -C $Mech3axRepo rev-parse cs-anim).Trim()
if ($LASTEXITCODE -ne 0 -or $forkCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve cs-anim in $Mech3axRepo."
}

# The enumeration runs on this host for both targets: --filter-platform evaluates every cfg for
# the named triple whatever the host is, and Cargo.lock's checksums pin each crate to the same
# bytes in this machine's registry as in the WSL registry the musl build compiled.
function Get-CrateSection([string] $Target) {
    Push-Location $script:Mech3axRepo
    try {
        # --filter-platform keeps the other platform's crates out; --offline keeps the
        # enumeration a read of the lockfile and the registry checkouts the binary was
        # actually built from, rather than a resolve that could pull something newer.
        $metadataJson = & cargo metadata --format-version 1 --offline --filter-platform $Target
        if ($LASTEXITCODE -ne 0) {
            throw "cargo metadata failed (exit $LASTEXITCODE) in $($script:Mech3axRepo) for $Target."
        }
    } finally {
        Pop-Location
    }
    $metadata = $metadataJson | ConvertFrom-Json

    # A null source is a workspace member (mech3ax's own crates), covered by LICENSE-unzbd.
    $crates = @($metadata.packages | Where-Object { $_.source } | Sort-Object name, version)
    if ($crates.Count -eq 0) {
        throw "cargo metadata resolved no external crates for $Target -- the filter or the lockfile is wrong."
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
        [void] $crateBlocks.AppendLine($script:Thin)
        [void] $crateBlocks.AppendLine("As shipped by: " + ($texts[$text] -join ", "))
        [void] $crateBlocks.AppendLine($script:Thin)
        [void] $crateBlocks.AppendLine()
        [void] $crateBlocks.AppendLine($text.TrimEnd())
        [void] $crateBlocks.AppendLine()
    }
    return [pscustomobject]@{
        Rows = $crateRows.ToString().TrimEnd(); Blocks = $crateBlocks.ToString().TrimEnd()
        Count = $crates.Count; TextCount = $texts.Count
    }
}

foreach ($platform in $Platforms) {
    Write-Host "Resolving unzbd's crates for $($platform.Target) at cs-anim $($forkCommit.Substring(0, 8))..." -ForegroundColor Cyan
    $platform | Add-Member Crates (Get-CrateSection $platform.Target)
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

function Get-PromptFontIntro([string] $Sep) {
    return @"
The controller button pictures CSVM draws in its prompts are characters of
PromptFont by Yukari "Shinmera" Hafner, published at
https://github.com/Shinmera/promptfont. The font file is packed unmodified inside
the exported .pck as data$($Sep)promptfont.ttf.bin; only the file name differs from
upstream's promptfont.ttf.

The font's copyright statement, as its own name table carries it:

$script:fontCopyrightLines

The licence text follows, byte-identical to the LICENSE.txt in the PromptFont
release and kept in this project as packaging\LICENSE-promptfont.

$($script:promptFontLicenseText.TrimEnd())
"@
}

# ---------------------------------------------------------------- Rust toolchain pin

# Two sections speak for what the pinned Rust toolchain puts into unzbd rather than for crates
# cargo resolves: the standard library (section 9, both platforms) and musl (section 10, Linux).
# Both were established for one toolchain. The standard library section is read out of that
# toolchain's own files, but its converter accepts only the markup this release's
# COPYRIGHT-library.html uses, and the musl version was read off this release's libc.a by hand,
# so a moved pin is a refusal rather than a silent re-read.
$RustPin      = "1.91.1"
$pinMatch = Select-String -Path (Join-Path $Mech3axRepo "rust-toolchain.toml") `
    -Pattern '^channel\s*=\s*"([^"]+)"'
$rustPinNow = if ($pinMatch) { $pinMatch.Matches[0].Groups[1].Value } else { "(none)" }
if ($rustPinNow -ne $RustPin) {
    throw "tools\mech3ax\rust-toolchain.toml pins Rust $rustPinNow, but the standard library and " +
        "musl sections were established for Rust $RustPin. Re-check the section 9 converter " +
        "against the new toolchain's share/doc/rust/COPYRIGHT-library.html, re-read which musl " +
        "its self-contained libc.a is (replace packaging\LICENSE-musl with that release's " +
        "COPYRIGHT), and update `$RustPin and `$MuslVersion here."
}

# ---------------------------------------------------------------- Rust standard library

# Every Rust binary has the standard library (core, alloc, std and the crates.io crates they are
# built from) compiled into it. It is Apache-2.0 OR MIT, and either choice obliges a binary
# distribution to carry the licence and its copyright notices; so do the MIT and Unicode-3.0
# terms of the pieces inside it. The Rust project publishes those notices for each release as
# share/doc/rust/COPYRIGHT-library.html, installed by the toolchain's rustc component, and the
# section is that file as plain text. The file lists only what the library's default features
# pull in: std's backtrace symbolizer (addr2line, object, miniz_oxide, adler2, memchr) is built
# into the gnu and musl targets' rust-std but absent from it. So the crates each target's
# rust-std actually carries are read off its rlibs, each of which names its own crate and
# version in the source paths compiled into it, and any not in the file get their texts from
# their crates.io release.

# The sysroot of the pinned toolchain that built each platform's unzbd: this host's for the
# msvc target, the WSL distro's for the musl one (ExportRelease.ps1 builds it there).
function Get-RustSysroot($P) {
    if ($P.Name -eq "Windows") {
        $version = (& rustc "+$script:RustPin" -V)
        $sysroot = (& rustc "+$script:RustPin" --print sysroot)
    } else {
        $wslHome = (& wsl.exe -d $script:LinuxDistro --exec printenv HOME)
        $rustc = "$("$wslHome".Trim())/.cargo/bin/rustc"
        $version = (& wsl.exe -d $script:LinuxDistro --exec $rustc "+$script:RustPin" -V)
        $wslSysroot = (& wsl.exe -d $script:LinuxDistro --exec $rustc "+$script:RustPin" --print sysroot)
        $sysroot = if ($wslSysroot) {
            (& wsl.exe -d $script:LinuxDistro --exec wslpath -w "$wslSysroot".Trim())
        }
    }
    if ("$version" -notmatch "^rustc $([regex]::Escape($script:RustPin)) " -or -not $sysroot) {
        throw "Rust $($script:RustPin) is not installed for the $($P.Name) build (rustc " +
            "+$($script:RustPin) -V answered '$version') -- it is the toolchain " +
            "tools\mech3ax\rust-toolchain.toml pins; see docs/tooling.md."
    }
    $sysroot = "$sysroot".Trim()
    $libDir = Join-Path $sysroot "lib\rustlib\$($P.Target)\lib"
    if (-not (Test-Path $libDir)) {
        throw "Rust $($script:RustPin) has no $($P.Target) standard library at $libDir."
    }
    return $sysroot
}

# Each rlib names where it was built from in the panic locations compiled into it: a crates.io
# dependency as /rust/deps/<crate>-<version>/, an in-tree crate as
# /rustc/<commit>/library/<crate>/. An rlib naming neither is refused rather than guessed, since
# that is how a crate this section should speak for would go missing.
function Get-StdCrates([string] $LibDir) {
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $deps = @{}
    foreach ($rlib in (Get-ChildItem $LibDir -Filter "*.rlib" | Sort-Object Name)) {
        if ($rlib.Name -notmatch '^lib(.+)-[0-9a-f]+\.rlib$') {
            throw "Unexpected standard library file name $($rlib.FullName)."
        }
        $crate = $Matches[1]
        $bytes = $latin1.GetString([System.IO.File]::ReadAllBytes($rlib.FullName))
        $versions = @([regex]::Matches($bytes,
                '/rust/deps[/\\]([A-Za-z0-9_.+-]+?)-([0-9]+\.[0-9]+\.[0-9]+[A-Za-z0-9.+-]*?)[/\\]') |
            Where-Object { ($_.Groups[1].Value -replace '-', '_') -eq $crate } |
            ForEach-Object { "$($_.Groups[1].Value)-$($_.Groups[2].Value)" } | Sort-Object -Unique)
        if ($versions.Count -gt 1) {
            throw "$($rlib.FullName) names more than one version of itself: $($versions -join ', ')."
        }
        if ($versions.Count -eq 1) {
            $deps[$versions[0]] = $true
            continue
        }
        $inTree = '/rustc/[0-9a-f]{40}[/\\]library[/\\]' + ($crate -replace '_', '[-_]') + '[/\\]'
        if (-not [regex]::IsMatch($bytes, $inTree)) {
            throw "$($rlib.FullName) names neither a crates.io release of $crate nor an in-tree " +
                "library/ directory -- the standard library's layout changed; re-read section 9's " +
                "sources before trusting it."
        }
    }
    return @($deps.Keys | Sort-Object)
}

# COPYRIGHT-library.html is generated by the Rust project's generate-copyright tool from one
# template, and uses the handful of tags below. Anything else is refused: a tag this converter
# does not know is text it could drop without anyone seeing. Licence bodies are <pre> blocks and
# are kept verbatim, character references decoded; everything else is reflowed to 78 columns. A
# licence body the file repeats word for word (the Apache-2.0 text most crates ship) is printed
# once and referred back to, as section 6 does. The package icon before each crate name is left
# out. The returned Crates are the "<name>-<version>" headings, and InTree the SPDX identifiers
# the in-tree part names.
function ConvertFrom-CopyrightHtml([string] $Html) {
    $known = @("html", "head", "meta", "title", "body", "h1", "h2", "h3", "ul", "li", "a", "p",
        "b", "code", "div", "details", "summary", "pre")
    $icon = [char]::ConvertFromUtf32(0x1F4E6)
    $out = New-Object System.Text.StringBuilder
    $para = New-Object System.Text.StringBuilder
    $pre = New-Object System.Text.StringBuilder
    $state = @{ InPre = $false; InHead = $false; Bullet = $false; Heading = ""; H2 = "";
        Crate = ""; Summary = ""; Href = ""; AnchorAt = 0 }
    $crates = New-Object System.Collections.ArrayList
    $inTree = New-Object System.Collections.ArrayList
    $seenBodies = @{}

    $flush = {
        $text = ([regex]::Replace($para.ToString(), '\s+', ' ')).Trim()
        [void] $para.Clear()
        if (-not $text) { return }
        $text = $text.Replace($icon, "").Trim()
        switch ($state.Heading) {
            "h1" { [void] $out.AppendLine($text); [void] $out.AppendLine("=" * $text.Length) }
            "h2" {
                [void] $out.AppendLine($text); [void] $out.AppendLine("-" * $text.Length)
                $state.H2 = $text
            }
            "h3" {
                [void] $out.AppendLine($script:Thin); [void] $out.AppendLine($text)
                [void] $out.AppendLine($script:Thin)
                $state.Crate = $text
                [void] $crates.Add($text)
            }
            default {
                if ($state.H2 -eq "In-tree files" -and $text -match '^License: (.+)$') {
                    foreach ($id in [regex]::Matches($Matches[1], '[A-Za-z0-9.+-]+')) {
                        if ($id.Value -notin @("AND", "OR", "WITH")) { [void] $inTree.Add($id.Value) }
                    }
                }
                $first = if ($state.Bullet) { "  - " } else { "" }
                $rest = if ($state.Bullet) { "    " } else { "" }
                $line = $first
                $empty = $true
                foreach ($word in $text.Split(' ')) {
                    if (-not $empty -and ($line.Length + 1 + $word.Length) -gt 78) {
                        [void] $out.AppendLine($line); $line = $rest; $empty = $true
                    }
                    $line = if ($empty) { "$line$word" } else { "$line $word" }
                    $empty = $false
                }
                [void] $out.AppendLine($line)
                # A list's items stand on consecutive lines; the list's end closes it.
                if ($state.Bullet) { return }
            }
        }
        [void] $out.AppendLine()
    }

    $pos = 0
    foreach ($m in [regex]::Matches($Html, '<!DOCTYPE[^>]*>|<!--.*?-->|<(/?)([A-Za-z0-9]+)((?:\s[^>]*)?)/?>',
            [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        $text = $Html.Substring($pos, $m.Index - $pos)
        $pos = $m.Index + $m.Length
        if ($state.InPre) { [void] $pre.Append($text) }
        elseif (-not $state.InHead) { [void] $para.Append([System.Net.WebUtility]::HtmlDecode($text)) }
        if (-not $m.Groups[2].Success) { continue }
        $closing = $m.Groups[1].Value -eq "/"
        $tag = $m.Groups[2].Value.ToLowerInvariant()
        if ($tag -notin $known) {
            throw "COPYRIGHT-library.html uses <$tag>, which the section 9 converter does not " +
                "handle -- extend ConvertFrom-CopyrightHtml rather than let its text be dropped."
        }
        if ($state.InPre -and $tag -ne "pre") {
            throw "COPYRIGHT-library.html has markup (<$tag>) inside a licence text."
        }
        switch ($tag) {
            "head" { $state.InHead = -not $closing }
            { $_ -in @("b", "code") } { }
            "a" {
                if (-not $closing) {
                    $hrefMatch = [regex]::Match($m.Groups[3].Value, 'href="([^"]*)"')
                    $state.Href = if ($hrefMatch.Success) { [System.Net.WebUtility]::HtmlDecode($hrefMatch.Groups[1].Value) } else { "" }
                    $state.AnchorAt = $para.Length
                } elseif ($state.Href -match '^https?://') {
                    $anchorText = $para.ToString().Substring($state.AnchorAt).Trim()
                    if ($anchorText -ne $state.Href) { [void] $para.Append(" <$($state.Href)>") }
                }
            }
            "pre" {
                if (-not $closing) {
                    & $flush
                    $state.InPre = $true
                    [void] $pre.Clear()
                } else {
                    $state.InPre = $false
                    $body = [System.Net.WebUtility]::HtmlDecode($pre.ToString()) -replace "`r`n", "`n"
                    if ($body.StartsWith("`n")) { $body = $body.Substring(1) }
                    $body = $body.TrimEnd()
                    $label = "$($state.Crate), $($state.Summary)"
                    if ($seenBodies.ContainsKey($body)) {
                        [void] $out.AppendLine("(The same text as $($seenBodies[$body]), above.)")
                    } else {
                        $seenBodies[$body] = $label
                        [void] $out.AppendLine($body)
                    }
                    [void] $out.AppendLine()
                }
            }
            default {
                # Every other tag is a block boundary. A <summary> is the name of the licence
                # file the <pre> after it holds, which a repeated body refers back to.
                if ($tag -eq "summary" -and $closing) {
                    $state.Summary = ([regex]::Replace($para.ToString(), '\s+', ' ')).Trim()
                }
                & $flush
                if ($tag -in @("h1", "h2", "h3")) { $state.Heading = if ($closing) { "" } else { $tag } }
                if ($tag -eq "li") { $state.Bullet = -not $closing }
                if ($tag -eq "ul" -and $closing) { [void] $out.AppendLine() }
            }
        }
    }
    & $flush
    return [pscustomobject]@{
        Text = $out.ToString().TrimEnd(); Crates = @($crates); InTree = @($inTree | Sort-Object -Unique)
    }
}

# A standard library crate COPYRIGHT-library.html does not list is fetched into the cargo
# registry at the exact version its rlib names, through a throwaway manifest in TEMP, and read
# from there as section 6 reads its crates. crates.io never changes a published version, so
# that checkout is the source the rlib was built from.
function Get-RegistryCrateDir([string] $NameVersion) {
    return Get-ChildItem (Join-Path $env:USERPROFILE ".cargo\registry\src\*\$NameVersion") `
        -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
}
function Save-RegistryCrates([string[]] $NameVersions) {
    $absent = @($NameVersions | Where-Object { -not (Get-RegistryCrateDir $_) })
    if ($absent.Count -eq 0) { return }
    $fetchDir = Join-Path $env:TEMP "csvm-notices-std-$PID"
    New-Item -ItemType Directory -Force (Join-Path $fetchDir "src") | Out-Null
    try {
        $deps = ($absent | ForEach-Object {
            $m = [regex]::Match($_, '^(.+?)-([0-9]+\..*)$')
            "$($m.Groups[1].Value) = `"=$($m.Groups[2].Value)`""
        }) -join "`n"
        [System.IO.File]::WriteAllText((Join-Path $fetchDir "Cargo.toml"),
            "[package]`nname = `"csvm-notices-std`"`nversion = `"0.0.0`"`nedition = `"2021`"`n`n[dependencies]`n$deps`n",
            $script:Utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $fetchDir "src\lib.rs"), "", $script:Utf8NoBom)
        Write-Host "Fetching $($absent -join ', ') from crates.io for section 9..." -ForegroundColor Cyan
        # cargo metadata unpacks each crate into registry\src, which cargo fetch alone does not.
        & cargo metadata --format-version 1 --manifest-path (Join-Path $fetchDir "Cargo.toml") | Out-Null
    } finally {
        Remove-Item $fetchDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($nameVersion in $absent) {
        if (-not (Get-RegistryCrateDir $nameVersion)) {
            throw "Could not fetch $nameVersion into the cargo registry; section 9 needs its licence texts."
        }
    }
}

function Get-StdSection($P) {
    $sysroot = Get-RustSysroot $P
    $docDir = Join-Path $sysroot "share\doc\rust"
    $copyrightFile = Join-Path $docDir "COPYRIGHT-library.html"
    if (-not (Test-Path $copyrightFile)) {
        throw "Not found: $copyrightFile -- the rustc component of Rust $($script:RustPin) " +
            "installs it; reinstall the toolchain."
    }
    $converted = ConvertFrom-CopyrightHtml (Read-Utf8 $copyrightFile)
    if ($converted.Crates.Count -eq 0 -or $converted.InTree.Count -eq 0) {
        throw "$copyrightFile converted to no crates or no in-tree licences -- its layout changed."
    }

    $licenceBlocks = New-Object System.Text.StringBuilder
    foreach ($id in $converted.InTree) {
        $licenceFile = Join-Path $docDir "licenses\$id.txt"
        if (-not (Test-Path $licenceFile)) {
            throw "COPYRIGHT-library.html names $id for in-tree files and $licenceFile is missing."
        }
        [void] $licenceBlocks.AppendLine($script:Thin)
        [void] $licenceBlocks.AppendLine("$id (share/doc/rust/licenses/$id.txt)")
        [void] $licenceBlocks.AppendLine($script:Thin)
        [void] $licenceBlocks.AppendLine()
        [void] $licenceBlocks.AppendLine((Read-Utf8 $licenceFile).TrimEnd())
        [void] $licenceBlocks.AppendLine()
    }

    $linked = Get-StdCrates (Join-Path $sysroot "lib\rustlib\$($P.Target)\lib")
    $unlisted = @($linked | Where-Object { $_ -notin $converted.Crates })
    if ($unlisted.Count -eq 0) {
        $partC = "Every crates.io crate the standard library for`n$($P.Target) is built from " +
            "is listed in part A."
    } else {
        Save-RegistryCrates $unlisted
        $rows = New-Object System.Text.StringBuilder
        $texts = [ordered]@{}
        foreach ($nameVersion in $unlisted) {
            $crateDir = Get-RegistryCrateDir $nameVersion
            $manifest = Read-Utf8 (Join-Path $crateDir.FullName "Cargo.toml")
            $spdx = [regex]::Match($manifest, '(?m)^license\s*=\s*"([^"]+)"').Groups[1].Value
            if (-not $spdx) { throw "$nameVersion's Cargo.toml declares no licence." }
            [void] $rows.AppendLine(("  {0,-32} {1}" -f $nameVersion, $spdx))
            $licenseFiles = @(Get-ChildItem $crateDir.FullName -File -Force |
                Where-Object { $_.Name -match '^(LICEN[CS]E|COPYING|NOTICE|UNLICENSE)' } | Sort-Object Name)
            if ($licenseFiles.Count -eq 0) { throw "$nameVersion ships no licence file." }
            foreach ($licenseFile in $licenseFiles) {
                $text = (Read-Utf8 $licenseFile.FullName).TrimEnd()
                if (-not $texts.Contains($text)) { $texts[$text] = New-Object System.Collections.ArrayList }
                [void] $texts[$text].Add("$nameVersion ($($licenseFile.Name))")
            }
        }
        $blocks = New-Object System.Text.StringBuilder
        foreach ($text in $texts.Keys) {
            [void] $blocks.AppendLine($script:Thin)
            [void] $blocks.AppendLine("As shipped by: " + ($texts[$text] -join ", "))
            [void] $blocks.AppendLine($script:Thin)
            [void] $blocks.AppendLine()
            [void] $blocks.AppendLine($text)
            [void] $blocks.AppendLine()
        }
        $partC = @"
The standard library for $($P.Target) is also built from these
crates, which std's backtrace support pulls in and part A does not list.
Their licence texts follow, read from each crate's crates.io release of the
version named.

$($rows.ToString().TrimEnd())

$($blocks.ToString().TrimEnd())
"@
    }

    $body = @"
$($P.Unzbd) is a Rust program, and the Rust standard library it is compiled
against (core, alloc, std and the crates they are built from) is linked into
it. That library is the one Rust $($script:RustPin) ships for the target
$($P.Target). It is licensed apart from mech3ax's own code and from the
crates in section 6: Apache-2.0 OR MIT, with the exceptions and third-party
crates part A names.

Part A is the Rust project's own statement of those terms for this release,
share/doc/rust/COPYRIGHT-library.html in the toolchain, as plain text: the
HTML markup is removed, character references are decoded, the paragraphs are
reflowed, and a licence text the file repeats word for word is printed once
and referred back to. Part B holds the licence texts part A names for the
library's own source files, from the toolchain's share/doc/rust/licenses/.
Part C covers the crates that the standard library files for
$($P.Target) name as their sources and part A does not list.

$script:Rule
Part A. COPYRIGHT-library.html
$script:Rule

$($converted.Text)

$script:Rule
Part B. Licences of the in-tree files
$script:Rule

$($licenceBlocks.ToString().TrimEnd())

$script:Rule
Part C. Crates part A does not list
$script:Rule

$partC
"@
    return [pscustomobject]@{ Body = $body; Unlisted = $unlisted; Listed = $converted.Crates.Count }
}

foreach ($platform in $Platforms) {
    Write-Host "Reading Rust $RustPin's standard library notices for $($platform.Target)..." -ForegroundColor Cyan
    $platform | Add-Member Std (Get-StdSection $platform)
}

# ---------------------------------------------------------------- musl (Linux only)

# The musl target links statically against the musl libc.a that Rust's own rust-std component
# bundles for it (the self-contained libc.a, not Debian's musl-dev), so the musl MIT notice
# travels with tools/unzbd. That libc.a carries no licence text and no version string. Its
# version was read off its symbols for Rust 1.91.1: qsort_r and the LFS64 names are present
# and statx is absent, which is musl 1.2.3. packaging\LICENSE-musl is the COPYRIGHT file of
# the musl-1.2.3 release tarball, byte-identical; .gitattributes keeps git's line-ending
# conversion off it, so the checked-out file is those bytes too. The pin check above covers a
# moved toolchain. LLVM's libunwind.a is linked too, and its Apache-2.0 WITH LLVM-exception
# terms waive the notice for binary form.
$MuslVersion  = "1.2.3"
$MuslLicense  = Join-Path $PSScriptRoot "LICENSE-musl"
if ($Linux) {
    if (-not (Test-Path $MuslLicense)) {
        throw "Not found: $MuslLicense -- the COPYRIGHT file of the musl-$MuslVersion release."
    }
    $muslIntro = @"
tools/unzbd is linked statically against the musl C library that Rust
$RustPin's x86_64-unknown-linux-musl target bundles, musl $MuslVersion
(https://musl.libc.org). That library ships no licence text of its own,
so what follows is the COPYRIGHT file of the musl-$MuslVersion release,
byte-identical, kept in this project as packaging\LICENSE-musl.

$((Read-Utf8 $MuslLicense).TrimEnd())
"@
}

# ---------------------------------------------------------------- assembly

function Section([string] $Number, [string] $Title, [string] $Body) {
    return @(
        $script:Rule
        "$Number. $Title"
        $script:Rule
        ""
        $Body.TrimEnd()
        ""
        ""
    ) -join "`n"
}

function Build-Notice($P) {
    $isLinux = $P.Name -eq "Linux"
    $dataDir = $P.DataDir + $P.Sep
    # musl's libc.a carries no text either, so on Linux section 10 is a second repository copy.
    # Section 9 comes first on both platforms so the sections the notices share keep one number.
    $regenerate = if ($isLinux) {
        @(
            "Regenerate with packaging\BuildThirdPartyNotices.ps1 -Linux, which reads every"
            "text in sections 1 to 6, 8 and 9 out of the shipped artefacts themselves or the"
            "Rust toolchain that built them. Sections 7 and 10 are the exceptions and say so"
            "in their own text: pl_mpeg publishes no licence file to read, and the musl"
            "library Rust links carries none."
        ) -join "`n"
    } else {
        @(
            "Regenerate with packaging\BuildThirdPartyNotices.ps1, which reads every text in"
            "sections 1 to 6, 8 and 9 out of the shipped artefacts themselves or the Rust"
            "toolchain that built them. Section 7 is the one exception and says so in its own"
            "text: pl_mpeg publishes no licence file to read."
        ) -join "`n"
    }
    $muslLine = if ($isLinux) { "`n 10. musl, the C library linked into $($P.Unzbd)" } else { "" }
    $header = @"
CSVM third-party notices
========================

This file carries the notices that the third-party software inside this build is
licensed on condition of carrying, and one notice for third-party source CSVM's own
code is ported from. It is not CSVM's own licence: CSVM is under the GNU General
Public License v3, whose text is in LICENSE beside this file, and the extractor
$($P.Unzbd) is under the EUPL-1.2, whose text is in LICENSE-unzbd.

This archive contains no Crimson Skies code, data or artwork. The game files a
player extracts with CSVM stay on their own machine.

Assembled from these exact payload versions, which ExportRelease.ps1 re-checks
against what it packages before it will build a $($P.Archive):

  Godot Engine build: $script:godotBuild
  .NET runtime version: $script:RuntimeVersion
  .NET runtime pack: $($P.PackName)
  unzbd crate target: $($P.Target)
  mech3ax cs-anim commit: $script:forkCommit

$regenerate

Sections
--------

  1. Godot Engine, the engine linked into $($P.Exe)
  2. Godot Engine third-party components
  3. Godot Engine third-party licence texts
  4. .NET runtime, published self-contained into $dataDir
  5. .NET runtime third-party notices
  6. Rust crates linked into $($P.Unzbd)
  7. pl_mpeg, the MPEG-1 decoder CSVM's video code is ported from
  8. PromptFont, the font the controller button pictures are drawn from
  9. The Rust standard library, linked into $($P.Unzbd)$muslLine

"@

    $crateIntro = @"
$($P.Unzbd) is built from the mech3ax fork, branch cs-anim, at commit
$script:forkCommit.
The fork's own code is EUPL-1.2 and its text is in LICENSE-unzbd, not repeated
here; the crates statically linked into the binary are under their own terms and
are listed below with the SPDX expression each crate's Cargo.toml declares.

The list is every non-workspace package cargo resolves for
$($P.Target), which is a superset of what any single binary links, so
nothing linked is missing from it. The licence texts follow, deduplicated by
content: a text several crates ship identically appears once, under all of their
names.

$($P.Crates.Rows)

$($P.Crates.Blocks)
"@

    $sections = @(
        $header
        (Section "1" "Godot Engine, the engine linked into $($P.Exe)" $script:godotLicense)
        (Section "2" "Godot Engine third-party components" $script:godotCopyright)
        (Section "3" "Godot Engine third-party licence texts" $script:godotLicenses)
        (Section "4" "The .NET runtime published into $dataDir" $P.DotnetLicense)
        (Section "5" ".NET runtime third-party notices" $P.DotnetNotices)
        (Section "6" "Rust crates linked into $($P.Unzbd)" $crateIntro)
        (Section "7" "pl_mpeg, the MPEG-1 decoder CSVM's video code is ported from" $script:plmpegIntro)
        (Section "8" "PromptFont, the font the controller button pictures are drawn from" (Get-PromptFontIntro $P.Sep))
        (Section "9" "The Rust standard library, linked into $($P.Unzbd)" $P.Std.Body)
    )
    if ($isLinux) {
        $sections += (Section "10" "musl, the C library linked into $($P.Unzbd)" $script:muslIntro)
    }
    return $sections -join "`n"
}

# The markers ExportRelease.ps1 and sandbox\LinuxRelease.ps1 refuse in the other platform's
# archive. Checked here too, so a notice that would fail there is never written.
$ForeignMarkers = @{
    Windows = @("linux-x64", "x86_64-unknown-linux-musl", "data_CSVM_linuxbsd_x86_64", "CSVM.x86_64")
    Linux   = @("win-x64", "x86_64-pc-windows-msvc", "data_CSVM_windows_x86_64", "unzbd.exe", "CSVM.exe")
}

foreach ($platform in $Platforms) {
    $document = Build-Notice $platform

    # Godot 4.7 embeds the FreeType licence with its copyright sign ALREADY double-encoded --
    # the engine hands back U+00C2 U+00A9 where one copyright sign was meant -- and the repo's
    # own CheckEncoding.ps1 is right to refuse to commit that. Undoing the sequence restores
    # the character upstream meant and changes no term of any licence, but it is still a
    # deliberate edit to a verbatim text, so it is confined to that one pattern and counted out
    # loud: a second occurrence turning up here is something to go and look at, not to wave
    # through. Written as regex escapes, not as the characters themselves: a BOM-less .ps1
    # carrying non-ASCII is mangled by PowerShell 5.1's own interpreter before it runs.
    $repairPattern = '\u00C2([\u00A0-\u00BF])'
    $repaired = [regex]::Matches($document, $repairPattern).Count
    $document = [regex]::Replace($document, $repairPattern, '$1')

    foreach ($marker in $ForeignMarkers[$platform.Name]) {
        if ($document.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "The $($platform.Name) notice names '$marker', which belongs to the other " +
                "platform's archive -- an upstream text or this script's wording has changed."
        }
    }

    # CRLF throughout, as git's autocrlf checkout of every text file here gives it, so a
    # regenerated file and a checked-out one are the same bytes. Normalise from LF rather than
    # replacing blindly, or the upstream files that already use CRLF gain a second carriage
    # return per line. The Linux notice too: git stores it LF, and ExportRelease.ps1 stages the
    # tarball's text files with LF, the committed bytes.
    $document = ($document -replace "`r`n", "`n") -replace "`n", "`r`n"
    [System.IO.File]::WriteAllText($platform.OutFile, $document, $Utf8NoBom)

    Write-Host "Wrote $($platform.OutFile)" -ForegroundColor Green
    Write-Host "  Godot $godotBuild, .NET runtime $RuntimeVersion ($($platform.PackName)), $($platform.Crates.Count) crates for $($platform.Target), $($platform.Crates.TextCount) distinct crate licence texts"
    Write-Host "  Rust $RustPin standard library: $($platform.Std.Listed) crates listed by COPYRIGHT-library.html, $($platform.Std.Unlisted.Count) more read off the $($platform.Target) rlibs$(if ($platform.Std.Unlisted.Count) { ': ' + ($platform.Std.Unlisted -join ', ') })"
    Write-Host "  Repaired $repaired double-encoded character(s) in upstream text (expected 1, Godot's FreeType notice)"
}
Write-Host "  pl_mpeg's MIT terms taken from packaging\LICENSE-plmpeg (upstream ships no licence file)"
Write-Host "  PromptFont's copyright read from its name table, its OFL text from packaging\LICENSE-promptfont"
if ($Linux) {
    Write-Host "  musl $MuslVersion's COPYRIGHT taken from packaging\LICENSE-musl (Rust $RustPin's bundled libc.a)"
}

