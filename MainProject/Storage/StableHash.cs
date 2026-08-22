namespace MyLovelyMail.MainProject.Storage
{
    /// <summary>
    /// The one hash the app uses to turn a string identity into a numeric uid. It lives in Storage
    /// rather than in a mail service because the store itself needs it to key local folders, and a
    /// store reaching up into Services/Mail would invert the layering.
    /// </summary>
    public static class StableHash
    {
        /// <summary>
        /// 32-bit FNV-1a. Stable across runs and machines — a uid derived from it must keep
        /// pointing at the same message after a restart, which rules out string.GetHashCode.
        /// </summary>
        public static uint Fnv1a(string text)
        {
            const uint OffsetBasis = 2166136261, Prime = 16777619;
            uint hash = OffsetBasis;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= Prime;
            }
            // 0 is reserved: a uid of 0 reads as "no uid" everywhere else in the store.
            return hash == 0 ? 1u : hash;
        }
    }
}
