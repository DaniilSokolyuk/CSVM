#!/usr/bin/env pwsh
# Doc-entry checker for the split docs/architecture/*.md and docs/cli.md.
#
# Rules (docs/PLAN-doc-cleanup.md, B12):
#   - docs/architecture/*.md: a "## src/..." entry's body (heading to next "## ", blank-trimmed)
#     is at most 8 lines, or 12 for a fixed list of heavy modules.
#   - docs/architecture.md: every "- " bullet under a "### src/" heading inside "## Module index"
#     is one physical line; a continuation line is a violation reported at the bullet's line.
#   - Existence: every "## src/<path>" heading names a file, directory or (for a "*" heading)
#     a glob match under CSVM/src.
#   - Coverage: every .cs under CSVM/src, except Testing/*Suites.cs and files under Mech3/Anim/,
#     appears as a heading in docs/architecture/*.md AND as a bullet in the index.
#   - docs/cli.md: every "- `--flag`" bullet under "## Flags" (through the line before the next
#     bullet or heading) is at most 600 characters; the three lab sections ("## The ...") are at
#     most 12 non-blank lines each; the distinct "--flag" token count in the index (before
#     "## Flags") equals the distinct flag count with a bullet (a bullet's own flag, plus a flag
#     the "shares ... bullet" reconciliation paragraph names as borrowing one).
#
# Pure ASCII on purpose (PROJECT_CONTEXT.md): PowerShell 5.1 mangles a BOM-less non-ASCII script
# before it runs. Files are READ only; nothing here writes.
#
# Usage:
#   ./CheckDocEntries.ps1                              the whole scope
#   ./CheckDocEntries.ps1 -Summary                      one line per file, worst first
#   ./CheckDocEntries.ps1 -Root <path>                  another worktree, with this copy's rules
#   ./CheckDocEntries.ps1 docs/architecture/Effects.md  just this file's findings
[CmdletBinding()]
param(
    [switch]$Summary,
    # $Path is declared first and claims position 0 plus every remaining positional argument, so
    # a bare path on the command line never gets bound to $Root instead (PowerShell assigns
    # positions in declaration order to any param without an explicit one).
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path,
    [string]$Root
)

# Modules whose entry body gets the 12-line cap instead of 8 (PLAN-doc-cleanup.md, B12).
$bigCapFiles = @(
    'GameSession.cs', 'FlightController.cs', 'FlightModel.cs', 'SceneBuilder.cs',
    'WorldBuilder.cs', 'AnimRuntime.cs', 'Projectile.cs'
)

# See CheckCommentCaps.ps1 for why $PSScriptRoot (not a cwd-derived root) is the default.
if (-not $Root) { $Root = $PSScriptRoot }
if (-not $Root) { $Root = git rev-parse --show-toplevel 2>$null }
if (-not $Root) { $Root = (Get-Location).Path }
$root = $Root

$findings = New-Object System.Collections.Generic.List[object]
function Add-Finding {
    param([string]$File, [int]$Line, [string]$Message)
    $findings.Add([pscustomobject]@{ File = $File; Line = $Line; Message = $Message })
}

function Read-Lines {
    param([string]$FullPath)
    (([IO.File]::ReadAllText($FullPath)) -replace "`r`n", "`n") -split "`n"
}

# ---------------------------------------------------------------------------
# docs/architecture/*.md: entry caps, plus the heading census used by the
# existence and coverage checks below.
# ---------------------------------------------------------------------------
$archDir = Join-Path $root 'docs\architecture'
$archFiles = @()
if (Test-Path -LiteralPath $archDir) {
    $archFiles = @(Get-ChildItem -LiteralPath $archDir -Filter *.md -File)
}

