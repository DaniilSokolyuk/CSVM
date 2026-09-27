#!/usr/bin/env pwsh
# The format-before-tests gate every harness's pre-tool hook calls: dotnet format, then a build
# that blocks on any StyleCop warning dotnet format could not fix.
#
# IT FIRES ON AN INVOCATION, NEVER A MENTION. The old guard was a substring test over the whole
# command, so reading, grepping or listing RunTests.ps1 rebuilt the assembly as surely as running
# it did, and a golden battery reading that tree mid-rebuild found no assembly at all
# (docs/verification.md INSTR-42). A command segment (the whole command, or a piece after ";",
# "&&", "||" or "|") counts only when it BEGINS with the runner, "dotnet test" or "git commit".
#
# THE BUILD IS A REBUILD ON PURPOSE. Analyzer warnings are emitted only when the compiler runs,
# and an incremental build of an up-to-date project skips CoreCompile, so a plain build would
# report a clean tree whenever nothing changed since the last build. That is exactly the commit
# case, and it would make the check silent where it matters.
#
# THE TREE IS THE ONE THE COMMAND NAMES. dotnet format WRITES, so formatting the wrong tree is
# worse than checking it. The runner's own path names a tree (an absolute RunTests.ps1 lives in
# one), a "git -C <tree>" names one, and only when neither does is the process directory used.
# A Set-Location inside the same command cannot be honoured: a pre-tool hook runs before it.
#
# Pure ASCII on purpose (PROJECT_CONTEXT.md). Exit code 2 blocks the tool call.
#
# Usage:
#   <hook payload on stdin> | ./FormatBeforeTests.ps1
#   ./FormatBeforeTests.ps1 -Command '.\RunTests.ps1 -Quick'
#   ./FormatBeforeTests.ps1 -ShowRoot -Command '...'   which tree that command would build, if any
#   ./FormatBeforeTests.ps1 -SelfTest                  exercise the trigger and tree resolution
[CmdletBinding()]
param(
    [string]$Command,
    [switch]$ShowRoot,
    [switch]$SelfTest
)

$scriptRoot = $PSScriptRoot
if (-not $scriptRoot) { $scriptRoot = (Get-Location).Path }

# A path as written on the command line, minus the quoting.
function Get-UnquotedPath {
    param([string]$Raw)
    $t = $Raw.Trim()
    if ($t.Length -ge 2) {
        $q = $t[0]
        if (($q -eq '"' -or $q -eq [char]39) -and $t[$t.Length - 1] -eq $q) {
            return $t.Substring(1, $t.Length - 2)
        }
    }
    return $t
}

# One quoted-or-bare token. Parenthesised because "," binds tighter than "+" in PowerShell.
$q = [char]39
$tokenGroup = '("[^"]*"|' + $q + '[^' + $q + ']*' + $q + '|\S+)'
$runnerGroup = '("[^"]*RunTests\.ps1"|' + $q + '[^' + $q + ']*RunTests\.ps1' + $q + '|[^\s"' + $q + ']*RunTests\.ps1)'

# git's own global options, which are what stands between "git" and its subcommand in the form
# CLAUDE.md prescribes for naming a tree, "git -C <tree> commit". A trigger testing for the two
# words adjacent misses every one of those, and one accepting any tokens in the gap turns
# "git log --grep='a git commit'" into a commit and formats a tree on the strength of a quoted word.
$gitGlobal = '(?:-[Cc]\s+' + $tokenGroup +
    '|--(?:git-dir|work-tree|namespace|exec-path|super-prefix|config-env)(?:=|\s+)' + $tokenGroup +
    '|--[a-z][a-z-]*|-[a-zA-Z])'

