[CmdletBinding()]
param(
    # The integration harness, whose copy named tool.exe records the arguments it receives.
    [Parameter(Mandatory)]
    [string] $Harness,
    # Also run every case through bash in WSL.
    [switch] $Wsl
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

# Proves the quoting playground's models against the real callers. For every caller and every corpus case,
# scripts/quoting-cases.ts gives the line the model generates and the arguments the model says the program
# receives from it; this script runs the line through the real caller to the tool.exe recorder and compares.
# The Run dialog cannot be driven by a script, and Task Scheduler passes the line as a shortcut does, so the
# shortcut stands for both.

$recordTimeoutSeconds = 30
$pollMilliseconds = 100
$repo = Split-Path -Path $PSScriptRoot -Parent
$utf8 = [System.Text.UTF8Encoding]::new($false)
$utf8WithBom = [System.Text.UTF8Encoding]::new($true)

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

# Starts a program with a raw command line, optionally feeding bytes to its standard input, and waits for it.
function Invoke-Raw([string] $FileName, [string] $Arguments, [byte[]] $InputBytes) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($FileName, $Arguments)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $null -ne $InputBytes
    $start.RedirectStandardOutput = $true
    $process = [System.Diagnostics.Process]::Start($start)
    if ($start.RedirectStandardInput) {
        $process.StandardInput.BaseStream.Write($InputBytes, 0, $InputBytes.Length)
        $process.StandardInput.Close()
    }
    $null = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
}

function Read-Records([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }
    return @(Get-Content -LiteralPath $Path -Encoding utf8 | Where-Object -FilterScript { $_ } | ForEach-Object -Process { $_ | ConvertFrom-Json })
}

function Wait-RecordCount([string] $Path, [int] $Count) {
    $deadline = [DateTime]::UtcNow.AddSeconds($recordTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and @(Read-Records -Path $Path).Count -lt $Count) {
        Start-Sleep -Milliseconds $pollMilliseconds
    }
}

function ConvertTo-Display([object[]] $Arguments) {
    return '[' + (@($Arguments) -join '] [') + ']'
}

