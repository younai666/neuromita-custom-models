<#
    package.ps1 — build and produce a release zip.

    Usage:
        powershell -File package.ps1
        powershell -File package.ps1 -GameDir "D:\Games\NeuroMita"    # also deploy to a game

    Output:
        dist\NeuroMita.CustomModels-<version>.zip
        dist\NeuroMita.CustomModels-<version>\            (unpacked staging folder)

    The zip contains everything a user needs: the plugin, AssimpNet, the native assimp library
    and the docs. Nothing has to be downloaded or compiled by the user.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$GameDir = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\NeuroMita.CustomModels'

# ---------- version ----------
$csprojPath = Join-Path $proj 'NeuroMita.CustomModels.csproj'
$csproj = Get-Content $csprojPath -Raw
$m = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
if (-not $m.Success) { throw "could not read <Version> from $csprojPath" }
$version = $m.Groups[1].Value.Trim()
Write-Host "packaging NeuroMita.CustomModels $version" -ForegroundColor Cyan

# ---------- build ----------
$buildArgs = @($proj, '-c', $Configuration, '-v', 'm')
if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }

Write-Host "`n[1/4] building..." -ForegroundColor Cyan
& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

$bin = Join-Path $proj "bin\$Configuration"
$pluginDll = Join-Path $bin 'NeuroMita.CustomModels.dll'
if (-not (Test-Path $pluginDll)) { throw "build output not found: $pluginDll" }

# ---------- locate the native library ----------
Write-Host "`n[2/4] locating assimp.dll..." -ForegroundColor Cyan
$assimp = $null
$candidates = @(
    (Join-Path $bin 'assimp.dll'),
    (Join-Path $env:USERPROFILE '.nuget\packages\assimpnet\4.1.0\runtimes\win-x64\native\assimp.dll')
)
foreach ($c in $candidates) {
    if (Test-Path $c) { $assimp = $c; break }
}
if (-not $assimp) {
    throw ("assimp.dll not found. Looked in:`n  " + ($candidates -join "`n  ") +
           "`nRestore the NuGet package first: dotnet restore")
}
Write-Host "    $assimp"

# ---------- stage ----------
Write-Host "`n[3/4] staging..." -ForegroundColor Cyan
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "NeuroMita.CustomModels-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$pluginDir = Join-Path $stage 'BepInEx\plugins'
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null

Copy-Item $pluginDll                                  $pluginDir -Force
Copy-Item (Join-Path $bin 'AssimpNet.dll')            $pluginDir -Force
Copy-Item $assimp                                     $pluginDir -Force

# AssetBundle path dependencies (AssetsTools.NET + its texture decoder).
# Collected by pattern so a version bump does not silently drop one.
$managedExtras = @(
    'AssetsTools.NET.dll',
    'AssetsTools.NET.Texture.dll',
    'AssetRipper.TextureDecoder.dll'
)
foreach ($name in $managedExtras) {
    $src = Join-Path $bin $name
    if (Test-Path $src) {
        Copy-Item $src $pluginDir -Force
    } else {
        throw "missing dependency in build output: $name (run: dotnet build -c $Configuration)"
    }
}
Copy-Item (Join-Path $root 'docs\READ-FIRST.txt')      $stage -Force
Copy-Item (Join-Path $root 'docs\install.bat')         $stage -Force
Copy-Item (Join-Path $root 'docs\install.ps1')         $stage -Force
Copy-Item (Join-Path $root 'LICENSE')                  $stage -Force

# Docs go into their own folder so the root of the archive stays obvious:
# read-me, the game-layout folders, and the licence. Nothing else.
$docDir = Join-Path $stage 'docs'
New-Item -ItemType Directory -Path $docDir -Force | Out-Null
Copy-Item (Join-Path $root 'README.md')                $docDir -Force
Copy-Item (Join-Path $root 'CHANGELOG.md')             $docDir -Force
Copy-Item (Join-Path $root 'docs\INSTALL.md')          $docDir -Force
Copy-Item (Join-Path $root 'docs\FAQ.md')              $docDir -Force
Copy-Item (Join-Path $root 'docs\FORMATS.md')          $docDir -Force

# Ready-made pack folder, so the user does not have to create it (and gets the routing rules).
$cmDir = Join-Path $stage 'CustomModels'
New-Item -ItemType Directory -Path $cmDir -Force | Out-Null
Copy-Item (Join-Path $root 'docs\PUT-PACKS-HERE.txt')  $cmDir -Force

Get-ChildItem $pluginDir -File | ForEach-Object {
    Write-Host ("    {0,-34} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB))
}

# ---------- zip ----------
Write-Host "`n[4/4] zipping..." -ForegroundColor Cyan
$zip = Join-Path $dist "NeuroMita.CustomModels-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("    {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

# ---------- optional deploy ----------
if ($GameDir) {
    $target = Join-Path $GameDir 'BepInEx\plugins'
    if (Test-Path $target) {
        Write-Host "`n[deploy] copying plugin into $target" -ForegroundColor Cyan
        Get-ChildItem $pluginDir -File | ForEach-Object {
            Copy-Item $_.FullName $target -Force
            Write-Host "    -> $($_.Name)"
        }
    } else {
        Write-Warning "not a BepInEx game folder, skipped deploy: $GameDir"
    }
}

Write-Host "`ndone." -ForegroundColor Green
Write-Host "  zip     : $zip"
Write-Host "  staging : $stage"
