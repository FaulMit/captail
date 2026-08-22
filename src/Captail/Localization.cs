using System.Globalization;
using System.Windows;

namespace Captail;

internal sealed record LanguageDefinition(
    string Code,
    string CultureName,
    string DisplayName,
    string ShortCode);

public static class Localization
{
    private const string DictionaryMarker = "Languages/Strings.";
    private static string _language = "en";

    internal static IReadOnlyList<LanguageDefinition> SupportedLanguages { get; } =
    [
        new("en", "en-US", "English", "EN"),
        new("ru", "ru-RU", "Русский", "RU"),
        new("uk", "uk-UA", "Українська", "UK"),
        new("zh", "zh-CN", "简体中文", "ZH"),
        new("es", "es-ES", "Español", "ES"),
        new("pt", "pt-BR", "Português (Brasil)", "PT"),
        new("de", "de-DE", "Deutsch", "DE"),
        new("fr", "fr-FR", "Français", "FR"),
        new("ja", "ja-JP", "日本語", "JA"),
        new("ko", "ko-KR", "한국어", "KO"),
        new("pl", "pl-PL", "Polski", "PL"),
    ];

    public static event Action? Changed;

    public static string Language => _language;
    public static bool IsRussian => _language == "ru";
    public static string CultureName => CurrentLanguage.CultureName;

    private static LanguageDefinition CurrentLanguage =>
        SupportedLanguages.First(language => language.Code == _language);

    public static void SetLanguage(string? language)
    {
        string normalized = NormalizeLanguage(language);

        _language = normalized;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CultureName);

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                $"Languages/Strings.{normalized}.xaml",
                UriKind.Relative),
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        int existingIndex = -1;
        for (int index = 0; index < dictionaries.Count; index++)
        {
            if (dictionaries[index].Source?.OriginalString.Contains(
                    DictionaryMarker,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                existingIndex = index;
                break;
            }
        }

        if (existingIndex >= 0)
            dictionaries[existingIndex] = dictionary;
        else
            dictionaries.Insert(0, dictionary);

        Changed?.Invoke();
    }

    internal static string NormalizeLanguage(string? language)
    {
        string normalized = language?.Trim().ToLowerInvariant() ?? "";
        LanguageDefinition? exact = SupportedLanguages.FirstOrDefault(item =>
            string.Equals(item.Code, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                item.CultureName,
                normalized,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Code;

        int separator = normalized.IndexOfAny(['-', '_']);
        string baseLanguage = separator > 0
            ? normalized[..separator]
            : normalized;
        return SupportedLanguages.Any(item => item.Code == baseLanguage)
            ? baseLanguage
            : "en";
    }

    internal static string DetectSystemLanguage() =>
        DetectSystemLanguage(CultureInfo.CurrentUICulture.Name);

    internal static string ResolveInitialLanguage(
        string? configuredLanguage,
        string? systemCultureName = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredLanguage))
            return NormalizeLanguage(configuredLanguage);

        return systemCultureName is null
            ? DetectSystemLanguage()
            : DetectSystemLanguage(systemCultureName);
    }

    internal static string DetectSystemLanguage(string? cultureName)
    {
        string normalized = cultureName?.Trim().Replace('_', '-').ToLowerInvariant() ?? "";
        if (normalized.Length == 0)
            return "en";

        LanguageDefinition? exact = SupportedLanguages.FirstOrDefault(item =>
            string.Equals(
                item.CultureName,
                normalized,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Code;

        int separator = normalized.IndexOf('-');
        string baseLanguage = separator > 0
            ? normalized[..separator]
            : normalized;
        return SupportedLanguages.Any(item => item.Code == baseLanguage)
            ? baseLanguage
            : "en";
    }

    public static string Text(string key) =>
        Application.Current?.TryFindResource(key)?.ToString() ?? key;

    public static string Format(string key, params object?[] arguments) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Text(key),
            arguments);
}