$json = & node (Join-Path -Path $PSScriptRoot -ChildPath 'quoting-cases.ts')
if ($LASTEXITCODE -ne 0) {
    throw 'scripts/quoting-cases.ts failed'
}
$cases = @($json | ConvertFrom-Json)
$root = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('headless-run-quoting-' + [guid]::NewGuid().ToString('N'))
$bin = Join-Path -Path $root -ChildPath 'bin'
$work = Join-Path -Path $root -ChildPath 'work'
$tool = Join-Path -Path $bin -ChildPath 'tool.exe'
$records = Join-Path -Path $bin -ChildPath 'received.jsonl'
$failures = [System.Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Path $bin, $work | Out-Null
try {
    Get-ChildItem -LiteralPath (Split-Path -Path (Resolve-Path -LiteralPath $Harness) -Parent) -File | Copy-Item -Destination $bin
    Copy-Item -LiteralPath $Harness -Destination $tool
    $gitBash = Find-GitBash
    $python = Find-Python

    foreach ($group in $cases | Group-Object -Property caller) {
        $caller = $group.Name
        $runnable = @($group.Group | Where-Object -FilterScript { $null -ne $_.line })
        foreach ($refused in $group.Group | Where-Object -FilterScript { $null -eq $_.line }) {
            Write-Output -InputObject "NOTE $caller $($refused.marker): the model refuses it: $($refused.error)"
        }
        Remove-Item -LiteralPath $records -ErrorAction SilentlyContinue
        $file = Join-Path -Path $work -ChildPath $caller
        $skip = $null
        switch ($caller) {
            'cmd-prompt' {
                # Lines read from standard input are parsed as the prompt parses them, not as a batch file. cmd reads a
                # pipe in the console code page, so a line outside ASCII goes through cmd /s /c instead, whose command
                # line is UTF-16 and which parses its command as the prompt does.
                $ascii = @($runnable | Where-Object -FilterScript { $_.line -notmatch '[^\x00-\x7F]' })
                $text = (($ascii | ForEach-Object -Process { '"' + $tool + '" ' + $_.line }) -join "`r`n") + "`r`n"
                Invoke-Raw -FileName $env:ComSpec -Arguments '/d /q' -InputBytes $utf8.GetBytes($text)
                foreach ($case in $runnable | Where-Object -FilterScript { $_.line -match '[^\x00-\x7F]' }) {
                    Invoke-Raw -FileName $env:ComSpec -Arguments ('/d /s /c ""' + $tool + '" ' + $case.line + '"')
                }
            }
            'cmd-batch' {
                $text = "@echo off`r`nchcp 65001 >nul`r`n" + (($runnable | ForEach-Object -Process { '"' + $tool + '" ' + $_.line }) -join "`r`n") + "`r`n"
                [System.IO.File]::WriteAllText($file + '.cmd', $text, $utf8)
                Invoke-Raw -FileName $env:ComSpec -Arguments ('/d /c "' + $file + '.cmd"')
            }
            { $_ -in 'pwsh', 'powershell' } {
                $text = ($runnable | ForEach-Object -Process { "& '" + $tool + "' " + $_.line }) -join "`n"
                [System.IO.File]::WriteAllText($file + '.ps1', $text, $utf8WithBom)
                $exe = if ($caller -eq 'pwsh') {
                    'pwsh.exe'
                }
                else {
                    Join-Path -Path $env:SystemRoot -ChildPath 'System32\WindowsPowerShell\v1.0\powershell.exe'
                }
                Invoke-Raw -FileName $exe -Arguments ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $file + '.ps1"')
            }
            'git-bash' {
                if (-not $gitBash) {
                    $skip = 'Git Bash not found'
                    break
                }
                $text = ($runnable | ForEach-Object -Process { "MSYS_NO_PATHCONV=1 '" + $tool + "' " + $_.line }) -join "`n"
                [System.IO.File]::WriteAllText($file + '.sh', $text + "`n", $utf8)
                Invoke-Raw -FileName $gitBash -Arguments ('"' + $file + '.sh"')
            }
            'wsl' {
                if (-not $Wsl) {
                    $skip = 'pass -Wsl to run it'
                    break
                }
                $linuxTool = (& wsl.exe -e wslpath -u $tool.Replace('\', '/')).Trim()
                $text = ($runnable | ForEach-Object -Process { "'" + $linuxTool + "' " + $_.line }) -join "`n"
                [System.IO.File]::WriteAllText($file + '.sh', $text + "`n", $utf8)
                $linuxScript = (& wsl.exe -e wslpath -u ($file + '.sh').Replace('\', '/')).Trim()
                Invoke-Raw -FileName 'wsl.exe' -Arguments ('--cd ~ -e bash "' + $linuxScript + '"')
            }
            'shortcut' {
                $shell = New-Object -ComObject WScript.Shell
                $count = 0
                # WScript.Shell stores some characters outside the ANSI code page as ?, a limit of the tool that writes
                # the shortcut rather than of the shortcut, so those cases are left to the raw caller.
                $wide = @($runnable | Where-Object -FilterScript { $_.line -match '[^\x00-\xFF]' })
                foreach ($case in $wide) {
                    Write-Output -InputObject "NOTE shortcut $($case.marker): not run, since WScript.Shell cannot store every character of it"
                }
                $runnable = @($runnable | Where-Object -FilterScript { $_.line -notmatch '[^\x00-\xFF]' })
                foreach ($case in $runnable) {
                    $link =$shell.CreateShortcut((Join-Path -Path $work -ChildPath ($case.marker + '.lnk')))
                    $link.TargetPath = $tool
                    $link.Arguments = $case.line
                    $link.WindowStyle = 7
                    $link.Save()
                    Invoke-Item -LiteralPath $link.FullName
                    $count++
                    Wait-RecordCount -Path $records -Count $count
                }
            }
            { $_ -in 'task-scheduler', 'run-dialog' } {
                $skip = 'the shortcut stands for it: the same raw line and the same %NAME% expansion'
            }
            'raw' {
                foreach ($case in $runnable) {
                    Invoke-Raw -FileName $tool -Arguments $case.line
                }
            }
            'node' {
                if (-not (Get-Command -Name 'node' -CommandType Application -ErrorAction SilentlyContinue)) {
                    $skip = 'node not found'
                    break
                }
                $text = "import { spawnSync } from 'node:child_process';`n" + (($runnable | ForEach-Object -Process { 'spawnSync(' + ($tool | ConvertTo-Json) + ', ' + $_.line + ');' }) -join "`n")
                [System.IO.File]::WriteAllText($file + '.mjs', $text, $utf8)
                Invoke-Raw -FileName 'node' -Arguments ('"' + $file + '.mjs"')
            }
            'python' {
                if (-not $python) {
                    $skip = 'Python not found'
                    break
                }
                $text = "import subprocess`n" + (($runnable | ForEach-Object -Process { 'subprocess.run([' + ($tool | ConvertTo-Json) + '] + ' + $_.line + ')' }) -join "`n")
                [System.IO.File]::WriteAllText($file + '.py', $text, $utf8)
                Invoke-Raw -FileName $python -Arguments ('-I "' + $file + '.py"')
            }
            'csharp' {
                # A file-based app, which dotnet run builds from the one .cs file.
                $text = "using System.Diagnostics;`n" + (($runnable | ForEach-Object -Process { 'Process.Start(new ProcessStartInfo(' + ($tool | ConvertTo-Json) + ') { ArgumentList = ' + $_.line + ' })!.WaitForExit();' }) -join "`n")
                [System.IO.File]::WriteAllText($file + '.cs', $text, $utf8)
                Invoke-Raw -FileName 'dotnet' -Arguments ('run "' + $file + '.cs"')
            }
            default {
                throw "scripts/quoting-cases.ts names an unknown caller '$caller'"
            }
        }

        if ($skip) {
            Write-Output -InputObject "SKIP ${caller}: $skip"
            continue
        }

        Wait-RecordCount -Path $records -Count $runnable.Count
        $received = @{}
        foreach ($record in Read-Records -Path $records) {
            $arguments = @($record.args)
            if ($arguments.Count -eq 0) {
                $failures.Add("${caller}: tool.exe ran with no arguments")
                continue
            }
            if ($received.ContainsKey($arguments[0])) {
                $failures.Add("${caller} $($arguments[0]): tool.exe ran more than once")
            }
            $received[$arguments[0]] = $arguments
        }
        $passed = 0
        foreach ($case in $runnable) {
            $expected = @($case.expected)
            if (-not $received.ContainsKey($case.marker)) {
                $failures.Add("${caller} $($case.marker): tool.exe never received it; line: $($case.line)")
                continue
            }
            $got = @($received[$case.marker])
            if ($got.Count -ne $expected.Count -or ($got -join "`n") -cne ($expected -join "`n")) {
                $failures.Add("${caller} $($case.marker): line $($case.line) gave $(ConvertTo-Display $got), the model says $(ConvertTo-Display $expected)")
                continue
            }
            $passed++
        }
        Write-Output -InputObject "$(if ($passed -eq $runnable.Count) { 'PASS' } else { 'DONE' }) $caller $passed of $($runnable.Count) matched the model"
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -ne 0) {
    $failures | ForEach-Object -Process { Write-Output -InputObject "FAIL $_" }
    throw "$($failures.Count) quoting case(s) disagree with the real caller"
}
