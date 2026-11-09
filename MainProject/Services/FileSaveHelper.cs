using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyLovelyMail.MainProject.Services;

/// <summary>
/// Provides native Save As file selection on Windows and opens the destination directory in Windows Explorer after saving.
/// </summary>
public static class FileSaveHelper
{
    /// <summary>Opens the directory containing <paramref name="savedPath"/> in Windows Explorer and selects the saved file.</summary>
    public static void RevealInFileExplorer(string savedPath)
    {
        if (string.IsNullOrWhiteSpace(savedPath) || !OperatingSystem.IsWindows()) return;
        try
        {
            string fullPath = Path.GetFullPath(savedPath);
            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
            }
            else if (Path.GetDirectoryName(fullPath) is { Length: > 0 } parentDir && Directory.Exists(parentDir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parentDir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log($"Could not reveal saved path '{savedPath}' in Explorer: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// Opens the native Windows Save File Dialog on Windows, returning the chosen full file path or null when canceled.
    /// </summary>
    public static string? PickSaveFilePath(string initialDirectory, string defaultFileName, string extension)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string cleanExt = extension.TrimStart('.');
            string filter = cleanExt.Equals("html", StringComparison.OrdinalIgnoreCase)
                ? "HTML Document (*.html)\0*.html\0All Files (*.*)\0*.*\0\0"
                : "Email Message (*.eml)\0*.eml\0All Files (*.*)\0*.*\0\0";

            const int MaxFileBufferChars = 1024;
            IntPtr fileBuffer = Marshal.AllocHGlobal(MaxFileBufferChars * 2);
            try
            {
                RtlZeroMemory(fileBuffer, MaxFileBufferChars * 2);
                string seed = Path.Combine(initialDirectory, defaultFileName);
                char[] chars = seed.ToCharArray();
                Marshal.Copy(chars, 0, fileBuffer, Math.Min(chars.Length, MaxFileBufferChars - 2));

                var ofn = new OPENFILENAMEW
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = GetActiveWindow(),
                    lpstrFilter = filter,
                    nFilterIndex = 1,
                    lpstrFile = fileBuffer,
                    nMaxFile = MaxFileBufferChars,
                    lpstrInitialDir = initialDirectory,
                    lpstrTitle = "Save Message As",
                    lpstrDefExt = cleanExt,
                    Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                };

                if (GetSaveFileNameW(ref ofn))
                {
                    return Marshal.PtrToStringUni(fileBuffer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuffer);
            }
        }
        catch (Exception ex)
        {
            Log($"Native save dialog failed: {ex.Message}", LogLevel.Warning);
        }
        return null;
    }

    const int OFN_OVERWRITEPROMPT = 0x00000002;
    const int OFN_NOCHANGEDIR = 0x00000008;
    const int OFN_PATHMUSTEXIST = 0x00000800;
    const int OFN_EXPLORER = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetSaveFileNameW(ref OPENFILENAMEW lpofn);

    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();

    [DllImport("kernel32.dll")]
    static extern void RtlZeroMemory(IntPtr dst, int length);
}
