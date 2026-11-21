[CmdletBinding()]
param(
    # The headless-run.exe to call; its directory is copied, so a JIT build's DLLs come along.
    [Parameter(Mandatory)]
    [string] $Launcher,
    # The integration harness, whose copy named tool.exe records the arguments it receives.
    [Parameter(Mandatory)]
    [string] $Harness,
    # Also run the WSL examples, through wsl.exe.
    [switch] $Wsl,
    # Also run the Task Scheduler example: it registers a task, runs it and unregisters it.
    [switch] $TaskScheduler
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

# Runs every block of the "Calling it from a shell" docs page that is marked <!-- example: KIND -->,
# exactly as written except that C:\Tools and C:\Scripts point at a temporary folder,
# and checks what tool.exe and job.ps1 received against the page's own <!-- example-argv --> list.

$taskName = 'headless-run example'
$recordTimeoutSeconds = 60
$taskTimeoutSeconds = 60
$pollMilliseconds = 250

$repo = Split-Path -Path $PSScriptRoot -Parent
$pageRelativePath = 'docs/src/content/docs/guides/calling-from-a-shell.md'
$page = Get-Content -LiteralPath (Join-Path -Path $repo -ChildPath $pageRelativePath) -Raw

function Get-ExpectedArguments {
    $match = [regex]::Match($page, '<!-- example-argv -->\r?\n((?:\d+\. `.*`\r?\n)+)')
    if (-not $match.Success) {
        throw "$pageRelativePath has no <!-- example-argv --> list"
    }
    return @($match.Groups[1].Value -split '\r?\n' | Where-Object -FilterScript { $_ } | ForEach-Object -Process {
            [regex]::Match($_, '^\d+\. `(.*)`$').Groups[1].Value
        })
}

function Get-Examples {
    $found = [regex]::Matches($page, '<!-- example: (?<kind>[a-z-]+) -->\r?\n```[a-z]+\r?\n(?<code>[\s\S]*?)```')
    if ($found.Count -eq 0) {
        throw "$pageRelativePath has no <!-- example: KIND --> blocks"
    }
    return @($found | ForEach-Object -Process {
            [pscustomobject]@{
                Kind = $_.Groups['kind'].Value
                Code = $_.Groups['code'].Value -replace '\r\n', "`n"
            }
        })
}

function Find-GitBash {
    $candidates = @(
        $env:ProgramW6432, $env:ProgramFiles | Where-Object -FilterScript { $_ } | ForEach-Object -Process { Join-Path -Path $_ -ChildPath 'Git\bin\bash.exe' }
        if ($env:LOCALAPPDATA) {
            Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Programs\Git\bin\bash.exe'
        }
        if ($env:USERPROFILE) {
            Join-Path -Path $env:USERPROFILE -ChildPath 'scoop\apps\git\current\bin\bash.exe'
        }
    )
    return $candidates | Where-Object -FilterScript { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Find-Python {
    # The python.exe of a Windows without Python is the Microsoft Store stub, so a WindowsApps hit does not count.
    foreach ($name in 'py', 'python') {
        $command = Get-Command -Name $name -CommandType Application -ErrorAction SilentlyContinue |
            Where-Object -FilterScript { $_.Source -notlike '*\WindowsApps\*' } |
            Select-Object -First 1
        if ($command) {
            return $command.Source
        }
    }
    return $null
}

# Starts a program with a raw command line, optionally feeding text to its standard input, and returns its exit code.
function Invoke-Raw([string] $FileName, [string] $Arguments, [string] $InputText, [switch] $Quiet) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($FileName, $Arguments)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $PSBoundParameters.ContainsKey('InputText')
    # Only standard output is redirected, so reading it to the end cannot deadlock on a full stderr pipe.
    $start.RedirectStandardOutput = $Quiet.IsPresent
    $process = [System.Diagnostics.Process]::Start($start)
    if ($start.RedirectStandardInput) {
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
    }
    if ($Quiet) {
        $null = $process.StandardOutput.ReadToEnd()
    }
    $process.WaitForExit()
    return $process.ExitCode
}

function Wait-Records([string] $Path, [int] $Count) {
    $deadline = [DateTime]::UtcNow.AddSeconds($recordTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $Path) -and @(Get-Content -LiteralPath $Path).Count -ge $Count) {
            return @(Get-Content -LiteralPath $Path | ForEach-Object -Process { $_ | ConvertFrom-Json })
        }
        Start-Sleep -Milliseconds $pollMilliseconds
    }
    throw "expected $Count record(s) in $Path within $recordTimeoutSeconds s"
}

