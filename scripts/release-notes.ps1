<#
.SYNOPSIS
Extracts a version's release notes from CHANGELOG.md and refuses notes no person wrote.

.DESCRIPTION
Reads the `## <version>` section of the changelog and classifies it. Headings, `**Full Changelog**:` and `**npm**:`
lines and bare URLs carry no reader-facing text, so a section of only those is empty, or a compare link alone when one
of them is a compare link. A section whose bullets are at least half commit subjects (a Conventional Commits prefix
such as `- fix(scope): subject`, a trailing commit hash, GitHub's generated `by @user in .../pull/<n>`) is a raw commit
list. A bold scope prefix (`- **tray:** subject`) is human-written. Anything but readable notes throws, so the release
stops before it publishes. With -OutFile, the notes are written there with `###` headings lifted to `##`.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,
    [string] $Path = (Join-Path -Path ($PSScriptRoot | Split-Path -Parent) -ChildPath 'CHANGELOG.md'),
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
trap {
    # $Error[0] reports the real fault line; a different exception there means it is stale.
    if ($Error.Count -gt 0 -and $Error[0] -is [System.Management.Automation.ErrorRecord] -and [object]::ReferenceEquals($Error[0].Exception, $_.Exception)) {
        throw $Error[0]
    }
    throw $_
}

$conventionalTypes = 'feat|fix|perf|refactor|chore|docs|test|tests|build|ci|style|revert'
$rawBulletPatterns = @(
    # `- feat(scope): subject`, `- abc1234 fix: subject`; never a bold prefix such as `- **build:** subject`.
    "(?i)^[-*+]\s+(?:\[?[0-9a-f]{7,40}\]?(?:\([^)]*\))?\s+)?(?:$conventionalTypes)(?:\([^)]*\))?!?:\s",
    # Conventional-changelog output: `* **scope:** subject ([abc1234](.../commit/abc1234))`.
    '(?i)\(\[?[0-9a-f]{7,40}\]?(?:\([^)]*/commit/[^)]*\))?\)\s*$',
    # GitHub's generated "What's Changed": `* subject by @user in https://github.com/o/r/pull/1`.
    '(?i)\sby @\S+ in https://github\.com/\S+/pull/\d+\s*$'
)
$boilerplateLine = '(?i)^(?:\*\*(?:Full Changelog|npm)\*\*\s*:\s*\S+|<?https?://\S+>?)$'
$headingLine = '^#{1,6}\s'
$bulletLine = '^[-*+]\s+'

if (-not (Test-Path -LiteralPath $Path)) {
    throw "$Path does not exist"
}
$lines = @(Get-Content -LiteralPath $Path)
$start = [Array]::FindIndex($lines, [Predicate[string]] { param($line) $line -match "^## $([regex]::Escape($Version))( |$)" })
if ($start -lt 0) {
    throw "CHANGELOG.md has no section for $Version"
}
$end = [Array]::FindIndex($lines, $start + 1, [Predicate[string]] { param($line) $line -match '^## ' })
if ($end -lt 0) {
    $end = $lines.Length
}
$body = if ($end -gt $start + 1) { $lines[($start + 1)..($end - 1)] } else { @() }
$notes = ($body -join "`n").Trim()

$trimmed = @($body | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$content = @($trimmed | Where-Object { $_ -notmatch $headingLine -and $_ -notmatch $boilerplateLine })
if ($content.Count -eq 0) {
    if (@($trimmed | Where-Object { $_.Contains('/compare/') }).Count -gt 0) {
        throw "The CHANGELOG.md section for $Version is only a compare link; write what changed for a reader"
    }
    throw "The CHANGELOG.md section for $Version is empty"
}
$bullets = @($content | Where-Object { $_ -match $bulletLine })
$rawBullets = @($bullets | Where-Object {
        $bullet = $_
        @($rawBulletPatterns | Where-Object { $bullet -match $_ }).Count -gt 0
    })
if ($bullets.Count -gt 0 -and $rawBullets.Count * 2 -ge $bullets.Count) {
    throw "The CHANGELOG.md section for $Version is a raw commit list ($($rawBullets.Count) of $($bullets.Count) bullets are commit subjects); write what changed for a reader"
}

if ($OutFile) {
    [IO.File]::WriteAllText($OutFile, ($notes -replace '(?m)^###', '##') + "`n")
}
