using OpenVideoMenu.Dvd.Export;

namespace OpenVideoMenu.Dvd.Tests;

/// <summary>Where disc cells land in a rip: the real clock on NTSC, and titles that list a cell more than once.</summary>
public class CellTimingTests
{
    [Fact]
    public void Ntsc_cells_run_on_the_rips_clock()
    {
        // The disc says 10 min; NTSC times count 30 frames a second, so the video really runs 600.6 s.
        using var disc = SyntheticDisc.Open();
        Assert.Equal(600, disc.GetPgc(DvdDomain.Title, 1, 1)!.Cells[0].Duration.TotalSeconds, 3);
        Assert.Equal(600.6, CellMediaIndex.CellsOf(disc, "t1", 1, 1, 1, null).Duration, 3);
        Assert.Equal(1201.2, CellMediaIndex.CellsOfPgc(disc, "p2", 2, 1, 2).Duration, 3);
    }

    [Fact]
    public void Pal_cell_times_are_exact()
    {
        using var disc = SyntheticDisc.Open(pal: true);
        Assert.Equal(600, CellMediaIndex.CellsOf(disc, "t1", 1, 1, 1, null).Duration, 3);
    }

    [Fact]
    public void A_repeated_cell_is_laid_out_each_time_unless_the_file_holds_it_once()
    {
        using var disc = SyntheticDisc.Open(repeatCell: true);
        var every = CellMediaIndex.CellsOf(disc, "t2", 2, 2, 1, null);
        Assert.Equal(2, every.Cells.Count);
        Assert.Equal(new[] { 0, 300.3 }, every.Starts);
        Assert.Equal(600.6, every.Duration, 3);

        var once = CellMediaIndex.CellsOf(disc, "t2", 2, 2, 1, null, onceEach: true);
        Assert.Single(once.Cells);
        Assert.Equal(300.3, once.Duration, 3);
        Assert.Equal(300.3, CellMediaIndex.CellsOfPgc(disc, "p2", 2, 1, 2, onceEach: true).Duration, 3);

        // Every listing of the cell plays the one copy.
        var index = new CellMediaIndex(new[] { once });
        var cell = new PlayedCell(1, 2, 2, 1, 1, 300);
        var plan = index.Map(new[] { cell });
        Assert.Equal(("t2", 0.0), (plan!.Media, plan.Start));
    }
}
