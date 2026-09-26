# Installation

There is an installer. Extract the archive into the game folder, double-click `install.bat`, and it
downloads the right BepInEx build and puts everything in place.

```
1. Extract this archive into the game folder   (the one with NeuroMita.exe)
2. Double-click install.bat
3. Launch the game once and wait ~25 seconds    (BepInEx generates its interop assemblies)
4. Put model packs into CustomModels\ and launch again
```

That is the whole installation. The rest of this page explains what the installer did, and how to do
it by hand if you prefer.

---

## Before you start

| | |
|---|---|
| Game | NeuroMita (Unity 6000.3.12f1, IL2CPP) |
| OS | Windows x64 |
| Loader | **BepInEx 6.0.0-be.788 or newer for IL2CPP win-x64** — `install.bat` fetches this for you |

### Why the BepInEx version matters

Most modding guides link to an older BepInEx 6 build. This game ships **IL2CPP metadata version 39**,
and older builds cannot read it:

| Build | Metadata it understands | Works here? |
|---|---|---|
| `6.0.0-pre.2` | 23 – 29 | ❌ |
| **`6.0.0-be.788`** | 23 – 106 | ✅ |

If you use an older build the game will fail at startup with:

```
Unsupported metadata version found! We support 23-31, got 39.
```

`install.bat` pins be.788 on purpose and will not pick a different build. If you install BepInEx
yourself, get it from the BepInEx bleeding-edge releases
(`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+`).

---

## What `install.bat` does

It runs `install.ps1`, which:

1. Works out the game folder — it is the folder the script sits in if you already extracted there,
   otherwise you are asked, or you can drag the game folder (or `NeuroMita.exe`) onto `install.bat`.
2. Copies the plugin DLLs into `<Game>\BepInEx\plugins\` (skipped if you already extracted there,
   since they are in the right place).
3. Checks for BepInEx. If `BepInEx\core\BepInEx.Unity.IL2CPP.dll` is missing it downloads
   **be.788** (about 34 MB) from the official BepInEx build server and extracts it. If an older
   build is present it says so instead of overwriting it — re-run with `-Force` to replace it:

   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1 -Force
   ```
4. Creates the `CustomModels` folder.

It never touches game files, and it does not need administrator rights — everything it writes lives
inside the game folder. Running it twice is harmless.

---

## Doing it by hand

<details>
<summary>Steps 1 and 2, manually</summary>

### Step 1 — Install BepInEx

