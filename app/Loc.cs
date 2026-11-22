global using static Soundboard.Loc;
using System.Globalization;

namespace Soundboard;

/// <summary>
/// User-interface language. Texts are written in English in the code and wrapped in T(...);
/// the French translations live in Loc.French.cs, keyed by the English text.
/// A text without a translation simply stays in English.
/// </summary>
static partial class Loc
{
    /// <summary>"" = follow Windows, "en", "fr".</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("", "Automatic (Windows)"), ("en", "English"), ("fr", "Français"),
    };

    public static bool French { get; private set; }

    public static void Init(string? preference)
    {
        French = preference switch
        {
            "fr" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr",
        };
    }

    /// <summary>The text in the current language.</summary>
    public static string T(string english) =>
        French && FrenchTexts.TryGetValue(english, out var fr) ? fr : english;

    /// <summary>The text in the current language, with {0}, {1}… filled in.</summary>
    public static string T(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);
}
