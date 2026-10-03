
namespace OpenVideoMenu.Dvd.Tests;

/// <summary>DVD VM command decoding. Byte sequences are taken from real discs mapped during development.</summary>
public class DvdCommandTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void JumpTT_decodes_title_number()
    {
        var r = DvdCommands.Evaluate(Hex("3002000000050000"), new VmState());
        Assert.Equal(EvalKind.Link, r.Kind);
        Assert.Equal(new DvdLink(LinkCmd.JumpTT, 5), r.Link);
    }

    [Fact]
    public void JumpVTS_PTT_decodes_title_and_chapter()
    {
        var r = DvdCommands.Evaluate(Hex("3005000100010000"), new VmState());
        Assert.Equal(LinkCmd.JumpVTS_PTT, r.Link.Cmd);
        Assert.Equal(1, r.Link.Data1);
        Assert.Equal(1, r.Link.Data2);
    }

    [Fact]
    public void LinkPGCN_decodes_pgc()
    {
        var r = DvdCommands.Evaluate(Hex("2004000000000003"), new VmState());
        Assert.Equal(new DvdLink(LinkCmd.LinkPGCN, 3), r.Link);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    public void Conditional_jump_compares_registers_in_bytes_6_and_7(ushort g0, ushort g1, bool jumps)
    {
        // "if (g0 == g1) JumpSS VMGM PGC 4" from The Honeymooners' first play. Reading the
        // condition from bytes 3-5 (the link layout) sent playback into a lockout loop.
        var s = new VmState();
        s.Gprm[0] = g0;
        s.Gprm[1] = g1;
        var r = DvdCommands.Evaluate(Hex("3026000400C00001"), s);
        if (jumps)
        {
            Assert.Equal(EvalKind.Link, r.Kind);
            Assert.Equal(new DvdLink(LinkCmd.JumpSS_VMGM_PGC, 4), r.Link);
        }
        else
        {
            Assert.Equal(EvalKind.None, r.Kind);
        }
    }

    [Fact]
    public void Set_immediate_writes_gprm()
    {
        var s = new VmState();
        var r = DvdCommands.Evaluate(Hex("7100000B00010000"), s);
        Assert.Equal(EvalKind.None, r.Kind);
        Assert.Equal(1, s.Gprm[11]);
    }

    [Fact]
    public void Set_then_LinkTailPGC()
    {
        // The Matrix language menu: "g1 = 3; LinkTailPGC".
        var s = new VmState();
        var r = DvdCommands.Evaluate(Hex("710100010003000D"), s);
        Assert.Equal(3, s.Gprm[1]);
        Assert.Equal(LinkCmd.LinkTailPGC, r.Link.Cmd);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void Compare_immediate_then_link(ushort g7, bool links)
    {
        var s = new VmState();
        s.Gprm[7] = g7;
        var r = DvdCommands.Evaluate(Hex("20A1000700010010"), s);
        Assert.Equal(links ? EvalKind.Link : EvalKind.None, r.Kind);
        if (links) Assert.Equal(LinkCmd.LinkRSM, r.Link.Cmd);
    }

    [Fact]
    public void SetHL_BTNN_sets_sprm8()
    {
        var s = new VmState();
        DvdCommands.Evaluate(Hex("5600000010000000"), s);
        Assert.Equal(4096, s.Sprm[8]);
        Assert.Equal(4, s.HighlightedButton);
    }

    [Fact]
    public void Conditional_goto()
    {
        var s = new VmState();
        s.Gprm[15] = 1;
        var r = DvdCommands.Evaluate(Hex("00A1000F00010012"), s);
        Assert.Equal(EvalKind.Goto, r.Kind);
        Assert.Equal(18, r.Line);
    }

    [Fact]
    public void SetGPRMMD_with_link()
    {
        // Jonah: "SetGPRMMD g0 = 1024; LinkPGCN 2".
        var s = new VmState();
        var r = DvdCommands.Evaluate(Hex("5304040000000002"), s);
        Assert.Equal(1024, s.Gprm[0]);
        Assert.Equal(new DvdLink(LinkCmd.LinkPGCN, 2), r.Link);
    }

    [Fact]
    public void Disassembly_is_readable()
    {
        Assert.Contains("JumpTT 5", DvdCommands.Disassemble(Hex("3002000000050000")));
        Assert.Contains("if (g0 == g1)", DvdCommands.Disassemble(Hex("3026000400C00001")));
    }
}
