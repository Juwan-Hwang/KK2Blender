using System;
using System.Collections.Generic;

namespace KK2Blender.Format.Bundle
{
    // The plain model of one character's KKBP Export Bundle (contract schema 1.3). The game side fills it
    // in Unity's space (left-handed, +Y up, metres, character-root relative); BundleWriter converts to
    // glTF's space in one place and assigns the Ids. Objects refer to each other by key: a string the
    // game side makes unique per kind (a transform path, with the Outfit in front for Outfit-owned
    // objects), which the writer turns into an Id.

    public sealed class BundleBone
    {
        public string Key;
        public string Name;
        public string ParentKey;
        /// <summary>The transform's path below the character root, as the bake names it.</summary>
        public string Path;
        /// <summary>Null for the body skeleton (in every Pass), else the Outfit that owns the bone.</summary>
        public int? Outfit;
        /// <summary>The bone's capture matrix in the character root's space, with its scale.</summary>
        public Affine Rest = Affine.Identity;
        public string Role;
        public string Group = "helper";
    }

    public sealed class BundlePrimitive
    {
        public int[] Indices;
        public string MaterialKey;
    }

    public sealed class BundleMorph
    {
        public string Name;
        public float[] Positions; // delta xyz per vertex
        public float[] Normals; // delta xyz per vertex, optional
    }

    public sealed class BundleMesh
    {
        public string Key;
        public string Name;
        public string Path;
        public bool Alternate;
        /// <summary>The renderer has a Unity Cloth; the bake records its simulated vertices.</summary>
        public bool Cloth;
        /// <summary>For each written vertex, the game mesh's vertex index (null: the same order).</summary>
        public int[] SourceVertices;
        public float[] Positions;
        public float[] Normals;
        public float[] Tangents; // xyzw per vertex, optional
        public readonly List<float[]> Uvs = new List<float[]>(); // uv per vertex, Unity's convention (v up)
        public float[] Colors; // rgba per vertex, optional
        public string[] BoneKeys;
        public int[] Joints; // 4 per vertex, indices into BoneKeys
        public float[] Weights; // 4 per vertex
        public readonly List<BundlePrimitive> Primitives = new List<BundlePrimitive>();
        public readonly List<BundleMorph> Morphs = new List<BundleMorph>();
        /// <summary>The game mesh's blend shape names in index order, for the map file.</summary>
        public readonly List<string> BlendShapes = new List<string>();
        /// <summary>Extra facts about the renderer for the map file (owner, renderer name, ...).</summary>
        public readonly JsonObject Info = new JsonObject();

        public int VertexCount
        {
            get { return Positions == null ? 0 : Positions.Length / 3; }
        }
    }

    public sealed class BundleTexture
    {
        public string Role;
        public string Raw;
        public string Variant;
        public string Pattern;
        public byte[] Png;
        /// <summary>Textures with the same source share one file.</summary>
        public string Source;
        public float[] Offset = { 0f, 0f };
        public float[] Scale = { 1f, 1f };

        public BundleTexture Clone()
        {
            return (BundleTexture)MemberwiseClone();
        }
    }

    public sealed class BundleMaterial
    {
        public string Key;
        public string Name;
        public string Shader = "";
        public string Role;
        public string BlendMode = "opaque";
        /// <summary>The renderer path and material slot it was captured from, for the map file.</summary>
        public string Path;
        public int Slot = -1;
        public readonly List<KeyValuePair<string, float[]>> Colors = new List<KeyValuePair<string, float[]>>();
        public readonly List<KeyValuePair<string, float>> Floats = new List<KeyValuePair<string, float>>();
        public readonly List<KeyValuePair<string, float[]>> Vectors = new List<KeyValuePair<string, float[]>>();
        public readonly List<BundleTexture> Textures = new List<BundleTexture>();
    }

    public sealed class BundleBodyTexture
    {
        public string MaterialKey;
        public BundleTexture Texture;
    }

    public sealed class BundlePass
    {
        public string Kind = "body";
        public int? Outfit;
        public readonly List<BundleMesh> Meshes = new List<BundleMesh>();
        public readonly List<BundleBodyTexture> BodyTextures = new List<BundleBodyTexture>();

        public static BundlePass Body()
        {
            return new BundlePass();
        }

        public static BundlePass ForOutfit(int outfit)
        {
            return new BundlePass { Kind = "outfit", Outfit = outfit };
        }
    }

    public sealed class BundleExpression
    {
        public string MeshKey;
        public string Target;
        public string Category = "other";
        public string Name;
        public string GameName;
    }

    public sealed class BundleTextureChange
    {
        public string Raw;
        public string Pattern;
    }

    public sealed class BundleMaterialChange
    {
        public string MaterialKey;
        public readonly List<BundleTextureChange> Textures = new List<BundleTextureChange>();
        public readonly List<KeyValuePair<string, float>> Floats = new List<KeyValuePair<string, float>>();
        public readonly List<KeyValuePair<string, float[]>> Vectors = new List<KeyValuePair<string, float[]>>();
    }

    public sealed class BundleEffect
    {
        public string Target;
        public readonly List<string> Shows = new List<string>(); // mesh keys
        public readonly List<string> Hides = new List<string>(); // mesh keys
        public readonly List<BundleMaterialChange> MaterialChanges = new List<BundleMaterialChange>();
    }

    public sealed class BundleParticle
    {
        public string BoneKey;
        public float Damping, Elasticity, Stiffness, Inert, Radius;
    }

    public sealed class BundleChain
    {
        public string RootKey;
        public int? Outfit;
        public float[] Gravity = { 0, 0, 0 }; // Unity world axes
        public float[] Force = { 0, 0, 0 };
        public float EndLength;
        public float[] EndOffset = { 0, 0, 0 }; // root-bone local
        public float Weight = 1f;
        public readonly List<string> Colliders = new List<string>(); // collider names
        public readonly List<string> Exclusions = new List<string>(); // bone keys
        public readonly List<BundleParticle> Particles = new List<BundleParticle>();
    }

    public sealed class BundleCollider
    {
        public string Name;
        public int? Outfit;
        public string BoneKey;
        public float[] Center = { 0, 0, 0 }; // bone-local
        public float[] Rotation = { 0, 0, 0, 1 }; // bone-local
        public float Radius, Height;
        public string Direction = "x";
        public string Bound = "outside";
    }

    public sealed class CharacterBundle
    {
        public const string SchemaVersion = "1.3";

        public string Name = "";
        public string Game = "KK";
        public string ExporterName = "KK2Blender";
        public string ExporterVersion = "0";
        public DateTime ExportedAt = DateTime.UtcNow;
        public byte[] Card;
        public bool AllOutfits = true;
        public bool AllVariations = true;
        public bool Physics = true;
        public bool EnabledShapeKeys;
        public bool CurrentPose;
        public readonly List<BundleBone> Bones = new List<BundleBone>();
        public readonly List<BundleMaterial> Materials = new List<BundleMaterial>();
        public readonly List<BundlePass> Passes = new List<BundlePass>();
        public readonly List<BundleExpression> Expressions = new List<BundleExpression>();
        public readonly List<BundleEffect> Effects = new List<BundleEffect>();
        public readonly List<BundleChain> Chains = new List<BundleChain>();
        public readonly List<BundleCollider> Colliders = new List<BundleCollider>();
        /// <summary>Anything else for the map file (kk2blender.json), such as the capture notes.</summary>
        public readonly JsonObject MapExtras = new JsonObject();
    }
}
