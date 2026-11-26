using System;
using System.Collections.Generic;
using KK2Blender.Format;
using Studio;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// Records one Studio character per frame into its own TrackSet:
    /// <list type="bullet">
    /// <item>"root": the character root's world matrix.</item>
    /// <item>"bone:&lt;path&gt;": every transform below the root, relative to the root. This is the game's
    /// final pose after animation, FK/IK, KKPE, joint corrections, look-at and DynamicBone physics.</item>
    /// <item>"vis:&lt;path&gt;": 1 when a renderer draws, else 0 (outfit changes, clothing states, accessories).</item>
    /// <item>"shape:&lt;path&gt;": every blend shape weight of a skinned renderer, 0..100 as the game has them.</item>
    /// <item>"face:eyes|mouth|eyebrow": per pattern, the weight (0..1) of its open and its closed shape,
    /// as the game's face controller set them this frame; this is what KKBP's expression keys need.</item>
    /// <item>"eyetex:&lt;i&gt;": texture offset and scale the eye-look controller set (pupil and highlights).</item>
    /// <item>"status", "clothes", "acc_show": the character's status values.</item>
    /// </list>
    /// Static facts (renderer owners, blend shape names, pattern counts) are collected alongside and
    /// written to the track file's header.
    /// </summary>
    internal sealed class CharacterSampler
    {
        private readonly OCIChar oci;
        private readonly ChaControl cha;
        private readonly Transform root;
        private readonly PathTable paths;
        private readonly TrackSet tracks = new TrackSet();
        private readonly JsonObject renderers = new JsonObject();
        private readonly JsonObject eyeTextures = new JsonObject();
        private readonly JsonObject facePatterns = new JsonObject();
        private readonly float[] matrix = new float[12];
        private readonly List<Transform> transformBuffer = new List<Transform>();
        private readonly List<Renderer> rendererBuffer = new List<Renderer>();

        public CharacterSampler(OCIChar oci)
        {
            this.oci = oci;
            cha = oci.charInfo;
            root = cha.transform;
            paths = new PathTable(root);
        }

        public OCIChar Oci { get { return oci; } }

        public ChaControl Cha { get { return cha; } }

        public TrackSet Tracks { get { return tracks; } }

        public void Sample(int frame)
        {
            tracks.BeginFrame(frame);
            if (cha == null || root == null)
            {
                return;
            }

            tracks.Put("root", "matrix", Space.Matrix(root.localToWorldMatrix));
            tracks.Put("visible", "bool", cha.gameObject.activeInHierarchy && (oci.objectInfo == null || oci.objectInfo.visible) ? 1f : 0f);

            Matrix4x4 toRoot = root.worldToLocalMatrix;
            transformBuffer.Clear();
            root.GetComponentsInChildren(true, transformBuffer);
            foreach (var t in transformBuffer)
            {
                if (t == root)
                {
                    continue;
                }
                Space.Matrix(toRoot * t.localToWorldMatrix, matrix, 0);
                tracks.Put("bone:" + paths.Name(t), "matrix", matrix);
            }

            rendererBuffer.Clear();
            root.GetComponentsInChildren(true, rendererBuffer);
            foreach (var r in rendererBuffer)
            {
                string path = paths.Name(r.transform);
                if (!renderers.Contains(path))
                {
                    renderers.Add(path, Describe(r));
                }
                tracks.Put("vis:" + path, "bool", r.enabled && r.gameObject.activeInHierarchy ? 1f : 0f);

                var smr = r as SkinnedMeshRenderer;
                if (smr != null && smr.sharedMesh != null && smr.sharedMesh.blendShapeCount > 0)
                {
                    int n = smr.sharedMesh.blendShapeCount;
                    var weights = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        weights[i] = smr.GetBlendShapeWeight(i);
                    }
                    Channel existing;
                    if (tracks.TryGet("shape:" + path, out existing) && existing.Width != n)
                    {
                        // A different mesh now sits at this path (an outfit swap); give it its own channel.
                        tracks.Put("shape:" + path + "@" + n, "weights", weights);
                    }
                    else
                    {
                        tracks.Put("shape:" + path, "weights", weights);
                    }
                }
            }

            SampleFace();
            SampleEyeTextures();
            SampleStatus();
        }

        private JsonObject Describe(Renderer r)
        {
            var info = new JsonObject()
                .Set("name", r.name)
                .Set("owner", Owner(r.transform))
                .Set("type", r is SkinnedMeshRenderer ? "skinned" : (r is MeshRenderer ? "mesh" : r.GetType().Name));
            Mesh mesh = null;
            var smr = r as SkinnedMeshRenderer;
            if (smr != null)
            {
                mesh = smr.sharedMesh;
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    mesh = mf.sharedMesh;
                }
            }
            if (mesh != null)
            {
                info.Set("mesh", mesh.name);
                if (mesh.blendShapeCount > 0)
                {
                    var names = new List<object>();
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        names.Add(mesh.GetBlendShapeName(i));
                    }
                    info.Set("blend_shapes", names);
                }
            }
            var mats = new List<object>();
            foreach (var m in r.sharedMaterials)
            {
                mats.Add(m != null ? m.name : null);
            }
            info.Set("materials", mats);
            return info;
        }

        /// <summary>What part of the character owns a transform: body, hair:N, clothes:N, accessory:N or other.</summary>
        private string Owner(Transform t)
        {
            for (var p = t; p != null && p != root; p = p.parent)
            {
                var go = p.gameObject;
                string found = Find(cha.objClothes, go, "clothes") ?? Find(cha.objAccessory, go, "accessory") ?? Find(cha.objHair, go, "hair");
                if (found != null)
                {
                    return found;
                }
                if (cha.objHead != null && go == cha.objHead)
                {
                    return "head";
                }
                if (cha.objBody != null && go == cha.objBody)
                {
                    return "body";
                }
            }
            return "other";
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
                    return kind + ":" + i;
                }
            }
            return null;
        }

        private void SampleFace()
        {
            var fbs = cha.fbsCtrl;
            if (fbs == null)
            {
                return;
            }
            SampleController("eyes", fbs.EyesCtrl);
            SampleController("mouth", fbs.MouthCtrl);
            SampleController("eyebrow", fbs.EyebrowCtrl);
        }

        /// <summary>
        /// Reads, for every pattern of a face controller, the weights it set on its first target mesh:
        /// [open_0, closed_0, open_1, closed_1, …] in 0..1. A blend shape index shared by two patterns
        /// is read for both, so such patterns can't be told apart; the game's own lists don't share them.
        /// </summary>
        private void SampleController(string name, FBSBase ctrl)
        {
            if (ctrl == null || ctrl.FBSTarget == null || ctrl.FBSTarget.Length == 0)
            {
                return;
            }
            var target = ctrl.FBSTarget[0];
            var smr = target.GetSkinnedMeshRenderer();
            if (smr == null || target.PtnSet == null || smr.sharedMesh == null)
            {
                return;
            }
            int count = target.PtnSet.Length;
            int shapes = smr.sharedMesh.blendShapeCount;
            var record = new float[count * 2];
            for (int p = 0; p < count; p++)
            {
                var set = target.PtnSet[p];
                record[p * 2] = set.Open >= 0 && set.Open < shapes ? smr.GetBlendShapeWeight(set.Open) / 100f : 0f;
                record[p * 2 + 1] = set.Close >= 0 && set.Close < shapes ? smr.GetBlendShapeWeight(set.Close) / 100f : 0f;
            }
            if (!facePatterns.Contains(name))
            {
                facePatterns.Add(name, new JsonObject()
                    .Set("patterns", count)
                    .Set("mesh", paths.Name(smr.transform))
                    .Set("layout", "open,closed per pattern"));
            }
            tracks.Put("face:" + name, "patterns", record);
        }

        private void SampleEyeTextures()
        {
            var ctrls = cha.eyeLookMatCtrl;
            if (ctrls == null)
            {
                return;
            }
            for (int i = 0; i < ctrls.Length; i++)
            {
                var c = ctrls[i];
                if (c == null || c._renderer == null || c.texStates == null)
                {
                    continue;
                }
                var mat = c._renderer.sharedMaterial;
                if (mat == null)
                {
                    continue;
                }
                var record = new float[c.texStates.Length * 4];
                var names = new List<object>();
                for (int s = 0; s < c.texStates.Length; s++)
                {
                    var state = c.texStates[s];
                    names.Add(state.texName);
                    if (string.IsNullOrEmpty(state.texName) || !mat.HasProperty(state.texName))
                    {
                        record[s * 4 + 2] = 1f;
                        record[s * 4 + 3] = 1f;
                        continue;
                    }
                    Vector2 o = mat.GetTextureOffset(state.texName);
                    Vector2 sc = mat.GetTextureScale(state.texName);
                    record[s * 4] = o.x;
                    record[s * 4 + 1] = o.y;
                    record[s * 4 + 2] = sc.x;
                    record[s * 4 + 3] = sc.y;
                }
                string key = "eyetex:" + i;
                if (!eyeTextures.Contains(key))
                {
                    eyeTextures.Add(key, new JsonObject()
                        .Set("renderer", paths.Name(c._renderer.transform))
                        .Set("material", mat.name)
                        .Set("textures", names)
                        .Set("layout", "offset_x,offset_y,scale_x,scale_y per texture"));
                }
                tracks.Put(key, "uv", record);
            }
        }

        private void SampleStatus()
        {
            var s = cha.fileStatus;
            if (s == null)
            {
                return;
            }
            tracks.Put("status", "status", new[]
            {
                (float)s.coordinateType, s.hohoAkaRate, (float)s.tearsLv, s.skinTuyaRate, s.nipStandRate,
                s.eyesOpenMax, s.mouthOpenMax,
            });
            if (s.clothesState != null)
            {
                var c = new float[s.clothesState.Length];
                for (int i = 0; i < c.Length; i++)
                {
                    c[i] = s.clothesState[i];
                }
                tracks.Put("clothes", "states", c);
            }
            if (s.showAccessory != null)
            {
                var a = new float[s.showAccessory.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    a[i] = s.showAccessory[i] ? 1f : 0f;
                }
                tracks.Put("acc_show", "bools", a);
            }
        }

        /// <summary>The track file header: what the channels mean, beyond their names.</summary>
        public JsonObject Header()
        {
            return new JsonObject()
                .Set("subject", "character")
                .Set("status_layout", new object[] { "coordinate", "blush", "tears_level", "skin_gloss", "nipple_stand", "eyes_open_max", "mouth_open_max" })
                .Set("renderers", renderers)
                .Set("face", facePatterns)
                .Set("eye_textures", eyeTextures);
        }
    }
}
