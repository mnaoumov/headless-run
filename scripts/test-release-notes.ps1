<#
.SYNOPSIS
Runs release-notes.ps1 against sample changelog sections: readable ones pass, the rest must be refused.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
trap {
    # $Error[0] reports the real fault line; a different exception there means it is stale.
    if ($Error.Count -gt 0 -and $Error[0] -is [System.Management.Automation.ErrorRecord] -and [object]::ReferenceEquals($Error[0].Exception, $_.Exception)) {
        throw $Error[0]
    }
    throw $_
}

$script = Join-Path -Path $PSScriptRoot -ChildPath 'release-notes.ps1'
$root = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath "headless-run-release-notes-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null

$cases = @(
    @{
        Name = 'readable'
        Section = "### New`n`n- ``--tray`` shows a notification-area icon while the job runs.`n- Missing parent directories of ``--stdout`` are created."
        Refusal = $null
        Notes = "## New`n`n- ``--tray`` shows a notification-area icon while the job runs.`n- Missing parent directories of ``--stdout`` are created.`n"
    },
    @{
        Name = 'bold scope prefix'
        Section = "- **build:** the executable names its publisher.`n- **test:** every README example runs as written."
        Refusal = $null
    },
    @{
        Name = 'readable with a compare link'
        Section = "- ``--event-log`` writes one event per run.`n`n**Full Changelog**: https://github.com/mnaoumov/headless-run/compare/v1.0.0...v1.1.0"
        Refusal = $null
    },
    @{
        Name = 'empty'
        Section = ''
        Refusal = 'is empty'
    },
    @{
        Name = 'headings only'
        Section = "### New`n`n### Fixes"
        Refusal = 'is empty'
    },
    @{
        Name = 'compare link only'
        Section = '**Full Changelog**: https://github.com/mnaoumov/headless-run/compare/v1.0.0...v1.1.0'
        Refusal = 'only a compare link'
    },
    @{
        Name = 'bare compare URL'
        Section = 'https://github.com/mnaoumov/headless-run/compare/v1.0.0...v1.1.0'
        Refusal = 'only a compare link'
    },
    @{
        Name = 'raw conventional subjects'
        Section = "- feat(tray): add the tray icon`n- fix: quote the tail for cmd`n- The event log is written once per run."
        Refusal = 'raw commit list'
    },
    @{
        Name = 'raw hash-prefixed subjects'
        Section = "- 0fec828 build: name the publisher`n- e9a8a5b docs: tested shell examples"
        Refusal = 'raw commit list'
    },
    @{
        Name = 'conventional-changelog trailing hashes'
        Section = "* **tray:** add the icon ([0fec828](https://github.com/mnaoumov/headless-run/commit/0fec828))`n* **docs:** shell examples ([e9a8a5b](https://github.com/mnaoumov/headless-run/commit/e9a8a5b))"
        Refusal = 'raw commit list'
    },
    @{
        Name = 'generated What''s Changed'
        Section = "### What's Changed`n* Add a tray icon by @mnaoumov in https://github.com/mnaoumov/headless-run/pull/3"
        Refusal = 'raw commit list'
    }
)

$failures = [Collections.Generic.List[string]]::new()
try {
    foreach ($case in $cases) {
        $changelog = Join-Path -Path $root -ChildPath 'CHANGELOG.md'
        $outFile = Join-Path -Path $root -ChildPath 'release-notes.md'
        Remove-Item -LiteralPath $outFile -ErrorAction Ignore
        [IO.File]::WriteAllText($changelog, "# Changelog`n`n## Unreleased`n`n- Not yet.`n`n## 9.9.9 - 2026-11-18`n`n$($case.Section)`n`n## 9.9.8 - 2026-11-01`n`n- An older entry.`n")
        $refusal = $null
        try {
            & $script -Version '9.9.9' -Path $changelog -OutFile $outFile
        }
        catch {
            $refusal = $_.Exception.Message
        }
        if ($null -eq $case.Refusal) {
            if ($null -ne $refusal) {
                $failures.Add("$($case.Name): refused readable notes: $refusal")
            }
            elseif ($case.ContainsKey('Notes') -and [IO.File]::ReadAllText($outFile) -cne $case.Notes) {
                $failures.Add("$($case.Name): wrote unexpected notes: $([IO.File]::ReadAllText($outFile))")
            }
        }
        elseif ($null -eq $refusal) {
            $failures.Add("$($case.Name): accepted notes it must refuse")
        }
        elseif (-not $refusal.Contains($case.Refusal)) {
            $failures.Add("$($case.Name): refused with '$refusal', expected '$($case.Refusal)'")
        }
    }

    $missing = $null
    try {
        & $script -Version '1.2.3' -Path $changelog
    }
    catch {
        $missing = $_.Exception.Message
    }
    if ($null -eq $missing -or -not $missing.Contains('no section for 1.2.3')) {
        $failures.Add("missing section: expected a refusal naming 1.2.3, got '$missing'")
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}

if ($failures.Count -gt 0) {
    throw "Release notes tests failed:`n$($failures -join "`n")"
}
Write-Host -Object "Release notes: $($cases.Count + 1) cases passed"
