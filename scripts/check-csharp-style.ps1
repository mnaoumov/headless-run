[CmdletBinding()]
param([string[]] $Path = @())

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# PowerShell 7 ships Roslyn for Add-Type; no analyzer package or runtime dependency is added.
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')

if ($Path.Count -eq 0) {
    $repo = Split-Path $PSScriptRoot -Parent
    $Path = @((Join-Path $repo 'src'), (Join-Path $repo 'tests'))
}
$files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
$problems = [Collections.Generic.List[string]]::new()

foreach ($file in $files) {
    $source = [IO.File]::ReadAllText($file.FullName)
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)
    $cancellation = [Threading.CancellationToken]::None
    $root = $tree.GetRoot()
    $lines = $tree.GetText($cancellation).Lines
    foreach ($diagnostic in $tree.GetDiagnostics($cancellation)) {
        if ($diagnostic.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) {
            $problems.Add("$($file.FullName): $diagnostic")
        }
    }
    foreach ($node in $root.DescendantNodes()) {
        $position = $node.GetLocation().GetLineSpan().StartLinePosition
        $location = "$($file.FullName):$($position.Line + 1)"
        if ($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax] -and
            $node.Variables.Count -gt 1) {
            $problems.Add("${location}: declare one variable per declaration.")
        }
        if ($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax]) {
            $opening = $node.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition
            $closing = $node.CloseBraceToken.GetLocation().GetLineSpan().StartLinePosition
            if ($opening.Line -eq $closing.Line -or
                $lines[$opening.Line].ToString().Substring(0, $opening.Character).Trim().Length -ne 0 -or
                $lines[$closing.Line].ToString().Substring(0, $closing.Character).Trim().Length -ne 0) {
                $problems.Add("${location}: expand block braces onto their own lines.")
            }
        }

        $siblings = @()
        if ($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax]) {
            $siblings = @($node.Members)
        }
        elseif ($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax] -or
            $node -is [Microsoft.CodeAnalysis.CSharp.Syntax.SwitchSectionSyntax]) {
            $siblings = @($node.Statements)
        }
        $occupiedLines = [Collections.Generic.HashSet[int]]::new()
        foreach ($sibling in $siblings) {
            $line = $sibling.GetLocation().GetLineSpan().StartLinePosition.Line
            if (-not $occupiedLines.Add($line)) {
                $problems.Add("$($file.FullName):$($line + 1): put each declaration or statement on its own line.")
            }
        }
    }
}

if ($problems.Count -ne 0) {
    $problems | Write-Output
    throw "C# syntax style check found $($problems.Count) violation(s)."
}
Write-Output "C# syntax style check passed for $($files.Count) file(s)."
