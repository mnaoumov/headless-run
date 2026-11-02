[CmdletBinding()]
param([string] $Launcher)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_PROCESSOR_COUNT = '2'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & (Join-Path $PSScriptRoot 'check-csharp-style.ps1')
    if (-not $Launcher) {
        dotnet build src/headless-run -c Release --nologo -m:2
        if ($LASTEXITCODE -ne 0) {
            throw 'Launcher build failed'
        }
        $Launcher = Join-Path $repo 'src/headless-run/bin/Release/net10.0/headless-run.exe'
    }
    dotnet build tests/Integration -c Release --nologo -m:2
    if ($LASTEXITCODE -ne 0) {
        throw 'Integration build failed'
    }
    foreach ($project in @('src/headless-run/headless-run.csproj', 'tests/Integration/Integration.csproj')) {
        dotnet format style $project --no-restore --severity info --diagnostics IDE0011 IDE0007 IDE0022 IDE0061 --verify-no-changes
        if ($LASTEXITCODE -ne 0) {
            throw "C# style check failed: $project"
        }
        dotnet format whitespace $project --no-restore --verify-no-changes
        if ($LASTEXITCODE -ne 0) {
            throw "C# formatting check failed: $project"
        }
    }
    & ./tests/Integration/bin/Release/net10.0/Integration.exe $Launcher
    if ($LASTEXITCODE -ne 0) {
        throw 'Integration tests failed'
    }
}
finally {
    Pop-Location
}
