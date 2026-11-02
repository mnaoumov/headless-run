[CmdletBinding()]
param([string] $Launcher)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_PROCESSOR_COUNT = '2'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    if (-not $Launcher) {
        dotnet build src/headless-run -c Release --nologo -m:2
        if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed' }
        $Launcher = Join-Path $repo 'src/headless-run/bin/Release/net10.0/headless-run.exe'
    }
    dotnet build tests/Integration -c Release --nologo -m:2
    if ($LASTEXITCODE -ne 0) { throw 'Integration build failed' }
    & ./tests/Integration/bin/Release/net10.0/Integration.exe $Launcher
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed' }
}
finally { Pop-Location }
