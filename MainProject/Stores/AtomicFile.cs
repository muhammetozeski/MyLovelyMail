using System.Text;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// Crash-safe text file writes: content goes to a temp sibling first and then replaces the
    /// target in one move, so a power cut or crash never leaves a half-written config on disk.
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>Suffix of the sibling a write lands in first; the next write of the same target overwrites any leftover.</summary>
        const string TempSuffix = ".tmp";

        public static void WriteAllText(string path, string content)
        {
            string tempPath = path + TempSuffix;
            File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }

        /// <summary>Same guarantee for binary content — cached message bodies go through here.</summary>
        public static void WriteAllBytes(string path, byte[] content)
        {
            string tempPath = path + TempSuffix;
            File.WriteAllBytes(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
    }
}
