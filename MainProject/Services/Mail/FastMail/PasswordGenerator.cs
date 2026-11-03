using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

/// <summary>
/// Cryptographically secure password generation engine using System.Security.Cryptography.RandomNumberGenerator.
/// </summary>
public static class PasswordGenerator
{
    #region Character Pool Constants

    private const string UppercaseChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string LowercaseChars = "abcdefghijklmnopqrstuvwxyz";
    private const string DigitChars = "0123456789";
    private const string SpecialSymbolChars = "!@#$%^&*()_+-=[]{}|;:,.<>?";
    private const string ExtendedSymbolChars = "~`\\/\"'<>{}";
    private const string SpaceChars = " ";
    private static readonly HashSet<char> AmbiguousChars = ['O', '0', 'l', '1', 'I', '|', 'B', '8'];

    #endregion

    #region Public API

    /// <summary>
    /// Generates a cryptographically secure random password based on specified options.
    /// </summary>
    /// <param name="options">Configuration specifying character inclusions, exclusions, and target length.</param>
    /// <returns>A securely generated password string.</returns>
    public static string Generate(PasswordOptions options)
    {
        var activeSets = BuildActiveCharacterSets(options);
        if (activeSets.Count == 0 || options.Length <= 0)
        {
            return string.Empty;
        }

        var fullPoolBuilder = new StringBuilder();
        foreach (var set in activeSets)
        {
            fullPoolBuilder.Append(set);
        }
        var fullPool = new string(fullPoolBuilder.ToString().Distinct().ToArray());

        if (string.IsNullOrEmpty(fullPool))
        {
            return string.Empty;
        }

        var passwordChars = new char[options.Length];
        var charIndex = 0;

        // Guaranteed set inclusion: Pick at least one char from each active set if length permits
        foreach (var set in activeSets)
        {
            if (charIndex >= options.Length)
            {
                break;
            }
            passwordChars[charIndex++] = set[RandomNumberGenerator.GetInt32(0, set.Length)];
        }

        // Fill remaining password length from full pool
        while (charIndex < options.Length)
        {
            passwordChars[charIndex++] = fullPool[RandomNumberGenerator.GetInt32(0, fullPool.Length)];
        }

        // Cryptographically secure Fisher-Yates shuffle
        SecureShuffle(passwordChars);

        return new string(passwordChars);
    }

    /// <summary>
    /// Calculates the total number of unique characters available based on options.
    /// </summary>
    /// <param name="options">Password configuration options.</param>
    /// <returns>The total size of the character pool.</returns>
    public static int CalculatePoolSize(PasswordOptions options)
    {
        var activeSets = BuildActiveCharacterSets(options);
        var uniqueChars = new HashSet<char>();
        foreach (var set in activeSets)
        {
            foreach (var c in set)
            {
                uniqueChars.Add(c);
            }
        }
        return uniqueChars.Count;
    }

    /// <summary>
    /// Calculates the entropy bits for a given password length and pool size.
    /// </summary>
    /// <param name="length">Password character length.</param>
    /// <param name="poolSize">Total unique character pool size.</param>
    /// <returns>Entropy value measured in bits.</returns>
    public static double CalculateEntropyBits(int length, int poolSize)
    {
        if (length <= 0 || poolSize <= 1)
        {
            return 0;
        }
        return Math.Round(length * Math.Log2(poolSize), 1);
    }

    /// <summary>
    /// Calculates required password length to achieve a target entropy bit count.
    /// </summary>
    /// <param name="targetBits">Desired entropy in bits.</param>
    /// <param name="poolSize">Total unique character pool size.</param>
    /// <returns>Required character length.</returns>
    public static int CalculateLengthFromBits(double targetBits, int poolSize)
    {
        if (targetBits <= 0 || poolSize <= 1)
        {
            return 4;
        }

        var bitsPerChar = Math.Log2(poolSize);
        var requiredLength = (int)Math.Ceiling(targetBits / bitsPerChar);
        return Math.Clamp(requiredLength, 4, 128);
    }

