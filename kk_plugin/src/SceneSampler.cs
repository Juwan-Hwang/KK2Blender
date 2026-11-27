using System.Collections.Generic;
using KK2Blender.Format;
using Studio;
using UnityEngine;

namespace KK2Blender
{
    /// <summary>
    /// Records everything in the scene that isn't a character, per frame, into one TrackSet:
    /// <list type="bullet">
    /// <item>"camera": the rendering camera's world matrix, then vertical FOV (degrees), near, far,
    /// orthographic (0/1) and orthographic size: 17 floats.</item>
    /// <item>"item:&lt;key&gt;": a Studio item's world matrix and whether it shows: 13 floats.</item>
    /// <item>"item:&lt;key&gt;:bone:&lt;path&gt;": bones of skinned or animated items, relative to the item.</item>
    /// <item>"light:&lt;n&gt;": every Unity light: world matrix, RGB, intensity, range, spot angle,
    /// enabled, shadow strength: 20 floats.</item>
    /// <item>"folder:&lt;key&gt;": a Studio folder's world matrix and visibility.</item>
    /// <item>"map": the map root's world matrix and whether it shows.</item>
    /// </list>
    /// </summary>
    internal sealed class SceneSampler
    {
        private readonly TrackSet tracks = new TrackSet();
        private readonly List<OCIItem> items = new List<OCIItem>();
        private readonly List<OCIFolder> folders = new List<OCIFolder>();
        private readonly List<OCICamera> cameras = new List<OCICamera>();
        private readonly Dictionary<OCIItem, PathTable> itemBones = new Dictionary<OCIItem, PathTable>();
        private readonly List<Light> lights = new List<Light>();
        private readonly Dictionary<Light, int> lightIds = new Dictionary<Light, int>();
        private readonly JsonObject lightInfo = new JsonObject();
        private readonly Dictionary<Light, OCILight> studioLights = new Dictionary<Light, OCILight>();
        private readonly float[] matrix = new float[12];
        private readonly List<Transform> buffer = new List<Transform>();

        public SceneSampler(IEnumerable<ObjectCtrlInfo> objects)
        {
            foreach (var o in objects)
            {
                var item = o as OCIItem;
                if (item != null && item.objectItem != null)
                {
                    items.Add(item);
                    if (NeedsBones(item))
                    {
                        itemBones[item] = new PathTable(item.objectItem.transform);
                    }
                }
                var folder = o as OCIFolder;
                if (folder != null && folder.objectItem != null)
                {
                    folders.Add(folder);
                }
                var cam = o as OCICamera;
                if (cam != null && cam.objectItem != null)
                {
                    cameras.Add(cam);
                }
                var light = o as OCILight;
                if (light != null && light.light != null)
                {
                    studioLights[light.light] = light;
                }
            }
        }

        public TrackSet Tracks { get { return tracks; } }

        public IList<OCIItem> Items { get { return items; } }

        /// <summary>Items whose shape moves: skinned meshes, an Animator, FK or dynamic bones.</summary>
        private static bool NeedsBones(OCIItem item)
        {
            if (item.animator != null || (item.dynamicBones != null && item.dynamicBones.Length > 0))
            {
                return true;
            }
            if (item.itemInfo != null && item.itemInfo.enableFK)
            {
                return true;
            }
            return item.objectItem.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }

        public void Sample(int frame)
        {
            tracks.BeginFrame(frame);
            SampleCamera();
            SampleStudioCameras();
            SampleItems();
            SampleFolders();
            SampleLights();
            SampleMap();
        }

        private void SampleCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            var r = new float[17];
            Space.Matrix(cam.transform.localToWorldMatrix, r, 0);
            r[12] = cam.fieldOfView;
            r[13] = cam.nearClipPlane;
            r[14] = cam.farClipPlane;
            r[15] = cam.orthographic ? 1f : 0f;
            r[16] = cam.orthographicSize;
            tracks.Put("camera", "camera", r);
        }

        /// <summary>
        /// "camera_target": the point Studio's camera orbits and its distance to it, which is also where
        /// Studio's depth of field focuses: target x, y, z, distance.
        /// "studiocam:&lt;key&gt;": each camera object placed in Studio: world matrix, shown, active.
        /// </summary>
        private void SampleStudioCameras()
        {
            var studio = Singleton<Studio.Studio>.Instance;
            if (studio != null && studio.cameraCtrl != null)
            {
                var data = studio.cameraCtrl.Export();
                if (data != null)
                {
                    tracks.Put("camera_target", "target", new[] { data.pos.x, data.pos.y, data.pos.z, Mathf.Abs(data.distance.z) });
                }
            }
            foreach (var cam in cameras)
            {
                if (cam.objectItem == null)
                {
                    continue;
                }
                var r = new float[14];
                Space.Matrix(cam.objectItem.transform.localToWorldMatrix, r, 0);
                r[12] = cam.objectItem.activeInHierarchy ? 1f : 0f;
                r[13] = cam.cameraInfo != null && cam.cameraInfo.active ? 1f : 0f;
                tracks.Put("studiocam:" + cam.objectInfo.dicKey, "matrix_vis_active", r);
            }
        }

