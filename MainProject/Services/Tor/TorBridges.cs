using System.Diagnostics;
using MyLovelyMail.MainProject.Stores;

namespace MyLovelyMail.MainProject.Services.Tor
{
    /// <summary>One configured bridge: the line tor is given, and the transport it needs a plugin for.</summary>
    /// <param name="Transport">"obfs4", "webtunnel", "snowflake"… or null for a plain bridge, which needs no plugin.</param>
    public sealed record TorBridge(string Line, string? Transport);

    /// <summary>
    /// Bridges and pluggable transports, for a network that blocks Tor itself.
    /// <para>
    /// Without them a user behind such a network gets six ladder rungs of "nothing answered" and a
    /// tor that never bootstraps, with nothing anywhere naming the actual problem. Both Tor
    /// installs this app looks for already ship the plugins next to the executable — the scoop
    /// package and the Tor Browser both carry <c>pluggable_transports</c> — so the pieces are
    /// present and only the wiring was missing.
    /// </para>
    /// </summary>
    public static class TorBridges
    {
        /// <summary>Line separator inside the setting. A real newline cannot be stored: the settings file is one key per line.</summary>
        public const char LineSeparator = '';

        /// <summary>
        /// Which executable provides a transport. One binary usually serves several: lyrebird is
        /// obfs4, meek_lite and webtunnel at once. Listed most current first, so an older install
        /// that still ships obfs4proxy keeps working.
        /// </summary>
        static readonly Dictionary<string, string[]> PluginsByTransport = new(StringComparer.OrdinalIgnoreCase)
        {
            ["obfs4"] = ["lyrebird", "obfs4proxy"],
            ["meek_lite"] = ["lyrebird", "obfs4proxy"],
            ["webtunnel"] = ["lyrebird"],
            ["scramblesuit"] = ["lyrebird", "obfs4proxy"],
            ["conjure"] = ["conjure-client"],
            ["snowflake"] = ["snowflake-client"]
        };

        /// <summary>The folders a Tor install puts its transports in, relative to the executable and to its parent.</summary>
        static readonly string[] PluginFolderNames = ["pluggable_transports", "PluggableTransports"];

        /// <summary>The configured bridges, in order, skipping blank lines and #comments.</summary>
        public static IReadOnlyList<TorBridge> Configured =>
        [
            .. Settings.TorBridgeLines.Value
                .Split([LineSeparator, '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => !line.StartsWith('#'))
                .Select(static line => new TorBridge(line, TransportOf(line)))
        ];

        /// <summary>
        /// The transport a bridge line names, or null when it names none. A line either starts with
        /// a transport word or straight with an address, and an address is told apart by carrying a
        /// colon (host:port) — "obfs4 10.0.0.1:443 …" against "10.0.0.1:9001 …".
        /// </summary>
        internal static string? TransportOf(string line)
        {
            string first = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            return first.Length == 0 || first.Contains(':') ? null : first;
        }

        /// <summary>
        /// The plugin executable for a transport, searched beside the tor that will run it.
        /// Throws when it is missing rather than returning null: starting plain Tor because the
        /// obfuscation could not be found would put the user's traffic on exactly the shape the
        /// network is blocking, which is the opposite of what they asked for, and it would do it
        /// silently.
        /// </summary>
        public static string RequirePlugin(string transport, string torExecutablePath)
        {
            var searched = new List<string>();
            string? torFolder = Path.GetDirectoryName(torExecutablePath);

            if (PluginsByTransport.TryGetValue(transport, out string[]? names))
            {
                foreach (string baseFolder in Roots(torFolder))
                    foreach (string folderName in PluginFolderNames)
                        foreach (string name in names)
                        {
                            string candidate = Path.Combine(baseFolder, folderName, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
                            searched.Add(candidate);
                            if (File.Exists(candidate)) return candidate;
                        }
            }

            throw new TorUnavailableException(
                $"The bridge transport '{transport}' needs a pluggable-transport program and none was found. "
                + (names is { Length: > 0 } ? $"Looked for {string.Join(" or ", names)} in: " : "Looked in: ")
                + string.Join("; ", searched.Select(Path.GetDirectoryName).Distinct())
                + ". Install it beside your tor, or remove that bridge line.",
                recoverable: false);
        }

        static IEnumerable<string> Roots(string? torFolder)
        {
            if (string.IsNullOrEmpty(torFolder)) yield break;
            yield return torFolder;
            if (Path.GetDirectoryName(torFolder) is { Length: > 0 } parent) yield return parent;
        }

        /// <summary>
        /// Adds the bridge options to a tor command line, and nothing at all when none are
        /// configured. Every distinct transport contributes one ClientTransportPlugin; every line
        /// contributes one Bridge.
        /// </summary>
        public static void AddTo(ProcessStartInfo startInfo, string torExecutablePath)
        {
            var bridges = Configured;
            if (bridges.Count == 0) return;

            startInfo.ArgumentList.Add("--UseBridges");
            startInfo.ArgumentList.Add("1");

            foreach (string transport in bridges.Select(static b => b.Transport)
                         .Where(static t => t is { Length: > 0 })
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Cast<string>())
            {
                startInfo.ArgumentList.Add("--ClientTransportPlugin");
                startInfo.ArgumentList.Add($"{transport} exec {RequirePlugin(transport, torExecutablePath)}");
            }

            foreach (var bridge in bridges)
            {
                startInfo.ArgumentList.Add("--Bridge");
                startInfo.ArgumentList.Add(bridge.Line);
            }

            Log($"Tor will use {bridges.Count} bridge line(s) over "
                + $"{bridges.Select(static b => b.Transport ?? "plain").Distinct().Count()} transport(s).");
        }

        /// <summary>What the settings card and the debug API show: each transport and the plugin found for it, or why not.</summary>
        public static object Describe(string? torExecutablePath) =>
            Configured
                .Select(static b => b.Transport)
                .Where(static t => t is { Length: > 0 })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(transport =>
                {
                    try
                    {
                        return new { transport, plugin = torExecutablePath is null ? null : RequirePlugin(transport!, torExecutablePath), problem = (string?)null };
                    }
                    catch (TorUnavailableException ex)
                    {
                        return new { transport, plugin = (string?)null, problem = (string?)ex.Message };
                    }
                })
                .ToList();
    }
}
