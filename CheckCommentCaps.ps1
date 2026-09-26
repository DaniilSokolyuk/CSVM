#!/usr/bin/env pwsh
# Comment-length cap checker for CSVM/src and CSVM.Tests.
#
# The caps are PROJECT_CONTEXT.md's coding conventions:
#   /// on a type          12 lines
#   /// on a member         6
#   // above a declaration  6
#   // above a statement    3
#   a sentence             25 words, and a block 6 sentences, in the blocks the commit touches
#
# A block over cap is a comment that has outgrown its subject: the fix is almost never to
# reflow it, but to move the part that is really a decode into docs/ and keep the prohibition
# on the member it binds. Exit code 1 when anything is over, so a PreToolUse hook can gate a
# commit on it.
#
# The line caps cover the whole scope. The sentence caps cover only the comment blocks with a
# changed line in the working tree (every block of a file named on the command line): the rule
# is older than its check, the tree carries the debt, and a block is fixed by whoever next
# edits it.
#
# Pure ASCII on purpose (PROJECT_CONTEXT.md): PowerShell 5.1 mangles a BOM-less non-ASCII
# script before it runs. Files are READ only; nothing here writes.
#
# Usage:
#   ./CheckCommentCaps.ps1                 the whole scope
#   ./CheckCommentCaps.ps1 -Summary        one line per file, worst first
#   ./CheckCommentCaps.ps1 -Root <path>    another worktree, with this copy's rules
#   ./CheckCommentCaps.ps1 a.cs b.cs       just these files
# PositionalBinding off: with it on, a bare file argument binds to -Root, the scan then finds no
# files under that "root", and the run reports clean without reading anything.
[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Summary,
    [string]$Root,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Path
)

$caps = @{ type = 12; member = 6; decl = 6; stmt = 3 }
$sentenceCap = 25
$blockCap = 6
$labels = @{
    type   = '/// on a type'
    member = '/// on a member'
    decl   = '// above a declaration'
    stmt   = '// above a statement'
}

# -Root names the tree to scan; a caller sweeping several worktrees passes each in turn, and this
# copy's rules apply to all of them. Without it, $PSScriptRoot names the worktree this file lives
# in regardless of the caller's cwd; a git-rev-parse-on-cwd root scanned whatever worktree the
# caller happened to be sitting in.
if (-not $Root) { $Root = $PSScriptRoot }
if (-not $Root) { $Root = git rev-parse --show-toplevel 2>$null }
if (-not $Root) { $Root = (Get-Location).Path }
$root = $Root

function Get-Scope {
    param([string]$Root)
    $dirs = @((Join-Path $Root 'CSVM/src'), (Join-Path $Root 'CSVM.Tests'))
    $files = @()
    foreach ($d in $dirs) {
        if (Test-Path -LiteralPath $d) {
            $files += @(Get-ChildItem -LiteralPath $d -Filter *.cs -Recurse -File)
        }
    }
    # Generated trees are not ours.
    $files | Where-Object { $_.FullName -notmatch '\\(obj|bin|\.godot)\\' } | ForEach-Object { $_.FullName }
}

# Which cap applies to the block ending at $end, and what it is attached to.
function Get-BlockKind {
    param([string[]]$Lines, [int]$End, [bool]$IsXml)
    # Blank lines and attributes sit between a comment and what it is attached to. An attribute may
    # wrap over several lines ([Suite("name", "a long description ...")] does), and only its first
    # line starts with '['; skipping just that one read the description as the declaration and
    # charged the comment the statement cap.
    $k = $End
    while ($k -lt $Lines.Count) {
        if ($Lines[$k].Trim() -eq '') { $k++; continue }
        if ($Lines[$k] -match '^\s*\[') {
            while ($k -lt $Lines.Count -and $Lines[$k].TrimEnd() -notmatch '\]$') { $k++ }
            $k++
            continue
        }
        break
    }
    $decl = if ($k -lt $Lines.Count) { $Lines[$k].Trim() } else { '' }
    if ($decl -match '\b(class|struct|enum|interface|record)\b' -and -not $decl.EndsWith(';')) {
        return @{ Kind = 'type'; Decl = $decl }
    }
    if ($IsXml) { return @{ Kind = 'member'; Decl = $decl } }
    if ($decl -match '^(private|public|internal|protected|const|static|readonly)') {
        return @{ Kind = 'decl'; Decl = $decl }
    }
    return @{ Kind = 'stmt'; Decl = $decl }
}