        private void SampleItems()
        {
            foreach (var item in items)
            {
                if (item.objectItem == null)
                {
                    continue;
                }
                var t = item.objectItem.transform;
                var r = new float[13];
                Space.Matrix(t.localToWorldMatrix, r, 0);
                r[12] = item.objectItem.activeInHierarchy ? 1f : 0f;
                string key = "item:" + item.objectInfo.dicKey;
                tracks.Put(key, "matrix_vis", r);

                PathTable bones;
                if (itemBones.TryGetValue(item, out bones))
                {
                    Matrix4x4 toItem = t.worldToLocalMatrix;
                    buffer.Clear();
                    t.GetComponentsInChildren(true, buffer);
                    foreach (var b in buffer)
                    {
                        if (b == t)
                        {
                            continue;
                        }
                        Space.Matrix(toItem * b.localToWorldMatrix, matrix, 0);
                        tracks.Put(key + ":bone:" + bones.Name(b), "matrix", matrix);
                    }
                }
            }
        }

        private void SampleFolders()
        {
            foreach (var folder in folders)
            {
                if (folder.objectItem == null)
                {
                    continue;
                }
                var r = new float[13];
                Space.Matrix(folder.objectItem.transform.localToWorldMatrix, r, 0);
                r[12] = folder.objectItem.activeInHierarchy ? 1f : 0f;
                tracks.Put("folder:" + folder.objectInfo.dicKey, "matrix_vis", r);
            }
        }

        private void SampleLights()
        {
            // New lights can appear (a map load, an item with a light), so look every frame.
            foreach (var l in Object.FindObjectsOfType<Light>())
            {
                if (!lightIds.ContainsKey(l))
                {
                    int id = lights.Count;
                    lights.Add(l);
                    lightIds[l] = id;
                    lightInfo.Add("light:" + id, DescribeLight(l));
                }
            }
            for (int i = 0; i < lights.Count; i++)
            {
                var l = lights[i];
                if (l == null)
                {
                    continue;
                }
                var r = new float[20];
                Space.Matrix(l.transform.localToWorldMatrix, r, 0);
                r[12] = l.color.r;
                r[13] = l.color.g;
                r[14] = l.color.b;
                r[15] = l.intensity;
                r[16] = l.range;
                r[17] = l.spotAngle;
                r[18] = l.enabled && l.gameObject.activeInHierarchy ? 1f : 0f;
                r[19] = l.shadows == LightShadows.None ? 0f : l.shadowStrength;
                tracks.Put("light:" + i, "light", r);
            }
        }

        private JsonObject DescribeLight(Light l)
        {
            var info = new JsonObject()
                .Set("name", l.name)
                .Set("type", l.type.ToString().ToLowerInvariant())
                .Set("path", FullPath(l.transform))
                .Set("culling_mask", l.cullingMask)
                .Set("render_mode", l.renderMode.ToString());
            OCILight oci;
            if (studioLights.TryGetValue(l, out oci))
            {
                info.Set("owner", "studio_light");
                info.Set("key", oci.objectInfo.dicKey);
            }
            else if (IsUnder(l.transform, Camera.main != null ? Camera.main.transform : null))
            {
                // Studio's character light follows the camera.
                info.Set("owner", "camera");
            }
            else if (Map.Instance != null && IsUnder(l.transform, Map.Instance.mapRoot != null ? Map.Instance.mapRoot.transform : null))
            {
                info.Set("owner", "map");
            }
            else
            {
                info.Set("owner", "other");
            }
            return info;
        }

        private void SampleMap()
        {
            var map = Map.Instance;
            if (map == null || map.mapRoot == null)
            {
                return;
            }
            var r = new float[13];
            Space.Matrix(map.mapRoot.transform.localToWorldMatrix, r, 0);
            r[12] = map.mapRoot.activeInHierarchy ? 1f : 0f;
            tracks.Put("map", "matrix_vis", r);
        }

        private static bool IsUnder(Transform t, Transform parent)
        {
            if (parent == null)
            {
                return false;
            }
            for (var p = t; p != null; p = p.parent)
            {
                if (p == parent)
                {
                    return true;
                }
            }
            return false;
        }

        private static string FullPath(Transform t)
        {
            return Space.PathBelow(t, null);
        }

        public JsonObject Header()
        {
            return new JsonObject()
                .Set("subject", "scene")
                .Set("camera_layout", "matrix[12],fov_deg,near,far,orthographic,ortho_size")
                .Set("camera_target_layout", "x,y,z,distance")
                .Set("light_layout", "matrix[12],r,g,b,intensity,range,spot_angle,enabled,shadow_strength")
                .Set("lights", lightInfo);
        }
    }
}
