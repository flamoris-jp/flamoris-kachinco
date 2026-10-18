namespace Kachinco.Core;

// Reference pixels scale by the short canvas dimension / 1080; anchors are normalized.
public sealed record CaptionStyle(string FontFamily = "Yu Gothic", double FontSize = 45,
    string Color = "#FFFFFF", string OutlineColor = "#000000", double OutlineWidth = 3.75,
    double X = .5, double Y = .9, double MaxWidth = .84,
    string Alignment = "Center", string VerticalAnchor = "Bottom",
    bool ShadowEnabled = false, string ShadowColor = "#80000000",
    double ShadowX = 2, double ShadowY = 2)
{
    public static CaptionStyle Default => new();
}

// Null means preserve that field, including in a multi-caption transaction.
public sealed record CaptionStylePatch(string? FontFamily = null, double? FontSize = null,
    string? Color = null, string? OutlineColor = null, double? OutlineWidth = null,
    double? X = null, double? Y = null, double? MaxWidth = null,
    string? Alignment = null, string? VerticalAnchor = null,
    bool? ShadowEnabled = null, string? ShadowColor = null,
    double? ShadowX = null, double? ShadowY = null);
