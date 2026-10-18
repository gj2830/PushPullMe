using System.Globalization;
using PushPullMe.Core;
using RsDocx.Core.Stl;

namespace PushPullMe.Editor;

/// <summary>The model is in metres, shown (and typed) in millimetres.</summary>
public static class Units
{
    /// <summary>Millimetres per metre: the view's unit against the model's.</summary>
    public const double Display = 1000;

    /// <summary>Metres as millimetres to show (at most two decimals, as the user writes numbers).</summary>
    public static string Mm(double metres) => (metres * Display).ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>A number typed (a comma or a point for the decimals).</summary>
    public static bool TryMm(string s, out double mm) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out mm);

    /// <summary>A distance with its parts along the axes.</summary>
    public static string Measure(Vec3 d) =>
        Lang.T($"{Mm(d.Length)} mm (x {Mm(Math.Abs(d.X))} · y {Mm(Math.Abs(d.Y))} · z {Mm(Math.Abs(d.Z))})", $"{Mm(d.Length)} mm (x {Mm(Math.Abs(d.X))} · y {Mm(Math.Abs(d.Y))} · z {Mm(Math.Abs(d.Z))})");
}
