using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KK2Blender.Format.Bundle;
using Studio;
using UnityEngine;

namespace KK2Blender.Character
{
    /// <summary>
    /// Captures one Studio character as a KKBP Export Bundle model (<see cref="CharacterBundle"/>), inside
    /// CharaStudio: the Body Pass (body, face, eyes, eyelines, eyebrows, teeth, tongue, tears, gag eyes),
    /// then one Outfit Pass per coordinate (clothes with their half-off meshes, hair, accessories,
    /// MoreAccessories' extra slots, mod clothes), the body alpha mask each coordinate puts on the body,
    /// materials with their textures and light/dark bakes, the face's finished expression keys plus every
    /// raw game blend shape ("raw:" + name), the tear and gag-eye showing keys, and physics.
    /// <para>
    /// Meshes are captured in their bind pose (<see cref="RestSolver"/>), so nothing in the scene is
    /// posed for the capture. The only thing it changes is the coordinate, to reach each outfit; the
    /// coordinate, clothing states and accessory visibility are put back afterwards.
    /// </para>
    /// </summary>
    internal sealed class CharacterCapture
    {
        private static readonly HashSet<string> HalfOffNodes = new HashSet<string> { "n_top_b", "n_bot_b", "n_bot_c", "n_panst_b", "o_bra_b", "o_inner_b" };

        private static readonly string[] TextureProps =
        {
            "_MainTex", "_ColorMask", "_NormalMap", "_NormalMapDetail", "_AlphaMask", "_LineMask", "_DetailMask",
            "_overtex1", "_overtex2", "_overtex3", "_expression", "_HairGloss", "_AnotherRamp", "_liquidmask",
            "_Texture2", "_Texture3", "_PatternMask1", "_PatternMask2", "_PatternMask3", "_PatternMask4", "_GlassRamp",
        };

        private static readonly string[] ColorProps =
        {
            "_Color", "_Color2", "_Color3", "_Color4", "_Color1_2", "_Color2_2", "_Color3_2", "_ShadowColor", "_shadowcolor",
            "_SpecularColor", "_LineColor", "_EmissionColor", "_overcolor1", "_overcolor2", "_overcolor3", "_GlossColor",
            "_LiquidTiling", "_Color5", "_Color6", "_Color7",
        };

        private static readonly string[] FloatProps =
        {
            "_SpecularPower", "_SpecularPowerNail", "_alpha_a", "_alpha_b", "_rimpower", "_rimV", "_ShadowExtend",
            "_ShadowExtendAnother", "_DetailBLineG", "_DetailRLineR", "_notusetexspecular", "_nip", "_nipsize", "_nip_specular",
            "_exppower", "_isHighLight", "_linetexon", "_liquidftop", "_liquidfbot", "_liquidbtop", "_liquidbbot", "_liquidface",
            "_SizeSpeed", "_SizeWidth", "_AngleSpeed", "_yurayura", "_rotation", "_Cutoff", "_tex1mask", "_LineWidthS",
            "_DetailNormalMapScale", "_EmissionPower", "_alpha", "_Alpha", "_reverse", "_hokuro", "_Blend",
        };

        private static readonly string[] VectorProps = { "_TileAnimation", "_HighLight", "_paint1", "_paint2" };

        private static readonly Dictionary<string, string> TextureRoles = new Dictionary<string, string>
        {
            ["_MainTex"] = "main", ["_ColorMask"] = "color_mask", ["_NormalMap"] = "normal", ["_NormalMapDetail"] = "detail_normal",
            ["_AlphaMask"] = "alpha_mask", ["_LineMask"] = "line_mask", ["_DetailMask"] = "detail_mask",
            ["_overtex1"] = "overlay", ["_overtex2"] = "overlay", ["_overtex3"] = "overlay",
        };

        private sealed class BoneRecord
        {
            public string Key, Name, ParentKey, Path;
            public int? Outfit;
            public Affine Current, Local;
            public bool Hair, UnderFace;
        }

        private sealed class RawMorph
        {
            public string Name;
            public float[] Positions, Normals;
        }

        private sealed class MeshRecord
        {
            public BundleMesh Mesh;
            public RestSolver.Skin Skin;
            public float[] Positions, Normals, Tangents;
            public readonly List<RawMorph> Morphs = new List<RawMorph>();
            public string HiddenKey; // "Tears big", "Gag eye 00", ... for meshes that rest inside the head
            public bool Face;
        }

        private readonly OCIChar oci;
        private readonly ChaControl cha;
        private readonly Transform root;
        private readonly PathTable paths;
        private readonly CharacterBundle bundle = new CharacterBundle();
        private readonly Dictionary<string, BoneRecord> bones = new Dictionary<string, BoneRecord>();
        private readonly List<BoneRecord> boneOrder = new List<BoneRecord>();
        private readonly List<MeshRecord> meshes = new List<MeshRecord>();
        private readonly Dictionary<Renderer, MeshRecord> byRenderer = new Dictionary<Renderer, MeshRecord>();
        private readonly HashSet<string> physicsBones = new HashSet<string>();
        private readonly TextureCapture textures = new TextureCapture();
        private readonly Dictionary<int, BundleTexture[]> bakes = new Dictionary<int, BundleTexture[]>();
        private readonly HashSet<GameObject> excluded = new HashSet<GameObject>();
        private LightBaker baker;
        private string bodyMaterialKey;
        private readonly List<string> warnings = new List<string>();

        public CharacterCapture(OCIChar oci, PathTable paths)
        {
            this.oci = oci;
            cha = oci.charInfo;
            root = cha.transform;
            this.paths = paths ?? new PathTable(root);
        }

        public CharacterBundle Bundle
        {
            get { return bundle; }
        }

        public List<string> Warnings
        {
            get { return warnings; }
        }

        public bool Bake = true;
        public Action<string> Status = s => { };

        // --- the run ------------------------------------------------------------------------------