$allHeadings = @()   # every "## src/<path>" heading found, across all namespace files
foreach ($af in $archFiles) {
    $lines = Read-Lines $af.FullName
    $rel = 'docs/architecture/' + $af.Name
    $headingIdx = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^## ') { $headingIdx += $i }
    }
    for ($h = 0; $h -lt $headingIdx.Count; $h++) {
        $line = $headingIdx[$h]
        $heading = $lines[$line]
        if ($heading -notmatch '^## src/') { continue }
        $headingPath = $heading.Substring(3).Trim()
        $allHeadings += [pscustomobject]@{ File = $rel; Line = $line + 1; Path = $headingPath }

        $bodyStart = $line + 1
        $bodyEnd = if ($h + 1 -lt $headingIdx.Count) { $headingIdx[$h + 1] - 1 } else { $lines.Count - 1 }
        $s = $bodyStart
        $e = $bodyEnd
        while ($s -le $e -and $lines[$s].Trim() -eq '') { $s++ }
        while ($e -ge $s -and $lines[$e].Trim() -eq '') { $e-- }
        $n = if ($s -le $e) { $e - $s + 1 } else { 0 }

        $fileName = ($headingPath -split '/')[-1]
        $cap = 8
        if ($bigCapFiles -contains $fileName) { $cap = 12 }
        if ($headingPath -match '\*Suites\.cs$') { $cap = 12 }
        if ($n -gt $cap) {
            Add-Finding -File $rel -Line ($line + 1) `
                -Message ('entry body {0} lines, cap {1} ({2})' -f $n, $cap, $headingPath)
        }
    }
}

# ---------------------------------------------------------------------------
# docs/architecture.md: one-physical-line index bullets, plus the index text
# used by the coverage "no index bullet" check.
# ---------------------------------------------------------------------------
$idxRelPath = 'docs/architecture.md'
$idxFull = Join-Path $root 'docs\architecture.md'
$idxLines = @()
if (Test-Path -LiteralPath $idxFull) {
    $idxLines = Read-Lines $idxFull

    $moduleIndexStart = -1
    for ($i = 0; $i -lt $idxLines.Count; $i++) {
        if ($idxLines[$i] -match '^## Module index') { $moduleIndexStart = $i; break }
    }
    if ($moduleIndexStart -ge 0) {
        # "### src/..." subsection headings inside the module index; the next "## " (not "### ")
        # heading, if any, closes the whole index section.
        $moduleIndexEnd = $idxLines.Count - 1
        for ($i = $moduleIndexStart + 1; $i -lt $idxLines.Count; $i++) {
            if ($idxLines[$i] -match '^## ' -and $idxLines[$i] -notmatch '^### ') {
                $moduleIndexEnd = $i - 1
                break
            }
        }
        $subHeadingIdx = @()
        for ($i = $moduleIndexStart; $i -le $moduleIndexEnd; $i++) {
            if ($idxLines[$i] -match '^### ' -and $idxLines[$i] -match 'src/') { $subHeadingIdx += $i }
        }
        foreach ($sh in $subHeadingIdx) {
            $sectionEndCandidates = @($subHeadingIdx | Where-Object { $_ -gt $sh })
            $sectionEnd = if ($sectionEndCandidates.Count -gt 0) { $sectionEndCandidates[0] - 1 } else { $moduleIndexEnd }
            for ($i = $sh + 1; $i -le $sectionEnd; $i++) {
                if ($idxLines[$i] -notmatch '^- ') { continue }
                $j = $i + 1
                if ($j -le $sectionEnd -and $idxLines[$j].Trim() -ne '' -and
                    $idxLines[$j] -notmatch '^- ' -and $idxLines[$j] -notmatch '^#') {
                    Add-Finding -File $idxRelPath -Line ($i + 1) `
                        -Message ('index bullet spans more than one physical line')
                }
            }
        }
    }
}
$idxText = ($idxLines -join "`n")

