using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyLovelyMail.MainProject.Stores
{
    /// <summary>
    /// The JsonSerializerOptions every store shares, so all persisted json agrees on one dialect:
    /// enums are written as their names, never as numbers.
    /// </summary>
    public static class JsonDefaults
    {
        /// <summary>For whole-file json (accounts, filters, tags, folder info) — indented so the files stay hand-readable.</summary>
        public static readonly JsonSerializerOptions Indented = new()
        {
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true
        };

        /// <summary>For index.jsonl entries — one message summary must stay on one line.</summary>
        public static readonly JsonSerializerOptions SingleLine = new()
        {
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
