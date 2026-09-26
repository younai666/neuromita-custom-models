using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityEngine;

namespace NeuroMita.CustomModels
{
    /// <summary>
    /// Unity AssetBundle（.vrmmod / 无扩展名等）模型包。
    ///
    /// 为什么自己解析而不是让 Unity 加载：
    /// 这个游戏从不加载 AssetBundle，该子系统从未初始化、类型从未注册，
    /// 运行时的 AssetBundle.LoadFromFile / FromMemory / FromStream 全部不可用。
    /// 所以这里直接在托管侧解析 UnityFS 容器和序列化数据。
    ///
    /// 只依赖 AssetsTools.NET（MIT）做容器与字段解析，顶点/索引按 channel 布局自己解。
    /// </summary>
    public sealed class BundlePackage : ModelPackage
    {
        private readonly List<ModelPart> _parts = new List<ModelPart>();
        private readonly Dictionary<string, UnityEngine.Texture2D> _textures =
            new Dictionary<string, UnityEngine.Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, UnityEngine.Texture2D> _texturesByPathId = new Dictionary<long, UnityEngine.Texture2D>();
        private readonly Dictionary<long, long> _mainTextureByMaterialPathId = new Dictionary<long, long>();
        private AssetsManager _am;
        private BundleFileInstance _bun;
        private AssetsFileInstance _inst;

        /// <summary>包里解出来的贴图（名字 -> 纹理），供装配时替换材质贴图。</summary>
        public Dictionary<string, UnityEngine.Texture2D> Textures => _textures;

        public override List<ModelPart> Parts => _parts;

        /// <summary>RootPath 可能是 bundle 文件本身，也可能是一个装着 bundle 的目录。</summary>
        private static string ResolveBundleFile(string path)
        {
            try
            {
                if (File.Exists(path)) return ModelPackage.LooksLikeBundle(path) ? path : null;
                if (!Directory.Exists(path)) return null;
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    if (ModelPackage.LooksLikeBundle(f)) return f;
            }
            catch { }
            return null;
        }

        // ---------- Unity 顶点属性/格式枚举 ----------
        private static readonly string[] ATTR_NAMES =
        {
            "Position","Normal","Tangent","Color","TexCoord0","TexCoord1","TexCoord2","TexCoord3",
            "TexCoord4","TexCoord5","TexCoord6","TexCoord7","BlendWeight","BlendIndices"
        };

        // VertexAttributeFormat: 0 Float32, 1 Float16, 2 UNorm8, 3 SNorm8,
        //                        4 UNorm16, 5 SNorm16, 6 UInt8, 7 SInt8, 8 UInt16, 9 SInt16, 10 UInt32, 11 SInt32
        private static int FormatSize(int fmt, int dim)
        {
            switch (fmt)
            {
                case 0: return 4 * dim;
                case 1: return 2 * dim;
                case 2: case 3: case 6: case 7: return dim;
                case 4: case 5: case 8: case 9: return 2 * dim;
                default: return 4 * dim;
            }
        }

        // ---------- 字段访问小工具（索引器在这个库上不可靠，一律按名遍历） ----------
        private static AssetTypeValueField F(AssetTypeValueField p, string n)
        {
            if (p == null || p.Children == null) return null;
            foreach (var c in p.Children) if (c != null && c.FieldName == n) return c;
            return null;
        }
        private static int I(AssetTypeValueField p, string n) { var f = F(p, n); try { return f == null ? -1 : f.AsInt; } catch { return -1; } }
        private static long PathId(AssetTypeValueField pointer) { var f = F(pointer, "m_PathID"); try { return f == null ? 0 : f.AsLong; } catch { return 0; } }
        private static string S(AssetTypeValueField p, string n) { var f = F(p, n); try { return f == null ? null : f.AsString; } catch { return null; } }

        /// <summary>vector 的 Array 元素列表（用于 SubMesh / Matrix4x4 这类结构数组）。</summary>
        private static List<AssetTypeValueField> ElemList(AssetTypeValueField vectorField)
        {
            var r = new List<AssetTypeValueField>();
            var arr = F(vectorField, "Array");
            if (arr == null) return r;
            if (arr.Children != null) foreach (var c in arr.Children) if (c != null) r.Add(c);
            return r;
        }

        /// <summary>vector&lt;UInt8&gt; 的字节（索引缓冲是这种形状）。</summary>
        private static byte[] ElemBytes(AssetTypeValueField vectorField)
        {
            var arr = F(vectorField, "Array");
            if (arr == null) return null;
            try { return arr.AsByteArray; } catch { return null; }
        }

