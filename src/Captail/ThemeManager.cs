using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Captail;

internal static class ThemeManager
{
    private sealed record Palette(
        Color Accent,
        Color Hover,
        Color Dim,
        Color OnAccent);

    private static readonly IReadOnlyDictionary<string, Palette> Palettes =
        new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase)
        {
            ["mint"] = New("#63E0BD", "#7DE8CB", "#2EA184", "#0B201A"),
            ["blue"] = New("#64B5F6", "#82C4FA", "#357EBD", "#081A28"),
            ["violet"] = New("#B69AF7", "#C8B2FA", "#785AC6", "#160F2B"),
            ["rose"] = New("#FF6666", "#FF8585", "#C54242", "#270B0B"),
            ["amber"] = New("#F5B94C", "#F8C96E", "#B97D1E", "#251702"),
        };

    private static readonly Dictionary<string, string> IconAssets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mint"] = "Captail.ico",
            ["blue"] = "CaptailBlue.ico",
            ["violet"] = "CaptailViolet.ico",
            ["rose"] = "CaptailRose.ico",
            ["amber"] = "CaptailAmber.ico",
        };

    internal static void ApplyAccent(string? name)
    {
        if (Application.Current is null)
            return;
        if (!Palettes.TryGetValue(name ?? "", out Palette? palette))
            palette = Palettes["mint"];

        ResourceDictionary resources = Application.Current.Resources;
        resources["AccentBrush"] = Brush(palette.Accent);
        resources["AccentHoverBrush"] = Brush(palette.Hover);
        resources["AccentDimBrush"] = Brush(palette.Dim);
        resources["OnAccentBrush"] = Brush(palette.OnAccent);
        resources["AccentSubtleBrush"] = Brush(WithAlpha(palette.Accent, 0x24));
        resources["AccentChipBgBrush"] = Brush(WithAlpha(palette.Accent, 0x12));
        resources["AccentChipBorderBrush"] = Brush(WithAlpha(palette.Accent, 0x40));
        resources["ApplicationIcon"] = LoadIcon(name);
    }

    internal static string IconAssetName(string? name) =>
        IconAssets.TryGetValue(name ?? "", out string? assetName)
            ? assetName
            : IconAssets["mint"];

    private static BitmapFrame LoadIcon(string? name)
    {
        using Stream stream = Application.GetResourceStream(
            new Uri($"Assets/{IconAssetName(name)}", UriKind.Relative)).Stream;
        var decoder = new IconBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames
            .OrderByDescending(candidate => candidate.PixelWidth)
            .First();
        frame.Freeze();
        return frame;
    }

    private static Palette New(string accent, string hover, string dim, string onAccent) =>
        new(Parse(accent), Parse(hover), Parse(dim), Parse(onAccent));

    private static Color Parse(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static SolidColorBrush Brush(Color color) => new(color);
}
