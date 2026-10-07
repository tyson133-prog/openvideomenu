using OpenVideoMenu.Dvd.Export;

namespace OpenVideoMenu.Dvd.Tests;

/// <summary>
/// Titles that play the same video: an exact duplicate plays the same cells; a near duplicate (a "white rabbit" mode)
/// has a few of its own, which a rip of the original alone doesn't hold.
/// </summary>
public class DuplicateTitleTests
{
    [Fact]
    public void A_title_with_the_same_cells_is_a_duplicate()
    {
        using var disc = SyntheticDisc.Open(second: SecondTitle.SameCells);
        var t2 = DiscMapBuilder.Build(disc).GetTitle(2)!;
        Assert.Equal(1, t2.DuplicateOf);
        Assert.False(t2.IsNearDuplicate);
        Assert.Empty(t2.OwnCells);
    }

    [Fact]
    public void A_title_with_cells_of_its_own_is_a_near_duplicate_that_lists_them()
    {
        using var disc = SyntheticDisc.Open(second: SecondTitle.NearlySameCells);
        var t2 = DiscMapBuilder.Build(disc).GetTitle(2)!;
        Assert.Equal(1, t2.DuplicateOf); // still the same video: the original's file plays it
        Assert.True(t2.IsNearDuplicate);
        var own = Assert.Single(t2.OwnCells);
        Assert.Equal((1, 2, 2, 110u, 119u), (own.Vtsn, own.Pgcn, own.Cell, own.FirstSector, own.LastSector));
        Assert.Equal(10, own.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Cells_no_file_holds_inside_a_played_title_are_warned_about()
    {
        using var disc = SyntheticDisc.Open(second: SecondTitle.NearlySameCells);
        // Only title 1 is ripped: title 2 plays from its file, but its own cell is in no file.
        var set = DvdMedia.OneFilePerTitle(disc, t => t == 1 ? "Title 1.mkv" : null);
        var builder = new MenuManifestBuilder(disc, DiscMapBuilder.Build(disc), set.Media, set.Index);
        Assert.True(builder.Build().Validate().Ok);
        var warning = Assert.Single(builder.Warnings, w => w.Code == DvdWarningCode.CellsNotRipped);
        Assert.Contains("title PGC 2 cell 2", warning.Message);
        Assert.Contains((1, 2, 2), builder.CellsNotRipped);
    }

    [Fact]
    public void A_rip_that_holds_every_cell_has_no_such_warning()
    {
        using var disc = SyntheticDisc.Open(second: SecondTitle.NearlySameCells);
        var set = DvdMedia.OneFilePerTitle(disc, t => $"Title {t}.mkv");
        var builder = new MenuManifestBuilder(disc, DiscMapBuilder.Build(disc), set.Media, set.Index);
        builder.Build();
        Assert.DoesNotContain(builder.Warnings, w => w.Code == DvdWarningCode.CellsNotRipped);
    }
}
