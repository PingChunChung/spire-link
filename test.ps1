param([string]$GameDir = $env:STS2_GAME_DIR, [string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
if ($GameDir) { $env:STS2_GAME_DIR = $GameDir }
# Each test project is a small console program that exits non-zero on the first failed check.
$projects = Get-ChildItem (Join-Path $PSScriptRoot 'tests') -Filter '*Tests.csproj' | Sort-Object Name
foreach ($project in $projects) {
    & $DotnetPath run --project $project.FullName -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "$($project.BaseName) failed." }
}
Write-Output "$($projects.Count) test projects passed."
