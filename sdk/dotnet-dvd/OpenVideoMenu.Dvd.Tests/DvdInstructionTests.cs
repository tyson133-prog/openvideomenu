
namespace OpenVideoMenu.Dvd.Tests;

/// <summary>The decoded form of a command must do exactly what running its bytes does.</summary>
public class DvdInstructionTests
{
    [Fact]
    public void Decoded_commands_behave_like_the_bytes()
    {
        var rng = new Random(1234);
        var bytes = new byte[8];
        for (var i = 0; i < 300_000; i++)
        {
            rng.NextBytes(bytes);
            // Keep the command types the VM knows (0-6) most of the time.
            bytes[0] = (byte)((bytes[0] & 0x1F) | (rng.Next(8) << 5));
            var cmd = (byte[])bytes.Clone();

            var state = new VmState();
            for (var g = 0; g < 16; g++) state.Gprm[g] = (ushort)(rng.Next(4) == 0 ? rng.Next(65536) : rng.Next(8));
            for (var s = 0; s < 24; s++) if (rng.Next(3) == 0) state.SetSprm(s, (ushort)rng.Next(16));

            var a = state.Clone();
            var b = state.Clone();
            var expected = DvdCommands.Evaluate(cmd, a);
            var actual = DvdCommands.Execute(DvdCommands.Decode(cmd), b);

            Assert.True(expected == actual, $"{DvdCommands.Disassemble(cmd)}: {expected} vs {actual}");
            Assert.True(a.Gprm.SequenceEqual(b.Gprm), $"{DvdCommands.Disassemble(cmd)}: GPRMs differ");
            Assert.True(a.Sprm.SequenceEqual(b.Sprm), $"{DvdCommands.Disassemble(cmd)}: SPRMs differ");
        }
    }
}