        public IEnumerator Run()
        {
            var file = cha.chaFile;
            bundle.Name = file.parameter.fullname ?? "";
            bundle.ExporterName = Plugin.Name;
            bundle.ExporterVersion = Plugin.Version;
            bundle.ExportedAt = DateTime.UtcNow;
            bundle.Card = file.pngData;
            bundle.Physics = true;

            int startCoordinate = cha.fileStatus.coordinateType;
            var clothesState = (byte[])cha.fileStatus.clothesState.Clone();
            var showAccessory = (bool[])cha.fileStatus.showAccessory.Clone();
            int coordinates = file.coordinate != null ? file.coordinate.Length : 1;

            FindExcluded();
            using (baker = Bake ? new LightBaker() : null)
            {
                try
                {
                    Status("body");
                    var bodyPass = BundlePass.Body();
                    bundle.Passes.Add(bodyPass);
                    RecordBones(null);
                    foreach (var r in BodyRenderers())
                    {
                        CaptureRenderer(r, bodyPass, null);
                        yield return null;
                    }
                    FaceExpressions();
                    CapturePhysics(null);

                    for (int c = 0; c < coordinates; c++)
                    {
                        Status("outfit " + (c + 1) + "/" + coordinates);
                        if (cha.fileStatus.coordinateType != c)
                        {
                            oci.SetCoordinateInfo((ChaFileDefine.CoordinateType)c);
                            for (int i = 0; i < 6; i++)
                            {
                                yield return null;
                            }
                            yield return new WaitForEndOfFrame();
                        }
                        FindExcluded();
                        var pass = BundlePass.ForOutfit(c);
                        bundle.Passes.Add(pass);
                        RecordBones(c);
                        foreach (var r in OutfitRenderers())
                        {
                            CaptureRenderer(r, pass, c);
                            yield return null;
                        }
                        BodyTextures(pass);
                        CapturePhysics(c);
                    }
                }
                finally
                {
                    if (cha != null && cha.fileStatus.coordinateType != startCoordinate)
                    {
                        oci.SetCoordinateInfo((ChaFileDefine.CoordinateType)startCoordinate);
                    }
                }
            }
            yield return null;
            yield return null;
            for (int i = 0; i < clothesState.Length && i < cha.fileStatus.clothesState.Length; i++)
            {
                cha.SetClothesState(i, clothesState[i]);
            }
            for (int i = 0; i < showAccessory.Length && i < cha.fileStatus.showAccessory.Length; i++)
            {
                cha.fileStatus.showAccessory[i] = showAccessory[i];
            }

            Status("rest pose");
            Finish();
        }

        // --- what belongs where -------------------------------------------------------------------------

        private void FindExcluded()
        {
            excluded.Clear();
            foreach (var key in new[]
            {
                "S_MNPA", "S_MNPB", "S_MOZ_ALL", "S_Son", "S_SimpleTop", "S_SimpleBody", "S_SimpleTongue", "S_GOMU", "S_TongueB",
            })
            {
                var go = Ref(key);
                if (go != null)
                {
                    excluded.Add(go);
                }
            }
            if (cha.objHitBody != null)
            {
                excluded.Add(cha.objHitBody);
            }
            if (cha.objHitHead != null)
            {
                excluded.Add(cha.objHitHead);
            }
        }

        /// <summary>
        /// A ChaReference object by its key's name. The key's number differs between KK builds (Darkness,
        /// Party), so it is looked up by name in the running game's enum rather than compiled in.
        /// </summary>
        private GameObject Ref(string key)
        {
            try
            {
                return cha.GetReferenceInfo((ChaReference.RefObjKey)Enum.Parse(typeof(ChaReference.RefObjKey), key));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private bool IsExcluded(Transform t)
        {
            for (var p = t; p != null && p != root; p = p.parent)
            {
                if (excluded.Contains(p.gameObject))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsHit(Transform t)
        {
            string n = t.name;
            return n.StartsWith("cf_hit", StringComparison.Ordinal) || n.StartsWith("cf_d_hit", StringComparison.Ordinal) || n.StartsWith("aa_hit", StringComparison.Ordinal);
        }

        /// <summary>What owns a transform: "clothes:N", "accessory:N", "hair:N", or null for the body.</summary>
        private string OutfitOwner(Transform t)
        {
            for (var p = t; p != null && p != root; p = p.parent)
            {
                var go = p.gameObject;
                string owner = Find(cha.objClothes, go, "clothes") ?? Find(cha.objAccessory, go, "accessory") ?? Find(cha.objHair, go, "hair");
                if (owner != null)
                {
                    return owner;
                }
                if (go.GetComponent<ChaAccessoryComponent>() != null)
                {
                    return "accessory:extra";
                }
                if (go.GetComponent<ChaClothesComponent>() != null)
                {
                    return "clothes:extra";
                }
                if (go.GetComponent<ChaCustomHairComponent>() != null)
                {
                    return "hair:extra";
                }
            }
            return null;
        }

        private static string Find(GameObject[] list, GameObject go, string kind)
        {
            if (list == null)
            {
                return null;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] != null && list[i] == go)
                {
                    return kind + ":" + i.ToString(CultureInfo.InvariantCulture);
                }
            }
            return null;
        }

        private static string Prefix(int? outfit)
        {
            return outfit.HasValue ? "outfit" + outfit.Value.ToString("00", CultureInfo.InvariantCulture) + ":" : "";
        }

        /// <summary>The bone key of a transform as captured in the given Pass (body transforms keep one key).</summary>
        private string KeyOf(Transform t, int? outfit)
        {
            if (t == null || t == root || !t.IsChildOf(root))
            {
                return null;
            }
            while (t != null && t != root && IsHit(t))
            {
                t = t.parent;
            }
            if (t == null || t == root)
            {
                return null;
            }
            bool owned = OutfitOwner(t) != null;
            return (owned ? Prefix(outfit) : "") + paths.Name(t);
        }

        private void RecordBones(int? outfit)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            Transform faceRoot = null;
            foreach (var t in all)
            {
                if (t.name == "cf_J_FaceRoot")
                {
                    faceRoot = t;
                    break;
                }
            }
            foreach (var t in all)
            {
                if (t == root || IsHit(t))
                {
                    continue;
                }
                string owner = OutfitOwner(t);
                if ((owner != null) != outfit.HasValue)
                {
                    continue; // body bones in the Body Pass, Outfit-owned bones in their Outfit's
                }
                string key = KeyOf(t, outfit);
                if (key == null || bones.ContainsKey(key))
                {
                    continue;
                }
                var parent = t.parent;
                while (parent != null && parent != root && IsHit(parent))
                {
                    parent = parent.parent;
                }
                string parentKey = parent == null || parent == root ? null : KeyOf(parent, outfit);
                var rec = new BoneRecord
                {
                    Key = key,
                    Name = t.name,
                    ParentKey = parentKey,
                    Path = paths.Name(t),
                    Outfit = owner != null ? outfit : null,
                    Current = ToAffine(root.worldToLocalMatrix * t.localToWorldMatrix),
                    Hair = owner != null && owner.StartsWith("hair", StringComparison.Ordinal),
                    UnderFace = faceRoot != null && t != faceRoot && t.IsChildOf(faceRoot),
                };
                if (parentKey == null)
                {
                    rec.Local = rec.Current;
                }
                else
                {
                    var parentWorld = parent.localToWorldMatrix;
                    rec.Local = ToAffine(parentWorld.inverse * t.localToWorldMatrix);
                }
                bones[key] = rec;
                boneOrder.Add(rec);
            }
        }

        private static Affine ToAffine(Matrix4x4 m)
        {
            return new Affine
            {
                M00 = m.m00, M01 = m.m01, M02 = m.m02, M03 = m.m03,
                M10 = m.m10, M11 = m.m11, M12 = m.m12, M13 = m.m13,
                M20 = m.m20, M21 = m.m21, M22 = m.m22, M23 = m.m23,
            };
        }

        private static bool Drawable(Renderer r)
        {
            return r is SkinnedMeshRenderer || r is MeshRenderer;
        }

        private IEnumerable<Renderer> BodyRenderers()
        {
            var seen = new HashSet<Renderer>();
            foreach (var top in new[] { cha.objBody, cha.objHead })
            {
                if (top == null)
                {
                    continue;
                }
                foreach (var r in top.GetComponentsInChildren<Renderer>(true))
                {
                    if (Drawable(r) && seen.Add(r) && !IsExcluded(r.transform) && OutfitOwner(r.transform) == null && r.name != "o_Mask")
                    {
                        yield return r;
                    }
                }
            }
        }

        private IEnumerable<Renderer> OutfitRenderers()
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (Drawable(r) && !IsExcluded(r.transform) && OutfitOwner(r.transform) != null && r.name != "o_Mask")
                {
                    yield return r;
                }
            }
        }