# Every comment block in a file: where it starts, its lines, and what it is attached to.
function Get-Blocks {
    param([string]$File)
    $lines = ([IO.File]::ReadAllText($File) -replace "`r`n", "`n") -split "`n"
    $out = @()
    $i = 0
    while ($i -lt $lines.Count) {
        $xml = $lines[$i] -match '^\s*///'
        $plain = $lines[$i] -match '^\s*//([^/]|$)'
        if (-not ($xml -or $plain)) { $i++; continue }
        $pattern = if ($xml) { '^\s*///' } else { '^\s*//([^/]|$)' }
        $j = $i
        while ($j -lt $lines.Count -and $lines[$j] -match $pattern) { $j++ }
        $info = Get-BlockKind -Lines $lines -End $j -IsXml $xml
        $out += [pscustomobject]@{
            Line = $i + 1; Length = $j - $i; Kind = $info.Kind; Decl = $info.Decl
            Text = @($lines[$i..($j - 1)])
        }
        $i = $j
    }
    $out
}

function Get-OverCap {
    param([string]$File)
    @(Get-Blocks -File $File) | Where-Object { $_.Length -gt $caps[$_.Kind] }
}

# The sentences of a block: comment markers and XML tags stripped, abbreviations kept whole, split
# where a terminator meets the next sentence's opening capital, quote or bracket. Lower-case
# openers (an identifier) do not split, so the count errs short, never long.
function Get-Sentences {
    param([string[]]$Text)
    $t = ($Text | ForEach-Object { $_ -replace '^\s*///?\s?', '' }) -join ' '
    $t = $t -replace '<[^>]+>', ' '
    $t = $t -replace '\b(e|i)\.(e|g)\.', '$1$2'
    $t = $t -replace '\b(vs|cf|etc|approx|ca)\.', '$1'
    $t = $t -replace '\s+', ' '
    [regex]::Split($t.Trim(), '(?<=[.!?])\s+(?=["''(\[A-Z]|[^\x00-\x7F])') |
        Where-Object { $_.Trim() -ne '' }
}

# Sentences over $sentenceCap words and blocks over $blockCap sentences, in the blocks that
# $Ranges touch. The rule predates its check, so the tree carries old debt; the block you are
# editing is the one you fix.
function Get-LongSentences {
    param([string]$File, $Ranges)
    $out = @()
    foreach ($b in @(Get-Blocks -File $File)) {
        if (-not (Test-BlockChanged -Block $b -Ranges $Ranges)) { continue }
        $sentences = @(Get-Sentences -Text $b.Text)
        if ($sentences.Count -gt $blockCap) {
            $out += [pscustomobject]@{
                Line = $b.Line; Words = 0; Sentences = $sentences.Count; Head = $sentences[0]
            }
        }
        foreach ($s in $sentences) {
            $words = @($s -split ' ' | Where-Object { $_ -ne '' }).Count
            if ($words -gt $sentenceCap) {
                $out += [pscustomobject]@{ Line = $b.Line; Words = $words; Sentences = 0; Head = $s }
            }
        }
    }
    $out
}

