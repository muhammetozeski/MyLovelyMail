namespace MyLovelyMail.MainProject.Services.Mail.FastMail;

/// <summary>
/// Defines the options for character set inclusions and password length constraints.
/// </summary>
public record PasswordOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether uppercase letters (A-Z) are included.
    /// </summary>
    public bool IncludeUppercase { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether lowercase letters (a-z) are included.
    /// </summary>
    public bool IncludeLowercase { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether digits (0-9) are included.
    /// </summary>
    public bool IncludeDigits { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether standard symbols (!@#$%^&amp;*()_+-=[]{}|;:,.&lt;&gt;?) are included.
    /// </summary>
    public bool IncludeSpecialSymbols { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether extended symbols (~`\/"'&lt;&gt;) are included.
    /// </summary>
    public bool IncludeExtendedSymbols { get; init; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether ambiguous characters (O, 0, l, 1, I, |) are excluded.
    /// </summary>
    public bool ExcludeAmbiguousCharacters { get; init; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether space characters are included.
    /// </summary>
    public bool IncludeSpaces { get; init; } = false;

    /// <summary>
    /// Gets or sets the target password length.
    /// </summary>
    public int Length { get; init; } = 16;
}
