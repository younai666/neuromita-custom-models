# Changelog

All notable changes to this project. Format follows [Keep a Changelog](https://keepachangelog.com/);
versioning is [Semantic Versioning](https://semver.org/).

## [0.2.0] — 2025-09-25

The headline: **AssetBundle packs now work**, and packs no longer need a config file.

### Added

- **AssetBundle (`.vrmmod` and other `UnityFS`) support.**
  Unity's own AssetBundle API is unusable on this game — it never loads an AssetBundle itself, so
  the subsystem is never initialised and the type is never registered. The plugin therefore parses
  the container in managed code with [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET)
  and builds the `Mesh` by hand.
  - read: vertices, normals, UV0/UV2, bone weights, bone indices, index buffer, bind poses, bone names
  - textures: `RGB24`, `RGBA32`, `ARGB32`, `DXT1` (BC1), `DXT5` (BC3), streamed `.resS` data, mip chains
- **Automatic slot matching.** A pack with no `addons_config.txt` is matched to the game's renderers
  by name (`Arm` → `Arms`, `Head` → `HeadPlayer`, …). If nothing matches, the plugin falls back to
  replacing the whole body and hiding the remaining parts, so a single-mesh pack still works.
- **Texture replacement** for AssetBundle packs, chosen per part (hair / body / clothing).
- **Character folders.** `CustomModels\Player\`, `CustomModels\Crazy\`, `CustomModels\Kind\`,
  `Cappie`/`Cappy`, `ShortHair`, `Mila`, `Sleepy`/`Dream`, `Ghost` — one subfolder per character,
  each subfolder inside it a pack. One install can drive several characters at once.
  - every instance of a character in the scene is handled, not just the first
  - the cache is cleared on scene change, because reloading destroys the skeleton the skin was bound to
  - without any character folder the old `TargetAvatar` + `PackDirectory` behaviour is unchanged
- **Weight repair** (`WeightRepair.cs`). Vertices that no bone drives are re-bound — to the nearest
  driven vertex, or when the whole mesh is undriven, to the nearest bone in bind pose. Such vertices
  collapse to the mesh origin under skinning, which shows up either as a long thin black spike
  dragged out of the model or as a whole part (a helmet, say) vanishing.

### Performance

- **A bundle is now parsed once per session.** Every install used to re-open the container from disk
  and re-decode its textures — a 4.8 s job for one of the sample packs — and a scene change plus the
  extra display instance paid that again and again. Parsed results are cached per path; one session
  showed 19 reuses after 2 parses. This was the cause of the multi-second freeze on every scene switch.
- **Textures are created without a mip chain.** `Texture2D(w, h, format, false)` lets the plugin skip
  `BuildMipChain` entirely: a 4096² map otherwise costs ~22M pixel operations and an 89 MB
  allocation. Decoding is unchanged; chain building drops to 0 ms.
- **The route table is cached** and the "all routes settled" early-out now runs *before* the
  directory scan. Previously `DiscoverRoutes` read the filesystem every 2 seconds, forever.
- **The scene is scanned once per tick**, not once per route, and renderer paths are built once
  instead of once per route (8 routes meant 8 full passes and 8× the path strings).
- **Bone lookup is indexed.** `FindByName` walked the whole skeleton tree per bone — O(n²); a 285-bone
  pack asked for 285 full tree walks per part. One name index per install now serves all lookups.

### Fixed

- **A long thin spike on some packs** (e.g. *Gothic Mita*'s body, which ships 4 weightless vertices).
- **A part disappearing entirely** when its mesh has no bone weights at all
  (e.g. *Master Chief*'s head — 3751 weightless vertices).
- **A model breaking after a scene change.** The skeleton the skin was bound to is destroyed and
  recreated, leaving dangling `bones` references; the route cache is now cleared per scene so the
  model is re-applied.
- **A pack's texture being applied even when its mesh failed to install.** The install failed, but the
  atlas was written onto the game's original material anyway — a character kept its own mesh and wore
  someone else's texture. Textures are now applied only after the swap succeeds.
- **One pack's texture bleeding onto every character.** Mitae share material instances, so writing
  `mainTexture` in place repainted all of them. The material is cloned per renderer first.
- **A latent crash.** `DropMissingWeights` read `mesh.boneWeights` at runtime, which takes IL2CPP down
  with `0xc0000005`. It only triggered on a pack with a bone the game lacks, so it was invisible until
  such a pack showed up. A missing bone now logs a warning instead of killing the game.
- **A part being counted as installed when it was not**, which could report "4/4 parts" for an install
  that swapped nothing.
- **A typo in the "waiting for target" guard** (`_waitoogged` → `_waitLogged`); it compiled, but the
  log line would never have printed.

### Changed

- `ModelPackage.Open` no longer refuses AssetBundle input.
- `DetectFormat` recognises a `UnityFS` file **inside** a pack directory, not only at the top level.

### Known limitations at this release

Retargeting is still not implemented, and it is now the main restriction: a pack whose bones are not
named after the game's skeleton (Mixamo `mixamorig:*`, Source `ValveBiped.*`, VRM `_N_joint_*`) is
read successfully but cannot be driven. The plugin reports
`incompatible rig: only N of M bones ... share a name` and skips that part.

## [0.1.0] — 2025-09-20

First release.

- FBX pack directory support (`*.fbx` + texture + `addons_config.txt`), the native format of
  *Miside Custom Models Loader*
- full `addons_config.txt` DSL: `replace_mesh`, `replace_tex`, `remove`, `recover`,
  `create_skinned_appendix`, `create_static_appendix`, `set_scale`, `move_position`, `set_rotation`
- alignment solved from bind poses at runtime — no hardcoded rotation
- bind poses taken from the game's own mesh
- config file and pack directory scan

[0.2.0]: https://github.com/younai666/neuromita-custom-models/releases/tag/v0.2.0
[0.1.0]: https://github.com/younai666/neuromita-custom-models/releases/tag/v0.1.0
