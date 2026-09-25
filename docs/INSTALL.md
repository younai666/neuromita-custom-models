# Installation

Follow the steps in order. Step 1 is the one people most often get wrong.

---

## Before you start

| | |
|---|---|
| Game | NeuroMita (Unity 6000.3.12f1, IL2CPP) |
| OS | Windows x64 |
| Loader | **BepInEx 6.0.0-be.788 or newer for IL2CPP win-x64** |

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

Get the correct build from the BepInEx bleeding-edge releases
(`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+`).

---

## Step 1 — Install BepInEx

1. Extract the BepInEx archive into the folder that contains `NeuroMita.exe`,
   `GameAssembly.dll` and `NeuroMita_Data\`. You should end up with:

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

2. **Launch the game once and wait.** On first launch BepInEx generates the interop assemblies
   (about 25 seconds, ~124 DLLs into `BepInEx\interop\`). When it finishes you can close the game.

   If `BepInEx\interop\` is missing afterwards, the BepInEx version is wrong — go back to the table above.

---

## Step 2 — Install the plugin

Copy these **three** files into `<Game>\BepInEx\plugins\`:

| File | Where it comes from |
|---|---|
| `NeuroMita.CustomModels.dll` | from this project's release zip, or your own build |
| `AssimpNet.dll` | same release zip |
| `assimp.dll` | same release zip (it is the native library) |

The result must look like this:

```
<Game>\BepInEx\plugins\
├── NeuroMita.CustomModels.dll
├── AssimpNet.dll
└── assimp.dll
```

> **`assimp.dll` is required.** Without it FBX packs cannot be read. The plugin will say so
> explicitly in the log rather than failing silently — look for
> `native assimp library not found`.
>
> You do **not** need to put it in the game root folder, and you do **not** need to install
> AssimpNet yourself.

---

## Step 3 — Add model packs

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

3. **Use the FBX version of a pack.** Packs distributed as a single `UnityFS` file
   (`.vrmmod` and similar) cannot be loaded at runtime; see the FAQ.

---

## Step 4 — Launch and verify

Start the game normally and **enter a scene where a Mita is present** (main menu → `米塔选择` →
a house). The plugin waits until a character exists; it does nothing on the main menu.

Open `BepInEx\LogOutput.log` and look for these lines:

```
[CM] target: 'MitaPerson Mita'  avatar='CrazyMitaAvatar'
[CM] packs found: 1 [...]
[CM] ===== installing 'CJMitav1.1-...' =====
[Inst] replaced 'CJ' <- part='CJ_mesh0000' ok=True bones=33 missing=0 residual=0.0000
[CM] INSTALL RESULT created=1 replaced=1 textured=1 removed=9 failed=0
```

A healthy install has **`failed=0`** and **`residual=0.0000`**.

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

Delete the three files from `BepInEx\plugins\` and the `CustomModels` folder.
Nothing outside those locations is touched — the plugin never modifies game files.
