using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace GpuTweak.Core.Localization;

/// <summary>
/// UI language. The Turkish text itself is the key: Turkish needs no table, other languages map the
/// Turkish text (with {0}-style placeholders) to their translation and fall back to Turkish when one is missing.
/// </summary>
public static class L
{
    public static readonly IReadOnlyList<string> Languages = ["tr", "en"];
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new();

    public static string Language { get; private set; } = "tr";
    public static event Action? LanguageChanged;

    /// <summary>Windows display language when it is one we have, otherwise English.</summary>
    public static string SystemDefault =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is var code && Languages.Contains(code) ? code : "en";

    public static void SetLanguage(string? language)
    {
        var lang = language is not null && Languages.Contains(language) ? language : SystemDefault;
        if (lang == Language) return;
        Language = lang;
        LanguageChanged?.Invoke();
    }

    public static string T(string turkish) =>
        Language == "tr" ? turkish : Table(Language).TryGetValue(turkish, out var text) ? text : turkish;

    public static string F(string turkish, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(turkish), args);

    internal static IReadOnlyDictionary<string, string> Table(string language)
    {
        lock (Tables)
        {
            if (Tables.TryGetValue(language, out var table)) return table;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"GpuTweak.Core.strings-{language}.json");
            table = stream is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            Tables[language] = table;
            return table;
        }
    }
}
