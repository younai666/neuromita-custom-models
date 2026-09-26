<#
    install.ps1 — one-shot installer for NeuroMita.CustomModels.

    What it does:
      1. Finds the game folder (or takes -GameDir).
      2. Copies the plugin DLLs into <Game>\BepInEx\plugins\.
      3. Installs BepInEx 6.0.0-be.788 automatically if it is missing or too old.
      4. Creates the CustomModels folder.

    It deliberately does NOT touch game files, and does not need administrator rights —
    everything it writes lives inside the game folder.

    Usage (normally via install.bat):
        powershell -ExecutionPolicy Bypass -File install.ps1
        powershell -ExecutionPolicy Bypass -File install.ps1 -GameDir "D:\Games\NeuroMita"
#>
[CmdletBinding()]
param(
    [string]$GameDir = '',
    [switch]$Force,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Pinned on purpose: this game ships IL2CPP metadata v39, and builds older than be.788
# cannot read it (they stop at "We support 23-31, got 39"). Do not float this.
$BepInExUrl = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
$BepInExName = 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788'

function Pause-IfInteractive {
    if (-not $NoPause) { Read-Host '  Press Enter to close' | Out-Null }
}

function Say($msg, $color = 'Gray') { Write-Host $msg -ForegroundColor $color }

function Fail($msg) {
    Say ''
    Say "  [X] $msg" 'Red'
    Say ''
    Say '  Nothing was changed. Fix the problem above and run this again.'
    Pause-IfInteractive
    exit 1
}

Say ''
Say '  NeuroMita.CustomModels - installer' 'Cyan'
Say '  ----------------------------------' 'Cyan'
Say ''

# ---------------------------------------------------------------- 1. game folder

$here = $PSScriptRoot
$isExtractedIntoGame = (Test-Path (Join-Path $here 'NeuroMita.exe'))

# A dropped NeuroMita.exe arrives as a file path - take its folder.
if ($GameDir -and (Test-Path $GameDir -PathType Leaf)) {
    $GameDir = Split-Path $GameDir -Parent
}

if (-not $GameDir) {
    if ($isExtractedIntoGame) {
        $GameDir = $here
        Say "  This archive is already inside the game folder: $GameDir" 'Gray'
    } else {
        # Look for the game next to us, then in the usual places.
        $guess = @(
            (Join-Path (Split-Path $here -Parent) 'NeuroMita.exe'),
            'C:\Program Files (x86)\Steam\steamapps\common\NeuroMita\NeuroMita.exe',
            'C:\Program Files\Steam\steamapps\common\NeuroMita\NeuroMita.exe'
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

        if ($guess) {
            $GameDir = Split-Path $guess -Parent
            Say "  Found the game at: $GameDir" 'Gray'
        } else {
            Say '  Where is NeuroMita installed?' 'Yellow'
            Say '  (the folder containing NeuroMita.exe - you can drag that folder onto this window)' 'Gray'
            Say ''
            $GameDir = (Read-Host '  Game folder').Trim('"', ' ')
        }
    }
}

if (-not (Test-Path (Join-Path $GameDir 'NeuroMita.exe'))) {
    Fail "no NeuroMita.exe in '$GameDir' - that is not the game folder."
}
Say "  Game folder: $GameDir" 'Green'

# ---------------------------------------------------------------- 2. plugin DLLs

$myPlugins = Join-Path $here 'BepInEx\plugins'
$destPlugins = Join-Path $GameDir 'BepInEx\plugins'

if ((Test-Path $myPlugins) -and ($myPlugins -ne $destPlugins)) {
    New-Item -ItemType Directory -Path $destPlugins -Force | Out-Null
    Say ''
    Say '  [1/4] Copying the plugin...' 'Cyan'
    Get-ChildItem $myPlugins -File | ForEach-Object {
        Copy-Item $_.FullName $destPlugins -Force
        Say ("        {0,-34} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB))
    }
} elseif (Test-Path $destPlugins) {
    Say ''
    Say '  [1/4] Plugin already in place' 'Cyan'
} else {
    Fail "the plugin DLLs are not next to this script (expected '$myPlugins'). " +
         'Extract the whole archive, do not move files out of it.'
}

# ---------------------------------------------------------------- 3. BepInEx

Say ''
Say '  [2/4] Checking BepInEx...' 'Cyan'

$coreDll = Join-Path $GameDir 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
$needBepInEx = $Force -or (-not (Test-Path $coreDll))

if (-not $needBepInEx) {
    # Report the version we found, and warn if it predates the metadata-v39 support.
    $ver = ''
    try { $ver = (Get-Item $coreDll).VersionInfo.ProductVersion } catch { }
    Say "        found BepInEx $ver" 'Gray'
    if ($ver -and ($ver -match 'be\.(\d+)') -and ([int]$Matches[1] -lt 788)) {
        Say "        [!] that build is older than be.788 and cannot read this game's metadata" 'Yellow'
        Say '        [i] run this installer with -Force to replace it' 'Yellow'
    }
}

if ($needBepInEx) {
    Say '        not installed - downloading it now (about 34 MB)' 'Yellow'
    $tmp = Join-Path $env:TEMP "nmcm-bepinex-$([guid]::NewGuid().ToString('N')).zip"
    try {
        $progress = $ProgressPreference
        $ProgressPreference = 'Continue'
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $tmp -UseBasicParsing
        $ProgressPreference = $progress
    } catch {
        Fail "could not download BepInEx: $($_.Exception.Message)`n" +
             "      Get it manually from https://builds.bepinex.dev/projects/bepinex_be`n" +
             "      ($BepInExName.zip) and extract it into the game folder."
    }

    $size = (Get-Item $tmp).Length
    Say ("        downloaded {0:N1} MB" -f ($size / 1MB)) 'Gray'

    Say '        extracting into the game folder...' 'Gray'
    try {
        Expand-Archive -Path $tmp -DestinationPath $GameDir -Force
    } catch {
        Fail "could not extract BepInEx: $($_.Exception.Message)"
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }

    foreach ($f in @('winhttp.dll', 'doorstop_config.ini', 'BepInEx\core\BepInEx.Unity.IL2CPP.dll')) {
        if (-not (Test-Path (Join-Path $GameDir $f))) { Fail "BepInEx extracted but '$f' is missing - the archive may be corrupt." }
    }
    Say '        BepInEx installed' 'Green'
}

# ---------------------------------------------------------------- 4. CustomModels

Say ''
Say '  [3/4] Preparing CustomModels...' 'Cyan'
$cm = Join-Path $GameDir 'CustomModels'
New-Item -ItemType Directory -Path $cm -Force | Out-Null
$placeholder = Join-Path $cm 'PUT-PACKS-HERE.txt'
$template = Join-Path $here 'CustomModels\PUT-PACKS-HERE.txt'
if ((Test-Path $template) -and -not (Test-Path $placeholder)) { Copy-Item $template $cm -Force }
Say "        $cm" 'Gray'

# ---------------------------------------------------------------- 5. done

$interop = Join-Path $GameDir 'BepInEx\interop'
$interopReady = Test-Path $interop

Say ''
Say '  [4/4] Done.' 'Green'
Say ''
Say '  Next:' 'Cyan'
if (-not $interopReady) {
    Say '    1. Launch the game ONCE and wait about 25 seconds.' 'White'
    Say '       BepInEx is generating BepInEx\interop\ on this first run.' 'Gray'
    Say '       When it finishes you can close the game.' 'Gray'
    Say '    2. Put your model packs into the CustomModels folder.' 'White'
    Say '    3. Launch again and enter a scene with a Mita.' 'White'
} else {
    Say '    1. Put your model packs into the CustomModels folder.' 'White'
    Say '    2. Launch the game and enter a scene with a Mita.' 'White'
}
Say ''
Say '  Full guide: docs\INSTALL.md      Problems: docs\FAQ.md' 'Gray'
Say '  Log to check: BepInEx\LogOutput.log  (look for "failed=0")' 'Gray'
Say ''
Pause-IfInteractive