# The lines a commit from $Root would carry, per file: the working tree's added lines against HEAD
# (staged or not, since git commit -a takes both), and every line of an untracked file. A comment
# block is in scope when one of its lines is among them, so a one-line fix in a file with old debt
# is never blocked on the rest of the file.
#
# During a merge, a line counts only when it is new against BOTH parents. Against HEAD alone,
# everything the other branch brought in reads as added, and the merge is charged the other side's
# old debt across every file it touched; the lines the merge author actually wrote, the conflict
# resolutions, differ from both parents and stay in scope.
function Get-ChangedLines {
    param([string]$Root)
    $map = Get-AddedRanges -Root $Root -Against 'HEAD'
    $mergeHead = & git -C $Root rev-parse -q --verify MERGE_HEAD 2>$null
    if ($mergeHead) {
        $theirs = Get-AddedRanges -Root $Root -Against $mergeHead
        foreach ($f in @($map.Keys)) {
            $kept = @()
            if ($theirs.ContainsKey($f)) {
                foreach ($a in $map[$f]) {
                    foreach ($b in $theirs[$f]) {
                        $lo = [Math]::Max($a[0], $b[0])
                        $hi = [Math]::Min($a[1], $b[1])
                        if ($lo -le $hi) { $kept += ,@($lo, $hi) }
                    }
                }
            }
            $map[$f] = $kept
        }
    }
    $untracked = & git -C $Root ls-files --others --exclude-standard -- CSVM/src CSVM.Tests 2>$null
    foreach ($p in @($untracked)) {
        if ($p -notmatch '\.cs$') { continue }
        $map[(Join-Path $Root ($p -replace '/', '\'))] = @(,@(1, [int]::MaxValue))
    }
    $map
}

# The working tree's added line ranges against one commit, per file.
function Get-AddedRanges {
    param([string]$Root, [string]$Against)
    $map = @{}
    $file = $null
    $diff = & git -C $Root diff $Against -U0 -- CSVM/src CSVM.Tests 2>$null
    foreach ($row in @($diff)) {
        if ($row -match '^\+\+\+ b/(.*\.cs)$') {
            $file = (Join-Path $Root ($Matches[1] -replace '/', '\'))
            if (-not $map.ContainsKey($file)) { $map[$file] = @() }
            continue
        }
        if ($row -match '^\+\+\+ ') { $file = $null; continue }
        if ($file -and $row -match '^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@') {
            $start = [int]$Matches[1]
            # An unmatched group is $null, not '': testing -ne '' read a one-line hunk as zero lines.
            $count = if ($Matches[2]) { [int]$Matches[2] } else { 1 }
            if ($count -gt 0) { $map[$file] += ,@($start, ($start + $count - 1)) }
        }
    }
    $map
}

function Test-BlockChanged {
    param($Block, $Ranges)
    $first = $Block.Line
    $last = $Block.Line + $Block.Length - 1
    foreach ($r in $Ranges) {
        if ($r[0] -le $last -and $r[1] -ge $first) { return $true }
    }
    $false
}

$targets = if ($Path) { $Path } else { Get-Scope -Root $root }
# Named files are checked whole; without names, the sentence scope is the changed lines.
$changed = if ($Path) { $null } else { Get-ChangedLines -Root $root }
$sentenceSet = @{}
if ($Path) {
    foreach ($f in $Path) {
        if (Test-Path -LiteralPath $f -PathType Leaf) {
            $sentenceSet[(Resolve-Path -LiteralPath $f).Path] = @(,@(1, [int]::MaxValue))
        }
    }
} else {
    foreach ($k in $changed.Keys) {
        if (Test-Path -LiteralPath $k -PathType Leaf) { $sentenceSet[(Resolve-Path -LiteralPath $k).Path] = $changed[$k] }
    }
}
$rows = @()
$totalBlocks = 0
$totalExcess = 0
$totalSentences = 0
foreach ($f in $targets) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { continue }
    # Resolve-Path (not the raw arg) goes to ReadAllText: it honors PowerShell's $PWD, while
    # a relative path handed straight to a .NET file API follows the process's own current
    # directory, which Set-Location does not keep in step with $PWD in a hosted session.
    $full = (Resolve-Path -LiteralPath $f).Path
    $bad = @(Get-OverCap -File $full)
    # The outer @() is not redundant: an if expression's result unrolls a one-element array to a
    # scalar, whose .Count is null, so a file with exactly one long sentence would not fail the gate.
    $long = @(if ($sentenceSet.ContainsKey($full)) {
        Get-LongSentences -File $full -Ranges $sentenceSet[$full]
    })
    if ($bad.Count -eq 0 -and $long.Count -eq 0) { continue }
    $rel = $full.Replace($root + '\', '')
    $excess = ($bad | ForEach-Object { $_.Length - $caps[$_.Kind] } | Measure-Object -Sum).Sum
    $totalBlocks += $bad.Count
    $totalExcess += $excess
    $totalSentences += $long.Count
    $rows += [pscustomobject]@{ Excess = $excess; Blocks = $bad.Count; Sentences = $long.Count; File = $rel }
    if (-not $Summary) {
        foreach ($b in $bad) {
            $shortDecl = if ($b.Decl.Length -gt 60) { $b.Decl.Substring(0, 60) } else { $b.Decl }
            Write-Output ('{0}:{1}  {2} lines, cap {3} ({4})  {5}' -f
                $rel, $b.Line, $b.Length, $caps[$b.Kind], $labels[$b.Kind], $shortDecl)
        }
        foreach ($s in $long) {
            $head = if ($s.Head.Length -gt 60) { $s.Head.Substring(0, 60) + '...' } else { $s.Head }
            if ($s.Sentences -gt 0) {
                Write-Output ('{0}:{1}  {2} sentences, cap {3}  {4}' -f $rel, $s.Line, $s.Sentences, $blockCap, $head)
            } else {
                Write-Output ('{0}:{1}  {2} words, cap {3}  {4}' -f $rel, $s.Line, $s.Words, $sentenceCap, $head)
            }
        }
    }
}

if ($Summary) {
    foreach ($r in ($rows | Sort-Object Excess, Sentences -Descending)) {
        Write-Output ('{0,6} excess  {1,4} blocks  {2,4} sentences  {3}' -f $r.Excess, $r.Blocks, $r.Sentences, $r.File)
    }
}

if ($totalBlocks -gt 0 -or $totalSentences -gt 0) {
    Write-Output ''
    Write-Output ('{0} blocks over cap, {1} excess lines, {2} long sentences, {3} files' -f
        $totalBlocks, $totalExcess, $totalSentences, $rows.Count)
    exit 1
}
Write-Output 'all comment blocks within cap'
exit 0
