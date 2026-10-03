using Raffaello.Core.Tracker;

namespace Raffaello.Core.Tests;

/// <summary>Real Hotel tracker shape names (03-Oct): room codes contain spaces and rooms drawn in parts carry suffixes.</summary>
public class PlanShapeResolveTests
{
    private static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "H3-SALT RM 14", "H1-L2-HOUSEKEEPING L2-003 (NO QS ROW)", "H6-L0-028", "H1-L2-235", "P2-106",
    };

    [Theory]
    [InlineData("RM_H3-SALT RM 14", "H3-SALT RM 14")]                                         // spaces inside the code
    [InlineData("RM_H1-L2-HOUSEKEEPING L2-003 (NO QS ROW)", "H1-L2-HOUSEKEEPING L2-003 (NO QS ROW)")]
    [InlineData("RM_H6-L0-028~2", "H6-L0-028")]                                                // second part of a room
    [InlineData("RM_H1-L2-235 (W)", "H1-L2-235")]                                              // west half
    [InlineData("RM_H1-L2-235 (E)~2", "H1-L2-235")]
    [InlineData("RM_P2-106 2", "P2-106")]                                                      // Branded duplicate suffix
    [InlineData("RM_H9-UNKNOWN ROOM~3", "H9-UNKNOWN ROOM")]                                    // unmatched keeps the full name
    public void Resolves_shape_to_room(string shape, string room) =>
        Assert.Equal(room, PlanDrawingParser.ResolveRoom(shape, Codes));
}