$expected = Get-ExpectedArguments
$examples = Get-Examples
$root = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('headless-run-shell-examples-' + [guid]::NewGuid().ToString('N'))
$tools = Join-Path -Path $root -ChildPath 'Tools'
$toolDirectory = Join-Path -Path $tools -ChildPath 'My Tool'
$scripts = Join-Path -Path $root -ChildPath 'Scripts'
$work = Join-Path -Path $root -ChildPath 'work'
$toolRecords = Join-Path -Path $toolDirectory -ChildPath 'received.jsonl'
$scriptRecords = Join-Path -Path $scripts -ChildPath 'received.jsonl'
$failures = [System.Collections.Generic.List[string]]::new()
$savedPath = $env:PATH

New-Item -ItemType Directory -Path $toolDirectory, $scripts, $work | Out-Null
try {
    Get-ChildItem -LiteralPath (Split-Path -Path (Resolve-Path -LiteralPath $Launcher) -Parent) -File | Copy-Item -Destination $tools
    if ((Split-Path -Path $Launcher -Leaf) -ne 'headless-run.exe') {
        Copy-Item -LiteralPath $Launcher -Destination (Join-Path -Path $tools -ChildPath 'headless-run.exe')
    }

    Get-ChildItem -LiteralPath (Split-Path -Path (Resolve-Path -LiteralPath $Harness) -Parent) -File | Copy-Item -Destination $toolDirectory
    Copy-Item -LiteralPath $Harness -Destination (Join-Path -Path $toolDirectory -ChildPath 'tool.exe')
    [System.IO.File]::WriteAllText((Join-Path -Path $scripts -ChildPath 'job.ps1'), @'
param([string] $Name, [switch] $DryRun)
$record = [pscustomobject]@{ name = $Name; dryRun = [bool] $DryRun; rest = @($args) } | ConvertTo-Json -Compress
[System.IO.File]::AppendAllText((Join-Path -Path $PSScriptRoot -ChildPath 'received.jsonl'), $record + "`n")
'@)
    $env:PATH = $tools + ';' + $env:PATH
    $gitBash = Find-GitBash
    $python = Find-Python

    foreach ($example in $examples) {
        $code = $example.Code.Replace('C:\\Tools\\', $tools.Replace('\', '\\') + '\\').Replace('C:\\Scripts\\', $scripts.Replace('\', '\\') + '\\')
        $code = $code.Replace('C:\Tools\', $tools + '\').Replace('C:\Scripts\', $scripts + '\')
        $callsScript = $example.Code.Contains('job.ps1')
        Remove-Item -LiteralPath $toolRecords, $scriptRecords -ErrorAction SilentlyContinue
        $file = Join-Path -Path $work -ChildPath $example.Kind
        $skip = $null
        switch ($example.Kind) {
            'cmd-prompt' {
                # Lines read from standard input are parsed as the prompt parses them, not as a batch file.
                $null = Invoke-Raw -FileName $env:ComSpec -Arguments '/d /q' -InputText ($code.Replace("`n", "`r`n")) -Quiet
            }
            'cmd-batch' {
                [System.IO.File]::WriteAllText($file + '.cmd', "@echo off`r`n" + $code.Replace("`n", "`r`n"))
                $null = Invoke-Raw -FileName $env:ComSpec -Arguments ('/d /c "' + $file + '.cmd"')
            }
            'pwsh' {
                [System.IO.File]::WriteAllText($file + '.ps1', $code)
                $null = Invoke-Raw -FileName 'pwsh.exe' -Arguments ('-NoProfile -NonInteractive -File "' + $file + '.ps1"')
            }
            'powershell' {
                [System.IO.File]::WriteAllText($file + '.ps1', $code)
                $null = Invoke-Raw -FileName (Join-Path -Path $env:SystemRoot -ChildPath 'System32\WindowsPowerShell\v1.0\powershell.exe') -Arguments ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $file + '.ps1"')
            }
            'git-bash' {
                if (-not $gitBash) {
                    $skip = 'Git Bash not found'
                    break
                }
                [System.IO.File]::WriteAllText($file + '.sh', $code)
                $null = Invoke-Raw -FileName $gitBash -Arguments ('"' + $file + '.sh"')
            }
            'wsl' {
                if (-not $Wsl) {
                    $skip = 'pass -Wsl to run it'
                    break
                }
                [System.IO.File]::WriteAllText($file + '.sh', $code)
                $linuxPath = (& wsl.exe -e wslpath -u ($file + '.sh').Replace('\', '/')).Trim()
                if ($LASTEXITCODE -ne 0) {
                    throw 'wslpath failed'
                }
                $null = Invoke-Raw -FileName 'wsl.exe' -Arguments ('--cd ~ -e bash "' + $linuxPath + '"')
            }
            'task-scheduler' {
                if (-not $TaskScheduler) {
                    $skip = 'pass -TaskScheduler to run it'
                    break
                }
                if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
                    throw "a scheduled task named '$taskName' already exists; refusing to replace it"
                }
                [System.IO.File]::WriteAllText($file + '.ps1', $code)
                try {
                    if ((Invoke-Raw -FileName 'pwsh.exe' -Arguments ('-NoProfile -NonInteractive -File "' + $file + '.ps1"') -Quiet) -ne 0) {
                        throw 'registering the task failed'
                    }
                    Start-ScheduledTask -TaskName $taskName
                    $deadline = [DateTime]::UtcNow.AddSeconds($taskTimeoutSeconds)
                    do {
                        Start-Sleep -Milliseconds $pollMilliseconds
                    } while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline)
                }
                finally {
                    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
                }
            }
            'shortcut' {
                # The example saves its .lnk files under the temporary C:\Tools and C:\Scripts, and opens them itself.
                [System.IO.File]::WriteAllText($file + '.ps1', $code)
                if ((Invoke-Raw -FileName 'pwsh.exe' -Arguments ('-NoProfile -NonInteractive -File "' + $file + '.ps1"') -Quiet) -ne 0) {
                    throw 'creating or opening the shortcuts failed'
                }
            }
            'node' {
                if (-not (Get-Command -Name 'node' -CommandType Application -ErrorAction SilentlyContinue)) {
                    $skip = 'node not found'
                    break
                }
                [System.IO.File]::WriteAllText($file + '.mjs', $code)
                $null = Invoke-Raw -FileName 'node' -Arguments ('"' + $file + '.mjs"')
            }
            'python' {
                if (-not $python) {
                    $skip = 'Python not found'
                    break
                }
                [System.IO.File]::WriteAllText($file + '.py', $code)
                $null = Invoke-Raw -FileName $python -Arguments ('"' + $file + '.py"')
            }
            'csharp' {
                # A file-based app, which dotnet run builds from the one .cs file.
                [System.IO.File]::WriteAllText($file + '.cs', $code)
                # A failed build leaves no record, which the check below reports.
                $null = Invoke-Raw -FileName 'dotnet' -Arguments ('run "' + $file + '.cs"')
            }
            default {
                throw "$pageRelativePath has an example of unknown kind '$($example.Kind)'"
            }
        }

        if ($skip) {
            Write-Output -InputObject "SKIP $($example.Kind): $skip"
            continue
        }

        try {
            $tool = Wait-Records -Path $toolRecords -Count 1
            $problems = @()
            if ($tool.Count -ne 1) {
                $problems += "tool.exe ran $($tool.Count) times"
            }
            $received = @($tool[0].args)
            if (($received -join "`n") -cne ($expected -join "`n") -or $received.Count -ne $expected.Count) {
                $problems += 'tool.exe received [' + ($received -join '] [') + ']'
            }
            if ($tool[0].greeting -cne 'hello world') {
                $problems += "GREETING was '$($tool[0].greeting)'"
            }
            if ($callsScript) {
                $script = Wait-Records -Path $scriptRecords -Count 1
                if ($script[0].name -cne 'nightly run' -or -not $script[0].dryRun -or @($script[0].rest).Count -ne 0) {
                    $problems += 'job.ps1 received ' + ($script[0] | ConvertTo-Json -Compress)
                }
            }
        }
        catch {
            $problems = @($_.Exception.Message)
        }

        if ($problems.Count -eq 0) {
            Write-Output -InputObject "PASS $($example.Kind)"
        }
        else {
            $failures.Add("$($example.Kind): " + ($problems -join '; '))
        }
    }
}
finally {
    $env:PATH = $savedPath
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -ne 0) {
    $failures | ForEach-Object -Process { Write-Output -InputObject "FAIL $_" }
    throw "$($failures.Count) shell example(s) in $pageRelativePath failed"
}
