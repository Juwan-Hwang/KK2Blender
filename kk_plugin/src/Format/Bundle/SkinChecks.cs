namespace KK2Blender.Format.Bundle
{
    /// <summary>Checks on a captured skin for the export self-check.</summary>
    public static class SkinChecks
    {
        /// <summary>The total vertex weight on joints whose bone couldn't be keyed (null in
        /// <paramref name="boneKeys"/>) or that point past the bones array: weight the bundle can't carry.</summary>
        public static double DroppedWeight(string[] boneKeys, int[] joints, float[] weights)
        {
            if (boneKeys == null || joints == null || weights == null)
            {
                return 0;
            }
            double dropped = 0;
            for (int i = 0; i < joints.Length && i < weights.Length; i++)
            {
                if (weights[i] <= 0)
                {
                    continue;
                }
                int j = joints[i];
                if (j < 0 || j >= boneKeys.Length || boneKeys[j] == null)
                {
                    dropped += weights[i];
                }
            }
            return dropped;
        }
    }
}