        private bool IsHalfOff(Transform t)
        {
            for (var p = t; p != null && p != root; p = p.parent)
            {
                if (HalfOffNodes.Contains(p.name))
                {
                    return true;
                }
                var go = p.gameObject;
                if (Find(cha.objClothes, go, "c") != null)
                {
                    return false;
                }
            }
            return false;
        }

        // --- meshes ---------------------------------------------------------------------------------------

        private static Mesh SourceMesh(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null)
            {
                return smr.sharedMesh;
            }
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private string MaterialRole(Renderer r, Material m, int slot, int? outfit)
        {
            if (outfit.HasValue)
            {
                return OutfitOwner(r.transform).StartsWith("hair", StringComparison.Ordinal) ? "hair" : null;
            }
            string name = r.name;
            if (r == cha.rendBody)
            {
                return slot == 0 ? "body" : null;
            }
            if (r == cha.rendFace)
            {
                return slot == 0 ? "face" : null;
            }
            if (r == cha.rendEyebrow)
            {
                return slot == 0 ? "eyebrow" : null;
            }
            if (r == cha.rendEyelineUp)
            {
                bool shadow = m != null && m.name.IndexOf("kage", StringComparison.OrdinalIgnoreCase) >= 0;
                return shadow || slot == 1 ? "eyeline_shadow" : "eyeline_upper";
            }
            if (r == cha.rendEyelineDown)
            {
                return "eyeline_lower";
            }
            if (r == cha.rendNose)
            {
                return "nose_line";
            }
            if (cha.rendEyeW != null && Array.IndexOf(cha.rendEyeW, r) >= 0)
            {
                return "eye_white";
            }
            if (cha.rendEye != null && Array.IndexOf(cha.rendEye, r) >= 0)
            {
                return name.IndexOf("_R", StringComparison.Ordinal) >= 0 ? "eye_r" : "eye_l";
            }
            if (name.StartsWith("cf_O_namida", StringComparison.Ordinal))
            {
                return "tears";
            }
            if (name.StartsWith("cf_O_gag_eye", StringComparison.Ordinal))
            {
                return "gag_eyes";
            }
            if (name.StartsWith("cf_O_tooth", StringComparison.Ordinal) || name.StartsWith("cf_O_canine", StringComparison.Ordinal))
            {
                return "teeth";
            }
            if (name == "o_tang")
            {
                return "tongue";
            }
            return null;
        }

        private void CaptureRenderer(Renderer r, BundlePass pass, int? outfit)
        {
            var mesh = SourceMesh(r);
            string path = paths.Name(r.transform);
            if (mesh == null)
            {
                return;
            }
            if (!mesh.isReadable)
            {
                warnings.Add(path + ": the game doesn't let its mesh be read; it was left out");
                return;
            }
            int n = mesh.vertexCount;
            if (n == 0)
            {
                return;
            }
            string prefix = outfit.HasValue ? Prefix(outfit) : "body:";
            var bm = new BundleMesh
            {
                Key = prefix + path,
                Name = r.name,
                Path = path,
                Alternate = outfit.HasValue && IsHalfOff(r.transform),
                Cloth = r.GetComponent<Cloth>() != null,
            };
            bm.Info.Set("owner", OutfitOwner(r.transform) ?? (cha.objHead != null && r.transform.IsChildOf(cha.objHead.transform) ? "head" : "body"));
            bm.Info.Set("renderer", r is SkinnedMeshRenderer ? "skinned" : "mesh");
            bm.Info.Set("mesh", mesh.name);
            bm.Info.Set("active", r.enabled && r.gameObject.activeInHierarchy);
            var rec = new MeshRecord { Mesh = bm, Face = r == cha.rendFace };

            // Vertex data, in the mesh's own space.
            rec.Positions = Flat(mesh.vertices);
            var normals = mesh.normals;
            rec.Normals = normals != null && normals.Length == n ? Flat(normals) : null;
            var tangents = mesh.tangents;
            rec.Tangents = tangents != null && tangents.Length == n ? Flat(tangents) : null;
            foreach (var uv in new[] { mesh.uv, mesh.uv2, mesh.uv3, mesh.uv4 })
            {
                if (uv == null || uv.Length != n)
                {
                    break;
                }
                bm.Uvs.Add(Flat(uv));
            }
            var colors = mesh.colors;
            if (colors != null && colors.Length == n)
            {
                bm.Colors = new float[n * 4];
                for (int i = 0; i < n; i++)
                {
                    bm.Colors[i * 4] = colors[i].r;
                    bm.Colors[i * 4 + 1] = colors[i].g;
                    bm.Colors[i * 4 + 2] = colors[i].b;
                    bm.Colors[i * 4 + 3] = colors[i].a;
                }
            }

            // Skin.
            var skin = new RestSolver.Skin { Key = bm.Key, RendererKey = KeyOf(r.transform, outfit) };
            BoneRecord rendererBone;
            if (skin.RendererKey != null && bones.TryGetValue(skin.RendererKey, out rendererBone))
            {
                skin.Renderer = rendererBone.Current;
            }
            var smr = r as SkinnedMeshRenderer;
            var smrBones = smr != null ? smr.bones : null;
            var weights = mesh.boneWeights;
            var bind = mesh.bindposes;
            if (smr != null && smrBones != null && smrBones.Length > 0 && weights != null && weights.Length == n && bind != null && bind.Length >= smrBones.Length)
            {
                skin.BoneKeys = new string[smrBones.Length];
                skin.Bindposes = new Affine[smrBones.Length];
                for (int j = 0; j < smrBones.Length; j++)
                {
                    string key = KeyOf(smrBones[j], outfit);
                    skin.BoneKeys[j] = key != null && bones.ContainsKey(key) ? key : null;
                    skin.Bindposes[j] = ToAffine(bind[j]);
                }
                skin.Joints = new int[n * 4];
                skin.Weights = new float[n * 4];
                for (int i = 0; i < n; i++)
                {
                    var w = weights[i];
                    skin.Joints[i * 4] = w.boneIndex0; skin.Weights[i * 4] = w.weight0;
                    skin.Joints[i * 4 + 1] = w.boneIndex1; skin.Weights[i * 4 + 1] = w.weight1;
                    skin.Joints[i * 4 + 2] = w.boneIndex2; skin.Weights[i * 4 + 2] = w.weight2;
                    skin.Joints[i * 4 + 3] = w.boneIndex3; skin.Weights[i * 4 + 3] = w.weight3;
                }
                bm.BoneKeys = skin.BoneKeys;
                bm.Joints = skin.Joints;
                bm.Weights = skin.Weights;
            }
            else
            {
                if (skin.RendererKey == null)
                {
                    warnings.Add(path + ": its transform isn't a bone; it was left out");
                    return;
                }
                skin.Rigid = true;
                bm.BoneKeys = new[] { skin.RendererKey };
                bm.Joints = new int[n * 4];
                bm.Weights = new float[n * 4];
                for (int i = 0; i < n; i++)
                {
                    bm.Weights[i * 4] = 1f;
                }
                skin.BoneKeys = bm.BoneKeys;
                skin.Joints = bm.Joints;
                skin.Weights = bm.Weights;
                skin.Bindposes = new[] { Affine.Identity };
            }
            rec.Skin = skin;

            // Raw blend shapes, in index order.
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                string shape = mesh.GetBlendShapeName(s);
                bm.BlendShapes.Add(shape);
                var morph = ReadShape(mesh, s);
                morph.Name = "raw:" + shape;
                rec.Morphs.Add(morph);
                bundle.Expressions.Add(new BundleExpression { MeshKey = bm.Key, Target = morph.Name, Category = "other", GameName = shape });
            }

