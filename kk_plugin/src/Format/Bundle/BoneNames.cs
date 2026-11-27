using System;
using System.Collections.Generic;

namespace KK2Blender.Format.Bundle
{
    /// <summary>
    /// What KK's body bones are, from their names: the contract's Bone Roles (Unity's humanoid bones in
    /// snake_case plus the KKBP extras) and Bone Groups. KK names its body skeleton the same in every
    /// character, male or female.
    /// </summary>
    public static class BoneNames
    {
        private static readonly Dictionary<string, string> Roles = MakeRoles();

        private static Dictionary<string, string> MakeRoles()
        {
            var r = new Dictionary<string, string>
            {
                ["cf_n_height"] = "body_root",
                ["cf_j_hips"] = "hips",
                ["cf_j_spine01"] = "spine",
                ["cf_j_spine02"] = "chest",
                ["cf_j_spine03"] = "upper_chest",
                ["cf_j_neck"] = "neck",
                ["cf_j_head"] = "head",
                ["cf_J_FaceRoot"] = "face_root",
                ["cf_j_tang_01"] = "tongue_root",
            };
            foreach (var side in new[] { "L", "R" })
            {
                string s = side == "L" ? "left_" : "right_";
                r["cf_j_shoulder_" + side] = s + "shoulder";
                r["cf_j_arm00_" + side] = s + "upper_arm";
                r["cf_j_forearm01_" + side] = s + "lower_arm";
                r["cf_j_hand_" + side] = s + "hand";
                r["cf_j_thigh00_" + side] = s + "upper_leg";
                r["cf_j_leg01_" + side] = s + "lower_leg";
                r["cf_j_foot_" + side] = s + "foot";
                r["cf_j_toes_" + side] = s + "toes";
                r["cf_J_hitomi_tx_" + side] = s + "eye";
                r["cf_j_bust01_" + side] = s + "breast";
                r["cf_pv_hand_" + side] = s + "hand_ik";
                r["cf_pv_foot_" + side] = s + "foot_ik";
                r["cf_pv_elbo_" + side] = s + "elbow_pole";
                r["cf_pv_knee_" + side] = s + "knee_pole";
                string[] fingers = { "thumb", "index", "middle", "ring", "little" };
                string[] joints = { "proximal", "intermediate", "distal" };
                foreach (var f in fingers)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        r["cf_j_" + f + "0" + (j + 1) + "_" + side] = s + f + "_" + joints[j];
                    }
                }
            }
            return r;
        }

        /// <summary>The Bone Role of a body bone, or null.</summary>
        public static string Role(string name)
        {
            string role;
            return name != null && Roles.TryGetValue(name, out role) ? role : null;
        }

        /// <summary>
        /// The Bone Group of a body skeleton bone from its name; <paramref name="underFace"/> says whether
        /// it is below the face root. The first rule that matches wins.
        /// </summary>
        public static string BodyGroup(string name, bool underFace)
        {
            string n = name ?? "";
            if (n.StartsWith("cf_j_sk_", StringComparison.Ordinal) || n.StartsWith("cf_d_sk_", StringComparison.Ordinal))
            {
                return "skirt";
            }
            if (n.IndexOf("bust", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("bnip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "breast";
            }
            if (IsNsfw(n))
            {
                return "nsfw";
            }
            if (underFace)
            {
                if (n.StartsWith("cf_J_Eye", StringComparison.Ordinal) || n.StartsWith("cf_J_hitomi", StringComparison.Ordinal) || n.StartsWith("cf_J_look", StringComparison.OrdinalIgnoreCase))
                {
                    return "eye";
                }
                if (n.StartsWith("cf_J_Mayu", StringComparison.Ordinal))
                {
                    return "eyebrow";
                }
                if (n.StartsWith("cf_J_Mouth", StringComparison.Ordinal) || n.StartsWith("cf_j_tang_", StringComparison.Ordinal) || n.StartsWith("cf_J_Chin", StringComparison.Ordinal))
                {
                    return "mouth";
                }
                return "face";
            }
            if (n.StartsWith("cf_j_tang_", StringComparison.Ordinal))
            {
                return "mouth";
            }
            if (n.StartsWith("cf_j_", StringComparison.Ordinal) || n.StartsWith("cf_s_", StringComparison.Ordinal) || n.StartsWith("cf_d_", StringComparison.Ordinal) || n.StartsWith("cf_n_", StringComparison.Ordinal))
            {
                return "deform";
            }
            return "helper";
        }

        private static bool IsNsfw(string n)
        {
            if (n.StartsWith("cf_J_Vagina", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            foreach (var p in new[] { "cf_j_", "cf_d_", "cf_s_" })
            {
                if (n == p + "kokan" || n == p + "ana")
                {
                    return true;
                }
            }
            return n.StartsWith("cm_J_dan", StringComparison.OrdinalIgnoreCase) || n.StartsWith("cf_j_dan", StringComparison.OrdinalIgnoreCase) || n.StartsWith("cm_j_dan", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Makes every helper bone safe to delete: a helper that anything weights (itself or a bone below
        /// it) or that physics uses becomes <paramref name="weightedGroup"/> (deform, or face under the face
        /// root, or the Outfit's own group), as the contract wants.
        /// </summary>
        public static void SettleHelpers(IList<BundleBone> bones, ICollection<string> usedKeys, Func<BundleBone, string> weightedGroup)
        {
            var byKey = new Dictionary<string, BundleBone>();
            foreach (var b in bones)
            {
                byKey[b.Key] = b;
            }
            var needed = new HashSet<string>();
            foreach (var key in usedKeys)
            {
                for (string k = key; k != null && byKey.ContainsKey(k) && needed.Add(k); k = byKey[k].ParentKey)
                {
                }
            }
            foreach (var b in bones)
            {
                if (b.Group == "helper" && needed.Contains(b.Key))
                {
                    b.Group = weightedGroup(b);
                }
            }
        }
    }
}
