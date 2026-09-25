# FAQ

## Nothing happens at all

Check the log at `BepInEx\LogOutput.log` first — the plugin reports what it is doing.

**1. Is the plugin even loading?** Look for:

```
[Info : BepInEx] Loading [NeuroMita.CustomModels 0.1.0]
[CM] runtime injected
```

If you see `0 plugins to load`, the plugin DLL is not in `BepInEx\plugins\`, or BepInEx itself is
not installed correctly.

**2. Are you in a scene with a character?** The plugin does nothing on the main menu. It logs:

```
[CM] waiting for a character whose Avatar name contains 'Crazy'
```

Enter a house (main menu → `米塔选择` → any Mita). You should then see `[CM] target: ...`.

**3. Are packs being found?**

```
[CM] packs found: 0 []
```

means `PackDirectory` is empty or does not exist. See [Installation](INSTALL.md) step 3.

---

## "native assimp library not found"

`assimp.dll` is missing. Copy it into `BepInEx\plugins\` next to `NeuroMita.CustomModels.dll`.

It ships in this project's release zip. If you are building from source, it comes from the
AssimpNet NuGet package at `~/.nuget/packages/assimpnet/4.1.0/runtimes/win-x64/native/assimp.dll`.

Without it, **no FBX pack can be read** — this is not optional.

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

**No.** Packs stored as a `UnityFS` AssetBundle (`.vrmmod` and similar) cannot be loaded at runtime
on this game, and this is not a missing feature — it is technically impossible from a plugin.

The game never loads AssetBundles itself (all its assets are compiled into the build), so the
subsystem is never initialised and the type is never registered. Every route was tried and all of
them fail:

| Route | Result |
|---|---|
| `AssetBundle.LoadFromFile(string)` | missing `Il2CppSystem.ReadOnlySpan<T>.GetPinnableReference()` |
| `AssetBundle.LoadFromMemory(...)` | the interop array allocation is broken |
| `AssetBundle.LoadFromStream(Stream)` | needs `Il2CppSystem.IO.Stream`, which cannot be obtained |
| native `il2cpp_runtime_invoke` | `NativeClassPtr == 0` — the class was never registered |

**Use the FBX version of the pack.** Most MiSide packs are published in FBX form.

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