    /// <summary>
    /// Calculates the entropy bits for an arbitrary string based on its content.
    /// </summary>
    public static double CalculateEntropyBits(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;

        int poolSize = 0;
        if (password.Any(char.IsLower)) poolSize += 26;
        if (password.Any(char.IsUpper)) poolSize += 26;
        if (password.Any(char.IsDigit)) poolSize += 10;
        if (password.Any(c => !char.IsLetterOrDigit(c))) poolSize += 32;
        
        if (poolSize == 0) return 0;

        bool IsSpatialAdjacent(char a, char b)
        {
            string[] keyboardRows = { "qwertyuiop", "asdfghjkl", "zxcvbnm", "1234567890" };
            char lowerA = char.ToLowerInvariant(a);
            char lowerB = char.ToLowerInvariant(b);

            foreach (var row in keyboardRows)
            {
                int indexA = row.IndexOf(lowerA);
                int indexB = row.IndexOf(lowerB);
                if (indexA != -1 && indexB != -1 && Math.Abs(indexA - indexB) == 1) return true;
            }
            return false;
        }

        double totalEntropyBits = Math.Log2(poolSize);

        for (int i = 1; i < password.Length; i++)
        {
            char current = password[i];
            char previous = password[i - 1];
            int asciiDiff = Math.Abs(current - previous);

            if (asciiDiff == 0) totalEntropyBits += Math.Log2(2); // Repeated character
            else if (asciiDiff == 1) totalEntropyBits += Math.Log2(4); // Sequential character
            else if (IsSpatialAdjacent(current, previous)) totalEntropyBits += Math.Log2(8); // Keyboard neighbour
            else totalEntropyBits += Math.Log2(poolSize); // Independent character
        }

        return totalEntropyBits;
    }

    public record ThreatProfile(string Title, ulong HashesPerSecond, string Description);

    public static readonly ThreatProfile[] ThreatProfiles = new ThreatProfile[]
    {
        new ThreatProfile("Online Web Attack", 100UL, "Slow web attacks targeting weak servers or systems with rate limiting (e.g., forum logins)."),
        new ThreatProfile("Modern CPU (Ryzen/Core i9)", 2_000_000_000UL, "A fast modern desktop processor computing common fast hashes (e.g., MD5)."),
        new ThreatProfile("Standard GPU (RTX 3060)", 30_000_000_000UL, "An entry-level graphics card used by a targeted attacker aiming at your accounts."),
        new ThreatProfile("High-End GPU (RTX 4090)", 164_000_000_000UL, "A single state-of-the-art gaming graphics card used by a professional individual hacker."),
        new ThreatProfile("Hacker Group (8x RTX 4090)", 1_312_000_000_000UL, "A small-scale mining rig consisting of 8 high-end graphics cards. Represents an organized group."),
        new ThreatProfile("Medium Botnet", 100_000_000_000_000UL, "Massive botnet networks composed of thousands of compromised zombie computers."),
        new ThreatProfile("State-Sponsored", 1_000_000_000_000_000UL, "The world's most advanced data centers and supercomputers. State-backed power targeting national defense.")
    };

    /// <summary>
    /// log10 of the largest representable millisecond count. Anything above it is reported as "Infinity".
    /// </summary>
    private static readonly double Log10OfMaxMilliseconds = Math.Log10((double)UInt128.MaxValue);

