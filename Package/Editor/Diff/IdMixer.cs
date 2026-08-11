namespace VisualGitDiff.Diff
{
    /// <summary>Deterministically mixes two fileIds into one — used wherever a synthetic
    /// identity needs to be unique per (A, B) pair without a real document backing it, while
    /// staying stable across the two revisions being compared since both inputs are.</summary>
    internal static class IdMixer
    {
        public static long Combine(long a, long b)
        {
            unchecked
            {
                var hash = 17L;
                hash = hash * 31 + a;
                hash = hash * 31 + b;
                return hash;
            }
        }
    }
}
