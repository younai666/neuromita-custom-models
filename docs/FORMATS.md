# Model pack formats

Two families of packs are found in the wild. They differ in container format, but both end up
providing the same three things: a skinned mesh, a bone list, and bind poses.

---

## 1. Unity AssetBundle (`UnityFS`)

Detected by the file header:

```
55 6E 69 74 79 46 53   "UnityFS"
```

The header also carries the Unity version the bundle was built with (e.g. `5.x.x 2021.3.35f1`).

Some packs use a custom extension such as `.vrmmod`; some have no extension at all. **The extension
is meaningless** — only the header matters.

### What is inside

Example — a VRM-derived pack:

```
container:
  assets/_tmp/model.prefab                 <- the prefab root
  assets/_tmp/bones.txt
  assets/_tmp/blendshapes.txt
  assets/_tmp/springbones.txt
  assets/_tmp/*.avatar/vrmavatar.asset
  assets/_tmp/*.meshes/default.baked.asset
  assets/_tmp/*.textures/*.png
  assets/vrm/mtoon/shaders/mtoon.shader
  assets/vrm/runtime/springbone/vrmspringbone.cs

objects:
  GameObject            x70      <- 1 root + skeleton bones
  Transform             x70
  SkinnedMeshRenderer   x1
  Mesh                  x1       (default.baked, 12403 verts)
  Avatar                x1       (VrmAvatar)
  Material              x1
  Texture2D             x1
  TextAsset             x3       (bones / blendshapes / springbones metadata)
  Shader                x2       (mtoon)
  MonoBehaviour         x1       (VRMSpringBone)
```

Another example — a Mita-skeleton pack:

```
container:
  assets/mods/dio/dio.prefab

objects:
  GameObject            x128     <- skeleton
  Transform             x128
  SkinnedMeshRenderer   x1       bones=148, mats=3
  Mesh                  x1       ('Attribute', 18814 verts, 148 bones)
  Material              x1
  Texture2D             x1       (Atlas_00001)
```

### Loading

**Unity's own loader cannot be used on this game.** It never loads an AssetBundle itself, so the
subsystem is never initialised and `AssetBundle` is never registered — every entry point fails
(missing `ReadOnlySpan.GetPinnableReference`, broken interop array allocation, no
`Il2CppSystem.IO.Stream`, `NativeClassPtr == 0`).

So `BundlePackage` reads the container in managed code instead, using **AssetsTools.NET** for the
container and serialised fields, plus its own decoders:

```
UnityFS  → blocks → AssetsFile (typetree) → GameObject/Transform/SkinnedMeshRenderer/Mesh/Texture2D
                                                │
   bone names  ← Transform.PathID → GameObject.PathID → m_Name
   mesh        ← m_VertexData (vertex streams) + m_IndexBuffer + m_BindPose
   texture     ← m_TextureFormat + m_StreamData (".resS" is read out of the container)
```

Everything ends up as plain managed arrays and is then handed to a hand-built `Mesh`. Nothing goes
through Unity's AssetBundle API.

#### Traps worth knowing

- **`ChannelInfo` has no `attribute` field** (Unity 2021). The vertex attribute id *is* the array
  index. Reading a field called `attribute` silently gives you the wrong dimension.
- **Streams are 16-byte aligned** inside `m_VertexData`. The buffer is longer than
  `stride × vertexCount`; without honouring the padding every weight and index is shifted by 8 bytes
  and the model comes out as a cloud of scattered triangles.
- **`m_IndexBuffer` is a byte vector**, not a struct vector — its `Array` node is itself a byte
  array, unlike `m_BindPose` whose `Array` node has element children.
- **Texture mip chains**: `Texture2D(int, int)` in IL2CPP creates a full mip chain, so
  `LoadRawTextureData` needs the whole chain (~89 MB for a 4096² RGBA), not just mip 0.
- **Do not touch meshes that belong to the game.** They are shared assets — clone first
  (`UnityEngine.Object.Instantiate(mesh)`).

---

## 2. FBX pack directory

This is the native format of *Miside Custom Models Loader*. A folder containing:

```
<PackName>/
├── addons_config.txt          <- the recipe
├── <Name>/<Name>.png          <- texture
└── <Name>/
    ├── <Name>Body.fbx
    ├── <Name>Hair.fbx
    └── <Name>Sweater.fbx
```

Detected by: a directory containing `addons_config.txt` and/or `*.fbx`.

### `addons_config.txt`

