using System.Security.Cryptography;

namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

public static class FastMailCredentials
{
    public static PasswordOptions DefaultPasswordOptions => new()
    {
        Length = 40, IncludeExtendedSymbols = true, IncludeSpaces = true
    };

    public static string GenerateUsername()
    {
        const string vowels = "aeiou";
        const string consonants = "bcdfghjklmnpqrstvwxyz";
        char[] name = new char[RandomNumberGenerator.GetInt32(5, 11)];
        bool vowel = RandomNumberGenerator.GetInt32(2) == 0;
        for (int i = 0; i < name.Length; i++)
        {
            string letters = vowel ? vowels : consonants;
            name[i] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
            vowel = !vowel;
        }
        return new string(name);
    }

    public static string GeneratePassword(PasswordOptions? options = null)
    {
        options ??= DefaultPasswordOptions;
        int poolSize = PasswordGenerator.CalculatePoolSize(options);
        if (poolSize < 2) throw new ArgumentException("Choose at least one character group containing two or more different characters.");
        options = options with { Length = Math.Max(options.Length, Math.Max(40, PasswordGenerator.CalculateLengthFromBits(256, poolSize))) };
        for (int i = 1; i <= 3000; i++)
        {
            string password = PasswordGenerator.Generate(options);
            if (PasswordGenerator.CalculateEntropyBits(password) >= 230) return password;
            if (i == 3000)
                Log("Fast Mail password generation reached 3000 attempts below the 230-bit minimum. This is unexpected; no weak password was returned.", LogLevel.Error);
        }
        throw new InvalidOperationException("Could not generate a password meeting the requested strength.");
    }
}