        // ---------- 打开 ----------
        public override bool Open()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var file = ResolveBundleFile(RootPath);
                if (file == null)
                {
                    Logging.Error("[Bundle] no UnityFS file found under: " + RootPath);
                    return false;
                }
                if (!string.Equals(file, RootPath, StringComparison.OrdinalIgnoreCase))
                    Logging.Verbose($"[Bundle] bundle file: {Path.GetFileName(file)}");

                _am = new AssetsManager();
                var swStep = System.Diagnostics.Stopwatch.StartNew();
                _bun = _am.LoadBundleFile(file, true);
                long msLoad = swStep.ElapsedMilliseconds;
                if (_bun == null) { Logging.Error("[Bundle] LoadBundleFile returned null: " + file); return false; }

                int fileCount = _bun.file.BlockAndDirInfo.DirectoryInfos.Count;
                Logging.Verbose($"[Bundle] container files = {fileCount}");

                swStep.Restart();
                _inst = _am.LoadAssetsFileFromBundle(_bun, 0, true);
                long msAssets = swStep.ElapsedMilliseconds;
                if (_inst == null) { Logging.Error("[Bundle] no assets file inside the bundle"); return false; }

                Logging.Info($"[Bundle] unity={_inst.file.Metadata.UnityVersion} assets={_inst.file.AssetInfos.Count}");

