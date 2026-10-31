param(
    [string]$GameDir = $env:STS2_GAME_DIR,
    [string]$DotnetPath = 'dotnet',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { throw 'Provide -GameDir or STS2_GAME_DIR.' }
$resolvedGameDir = (Resolve-Path -LiteralPath $GameDir).Path
$assembly = Join-Path $resolvedGameDir 'data_sts2_windows_x86_64/sts2.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Game assemblies not found.' }
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mod_manifest.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
& $DotnetPath build (Join-Path $PSScriptRoot 'SpireLink.csproj') -c Release "-p:STS2GameDir=$resolvedGameDir"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# The release is one folder players drop into mods/. Only the mod goes in: the game reads every .json under mods/ as a
# manifest, so the client, tools and examples ship separately.
$packageDir = Join-Path $PSScriptRoot ("dist/SpireLink-$version/SpireLink")
if (Test-Path -LiteralPath $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net9.0/SpireLink.dll') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mod_manifest.json') -Destination (Join-Path $packageDir 'SpireLink.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging/README.md') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/api.md') -Destination $packageDir
Compress-Archive -Path $packageDir -DestinationPath (Join-Path $PSScriptRoot "dist/SpireLink-$version.zip") -Force
if ($Install) {
    if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) { throw 'Exit the game normally before installing a Mod.' }
    $modsDir = Join-Path $resolvedGameDir 'mods'
    New-Item -ItemType Directory -Path $modsDir -Force | Out-Null
    $backupDir = Join-Path $PSScriptRoot ('.install-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $names = @('SpireLink.dll','SpireLink.json')
    foreach ($name in $names) {
        $source = [IO.Path]::GetFullPath((Join-Path $modsDir $name))
        if (-not $source.StartsWith([IO.Path]::GetFullPath($modsDir) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe Mod backup source.' }
        if (Test-Path -LiteralPath $source) {
            New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
            Move-Item -LiteralPath $source -Destination (Join-Path $backupDir $name)
        }
    }
    Copy-Item -LiteralPath (Join-Path $packageDir 'SpireLink.dll'),(Join-Path $packageDir 'SpireLink.json') -Destination $modsDir
    [pscustomobject]@{installed_to=$modsDir;backup_directory=$backupDir;dll_sha256=(Get-FileHash -LiteralPath (Join-Path $modsDir 'SpireLink.dll')).Hash} | ConvertTo-Json
} else { Write-Output "Package: $(Join-Path $PSScriptRoot "dist/SpireLink-$version.zip")" }
