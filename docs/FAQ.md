# FAQ

## Nothing happens at all

Check the log at `BepInEx\LogOutput.log` first — the plugin reports what it is doing.

**1. Is the plugin even loading?** Look for:

```
[Info : BepInEx] Loading [NeuroMita.CustomModels 0.2.0]
[CM] runtime injected
```

If you see `0 plugins to load`, the plugin DLL is not in `BepInEx\plugins\`, or BepInEx itself is
not installed correctly.

**2. Are you in a scene with a character?** The plugin does nothing on the main menu, and while it
is waiting it says so once per character:

```
[CM] 'Crazy': character not in this scene yet (1 pack(s) waiting)
[CM]   waiting; scene has: Scene/Mita Hands/Body (active=True)
```

Enter a house (main menu → `米塔选择` → any Mita). You should then see the character being picked up:

```
[CM] ===== character 'Crazy' -> 'Mita Crazy' (1 pack(s)) =====
```

**3. Are packs being found?** Two layouts exist, and each reports differently.

With **character folders** (`CustomModels\Crazy\…`) the plugin scans per character; an empty result
looks like the waiting message above.

With the **flat layout** it lists what it found:

```
[CM] pack dir: D:\Games\NeuroMita\CustomModels
[CM] packs found: 0 []
[CM] no model packs found; nothing to do
```

`0 []` means the folder is empty, and

```
[CM] pack directory does not exist: D:\Games\NeuroMita\CustomModels
```

means `PackDirectory` points somewhere wrong. See [Installation](INSTALL.md#adding-model-packs).

---

## AssetBundle packs (`.vrmmod`) do nothing, but FBX packs work

Almost always a missing dependency. The plugin ships **six** DLLs and all of them belong in
`BepInEx\plugins\`:

```
NeuroMita.CustomModels.dll
AssetsTools.NET.dll          <- read the UnityFS container
AssetsTools.NET.Texture.dll  <- decode its textures
AssetRipper.TextureDecoder.dll
AssimpNet.dll                <- read FBX packs
assimp.dll                   <- native library for AssimpNet
```

If `AssetsTools.NET.dll` (or one of the other two AssetsTools files) is missing, the AssetBundle
path cannot load at all, so `.vrmmod` packs fail while FBX packs keep working — which makes it look
like the packs themselves are at fault.

This is the failure mode of following an older guide: earlier releases shipped only the last three
files. **Extract the release zip into the game folder** instead of copying files by hand, and the
paths sort themselves out.

---

## "native assimp library not found"

`assimp.dll` is missing. It belongs in `BepInEx\plugins\`, next to `NeuroMita.CustomModels.dll`.

It ships in this project's release zip — extracting the zip into the game folder puts it in the right
place. If you are building from source, it comes from the AssimpNet NuGet package at
`~/.nuget/packages/assimpnet/4.1.0/runtimes/win-x64/native/assimp.dll`.

Without it, **no FBX pack can be read** — this is not optional. AssetBundle packs are unaffected.

---

## Only clothes appear, no body

Symptom: the outfit renders but floats in the air, the body is invisible.

Cause: the body mesh was created as a child of a renderer that the pack then removes, so it got
hidden along with its parent.

This was fixed in the plugin (`remove` now disables the renderer component instead of deactivating
the whole GameObject, and new parts are created as siblings). **Update to the current version.**

---

## The model is rotated / facing the wrong way / twisted

The plugin derives the orientation automatically by comparing the pack's bind poses with the
game's, so this should not happen for packs built on the game's skeleton.

Check the log line for the affected part:

```
[Apply] align: triad=... bones=33 avgErr=0.0000
```

| `avgErr` | Meaning |
|---|---|
| `0.0000` | Perfect fit — geometry is correct |
| small but non-zero | Skeleton is close but not identical; probably still usable |
| large (e.g. `> 0.05`) | The two rigs are not the same, expect visible distortion |

If `avgErr` is `0.0000` but the model still looks wrong, the problem is the mesh itself
(e.g. it was exported in a non-bind pose), not the alignment.

---

## "incompatible rig: only N of M bones ... share a name"

The pack was built for a **different skeleton**. It cannot be used.

This is the most common reason a pack does not work, and it is not fixable by configuration —
the bones simply do not correspond. Typical cases:

- **Mixamo / VRM rigs** — bones named `mixamorig:Hips` instead of `Hips`
- **Chibi or accessory meshes** with only a handful of bones, none matching the game skeleton

Most MiSide model packs *are* built on the game's skeleton and work fine. If you hit this, look for
an FBX variant of the pack from the same author.

---

## The plugin supports `.vrmmod` files, right?

**Yes**, since 0.2.0.

Packs stored as a `UnityFS` AssetBundle (`.vrmmod` and similar) cannot go through Unity's own
AssetBundle API on this game — the game never loads an AssetBundle itself, so the subsystem is never
initialised and the type is never registered. Every route fails:

| Route | Result |
|---|---|
| `AssetBundle.LoadFromFile(string)` | missing `Il2CppSystem.ReadOnlySpan<T>.GetPinnableReference()` |
| `AssetBundle.LoadFromMemory(...)` | the interop array allocation is broken |
| `AssetBundle.LoadFromStream(Stream)` | needs `Il2CppSystem.IO.Stream`, which cannot be obtained |
| native `il2cpp_runtime_invoke` | `NativeClassPtr == 0` — the class was never registered |

So the plugin **parses the container itself** in managed code (AssetsTools.NET + its own vertex,
index and texture decoders) and builds the `Mesh` by hand. Nothing is loaded through Unity's
AssetBundle subsystem.

**The catch is the skeleton, not the container.** A `.vrmmod` whose bones are named after the game
skeleton installs perfectly. One built on a foreign rig (`ValveBiped.*`, `mixamorig:*`, `_N_joint_*`)
is read fine but cannot be driven — the log says
`incompatible rig: only N of M bones share a name`. Retargeting is not implemented yet.

---

## Can I replace a different character?

Yes. Change `TargetAvatar` in the config file:

| Value | Matches |
|---|---|
| `Crazy` | `CrazyMitaAvatar` |
| `Good` | `Good MitaAvatar` |
| `Mila` | Mila |
| `ShortHair` | short-haired Mita |
| `Player` | `PlayerAvatar` |

It is a case-insensitive substring match against the Animator's **Avatar** name, which is the
reliable identifier (GameObject names are not unique across scenes).

To list the available Avatars, set `Verbose = true` and check the log — the target line prints
the Avatar name it matched.

---

## Several packs in the folder — do they conflict?

Yes. Every pack found is applied in turn, and they will overwrite each other's work.

To use one specific pack, set `ActivePack` to its folder name:

```
ActivePack = Gothic-Mita-by-Index 1.3-80
```

Or keep only one pack in the folder.

---

## Does this modify my game files?

No. The plugin only touches objects in memory at runtime.

It touches nothing outside of:

- `BepInEx\` (the loader itself, plus the plugin and its config)
- `<Game>\CustomModels\` (your packs)

Removing the plugin DLL and the `CustomModels` folder restores the game completely.

---

## The log is very long

Set `Verbose = false` in the config to drop per-part alignment details. The summary line
(`INSTALL RESULT ...`) is always printed.
