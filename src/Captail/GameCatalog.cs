using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Captail;

internal static class GameCatalog
{
    private const string ResourceName = "Captail.Assets.GameCatalog.json";
    private static readonly Lazy<CatalogIndex> Index = new(Load);

    internal static int Count => Index.Value.Count;

    internal static string? ExactNameForExecutable(string? executable)
    {
        string normalized = Config.NormalizeExecutableName(executable);
        return normalized.Length > 0 &&
               Index.Value.NamesByExecutable.TryGetValue(
                   normalized,
                   out string? name)
            ? name
            : null;
    }

    private static CatalogIndex Load()
    {
        try
        {
            using Stream stream = typeof(GameCatalog).Assembly
                .GetManifestResourceStream(ResourceName) ??
                throw new InvalidOperationException(
                    $"Embedded game catalog '{ResourceName}' is missing.");
            CatalogDocument? document = JsonSerializer.Deserialize<CatalogDocument>(stream);
            (string Name, string[] Executables)[] entries = (document?.Games ?? [])
                .Where(game =>
                    !string.IsNullOrWhiteSpace(game.Name) &&
                    game.Executables is { Count: > 0 })
                .Select(game => (
                    Name: game.Name!.Trim(),
                    Executables: game.Executables!
                        .Select(Config.NormalizeExecutableName)
                        .Where(executable => executable.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(executable => executable, StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .Where(entry => entry.Executables.Length > 0)
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var candidates = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string[] executables) in entries)
            {
                foreach (string executable in executables)
                {
                    if (!candidates.TryGetValue(executable, out List<string>? names))
                    {
                        names = [];
                        candidates.Add(executable, names);
                    }
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }

            Dictionary<string, string> exactNames = candidates
                .Where(candidate => candidate.Value.Count == 1)
                .ToDictionary(
                    candidate => candidate.Key,
                    candidate => candidate.Value[0],
                    StringComparer.OrdinalIgnoreCase);
            return new CatalogIndex(entries.Length, exactNames);
        }
        catch (Exception exception)
        {
            Log.Write($"Game catalog unavailable ({exception.GetType().Name}).");
            return new CatalogIndex(
                0,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed record CatalogIndex(
        int Count,
        IReadOnlyDictionary<string, string> NamesByExecutable);

    private sealed class CatalogDocument
    {
        [JsonPropertyName("games")]
        public List<CatalogGame>? Games { get; init; }
    }

    private sealed class CatalogGame
    {
        [JsonPropertyName("n")]
        public string? Name { get; init; }

        [JsonPropertyName("e")]
        public List<string>? Executables { get; init; }
    }
}