# Splits a command into the pieces a shell would run one after another. A separator inside a
# quoted string splits too; the piece that follows then begins mid-string and matches nothing,
# which is the right answer for a mention.
function Get-CommandSegments {
    param([string]$CommandLine)
    return @([regex]::Split($CommandLine, '&&|\|\||;|\|') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# What, if anything, this command invokes. Returns Kind ('runner', 'test', 'commit') and the tree
# the invocation itself names (the runner's directory or a -C path), or nothing for a mention.
function Get-Invocation {
    param([string]$CommandLine)
    foreach ($seg in (Get-CommandSegments -CommandLine $CommandLine)) {
        $s = $seg -replace '^\s*&\s*', ''
        $m = [regex]::Match($s, '^' + $runnerGroup + '(?:\s|$)')
        if ($m.Success) {
            $path = Get-UnquotedPath -Raw $m.Groups[1].Value
            $dir = Split-Path -Parent $path
            if ($dir -eq '.' -or $dir -eq '.\' -or $dir -eq './') { $dir = '' }
            return [pscustomobject]@{ Kind = 'runner'; Tree = $dir }
        }
        if ($s -match '^dotnet\s+test(?:\s|$)') {
            return [pscustomobject]@{ Kind = 'test'; Tree = '' }
        }
        if ($s -match ('^git(?:\s+' + $gitGlobal + ')*\s+commit(?:\s|$)')) {
            $c = [regex]::Match($s, '(?:^|\s)-C\s+' + $tokenGroup)
            $tree = ''
            if ($c.Success) { $tree = Get-UnquotedPath -Raw $c.Groups[1].Value }
            return [pscustomobject]@{ Kind = 'commit'; Tree = $tree }
        }
    }
    return $null
}

function Resolve-Toplevel {
    param([string]$Path)
    if (-not $Path) { return '' }
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $top = git -C $Path rev-parse --show-toplevel 2>$null
    if (-not $top) { return '' }
    $sep = [IO.Path]::DirectorySeparatorChar
    return (([string]$top) -replace '[\\/]', $sep).TrimEnd($sep)
}

# The tree to format and build, or empty when the command invokes nothing. The invocation's own
# tree wins; a "git -C <tree>" anywhere in the command is next, which keeps the documented
# "git -C <tree> rev-parse; .\RunTests.ps1" form working; the process directory is the fallback.
function Get-TargetRoot {
    param([string]$CommandLine, [string]$From)
    if (-not $From) { $From = (Get-Location).Path }
    $inv = Get-Invocation -CommandLine $CommandLine
    if (-not $inv) { return '' }
    $top = Resolve-Toplevel -Path $inv.Tree
    if ($top) { return $top }
    $c = [regex]::Match($CommandLine, '(?:^|\s)-C\s+' + $tokenGroup)
    if ($c.Success) {
        $top = Resolve-Toplevel -Path (Get-UnquotedPath -Raw $c.Groups[1].Value)
        if ($top) { return $top }
    }
    return (Resolve-Toplevel -Path $From)
}

# Formats, rebuilds, and returns the StyleCop lines dotnet format left behind (SA0001 excluded).
function Invoke-FormatAndBuild {
    param([string]$Root)
    $proj = Join-Path $Root 'CSVM/CSVM.csproj'
    if (-not (Test-Path -LiteralPath $proj)) { return @() }
    dotnet format $proj --verbosity quiet
    $buildOutput = dotnet build $proj --verbosity quiet --nologo -t:Rebuild 2>&1
    return @($buildOutput | Select-String -Pattern 'SA\d{4}' | Where-Object { $_.Line -notmatch 'SA0001' } | ForEach-Object { $_.Line })
}

# ---------------------------------------------------------------------------------------------
# Self-test. Exercises the trigger and the tree resolution against two throwaway repos. It never
# formats or builds anything.
# ---------------------------------------------------------------------------------------------
function Invoke-SelfTest {
    $base = Join-Path ([IO.Path]::GetTempPath()) ('csvm-formatgate-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $main = Join-Path $base 'main'
    $other = Join-Path $base 'other'
    $script:passed = 0
    $script:failed = 0

    function Assert-Row {
        param([string]$Name, [bool]$Condition)
        if ($Condition) { Write-Host ('  PASS  ' + $Name); $script:passed++ }
        else { Write-Host ('  FAIL  ' + $Name); $script:failed++ }
    }

    function Assert-Fires {
        param([string]$Name, [string]$CommandLine, [string]$Kind)
        $inv = Get-Invocation -CommandLine $CommandLine
        if ($Kind) { Assert-Row $Name ($null -ne $inv -and $inv.Kind -eq $Kind) }
        else { Assert-Row $Name ($null -eq $inv) }
    }

    try {
        foreach ($d in @($main, $other)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
            git -C $d init -q 2>&1 | Out-Null
            [IO.File]::WriteAllText((Join-Path $d 'RunTests.ps1'), 'exit 0', (New-Object System.Text.UTF8Encoding($false)))
        }
        # macOS's temp folder sits behind the /var -> /private/var link and git reports the
        # physical path, so the expected trees are taken from git's own spelling.
        if ($IsMacOS -or $IsLinux) {
            $main = Resolve-Toplevel -Path $main
            $other = Resolve-Toplevel -Path $other
        }

        # Mentions never fire.
        Assert-Fires 'mention  Get-Content .\RunTests.ps1' 'Get-Content .\RunTests.ps1' ''
        Assert-Fires 'mention  grep RunTests.ps1 CLAUDE.md' 'grep RunTests.ps1 CLAUDE.md' ''
        Assert-Fires 'mention  Get-Content RunTests.ps1 | Select-Object -First 5' 'Get-Content RunTests.ps1 | Select-Object -First 5' ''
        Assert-Fires 'mention  echo x | Select-String RunTests.ps1' 'echo x | Select-String RunTests.ps1' ''
        Assert-Fires 'mention  git log --grep=commit' 'git log --grep=commit' ''
        Assert-Fires 'mention  git log --grep=RunTests.ps1' 'git log --grep=RunTests.ps1' ''
        Assert-Fires 'mention  dotnet build then a dotnet test flag mid-segment' 'dotnet build CSVM --no-restore; echo "dotnet test"' ''
        Assert-Fires 'mention  a quoted runner mid-segment' 'Write-Host "run .\RunTests.ps1 later"' ''
        Assert-Fires 'mention  Test-Path RunTests.ps1' 'Test-Path RunTests.ps1' ''

        # Invocations fire, with their kind.
        Assert-Fires 'runner   .\RunTests.ps1 -Quick' '.\RunTests.ps1 -Quick' 'runner'
        Assert-Fires 'runner   & ".\RunTests.ps1" -Quick' '& ".\RunTests.ps1" -Quick' 'runner'
        Assert-Fires 'runner   after a leading git -C rev-parse' ('git -C ' + $other + ' rev-parse --show-toplevel; .\RunTests.ps1 -Quick') 'runner'
        Assert-Fires 'runner   after an unrelated first segment' 'Get-Date; .\RunTests.ps1 -Suite c1' 'runner'
        Assert-Fires 'runner   an absolute path' ($other + '\RunTests.ps1 -Quick') 'runner'
        Assert-Fires 'runner   with -SkipUnits' '.\RunTests.ps1 -Suite c1 -SkipUnits -SkipGoldens' 'runner'
        Assert-Fires 'test     dotnet test CSVM.Tests' 'dotnet test CSVM.Tests' 'test'
        Assert-Fires 'commit   git commit -m x' 'git commit -m x' 'commit'
        Assert-Fires 'commit   git -C wt commit -F msg.txt' ('git -C ' + $other + ' commit -F msg.txt') 'commit'
        Assert-Fires 'commit   git add -A && git commit -m x' 'git add -A && git commit -m x' 'commit'
        Assert-Fires 'commit   a message mentioning the runner still commits' 'git commit -m "quotes RunTests.ps1"' 'commit'
        Assert-Fires 'commit   git -c k=v commit' 'git -c user.email=x commit -m x' 'commit'
        Assert-Fires 'commit   git --work-tree= --git-dir= commit' 'git --work-tree=Z:\wt --git-dir=Z:\wt\.git commit -m x' 'commit'
        Assert-Fires 'commit   a valueless global before the subcommand' 'git --no-pager -C Z:\wt commit -F msg.txt' 'commit'
        Assert-Fires 'commit   a quoted -C path holding a space' 'git -C "Z:\a b\wt" commit -m x' 'commit'
        Assert-Fires 'mention  git log --grep="a git commit here"' 'git log --grep="a git commit here"' ''
        Assert-Fires 'mention  git commit-tree is a different subcommand' 'git commit-tree HEAD -m x' ''
        Assert-Fires 'mention  a non-global token before commit' 'git log commit' ''

        # Tree resolution: the invocation names it, a -C names it, the process directory is last.
        $r1 = Get-TargetRoot -CommandLine ($other + '\RunTests.ps1 -Quick') -From $main
        Assert-Row 'tree     an absolute runner path names its own tree' ($r1 -ieq $other)
        $r2 = Get-TargetRoot -CommandLine ('git -C ' + $other + ' commit -m x') -From $main
        Assert-Row 'tree     git -C names the commit tree' ($r2 -ieq $other)
        $r3 = Get-TargetRoot -CommandLine ('git -C "' + $other + '" rev-parse --show-toplevel; .\RunTests.ps1 -Quick') -From $main
        Assert-Row 'tree     a leading quoted git -C reaches a relative runner' ($r3 -ieq $other)
        $r4 = Get-TargetRoot -CommandLine '.\RunTests.ps1 -Quick' -From $main
        Assert-Row 'tree     a bare runner uses the process directory' ($r4 -ieq $main)
        $r5 = Get-TargetRoot -CommandLine ('Set-Location ' + $other + '; .\RunTests.ps1 -Quick') -From $main
        Assert-Row 'tree     a same-call Set-Location is not honoured' ($r5 -ieq $main)
        $r6 = Get-TargetRoot -CommandLine 'Get-Content .\RunTests.ps1' -From $main
        Assert-Row 'tree     a mention resolves no tree' (-not $r6)
        # dotnet format WRITES, so a lowercase -c read as -C would format a tree named by a config
        # key rather than by a path.
        $r7 = Get-TargetRoot -CommandLine 'git -c user.email=x commit -m x' -From $main
        Assert-Row 'tree     -c is a config key and names no tree' ($r7 -ieq $main)

        # The entry point, end to end. A mention exits 0 having printed nothing under -ShowRoot.
        $shown = @(& (Join-Path $scriptRoot 'FormatBeforeTests.ps1') -ShowRoot -Command 'Get-Content .\RunTests.ps1')
        Assert-Row 'entry    -ShowRoot on a mention prints nothing' ($LASTEXITCODE -eq 0 -and $shown.Count -eq 0)
        $shown = @(& (Join-Path $scriptRoot 'FormatBeforeTests.ps1') -ShowRoot -Command ($other + '\RunTests.ps1'))
        Assert-Row 'entry    -ShowRoot on an invocation prints its tree' ($shown.Count -eq 1 -and $shown[0] -ieq $other)
    }
    finally {
        if (Test-Path -LiteralPath $base) { Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue }
    }

    Write-Host ''
    Write-Host ('{0} passed, {1} failed' -f $passed, $failed)
    if ($failed -gt 0) { exit 1 }
    exit 0
}

if ($SelfTest) { Invoke-SelfTest }

if (-not $Command -and [Console]::IsInputRedirected) {
    $raw = [Console]::In.ReadToEnd()
    if ($raw) {
        try { $Command = [string]($raw | ConvertFrom-Json).tool_input.command } catch { $Command = '' }
    }
}
if (-not $Command) { exit 0 }

$root = Get-TargetRoot -CommandLine $Command
if ($ShowRoot) {
    if ($root) { Write-Output $root }
    exit 0
}
if (-not $root) { exit 0 }

$remaining = @(Invoke-FormatAndBuild -Root $root)
if ($remaining.Count -eq 0) { exit 0 }
foreach ($line in $remaining) { [Console]::Error.WriteLine($line) }
[Console]::Error.WriteLine('StyleCop warnings dotnet format could not auto-fix remain above (SA0001 excluded) - fix them by hand, then retry.')
exit 2