                swStep.Restart(); BuildNameIndex();      long msIdx = swStep.ElapsedMilliseconds;
                swStep.Restart(); CollectMeshes();       long msMesh = swStep.ElapsedMilliseconds;
                swStep.Restart(); CollectTextures();     long msTex = swStep.ElapsedMilliseconds;
                swStep.Restart(); CollectMaterialTextureLinks(); long msMat = swStep.ElapsedMilliseconds;
                Logging.Verbose($"[PERF] open '{Path.GetFileName(file)}' total={sw.ElapsedMilliseconds}ms " +
                             $"[loadBundle={msLoad} loadAssets={msAssets} nameIndex={msIdx} " +
                             $"meshes={msMesh} textures={msTex} materialLinks={msMat}] " +
                             $"{_parts.Count} parts, {_textures.Count} textures");
                return _parts.Count > 0;
            }
            catch (Exception e)
            {
                Logging.Error("[Bundle] open failed: " + e);
                return false;
            }
        }

        // ---------- PathID -> GameObject 名（骨架名要靠它，避免 PPtr 解析的坑） ----------
        private readonly Dictionary<long, string> _goNames = new Dictionary<long, string>();
        private readonly Dictionary<long, long> _tfToGo = new Dictionary<long, long>();

        private void BuildNameIndex()
        {
            foreach (var inf in _inst.file.AssetInfos)
            {
                try
                {
                    if (inf.TypeId == 1)          // GameObject
                    {
                        var g = _am.GetBaseField(_inst, inf);
                        var n = S(g, "m_Name");
                        if (n != null) _goNames[inf.PathId] = n;
                    }
                    else if (inf.TypeId == 4)     // Transform
                    {
                        var t = _am.GetBaseField(_inst, inf);
                        var go = F(t, "m_GameObject");
                        if (go != null) _tfToGo[inf.PathId] = PathId(go);
                    }
                }
                catch { }
            }
            Logging.Verbose($"[Bundle] name index: {_goNames.Count} gameobjects, {_tfToGo.Count} transforms");
        }

        private string BoneNameOf(AssetTypeValueField pptr)
        {
            long pid = PathId(pptr);
            long goId;
            if (_tfToGo.TryGetValue(pid, out goId))
            {
                string nm;
                if (_goNames.TryGetValue(goId, out nm)) return nm;
            }
            return null;
        }

        // ---------- 遍历网格 ----------
        private void CollectMeshes()
        {
            var swPass = System.Diagnostics.Stopwatch.StartNew();
            // mesh PathID -> 它的 SkinnedMeshRenderer（用来取骨骼名）
            var smrByMesh = new Dictionary<long, AssetTypeValueField>();
            foreach (var inf in _inst.file.AssetInfos)
            {
                if (inf.TypeId != 137) continue;   // SkinnedMeshRenderer
                try
                {
                    var smr = _am.GetBaseField(_inst, inf);
                    var mm = F(smr, "m_Mesh");
                    if (mm == null) continue;
                    long mp = PathId(mm);
                    if (mp != 0 && !smrByMesh.ContainsKey(mp))
                    {
                        smrByMesh[mp] = smr;
                    }
                }
                catch { }
            }

            Logging.Verbose($"[PERF]     smrPass={swPass.ElapsedMilliseconds}ms");
            swPass.Restart();
            foreach (var inf in _inst.file.AssetInfos)
            {
                if (inf.TypeId != 43) continue;    // Mesh
                try
                {
                    var mf = _am.GetBaseField(_inst, inf);
                    var part = BuildPart(mf, smrByMesh.ContainsKey(inf.PathId) ? smrByMesh[inf.PathId] : null);
                    if (part != null && part.Mesh != null && part.Mesh.vertexCount > 0)
                    {
                        _parts.Add(part);
                        Logging.Verbose($"[Bundle]   part '{part.Name}' verts={part.Mesh.vertexCount} bones={part.BoneNames.Length}");
                    }
                }
                catch (Exception e)
                {
                    Logging.Warn("[Bundle] mesh build failed: " + e.Message);
                }
                Logging.Verbose($"[PERF]     meshPass={swPass.ElapsedMilliseconds}ms");
            }
        }

        // ---------- 贴图 ----------
        private void CollectTextures()
        {
            foreach (var inf in _inst.file.AssetInfos)
            {
                if (inf.TypeId != 28) continue;   // Texture2D
                try
                {
                    var bf = _am.GetBaseField(_inst, inf);
                    var tf = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(bf);
                    string nm = tf.m_Name ?? ("tex" + inf.PathId);

                    // 数据可能内联，也可能流式存在 bundle 的 .resS 里
                    byte[] data = tf.pictureData;
                    if (data == null || data.Length == 0)
                        data = ReadFromStreamData(bf);
                    if (data == null || data.Length == 0)
                    {
                        Logging.Warn($"[Bundle] texture '{nm}': no data");
                        continue;
                    }

                    int w = tf.m_Width, h = tf.m_Height;
                    int fmtId = tf.m_TextureFormat;

                    // 解码成 RGBA32（数据可能是 DXT 压缩 + mipmap 链）
                    var swDec = System.Diagnostics.Stopwatch.StartNew();
                    byte[] rgba = DecodeToRgba32(data, w, h, fmtId);
                    if (rgba == null) rgba = tf.DecodeTextureRaw(data, false);
                    long msDec = swDec.ElapsedMilliseconds;
                    if (rgba == null)
                    {
                        Logging.Warn($"[Bundle] texture '{nm}': unsupported format {fmtId}");
                        continue;
                    }

                    // 不带 mipmap 创建贴图：可以省掉整条 mip 链的构建
                    // （4096² 的链要算 2200 万像素并分配约 89MB，是切场景卡顿的主因之一）。
                    UnityEngine.Texture2D tex = null;
                    try { tex = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false); } catch { }
                    if (tex == null) { try { tex = new UnityEngine.Texture2D(w, h); } catch { } }
                    if (tex == null) { Logging.Warn($"[Bundle] texture '{nm}': 无法创建 Texture2D"); continue; }
                    tex.name = nm;
                    _texturesByPathId[inf.PathId] = tex;

                    int levels = 1;
                    try { levels = tex.mipmapCount; } catch { }
                    if (levels < 1) levels = 1;

                    var swMip = System.Diagnostics.Stopwatch.StartNew();
                    var payload = levels > 1 ? BuildMipChain(rgba, w, h, levels) : rgba;
                    long msMip = swMip.ElapsedMilliseconds;

                    tex.LoadRawTextureData(payload);
                    tex.Apply();
                    _textures[nm] = tex;
                    Logging.Verbose($"[PERF] tex '{nm}' {w}x{h} fmt={fmtId} mips={levels} " +
                                 $"decode={msDec}ms mip={msMip}ms payload={payload.Length / 1048576.0:F1}MB");
                }
                catch (Exception e)
                {
                    Logging.Warn("[Bundle] texture failed: " + e.Message);
                }
            }
        }

        private void CollectMaterialTextureLinks()
        {
            foreach (var inf in _inst.file.AssetInfos)
            {
                if (inf.TypeId != 21) continue;
                try
                {
                    var material = _am.GetBaseField(_inst, inf);
                    var saved = F(material, "m_SavedProperties");
                    var texEnvs = ElemList(F(saved, "m_TexEnvs"));
                    foreach (var entry in texEnvs)
                    {
                        var propertyName = S(entry, "first");
                        if (string.IsNullOrEmpty(propertyName)) continue;
                        var texturePathId = PathId(F(F(entry, "second"), "m_Texture"));
                        if (texturePathId == 0) continue;
                        if (string.Equals(propertyName, "_MainTex", StringComparison.Ordinal) ||
                            (string.Equals(propertyName, "_BaseMap", StringComparison.Ordinal) &&
                             !_mainTextureByMaterialPathId.ContainsKey(inf.PathId)))
                            _mainTextureByMaterialPathId[inf.PathId] = texturePathId;
                    }
                }
                catch { }
            }
            Logging.Verbose($"[Bundle] main-texture links: {_mainTextureByMaterialPathId.Count} materials");
        }

        public UnityEngine.Texture2D GetSourceTexture(ModelPart part)
        {
            if (part == null || part.SourceMaterialPathId == 0) return null;
            return _mainTextureByMaterialPathId.TryGetValue(part.SourceMaterialPathId, out var texturePathId) &&
                   _texturesByPathId.TryGetValue(texturePathId, out var texture) ? texture : null;
        }

        // ---------- 从 bundle 的 .resS 流里取贴图数据 ----------
        private byte[] ReadFromStreamData(AssetTypeValueField textureField)
        {
            try
            {
                var sd = F(textureField, "m_StreamData");
                if (sd == null) return null;
                var pf = F(sd, "path");
                var of = F(sd, "offset");
                var sf = F(sd, "size");
                if (pf == null || sf == null) return null;

                string path = pf.AsString;
                long offset = 0, size = 0;
                try { if (of != null) offset = of.AsLong; } catch { try { if (of != null) offset = (long)of.AsULong; } catch { } }
                try { size = sf.AsLong; } catch { try { size = (long)sf.AsULong; } catch { } }

                if (string.IsNullOrEmpty(path) || size <= 0) return null;

                string entryName = path.Substring(path.LastIndexOf('/') + 1);
                var dir = _bun.file.BlockAndDirInfo.DirectoryInfos;
                for (int i = 0; i < dir.Count; i++)
                {
                    if (!string.Equals(dir[i].Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;

                    var reader = _bun.file.DataReader;
                    reader.Position = dir[i].Offset + offset;
                    var buf = reader.ReadBytes((int)size);
                    Logging.Verbose($"[Bundle]   resS '{entryName}' @{dir[i].Offset}+{offset} size={size} -> {buf.Length} B");
                    return buf;
                }
                Logging.Warn($"[Bundle] resS entry not found: {entryName}");
            }
            catch (Exception e) { Logging.Warn("[Bundle] ReadFromStreamData failed: " + e.Message); }
            return null;
        }

        // ---------- 贴图解码 ----------
        /// <summary>把各种 Unity 贴图格式解成 RGBA32（只取 mip 0）。</summary>
        private static byte[] DecodeToRgba32(byte[] data, int w, int h, int fmtId)
        {
            if (data == null || w <= 0 || h <= 0) return null;
            try
            {
                switch (fmtId)
                {
                    case 4:   // RGBA32
                        {
                            int need = w * h * 4;
                            if (data.Length < need) return null;
                            var outb = new byte[need];
                            Array.Copy(data, 0, outb, 0, need);
                            return outb;
                        }
                    case 3:   // RGB24
                        {
                            var outb = new byte[w * h * 4];
                            for (int i = 0, j = 0; i < w * h; i++, j += 3)
                            {
                                outb[i * 4 + 0] = data[j + 0];
                                outb[i * 4 + 1] = data[j + 1];
                                outb[i * 4 + 2] = data[j + 2];
                                outb[i * 4 + 3] = 255;
                            }
                            return outb;
                        }
                    case 10:  // DXT1 / BC1
                        return DecodeDxt1(data, w, h);
                    case 12:  // DXT5 / BC3
                        return DecodeDxt5(data, w, h);
                    case 5:   // ARGB32
                        {
                            var outb = new byte[w * h * 4];
                            for (int i = 0; i < w * h; i++)
                            {
                                outb[i * 4 + 0] = data[i * 4 + 1];
                                outb[i * 4 + 1] = data[i * 4 + 2];
                                outb[i * 4 + 2] = data[i * 4 + 3];
                                outb[i * 4 + 3] = data[i * 4 + 0];
                            }
                            return outb;
                        }
                    default:
                        return null;
                }
            }
            catch (Exception e)
            {
                Logging.Warn($"[Bundle] decode fmt={fmtId} failed: {e.Message}");
                return null;
            }
        }

        private static byte[] Rgb565(ushort c)
        {
            int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
            return new byte[]
            {
                (byte)((r << 3) | (r >> 2)),
                (byte)((g << 2) | (g >> 4)),
                (byte)((b << 3) | (b >> 2)),
                255
            };
        }

        private static byte[] Mix(byte[] a, byte[] b, int wa, int wb, int div)
        {
            return new byte[]
            {
                (byte)((a[0]*wa + b[0]*wb) / div),
                (byte)((a[1]*wa + b[1]*wb) / div),
                (byte)((a[2]*wa + b[2]*wb) / div),
                255
            };
        }

        /// <summary>DXT1 / BC1 解码（每 8 字节一个 4x4 块）。</summary>
        private static byte[] DecodeDxt1(byte[] src, int w, int h)
        {
            var dst = new byte[w * h * 4];
            int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
            int off = 0;
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    if (off + 8 > src.Length) return dst;
                    ushort c0 = BitConverter.ToUInt16(src, off);
                    ushort c1 = BitConverter.ToUInt16(src, off + 2);
                    uint bits = BitConverter.ToUInt32(src, off + 4);
                    off += 8;

                    var col = new byte[4][];
                    col[0] = Rgb565(c0);
                    col[1] = Rgb565(c1);
                    if (c0 > c1)
                    {
                        col[2] = Mix(col[0], col[1], 2, 1, 3);
                        col[3] = Mix(col[0], col[1], 1, 2, 3);
                    }
                    else
                    {
                        col[2] = Mix(col[0], col[1], 1, 1, 2);
                        col[3] = new byte[] { 0, 0, 0, 0 };
                    }

                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int px = bx * 4 + x, py = by * 4 + y;
                            if (px >= w || py >= h) continue;
                            int ci = (int)((bits >> (2 * (y * 4 + x))) & 3);
                            var c = col[ci];
                            int o = (py * w + px) * 4;
                            dst[o] = c[0]; dst[o + 1] = c[1]; dst[o + 2] = c[2]; dst[o + 3] = c[3];
                        }
                    }
                }
            }
            return dst;
        }

        /// <summary>DXT5 / BC3 解码。</summary>
        private static byte[] DecodeDxt5(byte[] src, int w, int h)
        {
            var dst = new byte[w * h * 4];
            int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
            int off = 0;
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    if (off + 16 > src.Length) return dst;
                    // alpha 块（8 字节）
                    byte a0 = src[off], a1 = src[off + 1];
                    ulong abits = 0;
                    for (int i = 0; i < 6; i++) abits |= (ulong)src[off + 2 + i] << (8 * i);
                    var alphas = new byte[8];
                    alphas[0] = a0; alphas[1] = a1;
                    if (a0 > a1)
                        for (int i = 1; i <= 6; i++) alphas[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                    else
                    {
                        for (int i = 1; i <= 4; i++) alphas[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                        alphas[6] = 0; alphas[7] = 255;
                    }

                    ushort c0 = BitConverter.ToUInt16(src, off + 8);
                    ushort c1 = BitConverter.ToUInt16(src, off + 10);
                    uint cbits = BitConverter.ToUInt32(src, off + 12);
                    off += 16;

                    var col = new byte[4][];
                    col[0] = Rgb565(c0);
                    col[1] = Rgb565(c1);
                    col[2] = Mix(col[0], col[1], 2, 1, 3);
                    col[3] = Mix(col[0], col[1], 1, 2, 3);

                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int px = bx * 4 + x, py = by * 4 + y;
                            if (px >= w || py >= h) continue;
                            int pi = y * 4 + x;
                            int ci = (int)((cbits >> (2 * pi)) & 3);
                            int ai = (int)((abits >> (3 * pi)) & 7);
                            var c = col[ci];
                            int o = (py * w + px) * 4;
                            dst[o] = c[0]; dst[o + 1] = c[1]; dst[o + 2] = c[2]; dst[o + 3] = alphas[ai];
                        }
                    }
                }
            }
            return dst;
        }

        /// <summary>
        /// IL2CPP 只暴露 Texture2D(w,h)，它自带 mipmap 链，
        /// 因此必须把整条链的数据补齐（逐级 2x2 平均下采样）。
        /// </summary>
        private static byte[] BuildMipChain(byte[] mip0, int w, int h, int levels)
        {
            int total = 0, cw = w, ch = h;
            for (int i = 0; i < levels; i++)
            {
                total += cw * ch * 4;
                if (cw == 1 && ch == 1) break;
                cw = Math.Max(1, cw / 2); ch = Math.Max(1, ch / 2);
            }

            var dst = new byte[total];
            int dstOff = 0;
            var cur = mip0;
            cw = w; ch = h;
            for (int i = 0; i < levels; i++)
            {
                int len = cw * ch * 4;
                int copy = Math.Min(len, cur.Length);
                Array.Copy(cur, 0, dst, dstOff, copy);
                dstOff += len;
                if (cw == 1 && ch == 1) break;

                int nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2);
                var next = new byte[nw * nh * 4];
                for (int y = 0; y < nh; y++)
                {
                    for (int x = 0; x < nw; x++)
                    {
                        int s0 = ((y * 2) * cw + (x * 2)) * 4;
                        int s1 = ((y * 2) * cw + Math.Min(cw - 1, x * 2 + 1)) * 4;
                        int s2 = (Math.Min(ch - 1, y * 2 + 1) * cw + (x * 2)) * 4;
                        int s3 = (Math.Min(ch - 1, y * 2 + 1) * cw + Math.Min(cw - 1, x * 2 + 1)) * 4;
                        int d = (y * nw + x) * 4;
                        for (int k = 0; k < 4; k++)
                            next[d + k] = (byte)((cur[s0 + k] + cur[s1 + k] + cur[s2 + k] + cur[s3 + k]) / 4);
                    }
                }
                cur = next; cw = nw; ch = nh;
            }
            return dst;
        }

        // ---------- 单个网格 ----------
        private ModelPart BuildPart(AssetTypeValueField mf, AssetTypeValueField smr)
        {
            string name = S(mf, "m_Name") ?? "mesh";
            var vd = F(mf, "m_VertexData");
            if (vd == null) return null;

            int vcount = I(vd, "m_VertexCount");
            if (vcount <= 0) return null;

            var dsF = F(vd, "m_DataSize");
            byte[] vraw = dsF != null ? dsF.AsByteArray : null;
            if (vraw == null || vraw.Length == 0) { Logging.Warn($"[Bundle] '{name}': no vertex bytes"); return null; }

            // ---- 通道布局 ----
            var chans = ElemList(F(vd, "m_Channels"));
            var stride = new Dictionary<int, int>();
            var layout = new List<int[]>();   // {stream, offset, format, dimension, attribute}
            bool hasUvChannel = false;
            for (int i = 0; i < chans.Count; i++)
            {
                var c = chans[i];
                if (c == null || c.Children == null || c.Children.Count < 4) continue;
                int st = I(c, "stream");
                int off = I(c, "offset");
                int fmt = I(c, "format");
                int dim = I(c, "dimension");
                if (dim <= 0) continue;
                if (i == 4)
                {
                    hasUvChannel = true;
                    if (fmt != 0 && fmt != 1)
                        Logging.Warn($"[Bundle] '{name}': unsupported UV0 format {fmt}");
                }
                // 注意：ChannelInfo 没有 attribute 字段，属性号就是数组下标本身
                layout.Add(new[] { st, off, fmt, dim, i });
                int end = off + FormatSize(fmt, dim);
                if (!stride.ContainsKey(st) || stride[st] < end) stride[st] = end;
            }
            if (!hasUvChannel) Logging.Warn($"[Bundle] '{name}': no UV0 channel");

            // stream 在缓冲区里首尾相接，且每个 stream 按 16 字节对齐
            var streamBase = new Dictionary<int, int>();
            var streamOrder = new List<int>(stride.Keys);
            streamOrder.Sort();
            bool hasBlendWeights = false, hasSingleBlendIndex = false;
            foreach (var channel in layout)
            {
                if (channel[4] == 12) hasBlendWeights = true;
                if (channel[4] == 13 && channel[3] == 1) hasSingleBlendIndex = true;
            }
            bool rigidSkin = !hasBlendWeights && hasSingleBlendIndex;
            if (rigidSkin) Logging.Verbose($"[Bundle] {name}: single-bone vertices use implicit unit weights");
            int acc = 0;
            foreach (var s in streamOrder)
            {
                streamBase[s] = acc;
                acc += stride[s] * vcount;
                acc = (acc + 15) & ~15;      // Unity 顶点缓冲按 16 字节对齐
            }

            if (acc > vraw.Length)
                Logging.Warn($"[Bundle] '{name}': layout needs {acc} B but only {vraw.Length} B present (will clamp)");

            // ---- 逐顶点取值 ----
            var verts = new Vector3[vcount];
            var norms = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            var weights = new BoneWeight[vcount];
            bool hasNormal = false, hasUv = false;

            var swV = System.Diagnostics.Stopwatch.StartNew();
            for (int v = 0; v < vcount; v++)
            {
                float w0 = 0, w1 = 0, w2 = 0, w3 = 0;
                int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
                bool readRigidIndex = false;

                for (int li = 0; li < layout.Count; li++)
                {
                    var L = layout[li];
                    int st = L[0], off = L[1], fmt = L[2], dim = L[3], attr = L[4];
                    if (!stride.ContainsKey(st)) continue;
                    int baseOff = streamBase[st] + v * stride[st] + off;
                    if (baseOff + FormatSize(fmt, dim) > vraw.Length) continue;

                    if (attr != 13 && fmt != 0 && fmt != 1)
                    {
                        if (li == 0) Logging.Warn($"[Bundle] '{name}': non-float vertex format {fmt} on attr {attr}, skipped");
                        continue;
                    }

                    switch (attr)
                    {
                        case 0:   // Position
                            if (dim >= 3) verts[v] = new Vector3(R(vraw, baseOff, fmt), R(vraw, baseOff + FormatSize(fmt, 1), fmt), R(vraw, baseOff + FormatSize(fmt, 2), fmt));
                            break;
                        case 1:   // Normal
                            if (dim >= 3) { norms[v] = new Vector3(R(vraw, baseOff, fmt), R(vraw, baseOff + FormatSize(fmt, 1), fmt), R(vraw, baseOff + FormatSize(fmt, 2), fmt)); hasNormal = true; }
                            break;
                        case 4:   // TexCoord0
                            if (dim >= 2) { uvs[v] = new Vector2(R(vraw, baseOff, fmt), R(vraw, baseOff + FormatSize(fmt, 1), fmt)); hasUv = true; }
                            break;
                        case 12:  // BlendWeight
                            if (dim >= 1) w0 = R(vraw, baseOff, fmt);
                            if (dim >= 2) w1 = R(vraw, baseOff + FormatSize(fmt, 1), fmt);
                            if (dim >= 3) w2 = R(vraw, baseOff + FormatSize(fmt, 2), fmt);
                            if (dim >= 4) w3 = R(vraw, baseOff + FormatSize(fmt, 3), fmt);
                            break;
                        case 13:  // BlendIndices（可能是 UInt8 / UInt16 / UInt32）
                            {
                                int sz = FormatSize(fmt, 1);
                                if (dim >= 1) { b0 = Idx(vraw, baseOff + sz * 0, fmt); readRigidIndex = true; }
                                if (dim >= 2) b1 = Idx(vraw, baseOff + sz * 1, fmt);
                                if (dim >= 3) b2 = Idx(vraw, baseOff + sz * 2, fmt);
                                if (dim >= 4) b3 = Idx(vraw, baseOff + sz * 3, fmt);
                            }
                            break;
                    }
                }

                if (rigidSkin && readRigidIndex) w0 = 1f;

                weights[v] = new BoneWeight
                {
                    weight0 = w0, weight1 = w1, weight2 = w2, weight3 = w3,
                    boneIndex0 = b0, boneIndex1 = b1, boneIndex2 = b2, boneIndex3 = b3
                };
            }

            // ---- 索引 ----
            swV.Stop();
            var swI = System.Diagnostics.Stopwatch.StartNew();
            byte[] ibraw = ElemBytes(F(mf, "m_IndexBuffer"));
            int indexFormat = I(mf, "m_IndexFormat");   // 0 = UInt16, 1 = UInt32
            var tris = ReadIndices(ibraw, indexFormat, F(mf, "m_SubMeshes"), vcount);
            swI.Stop();
            var swB = System.Diagnostics.Stopwatch.StartNew();

            // ---- bindpose ----
            var bpList = ElemList(F(mf, "m_BindPose"));
            var bindposes = new Matrix4x4[bpList.Count];
            for (int i = 0; i < bpList.Count; i++) bindposes[i] = ReadMatrix(bpList[i]);

            // ---- 骨骼名（来自引用它的 SkinnedMeshRenderer） ----
            swB.Stop();
            var swN = System.Diagnostics.Stopwatch.StartNew();
            string[] boneNames = ReadBoneNames(smr, bindposes.Length);
            swN.Stop();

            // ---- 建 Mesh ----
            var mesh = new Mesh();
            mesh.name = name;
            try { mesh.indexFormat = verts.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16; } catch { }
            mesh.vertices = verts;
            if (hasNormal) mesh.normals = norms; else mesh.RecalculateNormals();
            if (hasUv) mesh.uv = uvs;
            // 通用权重清理：补上孤儿顶点并归一化（与 FBX 路线同一个修复）
            int repaired = WeightRepair.Fix(verts, weights, bindposes);
            if (repaired > 0)
                Logging.Verbose($"[Bundle] {name}: repaired {repaired} weightless vertex/vertices");

            var swM = System.Diagnostics.Stopwatch.StartNew();
            mesh.boneWeights = weights;
            mesh.bindposes = bindposes;
            if (tris != null && tris.Length > 0) mesh.triangles = tris;
            mesh.RecalculateBounds();

            Logging.Verbose(string.Format(
                "[Bundle] {0}: verts={1} tris={2} bones={3} bounds={4}",
                name, verts.Length, tris == null ? 0 : tris.Length, boneNames.Length, mesh.bounds));

            return new ModelPart
            {
                Name = name,
                Mesh = mesh,
                BoneNames = boneNames,
                Bindposes = bindposes,
                SourceMaterialPathId = smr != null && ElemList(F(smr, "m_Materials")).Count > 0
                    ? PathId(ElemList(F(smr, "m_Materials"))[0]) : 0,
                SourceFile = RootPath
            };
        }

        private static float R(byte[] b, int o, int fmt)
        {
            if (fmt == 0) return BitConverter.ToSingle(b, o);
            ushort bits = BitConverter.ToUInt16(b, o);
            int exponent = (bits >> 10) & 31;
            int mantissa = bits & 1023;
            float magnitude = exponent == 0
                ? (float)(mantissa * Math.Pow(2, -24))
                : exponent == 31
                    ? (mantissa == 0 ? float.PositiveInfinity : float.NaN)
                    : (float)((1 + mantissa / 1024.0) * Math.Pow(2, exponent - 15));
            return (bits & 0x8000) != 0 ? -magnitude : magnitude;
        }

        /// <summary>按 VertexAttributeFormat 读一个骨骼索引。</summary>
        private static int Idx(byte[] b, int o, int fmt)
        {
            try
            {
                switch (fmt)
                {
                    case 2: case 6: return b[o];                            // UNorm8 / UInt8
                    case 3: case 7: return (sbyte)b[o];                     // SNorm8 / SInt8
                    case 4: case 8: return BitConverter.ToUInt16(b, o);     // UNorm16 / UInt16
                    case 5: case 9: return BitConverter.ToInt16(b, o);      // SNorm16 / SInt16
                    case 10: return (int)BitConverter.ToUInt32(b, o);       // UInt32
                    case 11: return BitConverter.ToInt32(b, o);             // SInt32
                    default: return (int)BitConverter.ToSingle(b, o);       // Float32
                }
            }
            catch { return 0; }
        }

        private static int[] ReadIndices(byte[] ib, int format, AssetTypeValueField subMeshes, int vcount)
        {
            if (ib == null || ib.Length < 2) return null;
            var subs = ElemList(subMeshes);
            int total = 0;
            foreach (var s in subs) total += I(s, "indexCount");
            if (total <= 0) total = ib.Length / 2;

            int max = Math.Min(total, format == 1 ? ib.Length / 4 : ib.Length / 2);
            var outIdx = new int[max];
            for (int i = 0; i < max; i++)
            {
                int v = format == 1 ? (int)BitConverter.ToUInt32(ib, i * 4) : BitConverter.ToUInt16(ib, i * 2);
                outIdx[i] = (v >= 0 && v < vcount) ? v : 0;
            }
            return outIdx;
        }

        private static Matrix4x4 ReadMatrix(AssetTypeValueField m)
        {
            // Matrix4x4 在 typetree 里是 16 个 float 子节点
            var vals = new float[16];
            if (m != null && m.Children != null)
            {
                int n = Math.Min(16, m.Children.Count);
                for (int i = 0; i < n; i++)
                {
                    try { vals[i] = m.Children[i].AsFloat; } catch { vals[i] = 0f; }
                }
            }
            var mm = new Matrix4x4();
            // 序列化字段名是 e{row}{col}，顺序即内存顺序
            mm.m00 = vals[0]; mm.m01 = vals[1]; mm.m02 = vals[2]; mm.m03 = vals[3];
            mm.m10 = vals[4]; mm.m11 = vals[5]; mm.m12 = vals[6]; mm.m13 = vals[7];
            mm.m20 = vals[8]; mm.m21 = vals[9]; mm.m22 = vals[10]; mm.m23 = vals[11];
            mm.m30 = vals[12]; mm.m31 = vals[13]; mm.m32 = vals[14]; mm.m33 = vals[15];
            return mm;
        }

        private string[] ReadBoneNames(AssetTypeValueField smr, int bindposeCount)
        {
            var names = new List<string>();
            if (smr != null)
            {
                var bones = ElemList(F(smr, "m_Bones"));
                foreach (var b in bones) names.Add(BoneNameOf(b) ?? "");
            }
            // 骨骼数应与 bindpose 数一致；不一致就用 bindpose 数补齐/截断
            if (bindposeCount > 0)
            {
                while (names.Count < bindposeCount) names.Add("");
                if (names.Count > bindposeCount) names.RemoveRange(bindposeCount, names.Count - bindposeCount);
            }
            return names.ToArray();
        }

        public override void Dispose()
        {
            try { if (_am != null) _am.UnloadAll(true); } catch { }
            _am = null; _bun = null; _inst = null;
        }
    }
}
