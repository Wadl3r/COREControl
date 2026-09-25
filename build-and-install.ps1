<#
.SYNOPSIS
    Builds CORE Control in Release and installs the whole output folder into the game.

.DESCRIPTION
    1. Resolves the game folder from -GameDir, else $env:NUCLEAR_OPTION_DIR.
    2. Runs `dotnet build COREControl.csproj -c Release`.
    3. Copies EVERYTHING in bin\Release\net472 (DLL, PDB, shipped Mission JSON files) to
       <GameDir>\BepInEx\plugins\COREControl\. The mission JSON files must sit beside the DLL:
       CommanderMissionInstaller reads them from the plugin folder at load time.
    4. Warns if an upstream build of this mod (plugins\GroundControlRts or
       plugins\NuclearOptionCommander) is installed. Those are separate mods now and are left alone,
       but CORE Control declares itself incompatible with both, so BepInEx skips CORE Control while
       either is loaded. Move the other one to disabledPlugins to play CORE Control.

.PARAMETER Dev
    Hot-reload mode. Installs into <GameDir>\BepInEx\scripts\ instead of plugins\, for the
    ScriptEngine plugin (BepInEx.Debug). With the game running, press F6 (ScriptEngine's default
    reload key) and the new build loads without a restart. The plugins\COREControl folder is
    removed so the mod is never loaded twice. Requires BepInEx\plugins\ScriptEngine.dll.

    Without -Dev the script installs to plugins\ and removes any scripts\ copy, restoring the
    normal release layout.

.EXAMPLE
    .\build-and-install.ps1
    .\build-and-install.ps1 -Dev
    .\build-and-install.ps1 -GameDir "D:\Steam\steamapps\common\Nuclear Option"
    .\build-and-install.ps1 -Clean
#>
[CmdletBinding()]
param(
    [string]$GameDir = $env:NUCLEAR_OPTION_DIR,
    [switch]$Clean,
    [switch]$Dev
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    throw "Game folder unknown. Pass -GameDir or set the NUCLEAR_OPTION_DIR environment variable."
}
if (-not (Test-Path (Join-Path $GameDir 'NuclearOption_Data\Managed\Assembly-CSharp.dll'))) {
    throw "Assembly-CSharp.dll not found under '$GameDir'. Is this the Nuclear Option install folder?"
}
if (-not (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
    throw "BepInEx not found under '$GameDir\BepInEx'. Install BepInEx 5 first."
}

# The csproj reads NUCLEAR_OPTION_DIR; make sure this process sees the folder we validated.
$env:NUCLEAR_OPTION_DIR = $GameDir

$outDir = Join-Path $repo 'bin\Release\net472'
if ($Clean -and (Test-Path $outDir)) {
    Write-Host "Cleaning $outDir"
    Remove-Item -Recurse -Force $outDir
}

Write-Host "Building COREControl (Release)..."
& dotnet build (Join-Path $repo 'COREControl.csproj') -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$pluginsDir = Join-Path $GameDir 'BepInEx\plugins'
foreach ($upstream in @('GroundControlRts', 'NuclearOptionCommander')) {
    $upstreamDir = Join-Path $pluginsDir $upstream
    if (Test-Path $upstreamDir) {
        Write-Warning ("$upstreamDir is an upstream build of this mod. CORE Control will not load while it is " +
            "loaded (both patch the same game methods). Move it to BepInEx\disabledPlugins to play CORE Control.")
    }
}

$releaseDir = Join-Path $pluginsDir 'COREControl'
$scriptsDir = Join-Path $GameDir 'BepInEx\scripts'

if ($Dev) {
    if (-not (Test-Path (Join-Path $pluginsDir 'ScriptEngine.dll'))) {
        throw "ScriptEngine.dll not found in $pluginsDir. -Dev needs the ScriptEngine plugin from BepInEx.Debug."
    }
    # Exactly one copy of the mod may exist. Release copy out, hot-reload copy in.
    if (Test-Path $releaseDir) {
        Write-Host "Removing release copy $releaseDir (hot-reload copy takes over)."
        Remove-Item -Recurse -Force $releaseDir
    }
    New-Item -ItemType Directory -Force $scriptsDir | Out-Null
    # ScriptEngine loads every DLL in scripts\ from bytes, so the game never locks the file and the
    # copy succeeds while the game is running. The PDB goes too so stack traces keep line numbers.
    # The mission JSON is not needed here: a hot-loaded assembly has no folder for the installer to
    # read, and the mission is already installed by the first normal load.
    Write-Host "Installing (hot-reload) $outDir\COREControl.dll/.pdb -> $scriptsDir"
    Copy-Item -Path (Join-Path $outDir 'COREControl.dll') -Destination $scriptsDir -Force
    Copy-Item -Path (Join-Path $outDir 'COREControl.pdb') -Destination $scriptsDir -Force -ErrorAction SilentlyContinue
    $installDir = $scriptsDir
    $hint = "Game running? Press F6 to reload. Not running? Launch it; ScriptEngine loads scripts\ at start."
}
else {
    $stale = Join-Path $scriptsDir 'COREControl.dll'
    if (Test-Path $stale) {
        Write-Host "Removing hot-reload copy $stale (release copy takes over)."
        Remove-Item -Force $stale
        Remove-Item -Force (Join-Path $scriptsDir 'COREControl.pdb') -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force $releaseDir | Out-Null
    Write-Host "Installing $outDir\* -> $releaseDir"
    Copy-Item -Path (Join-Path $outDir '*') -Destination $releaseDir -Recurse -Force
    $installDir = $releaseDir
    $hint = "Launch Nuclear Option; check BepInEx\LogOutput.log for 'CORE Control' load lines."
}

Write-Host ""
Write-Host "Installed files in ${installDir}:"
Get-ChildItem $installDir | ForEach-Object { Write-Host ("  " + $_.Name) }
Write-Host ""
Write-Host "Done. $hint"