# ---------------------------------------------------------------------------
# Existence: every "## src/<path>" heading names something real under CSVM/src.
# ---------------------------------------------------------------------------
$srcRoot = Join-Path $root 'CSVM\src'
foreach ($h in $allHeadings) {
    $p = $h.Path -replace '^src/', ''
    $ok = $false
    if ($p -match '\*') {
        $dir = Split-Path $p -Parent
        $pat = Split-Path $p -Leaf
        $full = if ($dir) { Join-Path $srcRoot $dir } else { $srcRoot }
        if (Test-Path -LiteralPath $full) {
            $m = @(Get-ChildItem -LiteralPath $full -Filter $pat -File -ErrorAction SilentlyContinue)
            $ok = $m.Count -gt 0
        }
    } elseif ($p.EndsWith('/')) {
        $full = Join-Path $srcRoot $p.TrimEnd('/')
        $ok = Test-Path -LiteralPath $full -PathType Container
    } else {
        $full = Join-Path $srcRoot $p
        $ok = Test-Path -LiteralPath $full -PathType Leaf
    }
    if (-not $ok) {
        Add-Finding -File $h.File -Line $h.Line `
            -Message ('heading names no file, directory or match under CSVM/src ({0})' -f $h.Path)
    }
}

# ---------------------------------------------------------------------------
# Coverage: every .cs under CSVM/src (minus the two excluded groups) needs a
# heading in docs/architecture/*.md AND a bullet in docs/architecture.md.
# ---------------------------------------------------------------------------
$coverageBucket = 'CSVM/src'
if (Test-Path -LiteralPath $srcRoot) {
    # A heading or bullet covers a file exactly, by a "*" glob in the same directory
    # (src/UI/Hangar/Hangar*Page.cs), or by a trailing-slash directory path (src/Mech3/Anim/).
    function Test-Covered([string]$srcPath, [string[]]$patterns) {
        foreach ($pat in $patterns) {
            if ($pat -eq $srcPath) { return $true }
            if ($pat.EndsWith('/')) { if ($srcPath.StartsWith($pat)) { return $true }; continue }
            if ($pat.Contains('*')) {
                $patDir = Split-Path $pat -Parent
                $srcDir = Split-Path $srcPath -Parent
                if ($patDir -eq $srcDir -and $srcPath -like $pat) { return $true }
            }
        }
        return $false
    }
    $headingPatterns = @($allHeadings | ForEach-Object { $_.Path } | Select-Object -Unique)
    $bulletPatterns = @([regex]::Matches($idxText, 'src/[A-Za-z0-9_./*-]+') |
        ForEach-Object { $_.Value } | Select-Object -Unique)

    $allCs = @(Get-ChildItem -LiteralPath $srcRoot -Filter *.cs -Recurse -File)
    $allCs = $allCs | Where-Object { $_.FullName -notmatch '\\(obj|bin|\.godot)\\' }
    foreach ($cs in $allCs) {
        $rel = $cs.FullName.Substring($srcRoot.Length + 1).Replace('\', '/')
        if ($rel -match '(^|/)Testing/[^/]*Suites\.cs$') { continue }
        if ($rel -match '(^|/)Mech3/Anim/') { continue }

        $srcPath = 'src/' + $rel
        if (-not (Test-Covered $srcPath $headingPatterns)) {
            Add-Finding -File $coverageBucket -Line 0 -Message ('CSVM/src/{0}: no entry' -f $rel)
        }
        if (-not (Test-Covered $srcPath $bulletPatterns)) {
            Add-Finding -File $coverageBucket -Line 0 -Message ('CSVM/src/{0}: no index bullet' -f $rel)
        }
    }
}

# ---------------------------------------------------------------------------
# docs/cli.md: flag-bullet cap, lab-section line cap, index/bullet reconciliation.
# ---------------------------------------------------------------------------
$cliRelPath = 'docs/cli.md'
$cliFull = Join-Path $root 'docs\cli.md'
if (Test-Path -LiteralPath $cliFull) {
    $cliLines = Read-Lines $cliFull

    $flagIndexLine = -1
    $flagsLine = -1
    $labHeadingIdx = @()
    for ($i = 0; $i -lt $cliLines.Count; $i++) {
        if ($cliLines[$i] -match '^## Flag index' -and $flagIndexLine -eq -1) { $flagIndexLine = $i }
        if ($cliLines[$i] -eq '## Flags' -and $flagsLine -eq -1) { $flagsLine = $i }
        if ($cliLines[$i] -match '^## The ') { $labHeadingIdx += $i }
    }

    if ($flagsLine -ge 0) {
        $flagsEnd = if ($labHeadingIdx.Count -gt 0) { ($labHeadingIdx | Sort-Object)[0] - 1 } else { $cliLines.Count - 1 }
        $bulletStarts = @()
        for ($i = $flagsLine + 1; $i -le $flagsEnd; $i++) {
            if ($cliLines[$i] -match '^- `--') { $bulletStarts += $i }
        }
        for ($b = 0; $b -lt $bulletStarts.Count; $b++) {
            $s = $bulletStarts[$b]
            $e = if ($b + 1 -lt $bulletStarts.Count) { $bulletStarts[$b + 1] - 1 } else { $flagsEnd }
            $text = ($cliLines[$s..$e] -join "`n")
            if ($text.Length -gt 600) {
                $flag = if ($cliLines[$s] -match '^- `(--[A-Za-z0-9-]+)') { $matches[1] } else { '?' }
                Add-Finding -File $cliRelPath -Line ($s + 1) `
                    -Message ('flag bullet {0} chars, cap 600 ({1})' -f $text.Length, $flag)
            }
        }
    }

    foreach ($h in $labHeadingIdx) {
        $laterHeadings = @($labHeadingIdx | Where-Object { $_ -gt $h })
        $nextAnyHeading = -1
        for ($i = $h + 1; $i -lt $cliLines.Count; $i++) {
            if ($cliLines[$i] -match '^## ') { $nextAnyHeading = $i; break }
        }
        $end = if ($nextAnyHeading -ge 0) { $nextAnyHeading - 1 } else { $cliLines.Count - 1 }
        $body = if ($h + 1 -le $end) { $cliLines[($h + 1)..$end] } else { @() }
        $nonBlank = @($body | Where-Object { $_.Trim() -ne '' }).Count
        if ($nonBlank -gt 12) {
            $title = $cliLines[$h].Substring(3).Trim()
            Add-Finding -File $cliRelPath -Line ($h + 1) `
                -Message ('lab section {0} non-blank lines, cap 12 ({1})' -f $nonBlank, $title)
        }
    }

    if ($flagIndexLine -ge 0 -and $flagsLine -gt $flagIndexLine) {
        $idxSection = ($cliLines[$flagIndexLine..($flagsLine - 1)] -join "`n")
        $indexTokens = @([regex]::Matches($idxSection, '--[A-Za-z][A-Za-z0-9-]*') |
                ForEach-Object { $_.Value } | Select-Object -Unique)

        $wholeText = ($cliLines -join "`n")
        $bulletTokens = @([regex]::Matches($wholeText, '(?m)^- `(--[A-Za-z][A-Za-z0-9-]*)') |
                ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        $sharedTokens = @([regex]::Matches($wholeText, '`(--[A-Za-z][A-Za-z0-9-]*)`\s+shares\s+`--') |
                ForEach-Object { $_.Groups[1].Value })
        $flagsWithBullet = @(($bulletTokens + $sharedTokens) | Select-Object -Unique)

        if ($indexTokens.Count -ne $flagsWithBullet.Count) {
            Add-Finding -File $cliRelPath -Line ($flagIndexLine + 1) `
                -Message ('index token count {0} != flags-with-bullet count {1}' -f
                    $indexTokens.Count, $flagsWithBullet.Count)
        }
    }
}

# ---------------------------------------------------------------------------
# Path arguments restrict which findings are reported (they are computed over
# the whole scope regardless, since existence/coverage/reconciliation are
# whole-tree invariants; a Path argument narrows the report, not the sweep).
# ---------------------------------------------------------------------------
if ($Path) {
    $resolvedTargets = @()
    foreach ($p in $Path) {
        # A relative path is resolved against $root, never the session's cwd: a bare
        # "docs/architecture.md" also exists in the main checkout, and Test-Path against cwd
        # would silently pick that copy up instead of this worktree's (CLAUDE.md's worktree trap).
        $candidate = if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $root $p }
        if (Test-Path -LiteralPath $candidate) { $resolvedTargets += (Resolve-Path -LiteralPath $candidate).Path }
    }
    $targetFullPaths = @($resolvedTargets | ForEach-Object { $_.TrimEnd('\') })
    $findings = $findings | Where-Object {
        $full = Join-Path $root ($_.File -replace '/', '\')
        if (Test-Path -LiteralPath $full) { $full = (Resolve-Path -LiteralPath $full).Path }
        $targetFullPaths -contains $full
    }
}

# ---------------------------------------------------------------------------
# Report.
# ---------------------------------------------------------------------------
$byFile = $findings | Group-Object File

if ($Summary) {
    foreach ($g in ($byFile | Sort-Object Count -Descending)) {
        Write-Output ('{0,6} findings  {1}' -f $g.Count, $g.Name)
    }
} else {
    foreach ($f in ($findings | Sort-Object File, Line)) {
        if ($f.Line -gt 0) {
            Write-Output ('{0}:{1}: {2}' -f $f.File, $f.Line, $f.Message)
        } else {
            Write-Output ('{0}: {1}' -f $f.File, $f.Message)
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Output ''
    Write-Output ('{0} findings, {1} files' -f $findings.Count, $byFile.Count)
    exit 1
}
Write-Output 'all doc entries within cap and coverage'
exit 0
