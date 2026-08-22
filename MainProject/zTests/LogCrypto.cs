using System.Text;

/// <summary>
/// Line-level obfuscation for log files: XOR keystream + Base64. Logs can contain mail subjects
/// and addresses, so they must not sit on disk as plain text — but they are not secrets either,
/// so a fast symmetric scramble is the right weight (per user requirement: very fast, does not
/// need to be strong). One Base64 line in the file = one encrypted log entry.
/// </summary>
#pragma warning disable CA1050
public static class LogCrypto
#pragma warning restore CA1050
{
    static readonly byte[] Key = Encoding.UTF8.GetBytes("MyLovelyMail-log-scramble-key-v1");

    /// <summary>XORs the buffer in place with the repeating key; running it twice restores the original bytes.</summary>
    static void ApplyKeystream(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] ^= Key[i % Key.Length];
    }

    /// <summary>Turns one log entry into one Base64 line for the log file.</summary>
    public static string Encrypt(string plainText)
    {
        byte[] data = Encoding.UTF8.GetBytes(plainText);
        ApplyKeystream(data);
        return Convert.ToBase64String(data);
    }

    /// <summary>Reverses <see cref="Encrypt"/>. Returns the raw line unchanged when it is not valid Base64 (mixed/legacy files stay readable).</summary>
    public static string Decrypt(string encryptedLine)
    {
        try
        {
            byte[] data = Convert.FromBase64String(encryptedLine);
            ApplyKeystream(data);
            return Encoding.UTF8.GetString(data);
        }
        catch (FormatException)
        {
            return encryptedLine;
        }
    }
}