            // Primitives and materials. Extra materials draw the last submesh again, as Unity does.
            var materials = r.sharedMaterials;
            int subs = mesh.subMeshCount;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                var m = materials[slot];
                if (m == null)
                {
                    continue;
                }
                int sub = Math.Min(slot, subs - 1);
                var tris = mesh.GetTriangles(sub);
                if (tris == null || tris.Length < 3)
                {
                    continue;
                }
                string matKey = prefix + path + ":" + slot.ToString(CultureInfo.InvariantCulture);
                var material = CaptureMaterial(r, m, slot, sub, mesh, matKey, path, MaterialRole(r, m, slot, outfit), outfit);
                bundle.Materials.Add(material);
                if (r == cha.rendBody && slot == 0 && !outfit.HasValue)
                {
                    bodyMaterialKey = matKey;
                }
                bm.Primitives.Add(new BundlePrimitive { Indices = tris, MaterialKey = matKey });
            }
            if (bm.Primitives.Count == 0)
            {
                // Still record it, without triangles; the writer leaves it out.
            }

            if (!outfit.HasValue)
            {
                if (r.name.StartsWith("cf_O_namida_", StringComparison.Ordinal))
                {
                    string size = r.name.Substring("cf_O_namida_".Length);
                    rec.HiddenKey = size == "L" ? "Tears big" : size == "M" ? "Tears med" : size == "S" ? "Tears small" : null;
                }
                else if (r.name.StartsWith("cf_O_gag_eye_0", StringComparison.Ordinal) && r.name.Length == "cf_O_gag_eye_00".Length)
                {
                    rec.HiddenKey = "Gag eye 0" + r.name.Substring(r.name.Length - 1);
                }
            }

            pass.Meshes.Add(bm);
            meshes.Add(rec);
            byRenderer[r] = rec;
        }

        private static RawMorph ReadShape(Mesh mesh, int shape)
        {
            int n = mesh.vertexCount;
            int frames = mesh.GetBlendShapeFrameCount(shape);
            int frame = frames - 1;
            for (int f = 0; f < frames; f++)
            {
                if (Mathf.Approximately(mesh.GetBlendShapeFrameWeight(shape, f), 100f))
                {
                    frame = f;
                }
            }
            float weight = frames > 0 ? mesh.GetBlendShapeFrameWeight(shape, frame) : 100f;
            float scale = weight > 0 ? 100f / weight : 1f;
            var dv = new Vector3[n];
            var dn = new Vector3[n];
            var dt = new Vector3[n];
            if (frames > 0)
            {
                mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
            }
            var morph = new RawMorph { Positions = Flat(dv), Normals = Flat(dn) };
            if (!Mathf.Approximately(scale, 1f))
            {
                for (int i = 0; i < morph.Positions.Length; i++)
                {
                    morph.Positions[i] *= scale;
                    morph.Normals[i] *= scale;
                }
            }
            return morph;
        }

        private static float[] Flat(Vector3[] v)
        {
            var r = new float[v.Length * 3];
            for (int i = 0; i < v.Length; i++)
            {
                r[i * 3] = v[i].x;
                r[i * 3 + 1] = v[i].y;
                r[i * 3 + 2] = v[i].z;
            }
            return r;
        }

        private static float[] Flat(Vector2[] v)
        {
            var r = new float[v.Length * 2];
            for (int i = 0; i < v.Length; i++)
            {
                r[i * 2] = v[i].x;
                r[i * 2 + 1] = v[i].y;
            }
            return r;
        }

        private static float[] Flat(Vector4[] v)
        {
            var r = new float[v.Length * 4];
            for (int i = 0; i < v.Length; i++)
            {
                r[i * 4] = v[i].x;
                r[i * 4 + 1] = v[i].y;
                r[i * 4 + 2] = v[i].z;
                r[i * 4 + 3] = v[i].w;
            }
            return r;
        }

        // --- materials ------------------------------------------------------------------------------------

        private static string BlendMode(Material m)
        {
            string shader = m.shader != null ? m.shader.name : "";
            if (shader.IndexOf("glasses", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "glasses";
            }
            int queue = m.renderQueue;
            if (queue >= 3000)
            {
                return "alpha";
            }
            if (queue >= 2450)
            {
                return "cutout";
            }
            return "opaque";
        }

        private BundleTexture TextureOf(Material m, string prop, string variant = null)
        {
            var tex = m.GetTexture(prop);
            if (tex == null)
            {
                return null;
            }
            string role;
            TextureRoles.TryGetValue(prop, out role);
            bool normal = role == "normal" || role == "detail_normal";
            var png = textures.Png(tex, normal);
            if (png == null)
            {
                return null;
            }
            var offset = m.GetTextureOffset(prop);
            var scale = m.GetTextureScale(prop);
            return new BundleTexture
            {
                Role = role,
                Raw = prop,
                Variant = variant,
                Png = png,
                Source = (normal ? "n" : "t") + tex.GetInstanceID().ToString(CultureInfo.InvariantCulture),
                Offset = new[] { offset.x, offset.y },
                Scale = new[] { scale.x, scale.y },
            };
        }

        private BundleMaterial CaptureMaterial(Renderer r, Material m, int slot, int submesh, Mesh mesh, string key, string path, string role, int? outfit)
        {
            var bmat = new BundleMaterial
            {
                Key = key,
                Name = m.name.Replace(" (Instance)", ""),
                Shader = m.shader != null ? m.shader.name : "",
                Role = role,
                BlendMode = BlendMode(m),
                Path = path,
                Slot = slot,
            };
            foreach (var p in ColorProps)
            {
                if (m.HasProperty(p))
                {
                    var c = m.GetColor(p);
                    bmat.Colors.Add(new KeyValuePair<string, float[]>(p, new[] { c.r, c.g, c.b, c.a }));
                }
            }
            foreach (var p in FloatProps)
            {
                if (m.HasProperty(p))
                {
                    bmat.Floats.Add(new KeyValuePair<string, float>(p, m.GetFloat(p)));
                }
            }
            foreach (var p in VectorProps)
            {
                if (m.HasProperty(p))
                {
                    var v = m.GetVector(p);
                    bmat.Vectors.Add(new KeyValuePair<string, float[]>(p, new[] { v.x, v.y, v.z, v.w }));
                }
            }
            bool bodyMaterial = r == cha.rendBody && slot == 0 && !outfit.HasValue;
            foreach (var p in TextureProps)
            {
                if (!m.HasProperty(p) || (bodyMaterial && p == "_AlphaMask"))
                {
                    continue; // the body's alpha mask is the Outfit's (body_textures)
                }
                var t = TextureOf(m, p);
                if (t != null)
                {
                    bmat.Textures.Add(t);
                }
            }
            if (baker != null && role != "tears" && role != "gag_eyes")
            {
                AddBakes(r, m, submesh, mesh, bmat, role == "body" || role == "face");
            }
            return bmat;
        }

        private void AddBakes(Renderer r, Material m, int submesh, Mesh mesh, BundleMaterial bmat, bool skin)
        {
            bool overlay = LightBaker.HasOverlay(m);
            int cacheKey = m.GetInstanceID();
            BundleTexture[] made;
            if (overlay || !bakes.TryGetValue(cacheKey, out made))
            {
                var main = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                int w = main != null ? main.width : 512;
                int h = main != null ? main.height : 512;
                if (skin)
                {
                    w = h = 4096;
                }
                w = Mathf.Clamp(w, 4, 4096);
                h = Mathf.Clamp(h, 4, 4096);
                Mesh flat = overlay ? LightBaker.Flatten(mesh) : null;
                byte[] lit, dark;
                try
                {
                    baker.Bake(r, m, submesh, flat, w, h, out lit, out dark);
                }
                catch (Exception e)
                {
                    warnings.Add(bmat.Path + ": the light/dark bake failed (" + e.Message + ")");
                    return;
                }
                finally
                {
                    if (flat != null)
                    {
                        UnityEngine.Object.DestroyImmediate(flat);
                    }
                }
                var offset = m.HasProperty("_MainTex") ? m.GetTextureOffset("_MainTex") : Vector2.zero;
                var scale = m.HasProperty("_MainTex") ? m.GetTextureScale("_MainTex") : Vector2.one;
                made = new[]
                {
                    new BundleTexture { Role = "main", Raw = "_MainTex", Variant = "light", Png = lit, Offset = new[] { offset.x, offset.y }, Scale = new[] { scale.x, scale.y } },
                    new BundleTexture { Role = "main", Raw = "_MainTex", Variant = "dark", Png = dark, Offset = new[] { offset.x, offset.y }, Scale = new[] { scale.x, scale.y } },
                };
                if (!overlay)
                {
                    bakes[cacheKey] = made;
                }
            }
            // The bake covers UV space once: its placement is the identity.
            foreach (var t in made)
            {
                var copy = t.Clone();
                copy.Offset = new[] { 0f, 0f };
                copy.Scale = new[] { 1f, 1f };
                bmat.Textures.Add(copy);
            }
        }

        private void BodyTextures(BundlePass pass)
        {
            if (bodyMaterialKey == null || cha.rendBody == null)
            {
                return;
            }
            var m = cha.rendBody.sharedMaterial;
            if (m == null || !m.HasProperty("_AlphaMask"))
            {
                return;
            }
            var t = TextureOf(m, "_AlphaMask");
            if (t != null)
            {
                pass.BodyTextures.Add(new BundleBodyTexture { MaterialKey = bodyMaterialKey, Texture = t });
            }
        }

        // --- expressions ----------------------------------------------------------------------------------

        private void AddPatternKey(MeshRecord rec, int shapeIndex, string category, string name, string gameName, string fallbackTarget)
        {
            if (rec == null || shapeIndex < 0 || shapeIndex >= rec.Mesh.BlendShapes.Count)
            {
                return;
            }
            string target = name ?? fallbackTarget;
            if (rec.Morphs.Exists(m => m.Name == target))
            {
                return;
            }
            var raw = rec.Morphs[shapeIndex]; // raw morphs come first, in blend shape order
            rec.Morphs.Add(new RawMorph { Name = target, Positions = raw.Positions, Normals = raw.Normals });
            bundle.Expressions.Add(new BundleExpression
            {
                MeshKey = rec.Mesh.Key,
                Target = target,
                Category = name != null ? KkExpressionTable.CategoryOf(name) : category,
                Name = name,
                GameName = gameName,
            });
        }

        private IEnumerable<KeyValuePair<MeshRecord, FBSTargetInfo>> Targets(FBSBase ctrl)
        {
            if (ctrl == null || ctrl.FBSTarget == null)
            {
                yield break;
            }
            foreach (var t in ctrl.FBSTarget)
            {
                var smr = t != null ? t.GetSkinnedMeshRenderer() : null;
                MeshRecord rec;
                if (smr != null && t.PtnSet != null && byRenderer.TryGetValue(smr, out rec))
                {
                    yield return new KeyValuePair<MeshRecord, FBSTargetInfo>(rec, t);
                }
            }
        }

        private void FaceExpressions()
        {
            var fbs = cha.fbsCtrl;
            if (fbs != null)
            {
                EyeKeys(fbs.EyesCtrl);
                foreach (var kv in Targets(fbs.EyebrowCtrl))
                {
                    for (int p = 0; p < kv.Value.PtnSet.Length; p++)
                    {
                        var entry = KkExpressionTable.Find("eyebrow", p);
                        AddPatternKey(kv.Key, kv.Value.PtnSet[p].Open, "eyebrow", entry != null ? entry.Name : null, "eyebrow " + p, "Eyebrow pattern " + p);
                    }
                }
                foreach (var kv in Targets(fbs.MouthCtrl))
                {
                    for (int p = 0; p < kv.Value.PtnSet.Length; p++)
                    {
                        var entry = KkExpressionTable.Find("mouth", p);
                        var set = kv.Value.PtnSet[p];
                        AddPatternKey(kv.Key, set.Open, "mouth", entry != null ? entry.Name : null, "mouth " + p, "Mouth pattern " + p);
                        if (set.Close != set.Open)
                        {
                            AddPatternKey(kv.Key, set.Close, "mouth", entry != null ? entry.ClosedName : null, "mouth " + p + " closed",
                                "Mouth pattern " + p + ", Closed");
                        }
                    }
                }
            }
            Recipes();
            HiddenMeshes();
        }

        private void EyeKeys(FBSBase eyes)
        {
            var infos = cha.lstCtrl != null ? cha.lstCtrl.GetCategoryInfo(ChaListDefine.CategoryNo.cha_eyeset) : null;
            if (infos == null)
            {
                return;
            }
            var ids = new List<int>(infos.Keys);
            ids.Sort();
            var targets = new List<KeyValuePair<MeshRecord, FBSTargetInfo>>(Targets(eyes));
            for (int i = 0; i < ids.Count; i++)
            {
                var info = infos[ids[i]];
                var entry = KkExpressionTable.Find("eye", i);
                string gameName = info.Name;
                string fallback = "Eye set " + ids[i].ToString(CultureInfo.InvariantCulture) + " " + (gameName ?? "");
                string target = entry != null ? entry.Name : fallback.Trim();
                int ptn = info.GetInfoInt(ChaListDefine.KeyType.EyesPtn);
                foreach (var kv in targets)
                {
                    if (ptn >= 0 && ptn < kv.Value.PtnSet.Length)
                    {
                        AddPatternKey(kv.Key, kv.Value.PtnSet[ptn].Open, "eye", entry != null ? entry.Name : null, gameName, target);
                    }
                }
                EyeSetEffects(info, target);
            }
        }

        private void EyeSetEffects(ListInfoBase info, string target)
        {
            var fx = new BundleEffect { Target = target };
            int gag = info.GetInfoInt(ChaListDefine.KeyType.EyeObjNo);
            if (gag >= 1 && gag <= 3)
            {
                var go = Ref("N_Gag0" + (gag - 1).ToString(CultureInfo.InvariantCulture));
                var r = go != null ? go.GetComponent<Renderer>() : null;
                MeshRecord rec;
                if (r != null && byRenderer.TryGetValue(r, out rec))
                {
                    fx.Shows.Add(rec.Mesh.Key);
                    var tex = LoadListTexture(info, ChaListDefine.KeyType.MainTexAB, ChaListDefine.KeyType.MainTex);
                    var matKey = rec.Mesh.Primitives.Count > 0 ? rec.Mesh.Primitives[0].MaterialKey : null;
                    var mat = matKey != null ? bundle.Materials.Find(x => x.Key == matKey) : null;
                    if (mat != null)
                    {
                        var change = new BundleMaterialChange { MaterialKey = matKey };
                        if (tex != null && AddPattern(mat, "_MainTex", "main", tex))
                        {
                            change.Textures.Add(new BundleTextureChange { Raw = "_MainTex", Pattern = tex.name });
                        }
                        var tile = Vector4.zero;
                        var parts = (info.GetInfo(ChaListDefine.KeyType.TileAnim) ?? "").Split('/');
                        if (parts.Length >= 3)
                        {
                            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out tile.x);
                            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out tile.y);
                            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out tile.z);
                        }
                        change.Vectors.Add(new KeyValuePair<string, float[]>("_TileAnimation", new[] { tile.x, tile.y, tile.z, tile.w }));
                        change.Floats.Add(new KeyValuePair<string, float>("_SizeSpeed", info.GetInfoFloat(ChaListDefine.KeyType.SizeSpeed)));
                        change.Floats.Add(new KeyValuePair<string, float>("_SizeWidth", info.GetInfoFloat(ChaListDefine.KeyType.SizeWidth)));
                        change.Floats.Add(new KeyValuePair<string, float>("_AngleSpeed", info.GetInfoFloat(ChaListDefine.KeyType.AngleSpeed)));
                        change.Floats.Add(new KeyValuePair<string, float>("_yurayura", info.GetInfoFloat(ChaListDefine.KeyType.Yurayura)));
                        fx.MaterialChanges.Add(change);
                    }
                }
            }
            if (info.GetInfoInt(ChaListDefine.KeyType.EyeHitomi) == 0)
            {
                HideUnder(fx, Ref("N_Hitomi"));
            }
            if (info.GetInfoInt(ChaListDefine.KeyType.EyeBase) != 1)
            {
                HideUnder(fx, Ref("N_EyeBase"));
            }
            var expression = LoadListTexture(info, ChaListDefine.KeyType.EpsTexAB, ChaListDefine.KeyType.EpsTex);
            if (expression != null && cha.rendEye != null)
            {
                foreach (var eye in cha.rendEye)
                {
                    MeshRecord rec;
                    if (eye == null || !byRenderer.TryGetValue(eye, out rec) || rec.Mesh.Primitives.Count == 0)
                    {
                        continue;
                    }
                    string matKey = rec.Mesh.Primitives[0].MaterialKey;
                    var mat = bundle.Materials.Find(x => x.Key == matKey);
                    if (mat != null && AddPattern(mat, "_expression", null, expression))
                    {
                        var change = new BundleMaterialChange { MaterialKey = matKey };
                        change.Textures.Add(new BundleTextureChange { Raw = "_expression", Pattern = expression.name });
                        change.Floats.Add(new KeyValuePair<string, float>("_exppower", 1f));
                        fx.MaterialChanges.Add(change);
                    }
                }
            }
            if (fx.Shows.Count > 0 || fx.Hides.Count > 0 || fx.MaterialChanges.Count > 0)
            {
                bundle.Effects.Add(fx);
            }
        }

        private void HideUnder(BundleEffect fx, GameObject node)
        {
            if (node == null)
            {
                return;
            }
            foreach (var r in node.GetComponentsInChildren<Renderer>(true))
            {
                MeshRecord rec;
                if (byRenderer.TryGetValue(r, out rec) && !fx.Hides.Contains(rec.Mesh.Key))
                {
                    fx.Hides.Add(rec.Mesh.Key);
                }
            }
        }

        private Texture LoadListTexture(ListInfoBase info, ChaListDefine.KeyType bundleKey, ChaListDefine.KeyType assetKey)
        {
            string ab = info.GetInfo(bundleKey);
            string asset = info.GetInfo(assetKey);
            if (string.IsNullOrEmpty(ab) || string.IsNullOrEmpty(asset) || ab == "0" || asset == "0")
            {
                return null;
            }
            if (!cha.hiPoly)
            {
                asset += "_low";
            }
            try
            {
                return CommonLib.LoadAsset<Texture2D>(ab, asset, false, string.Empty);
            }
            catch (Exception e)
            {
                warnings.Add("Couldn't load " + ab + "/" + asset + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Adds a texture the game swaps into a material slot as one of its patterns.</summary>
        private bool AddPattern(BundleMaterial mat, string raw, string role, Texture tex)
        {
            if (mat.Textures.Exists(t => t.Raw == raw && t.Pattern == tex.name))
            {
                return true;
            }
            var png = textures.Png(tex, false);
            if (png == null)
            {
                return false;
            }
            mat.Textures.Add(new BundleTexture { Role = role, Raw = raw, Pattern = tex.name, Png = png, Source = "t" + tex.GetInstanceID().ToString(CultureInfo.InvariantCulture) });
            return true;
        }

        private void Recipes()
        {
            foreach (var recipe in KkExpressionTable.Recipes)
            {
                foreach (var rec in meshes)
                {
                    if (rec.Mesh.Key.StartsWith("outfit", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    float[] pos = null, nrm = null;
                    foreach (var shape in recipe.Shapes)
                    {
                        int index = rec.Mesh.BlendShapes.IndexOf(shape.Key);
                        if (index < 0)
                        {
                            continue;
                        }
                        var raw = rec.Morphs[index];
                        if (pos == null)
                        {
                            pos = new float[raw.Positions.Length];
                            nrm = new float[raw.Normals.Length];
                        }
                        for (int i = 0; i < pos.Length; i++)
                        {
                            pos[i] += raw.Positions[i] * shape.Value;
                            nrm[i] += raw.Normals[i] * shape.Value;
                        }
                    }
                    if (pos != null && !rec.Morphs.Exists(m => m.Name == recipe.Name))
                    {
                        rec.Morphs.Add(new RawMorph { Name = recipe.Name, Positions = pos, Normals = nrm });
                        bundle.Expressions.Add(new BundleExpression { MeshKey = rec.Mesh.Key, Target = recipe.Name, Category = KkExpressionTable.CategoryOf(recipe.Name), Name = recipe.Name });
                    }
                }
            }
        }

        private void HiddenMeshes()
        {
            foreach (var rec in meshes)
            {
                if (rec.HiddenKey != null)
                {
                    bundle.Expressions.Add(new BundleExpression
                    {
                        MeshKey = rec.Mesh.Key,
                        Target = rec.HiddenKey,
                        Category = KkExpressionTable.CategoryOf(rec.HiddenKey),
                        Name = rec.HiddenKey,
                    });
                }
            }
        }

        // --- physics -------------------------------------------------------------------------------------

        private void CapturePhysics(int? outfit)
        {
            foreach (var d in root.GetComponentsInChildren<DynamicBone>(true))
            {
                if ((OutfitOwner(d.transform) != null) != outfit.HasValue || d.m_Root == null)
                {
                    continue;
                }
                var chain = new BundleChain
                {
                    RootKey = KeyOf(d.m_Root, outfit),
                    Outfit = outfit,
                    Gravity = V(d.m_Gravity),
                    Force = V(d.m_Force),
                    EndLength = d.m_EndLength,
                    EndOffset = V(d.m_EndOffset),
                };
                if (chain.RootKey == null)
                {
                    continue;
                }
                var exclusions = new HashSet<Transform>();
                if (d.m_Exclusions != null)
                {
                    foreach (var e in d.m_Exclusions)
                    {
                        if (e != null)
                        {
                            exclusions.Add(e);
                            string k = KeyOf(e, outfit);
                            if (k != null)
                            {
                                chain.Exclusions.Add(k);
                            }
                        }
                    }
                }
                var chainBones = new List<KeyValuePair<Transform, int>>();
                Walk(d.m_Root, 0, exclusions, chainBones);
                int maxDepth = 1;
                foreach (var kv in chainBones)
                {
                    maxDepth = Math.Max(maxDepth, kv.Value);
                }
                foreach (var kv in chainBones)
                {
                    string k = KeyOf(kv.Key, outfit);
                    if (k == null)
                    {
                        continue;
                    }
                    float at = (float)kv.Value / maxDepth;
                    chain.Particles.Add(new BundleParticle
                    {
                        BoneKey = k,
                        Damping = d.m_Damping * Curve(d.m_DampingDistrib, at),
                        Elasticity = d.m_Elasticity * Curve(d.m_ElasticityDistrib, at),
                        Stiffness = d.m_Stiffness * Curve(d.m_StiffnessDistrib, at),
                        Inert = d.m_Inert * Curve(d.m_InertDistrib, at),
                        Radius = d.m_Radius * Curve(d.m_RadiusDistrib, at),
                    });
                    physicsBones.Add(k);
                }
                if (d.m_Colliders != null)
                {
                    foreach (var c in d.m_Colliders)
                    {
                        string name = Collider(c, outfit);
                        if (name != null && !chain.Colliders.Contains(name))
                        {
                            chain.Colliders.Add(name);
                        }
                    }
                }
                bundle.Chains.Add(chain);
            }
            foreach (var d in root.GetComponentsInChildren<DynamicBone_Ver02>(true))
            {
                if ((OutfitOwner(d.transform) != null) != outfit.HasValue || d.Bones == null || d.Bones.Count == 0)
                {
                    continue;
                }
                var first = d.Root != null ? d.Root : d.Bones[0];
                var chain = new BundleChain { RootKey = KeyOf(first, outfit), Outfit = outfit, Gravity = V(d.Gravity), Force = V(d.Force) };
                if (chain.RootKey == null)
                {
                    continue;
                }
                List<DynamicBone_Ver02.BoneParameter> parameters = null;
                if (d.Patterns != null && d.PtnNo >= 0 && d.PtnNo < d.Patterns.Count)
                {
                    parameters = d.Patterns[d.PtnNo].Params;
                }
                for (int i = 0; i < d.Bones.Count; i++)
                {
                    string k = KeyOf(d.Bones[i], outfit);
                    if (k == null)
                    {
                        continue;
                    }
                    var p = parameters != null && i < parameters.Count ? parameters[i] : null;
                    chain.Particles.Add(new BundleParticle
                    {
                        BoneKey = k,
                        Damping = p != null ? p.Damping : 0f,
                        Elasticity = p != null ? p.Elasticity : 0f,
                        Stiffness = p != null ? p.Stiffness : 0f,
                        Inert = p != null ? p.Inert : 0f,
                        Radius = p != null ? p.CollisionRadius : 0f,
                    });
                    physicsBones.Add(k);
                }
                if (d.Colliders != null)
                {
                    foreach (var c in d.Colliders)
                    {
                        string name = Collider(c, outfit);
                        if (name != null && !chain.Colliders.Contains(name))
                        {
                            chain.Colliders.Add(name);
                        }
                    }
                }
                bundle.Chains.Add(chain);
            }
        }

        private static float Curve(AnimationCurve c, float at)
        {
            return c != null && c.keys.Length > 0 ? c.Evaluate(at) : 1f;
        }

        private static void Walk(Transform t, int depth, HashSet<Transform> exclusions, List<KeyValuePair<Transform, int>> into)
        {
            if (t == null || exclusions.Contains(t))
            {
                return;
            }
            into.Add(new KeyValuePair<Transform, int>(t, depth));
            for (int i = 0; i < t.childCount; i++)
            {
                Walk(t.GetChild(i), depth + 1, exclusions, into);
            }
        }

        private static float[] V(Vector3 v)
        {
            return new[] { v.x, v.y, v.z };
        }

        private readonly Dictionary<DynamicBoneCollider, string> colliderNames = new Dictionary<DynamicBoneCollider, string>();

        private string Collider(DynamicBoneCollider c, int? outfit)
        {
            if (c == null || !c.transform.IsChildOf(root))
            {
                return null;
            }
            string name;
            if (colliderNames.TryGetValue(c, out name))
            {
                return name;
            }
            var t = c.transform;
            var at = t;
            while (at != null && at != root && (IsHit(at) || IsExcluded(at)))
            {
                at = at.parent;
            }
            if (at == null || at == root)
            {
                return null;
            }
            string boneKey = KeyOf(at, outfit);
            if (boneKey == null)
            {
                return null;
            }
            var center = at.InverseTransformPoint(t.TransformPoint(c.m_Center));
            var rotation = Quaternion.Inverse(at.rotation) * t.rotation;
            name = c.name;
            for (int n = 2; bundle.Colliders.Exists(x => x.Name == name); n++)
            {
                name = c.name + "_" + n.ToString(CultureInfo.InvariantCulture);
            }
            bundle.Colliders.Add(new BundleCollider
            {
                Name = name,
                Outfit = OutfitOwner(t) != null ? outfit : null,
                BoneKey = boneKey,
                Center = V(center),
                Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                Radius = c.m_Radius,
                Height = c.m_Height,
                Direction = c.m_Direction.ToString().ToLowerInvariant(),
                Bound = c.m_Bound.ToString().ToLowerInvariant(),
            });
            colliderNames[c] = name;
            physicsBones.Add(boneKey);
            return name;
        }

        // --- the rest pose and the bundle model --------------------------------------------------------------

        private void Finish()
        {
            foreach (var b in boneOrder)
            {
                if (b.ParentKey != null && !bones.ContainsKey(b.ParentKey))
                {
                    b.ParentKey = null;
                }
            }
            var solver = new RestSolver(boneOrder.ConvertAll(b => new RestSolver.Bone { Key = b.Key, ParentKey = b.ParentKey, Current = b.Current, Local = b.Local }));
            // Body first, then the Outfits in order: the body's bind pose places the shared skeleton.
            solver.Solve(meshes.ConvertAll(m => m.Skin));

            var weighted = new HashSet<string>(physicsBones);
            foreach (var rec in meshes)
            {
                var s = rec.Skin;
                if (!s.Exact)
                {
                    warnings.Add(rec.Mesh.Path + ": its bind pose differs from the body's by up to " + s.Mismatch.ToString("0.0000", CultureInfo.InvariantCulture) +
                        "; its rest shape is blended (exact only where one bone carries a vertex)");
                }
                var skinning = new Skinning(s, rec.Mesh.VertexCount);
                rec.Mesh.Positions = skinning.Positions(rec.Positions);
                rec.Mesh.Normals = rec.Normals != null ? skinning.Normals(rec.Normals) : null;
                rec.Mesh.Tangents = rec.Tangents != null && rec.Normals != null ? skinning.Tangents(rec.Tangents) : null;
                foreach (var morph in rec.Morphs)
                {
                    rec.Mesh.Morphs.Add(new BundleMorph
                    {
                        Name = morph.Name,
                        Positions = skinning.Offsets(morph.Positions),
                        Normals = rec.Normals != null ? skinning.Normals(morph.Normals, true) : null,
                    });
                }
                for (int i = 0; i < s.Joints.Length; i++)
                {
                    int j = s.Joints[i];
                    if (s.Weights[i] > 0 && j >= 0 && j < s.BoneKeys.Length && s.BoneKeys[j] != null)
                    {
                        weighted.Add(s.BoneKeys[j]);
                    }
                }
            }
            HideInsideHead();

            foreach (var b in boneOrder)
            {
                bool body = !b.Outfit.HasValue;
                bundle.Bones.Add(new BundleBone
                {
                    Key = b.Key,
                    Name = b.Name,
                    ParentKey = b.ParentKey,
                    Path = b.Path,
                    Outfit = b.Outfit,
                    Rest = solver.RestOf(b.Key),
                    Role = body ? BoneNames.Role(b.Name) : null,
                    Group = body ? BoneNames.BodyGroup(b.Name, b.UnderFace) : "helper",
                });
            }
            var recordByKey = new Dictionary<string, BoneRecord>();
            foreach (var b in boneOrder)
            {
                recordByKey[b.Key] = b;
            }
            BoneNames.SettleHelpers(bundle.Bones, weighted, bone =>
            {
                var r = recordByKey[bone.Key];
                if (r.Outfit.HasValue)
                {
                    return r.Hair ? "hair" : "accessory";
                }
                return r.UnderFace ? "face" : "deform";
            });
            bundle.MapExtras.Set("capture", new Format.JsonObject()
                .Set("pose", "bind")
                .Set("note", "Bones rest at the meshes' bind pose; a bone's game_scale is its scale in that pose. " +
                    "A vertex follows the game's root-relative bone matrix M as M * inverse(rest * game_scale) * v."));
            if (warnings.Count > 0)
            {
                bundle.MapExtras.Set("capture_warnings", warnings);
            }
        }

        /// <summary>
        /// Tears and gag eyes rest inside the head, half as wide and pushed back by half the face's depth,
        /// and their showing key moves them out to where the game draws them (KKBP ADR 0010). The other
        /// shape keys of these meshes stay offsets, so they add to either.
        /// </summary>
        private void HideInsideHead()
        {
            var face = meshes.Find(m => m.Face);
            if (face == null)
            {
                return;
            }
            float minZ = float.MaxValue, maxZ = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
            var p = face.Mesh.Positions;
            for (int i = 0; i < p.Length; i += 3)
            {
                minX = Math.Min(minX, p[i]);
                maxX = Math.Max(maxX, p[i]);
                minZ = Math.Min(minZ, p[i + 2]);
                maxZ = Math.Max(maxZ, p[i + 2]);
            }
            float cx = (minX + maxX) * 0.5f;
            float back = (maxZ - minZ) * 0.5f;
            foreach (var rec in meshes)
            {
                if (rec.HiddenKey == null)
                {
                    continue;
                }
                var pos = rec.Mesh.Positions;
                var show = new float[pos.Length];
                for (int i = 0; i < pos.Length; i += 3)
                {
                    float hx = cx + (pos[i] - cx) * 0.5f;
                    float hz = pos[i + 2] - back;
                    show[i] = pos[i] - hx;
                    show[i + 2] = pos[i + 2] - hz;
                    pos[i] = hx;
                    pos[i + 2] = hz;
                }
                rec.Mesh.Morphs.Add(new BundleMorph { Name = rec.HiddenKey, Positions = show, Normals = rec.Mesh.Normals != null ? new float[pos.Length] : null });
            }
        }
    }
}
