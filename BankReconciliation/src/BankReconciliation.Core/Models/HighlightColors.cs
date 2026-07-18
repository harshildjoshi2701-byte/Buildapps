namespace BankReconciliation.Core.Models;

/// <summary>
/// Hex colors (e.g. "#C6EFCE") used to fill matched rows. All configurable
/// from Settings. <see cref="CombinationPalette"/> holds several distinct
/// blue/purple shades — every transaction within one combination match
/// group gets the SAME shade (so an accountant can visually trace which
/// R365 rows belong to which bank deposit), while different groups get
/// different shades so adjacent groups in a filtered/sorted grid don't blur
/// together. All shades are deliberately from the same cool-toned family so
/// the sheet still reads as "blue = combination match" at a glance.
/// </summary>
public sealed class HighlightColors
{
    public string MatchedHex { get; set; } = "#C6EFCE";        // green
    public string ManualReviewHex { get; set; } = "#FFEB9C";   // yellow
    public string NoMatchHex { get; set; } = "#FFC7CE";        // red

    public List<string> CombinationPalette { get; set; } = new()
    {
        "#B4C7E7", "#C9C1F0", "#A9D4E8", "#CBB4E0", "#9FC5E8",
        "#B6A6D9", "#8EC6D9", "#D0A9E0", "#A3C4E0", "#BFA8DB",
    };

    public string ColorForGroup(int groupId)
    {
        if (CombinationPalette.Count == 0) return "#B4C7E7";
        var idx = ((groupId % CombinationPalette.Count) + CombinationPalette.Count) % CombinationPalette.Count;
        return CombinationPalette[idx];
    }

    public HighlightColors Clone() => new()
    {
        MatchedHex = MatchedHex,
        ManualReviewHex = ManualReviewHex,
        NoMatchHex = NoMatchHex,
        CombinationPalette = new List<string>(CombinationPalette),
    };
}
