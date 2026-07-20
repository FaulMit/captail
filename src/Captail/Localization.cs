using System.Globalization;
using System.Windows;

namespace Captail;

public static class Localization
{
    private const string DictionaryMarker = "Languages/Strings.";
    private static string _language = "en";

    public static event Action? Changed;

    public static string Language => _language;
    public static bool IsRussian => _language == "ru";

    public static void SetLanguage(string? language)
    {
        string normalized = string.Equals(
            language,
            "ru",
            StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : "en";

        _language = normalized;
        CultureInfo.CurrentUICulture =
            CultureInfo.GetCultureInfo(normalized == "ru" ? "ru-RU" : "en-US");

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

    public static string Text(string key) =>
        Application.Current.TryFindResource(key)?.ToString() ?? key;

    public static string Format(string key, params object?[] arguments) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Text(key),
            arguments);
}