    /// <summary>
    /// Estimates the time required to crack the password based on a specific threat profile and quantum vulnerability.
    /// </summary>
    public static UInt128 CalculateCrackTimeMilliseconds(string password, ulong hashesPerSecond, bool quantumThreat)
    {
        if (string.IsNullOrEmpty(password) || hashesPerSecond == 0) return 0;

        double totalEntropyBits = CalculateEntropyBits(password);
        if (quantumThreat)
        {
            totalEntropyBits /= 2.0;
        }

        // On average half of the key space is searched before a hit, hence one bit less.
        double effectiveBits = Math.Max(0, totalEntropyBits - 1);

        // The guess count is 2^effectiveBits, which leaves every fixed-width integer type behind
        // once the password is longer than a couple of dozen characters. Only the magnitude is
        // displayed, so the estimate is carried as a base-10 exponent:
        // log10(ms) = effectiveBits * log10(2) - log10(hashes per second) + log10(1000).
        double log10Milliseconds = (effectiveBits * Math.Log10(2)) - Math.Log10(hashesPerSecond) + 3;

        // Past this point the result is far beyond the age of the universe and no longer
        // representable, so it is reported as the maximum value and rendered as "Infinity".
        if (log10Milliseconds >= Log10OfMaxMilliseconds)
        {
            return UInt128.MaxValue;
        }

        return log10Milliseconds <= 0 ? 0 : (UInt128)Math.Pow(10, log10Milliseconds);
    }

    /// <summary>
    /// Formats milliseconds into a human-readable string with dynamic scaling.
    /// </summary>
    public static string FormatCrackTime(UInt128 ms)
    {
        if (ms == UInt128.MaxValue) return "Infinity";
        if (ms < 1000) return $"{ms} milliseconds";
        
        UInt128 seconds = ms / 1000;
        if (seconds < 60) return $"{seconds} seconds";
        
        UInt128 minutes = seconds / 60;
        if (minutes < 60) return $"{minutes} minutes";
        
        UInt128 hours = minutes / 60;
        if (hours < 24) return $"{hours} hours";
        
        UInt128 days = hours / 24;
        if (days < 30) return $"{days} days";
        
        UInt128 months = days / 30;
        if (months < 12) return $"{months} months";
        
        UInt128 years = days / 365;
        if (years < 100) return $"{years} years";
        
        UInt128 centuries = years / 100;
        return $"{centuries} centuries";
    }

    #endregion

    #region Internal Helpers

    /// <summary>
    /// Filters and compiles active character sets based on configuration options.
    /// </summary>
    /// <param name="options">Options determining set inclusions and exclusions.</param>
    /// <returns>List of active character set strings.</returns>
    private static List<string> BuildActiveCharacterSets(PasswordOptions options)
    {
        var sets = new List<string>();

        if (options.IncludeUppercase) AddSet(sets, UppercaseChars, options.ExcludeAmbiguousCharacters);
        if (options.IncludeLowercase) AddSet(sets, LowercaseChars, options.ExcludeAmbiguousCharacters);
        if (options.IncludeDigits) AddSet(sets, DigitChars, options.ExcludeAmbiguousCharacters);
        if (options.IncludeSpecialSymbols) AddSet(sets, SpecialSymbolChars, options.ExcludeAmbiguousCharacters);
        if (options.IncludeExtendedSymbols) AddSet(sets, ExtendedSymbolChars, options.ExcludeAmbiguousCharacters);
        if (options.IncludeSpaces) AddSet(sets, SpaceChars, options.ExcludeAmbiguousCharacters);

        return sets;
    }

    /// <summary>
    /// Adds a character set to the list after applying ambiguous character filter if requested.
    /// </summary>
    private static void AddSet(List<string> targetList, string sourceSet, bool filterAmbiguous)
    {
        if (!filterAmbiguous)
        {
            targetList.Add(sourceSet);
            return;
        }

        var filteredBuilder = new StringBuilder();
        foreach (var c in sourceSet)
        {
            if (!AmbiguousChars.Contains(c))
            {
                filteredBuilder.Append(c);
            }
        }

        var filteredSet = filteredBuilder.ToString();
        if (filteredSet.Length > 0)
        {
            targetList.Add(filteredSet);
        }
    }

    /// <summary>
    /// Shuffles character array in-place using RandomNumberGenerator.GetInt32.
    /// </summary>
    private static void SecureShuffle(char[] array)
    {
        for (var i = array.Length - 1; i > 0; i--)
        {
            var randomIndex = RandomNumberGenerator.GetInt32(0, i + 1);
            (array[i], array[randomIndex]) = (array[randomIndex], array[i]);
        }
    }

    #endregion
}
