namespace OpenVideoMenu.Dvd.Tests;

public class NavPackTests
{
    /// <summary>Builds a NAV sector with a pack header, system header and PCI packet.</summary>
    private static byte[] NavSector(int buttonCount, int btnMd, Action<byte[], int>? writeButtons = null, int forcedActivate = 0)
    {
        var b = new byte[2048];
        b[0] = 0; b[1] = 0; b[2] = 1; b[3] = 0xBA;
        b[13] = 0xF8; // no stuffing
        // System header at 14 (length 18 -> 24 bytes total).
        b[14] = 0; b[15] = 0; b[16] = 1; b[17] = 0xBB; b[18] = 0; b[19] = 18;
        // PCI private stream 2 at 38.
        var p = 38;
        b[p] = 0; b[p + 1] = 0; b[p + 2] = 1; b[p + 3] = 0xBF; b[p + 4] = 0x03; b[p + 5] = 0xD4; b[p + 6] = 0x00;
        var pci = p + 7;
        var hli = pci + 0x60;
        b[hli + 1] = 1; // hli_ss = 1
        b[hli + 14] = (byte)(btnMd >> 8);
        b[hli + 15] = (byte)btnMd;
        b[hli + 17] = (byte)buttonCount;
        b[hli + 21] = (byte)forcedActivate;
        writeButtons?.Invoke(b, hli + 22 + 24);
        return b;
    }

    private static void WriteButton(byte[] b, int at, int x0, int x1, int y0, int y1, int up, int down, int left, int right)
    {
        b[at] = (byte)((x0 >> 4) & 0x3F);
        b[at + 1] = (byte)(((x0 & 0xF) << 4) | ((x1 >> 8) & 3));
        b[at + 2] = (byte)x1;
        b[at + 3] = (byte)((y0 >> 4) & 0x3F);
        b[at + 4] = (byte)(((y0 & 0xF) << 4) | ((y1 >> 8) & 3));
        b[at + 5] = (byte)y1;
        b[at + 6] = (byte)up;
        b[at + 7] = (byte)down;
        b[at + 8] = (byte)left;
        b[at + 9] = (byte)right;
        new byte[] { 0x30, 0x02, 0, 0, 0, 0x05, 0, 0 }.CopyTo(b, at + 10);
    }

    [Fact]
    public void Parses_button_rectangles_neighbors_and_commands()
    {
        var sector = NavSector(2, 0x1000, (b, at) =>
        {
            WriteButton(b, at, 492, 643, 270, 313, 1, 2, 1, 1);
            WriteButton(b, at + 18, 470, 642, 321, 363, 1, 2, 2, 2);
        });
        Assert.True(NavPackReader.TryLocatePciForDebug(sector, out var pci));
        var hl = NavPackReader.ParseHighlight(sector, pci, wideMenu: false, sector: 0);
        Assert.NotNull(hl);
        Assert.Equal(2, hl!.Buttons.Count);
        var b1 = hl.Buttons[0];
        Assert.Equal((492, 643, 270, 313), (b1.X0, b1.X1, b1.Y0, b1.Y1));
        Assert.Equal(2, b1.Down);
        Assert.Equal("3002000000050000", Convert.ToHexString(b1.Command));
    }

    [Fact]
    public void Picks_the_wide_button_group_for_16x9_menus()
    {
        // Two groups: group 1 = 4:3 normal (type 0), group 2 = wide (type 1). 18 buttons per group.
        var btnMd = (2 << 12) | (0 << 8) | (1 << 4);
        var sector = NavSector(1, btnMd, (b, at) =>
        {
            WriteButton(b, at, 10, 20, 10, 20, 1, 1, 1, 1);
            WriteButton(b, at + 18 * 18, 100, 200, 100, 200, 1, 1, 1, 1);
        });
        NavPackReader.TryLocatePciForDebug(sector, out var pci);
        var wide = NavPackReader.ParseHighlight(sector, pci, wideMenu: true, sector: 0)!;
        var normal = NavPackReader.ParseHighlight(sector, pci, wideMenu: false, sector: 0)!;
        Assert.Equal(2, wide.SelectedGroup);
        Assert.Equal(100, wide.Buttons[0].X0);
        Assert.Equal(1, normal.SelectedGroup);
        Assert.Equal(10, normal.Buttons[0].X0);
    }

    [Fact]
    public void No_highlight_when_hli_status_is_zero()
    {
        var sector = NavSector(2, 0x1000);
        NavPackReader.TryLocatePciForDebug(sector, out var pci);
        sector[pci + 0x60 + 1] = 0;
        Assert.Null(NavPackReader.ParseHighlight(sector, pci, false, 0));
    }
}
