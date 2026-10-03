using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Cups.CupHistory;

/// <summary>
/// Stable team identifiers shared by the Cup history read slices. A Color team
/// key is the lower-case sporting-color name; a Type team key is the stored
/// creature type itself and needs no mapping.
/// </summary>
public static class CupTeamKeys
{
    public const string ColorCup = "Color";

    public const string TypeCup = "Type";

    public static string ColorKey(SportingColor color) => color switch
    {
        SportingColor.White => "white",
        SportingColor.Blue => "blue",
        SportingColor.Black => "black",
        SportingColor.Red => "red",
        SportingColor.Green => "green",
        SportingColor.Multicolor => "multicolor",
        SportingColor.Hybrid => "hybrid",
        SportingColor.Colorless => "colorless",
        _ => throw new InvalidOperationException($"Unknown sporting color {(int)color}."),
    };

    public static string ColorName(SportingColor color) => color.ToString();

    /// <summary>Names only: a numeric key such as "3" never resolves to a color.</summary>
    public static bool TryParseColorKey(string? teamKey, out SportingColor color)
    {
        foreach (SportingColor candidate in Enum.GetValues<SportingColor>())
        {
            if (string.Equals(ColorKey(candidate), teamKey, StringComparison.OrdinalIgnoreCase))
            {
                color = candidate;
                return true;
            }
        }

        color = default;
        return false;
    }
}
