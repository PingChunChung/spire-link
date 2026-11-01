param(
    [string]$GameDir = $env:STS2_GAME_DIR,
    [string]$DotnetPath = 'dotnet',
    [switch]$Install,
    [switch]$Workshop
)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { throw 'Provide -GameDir or STS2_GAME_DIR.' }
$resolvedGameDir = (Resolve-Path -LiteralPath $GameDir).Path
$assembly = Join-Path $resolvedGameDir 'data_sts2_windows_x86_64/sts2.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Game assemblies not found.' }
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mod_manifest.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
# The manifest, the csproj and the Version constant in the mod must agree; a release with mismatched numbers stops here.
$projectVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SpireLink.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($projectVersion -ne $version) { throw "Version mismatch: mod_manifest.json has $version, SpireLink.csproj has $projectVersion." }
$sourceVersion = [regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/SpireLinkMod.cs') -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if ($sourceVersion -ne $version) { throw "Version mismatch: mod_manifest.json has $version, SpireLinkMod.Version is $sourceVersion." }
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
if ($Workshop) {
    # workshop/ is the ModUploader workspace: content/ gets the same files as the release folder. workshop.json, image.png,
    # previews/ and the mod_id.txt written by the first upload stay as they are.
    $contentDir = Join-Path $PSScriptRoot 'workshop/content'
    if (Test-Path -LiteralPath $contentDir) { Remove-Item -LiteralPath $contentDir -Recurse -Force }
    New-Item -ItemType Directory -Path $contentDir -Force | Out-Null
    Copy-Item -Path (Join-Path $packageDir '*') -Destination $contentDir
    $missing = @('image.png') | Where-Object { -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "workshop/$_")) }
    if ($missing) { Write-Warning "Workshop workspace is missing: $($missing -join ', '). ModUploader requires image.png (< 1 MB)." }
    Write-Output "Workshop content: $contentDir"
}
if ($Install) {
    if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) { throw 'Exit the game normally before installing a Mod.' }
    # Same layout as the release zip: mods/SpireLink/SpireLink.dll and SpireLink.json. A DLL and manifest left in the mods/
    # root by an older install would load the same id twice, so they are moved to the backup too.
    $modsDir = Join-Path $resolvedGameDir 'mods'
    $installDir = Join-Path $modsDir 'SpireLink'
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    $backupDir = Join-Path $PSScriptRoot ('.install-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    # Each entry is the file to move and its place under the backup folder.
    $moves = @()
    foreach ($name in @('SpireLink.dll','SpireLink.json')) {
        $moves += ,@((Join-Path $installDir $name), (Join-Path $backupDir "SpireLink/$name"))
        $moves += ,@((Join-Path $modsDir $name), (Join-Path $backupDir $name))
    }
    foreach ($move in $moves) {
        $source = [IO.Path]::GetFullPath($move[0])
        if (-not $source.StartsWith([IO.Path]::GetFullPath($modsDir) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe Mod backup source.' }
        if (Test-Path -LiteralPath $source) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $move[1]) -Force | Out-Null
            Move-Item -LiteralPath $source -Destination $move[1]
        }
    }
    Copy-Item -LiteralPath (Join-Path $packageDir 'SpireLink.dll'),(Join-Path $packageDir 'SpireLink.json') -Destination $installDir
    [pscustomobject]@{installed_to=$installDir;backup_directory=$backupDir;dll_sha256=(Get-FileHash -LiteralPath (Join-Path $installDir 'SpireLink.dll')).Hash} | ConvertTo-Json
} else { Write-Output "Package: $(Join-Path $PSScriptRoot "dist/SpireLink-$version.zip")" }