A small command language. Full command reference (taken from a pack's own header comments):

```
# Definition
create_static_appendix  Mita Name ParentRenderer KeyWord
create_skinned_appendix Mita Name ParentRenderer KeyWord
replace_tex             Mita Name TextureFilename  KeyWord
replace_mesh            Mita Name MeshFilename MeshName KeyWord
remove                  Mita Name KeyWords
recover                 Mita Name KeyWords
set_scale               Mita Name x y z KeyWord
move_position           Mita Name x y z KeyWords
set_rotation            Mita Name x y z w KeyWord

# Unique commands (no deactivation counterpart)
trailer | halloween | christmas

# Button structure
*ButtonName
<commands on activation>
-<commands on deactivation>
```

`KeyWord` selects which Mita variants the command applies to, by matching against the Mita's name:

- `all` (or omitted) — every variant
- `!Core` — every variant **except** those whose name contains `Core`
- multiple keywords can be listed

The Mita variant names known to the loader:

```
Usual, MitaTrue, ShortHairs, Kind, Cap, Little, Maneken, Black, Dreamer, Mila,
Creepy, Core, MitaGame, MitaPerson Mita, Dream, Future, Broke, Glasses,
MitaPerson Future, CreepyMita, Know, Longer
```

### Example

```
*GothicMita
replace_tex  Mita Sweater GothicMita\GothicMita !Little !Broke !Maneken !Black !Mila !Glasses !Creepy !Core !Longer
replace_mesh Mita Sweater GothicMita\GothicMitaSweater GothicMitaSweater !Little ...
replace_tex  Mita Clothes GothicMita\GothicMita !Little ...
create_skinned_appendix Mita GothicMitaBody Body !Little ...
replace_mesh Mita GothicMitaBody GothicMita\GothicMitaBody GothicMitaBody !Little ...
remove  Mita Body   !Little ...
remove  Mita Head   !Little ...
remove  Mita Hairs  !Little ...
-remove  Mita GothicMita
-recover Mita Body
-recover Mita Head
```

Reading it: *create a new skinned renderer named `GothicMitaBody` parented to `Body`, give it the
mesh from `GothicMitaBody.fbx`, texture it with `GothicMita.png`, and hide the original
`Body` / `Head` / `Hairs` renderers.* The `-` lines describe how to undo it.

`Name` in `replace_mesh Mita <Name> ...` refers to the **renderer slot name** on the character
(`Body`, `Head`, `Hair`, `Hairs`, `Sweater`, `Skirt`, `Shoes`, `Pantyhose`, `Clothes`, `Gloves`,
`Attribute`).

---

## Skeleton families

Both families are Unity **Humanoid** rigs, but only the first currently works with this plugin.

### A. Game-native naming (supported)

Bone names match the game's own skeleton, so mapping by name works directly:

```
RootNode
└── Mita Skeleton
    ├── Hips
    │   ├── Left leg → Left knee → Left ankle → Left toe
    │   ├── Right leg → Right knee → Right ankle → Right toe
    │   └── Spine → Chest → Neck2 → Neck1 → Head
    │       ├── Left Eye / Right Eye / Left_Eye_Track / Right_Eye_Track
    │       ├── Left shoulder → Left arm → Left elbow → Left wrist → fingers
    │       └── Right shoulder → Right arm → Right elbow → Right wrist → fingers
```

Finger bones: `IndexFinger1-3`, `MiddleFinger1-3`, `RingFinger1-3`, `Thumb0-2`, suffixed `_L` / `_R`.
Some packs add twist bones (`TwistForearm.L`, `TwistForearm1.R`, …).

The skeleton root node name varies between packs but the bones below it do not:

| Pack | Root node |
|---|---|
| CJ Mita | `Mita Skeleton` |
| Gothic Mita (body/sweater) | `Mita Skeleton no hair` |
| Gothic Mita (hair) | `Mita Skeleton Hair` |
| Jojo Dio | (bones directly: `Hips`, `Right toe`, `TwistForearm.L`, …) |

Bone counts also vary per mesh — the mesh only lists the bones it actually uses:

| Mesh | Bones |
|---|---|
| game `Body` | 148 |
| game `Head` / `Hair` / `Attribute` | 173 |
| Dio `Attribute` | 148 |
| CJ `CJ_mesh0000` | 33 |

### B. Humanoid, foreign naming (not yet supported)

Example: a VRM pack using Mixamo names:

```
mixamorig:Hips
mixamorig:LeftFoot / LeftLeg / LeftUpLeg
mixamorig:LeftHand / LeftForeArm / LeftShoulder
mixamorig:RightHandThumb1-4 / RightHandIndex1-4 / ...
HeadTop_End
```

These carry a `VrmAvatar` (a Humanoid avatar), so a semantic mapping through
`HumanBodyBones` is possible in principle — that is on the roadmap, not implemented yet.

---

## Game-side reference

Character renderer slots observed on Crazy Mita (`MitaCore (Start)/Mitas/Mita Crazy/MitaPerson Mita`,
Animator avatar `CrazyMitaAvatar`):

| Renderer | Mesh | Bones | Materials |
|---|---|---|---|
| `Head` | `Head_1` | 173 | 2 |
| `FaceLayer` | `FaceLayer` | 173 | 1 |
| `Hairs` | `Hair` | 173 | 1 |
| `SweaterSlot` | `Sweater_1` | 173 | 1 |
| `SkirtSlot` | `Skirt` | 148 | 1 |
| `ShoesSlot` | `Shoes` | 148 | 1 |
| `PantyhoseSlot` | `Pantyhose` | 148 | 1 |
| `BodySlot` | `Body` | 148 | 1 |
| `AttributeSlot` | `Attribute` | 173 | 1 |

Character material shader: `RealToon/Version 5/Default/Default`.

> `FaceLayer` carries the school-uniform collar in addition to the face, and its collar bones are
> parented to the chest/shoulders — hiding or keeping it is a trade-off between a clean silhouette
> and working facial expressions.
