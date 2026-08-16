using System.Text;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// Crash-safe text file writes: content goes to a temp sibling first and then replaces the
    /// target in one move, so a power cut or crash never leaves a half-written config on disk.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
    }
}