Extract the BepInEx archive into the folder that contains `NeuroMita.exe`, `GameAssembly.dll` and
`NeuroMita_Data\`. You should end up with:

```
<Game>\
├── NeuroMita.exe
├── GameAssembly.dll
├── NeuroMita_Data\
├── winhttp.dll              <- from BepInEx
├── doorstop_config.ini      <- from BepInEx
├── .doorstop_version        <- from BepInEx
├── BepInEx\                 <- from BepInEx
└── dotnet\                  <- from BepInEx
```

Then **launch the game once and wait** — about 25 seconds, while BepInEx writes ~124 DLLs into
`BepInEx\interop\`. Close the game when it finishes. If `BepInEx\interop\` is still missing, the
BepInEx version is wrong (see the table above).

### Step 2 — Install the plugin

Extract the release zip into the game folder as well. It already mirrors the game layout, so the six
DLLs land in `BepInEx\plugins\` on their own.

</details>

---

## The six plugin DLLs

Whichever route you take, this is the complete set, and all of it goes in `<Game>\BepInEx\plugins\`:

```
BepInEx\plugins\
├── NeuroMita.CustomModels.dll          <- the plugin
├── AssetsTools.NET.dll                 <- AssetBundle (.vrmmod) reading
├── AssetsTools.NET.Texture.dll
├── AssetRipper.TextureDecoder.dll
├── AssimpNet.dll                       <- FBX reading
└── assimp.dll                          <- native library for the above
```

> ### All six are required
>
> Do **not** copy files by hand from an older guide. Earlier versions of this plugin shipped only
> three DLLs (`NeuroMita.CustomModels.dll`, `AssimpNet.dll`, `assimp.dll`); AssetBundle support added
> the other three. If `AssetsTools.NET.dll` is missing, **every `.vrmmod` pack fails to load** — the
> plugin itself still loads, so it looks like the packs are broken rather than the install.
>
> The same applies in reverse: without `assimp.dll` FBX packs cannot be read, and the log says so
> explicitly (`native assimp library not found`) rather than failing silently.

Nothing goes in the game root folder, and you do **not** need to install AssetsTools.NET or AssimpNet
yourself — they are in the release zip.

---

## Adding model packs

1. Create a folder named `CustomModels` next to `NeuroMita.exe`:

   ```
   <Game>\CustomModels\
   ```

2. Put each model pack in **its own subfolder**:

   ```
   <Game>\CustomModels\
   ├── CJMitav1.1-73-1-1-0-1739264474\
   │   └── CJ\
   │       ├── addons_config.txt
   │       ├── CJMita.fbx
   │       └── cloth_merge.png
   └── Gothic-Mita-by-Index 1.3-80\
       ├── addons_config.txt
       └── GothicMita\
           ├── GothicMitaBody.fbx
           ├── GothicMitaHair.fbx
           ├── GothicMitaSweater.fbx
           └── GothicMita.png
   ```

   The exact internal layout does not matter — the plugin searches recursively for
   `addons_config.txt` and `*.fbx`.

3. **AssetBundle packs work as-is.** A single `UnityFS` file (`.vrmmod` and similar) can be dropped
   straight in — the plugin parses the container itself. Just remember the skeleton still has to
   match the game's bone names; see the FAQ.

4. **To drive several characters at once, use character folders.** A subfolder whose name matches a
   known character id is treated as a character folder, and each folder inside it is one pack:

   ```
   <Game>\CustomModels\
   ├── Player\          <- the player character
   │   └── MasterChief\
   ├── Crazy\           <- Crazy Mita
   │   └── Gothic\
   ├── Kind\            <- Kind Mita
   │   └── SomePack\
   └── Mila\            <- Mila
       └── CJ\
   ```

   Recognised ids: `Player`, `Crazy`, `Kind`, `Cappie`/`Cappy`, `ShortHair`, `Mila`,
   `Sleepy`/`Dream`, `Ghost`. With character folders you no longer set `TargetAvatar` — each pack is
   routed to its own character, and every instance of that character in the scene is handled.

   Both layouts can be mixed; anything not inside a recognised id falls back to the old
   `TargetAvatar` behaviour.

---

## Launching and verifying

Start the game normally and **enter a scene where a Mita is present** (main menu → `米塔选择` →
a house). The plugin waits until a character exists.

Open `BepInEx\LogOutput.log` and look for the install block.

**FBX pack with `addons_config.txt`:**

```
[CM] ===== character 'Crazy' -> 'Mita Crazy' (1 pack(s)) =====
[CM] ----- installing 'Gothic' -----
[CM] format: fbx-dir
[CM] parts: 3
[Inst] indexed 17 slots: Head, Head_1, FaceLayer, Hairs, Hair, SweaterSlot, ...
[Inst] button 'GothicMita' target='Crazy' activate=18 deactivate=10
[CM] INSTALL RESULT created=1 replaced=3 textured=3 removed=7 recovered=0 transformed=0 skipped=4 failed=0
```

**AssetBundle pack (`.vrmmod`), matched to slots automatically:**

```
[CM] ===== character 'Player' -> 'Person' (1 pack(s)) =====
[CM] format: assetbundle
[Bundle] unity=2021.3.35f1 assets=319
[CM] RESULT part='Master_Chief_Arm' ok=True bones=69 missing=0 residual=0.0000
[CM] RESULT part='Master_Chief_Head' ok=True bones=69 missing=0 residual=0.0000
[CM] auto-slotted 4/4 parts
```

A healthy install has **`failed=0`** and every part reporting **`ok=True`** with a small
**`residual`** — `0.0000` for a pack rigged to the game's skeleton.

**If a pack is rejected instead**, the reason is stated outright:

```
[Apply] REJECTED 'Attribute': align fit too poor: avg error 0.2952 is 13.2% of the
model size (limit 2%). This pack's rest pose does not match the game skeleton — it
was most likely built on a different rig, so replacing with it would deform the model.
```

That is intentional: a pack whose skeleton does not really match cannot be fitted, and installing it
anyway would show a broken pose that looks like success. See the FAQ for what "built on a different
rig" means and how to convert such a pack.

---

## Configuration

A config file is created on first launch:

```
<Game>\BepInEx\config\com.neuromita.custommodels.cfg
```

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Master switch |
| `PackDirectory` | `<Game>\CustomModels` | Where packs are scanned |
| `TargetAvatar` | `Crazy` | Which character to replace, matched against the Avatar name (`Crazy`, `Good`, `Mila`, `ShortHair`, `Player`, …) |
| `ActivePack` | *(empty)* | Install only this pack. Empty = every pack found |
| `FallbackRenderer` | `Body` | Only for bare `.fbx` files without a config file |
| `Verbose` | `true` | Per-part alignment details in the log |

Changing any of these requires a game restart.

---

## Uninstalling

Delete `NeuroMita.CustomModels.dll` from `BepInEx\plugins\` and remove the `CustomModels` folder.
Nothing outside those locations is touched — the plugin never modifies game files, and the other five
DLLs in `plugins\` are shared libraries that other mods may also use, so leave them unless you are
sure nothing else needs them.

If BepInEx itself was only installed for this plugin, removing `winhttp.dll`,
`doorstop_config.ini`, `.doorstop_version`, `BepInEx\` and `dotnet\` from the game folder reverts the
game completely.
