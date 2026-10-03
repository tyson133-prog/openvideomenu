using System;
using System.Collections.Generic;

namespace OpenVideoMenu.Dvd.Commands;

public enum OperandKind
{
    Immediate,
    Gprm,
    Sprm,
}

/// <summary>A command operand: a 16-bit constant, a general register (GPRM 0-15) or a system register (SPRM 0-23).</summary>
public readonly record struct Operand(OperandKind Kind, int Value)
{
    public static Operand Imm(int v) => new(OperandKind.Immediate, v & 0xFFFF);

    /// <summary>A register byte as commands encode it: bit 7 set = SPRM (low 5 bits), else GPRM (low 4 bits).</summary>
    public static Operand Reg(int r) => (r & 0x80) != 0 ? new(OperandKind.Sprm, r & 0x1F) : new(OperandKind.Gprm, r & 0x0F);

    public static Operand G(int n) => new(OperandKind.Gprm, n & 0x0F);

    public override string ToString() => Kind switch
    {
        OperandKind.Immediate => Value.ToString(),
        OperandKind.Gprm => $"g{Value}",
        _ => $"s{Value}",
    };
}

/// <summary>A comparison: 1 = and (any bit), 2 ==, 3 !=, 4 &gt;=, 5 &gt;, 6 &lt;=, 7 &lt;.</summary>
public sealed record DvdCondition(Operand Left, int Op, Operand Right)
{
    public override string ToString() => $"{Left} {(Op is >= 1 and <= 7 ? new[] { "", "&", "==", "!=", ">=", ">", "<=", "<" }[Op] : "?")} {Right}";
}

/// <summary>One effect of a command, in the order it happens.</summary>
public abstract record DvdOp;

/// <summary>GPRM set: 1 mov, 2 swap (with <see cref="SwapWith"/>), 3 add, 4 sub, 5 mul, 6 div, 7 mod, 8 rnd, 9 and, 10 or, 11 xor.</summary>
public sealed record SetGprmOp(int Register, int Op, Operand Source, int SwapWith) : DvdOp;

/// <summary>SetSTN: the audio, subpicture and angle streams (each null when not changed).</summary>
public sealed record SetStreamsOp(Operand? Audio, Operand? Subpicture, Operand? Angle) : DvdOp;

public sealed record SetNavTimerOp(Operand Seconds, int Pgcn) : DvdOp;

/// <summary>SetGPRMMD: a register set, possibly switching it to counter mode (counting seconds).</summary>
public sealed record SetGprmModeOp(int Register, Operand Value, bool Counter) : DvdOp;

/// <summary>SetHL_BTNN: SPRM 8 (the highlighted button number × 1024).</summary>
public sealed record SetHighlightOp(Operand Value) : DvdOp;

/// <summary>SetAMXMD: SPRM 11, which karaoke audio channels mix in.</summary>
public sealed record SetKaraokeOp(Operand Value) : DvdOp;

/// <summary>SetTmpPML: the temporary parental level, then a Goto.</summary>
public sealed record SetParentalOp(int Level) : DvdOp;

public sealed record GotoOp(int Line) : DvdOp;

public sealed record BreakOp : DvdOp;

public sealed record LinkOp(DvdLink Link) : DvdOp;

/// <summary>
/// A navigation command decoded into effects: <see cref="Before"/> always runs, then <see cref="Condition"/>
/// is tested once, <see cref="Guarded"/> runs when it holds (always when there's no condition), and
/// <see cref="After"/> always runs. This covers every command type, e.g. type 4 sets a register first and then
/// tests it for its link, and type 6 tests before a conditional set but links regardless.
/// </summary>
public sealed class DvdInstruction
{
    public static readonly DvdInstruction Nop = new();

    public IReadOnlyList<DvdOp> Before { get; init; } = Array.Empty<DvdOp>();
    public DvdCondition? Condition { get; init; }
    public IReadOnlyList<DvdOp> Guarded { get; init; } = Array.Empty<DvdOp>();
    public IReadOnlyList<DvdOp> After { get; init; } = Array.Empty<DvdOp>();

    /// <summary>Registers this command reads (for deciding which player state a disc needs).</summary>
    public IEnumerable<Operand> Reads()
    {
        if (Condition is { } c) { yield return c.Left; yield return c.Right; }
        foreach (var op in AllOps())
        {
            switch (op)
            {
                case SetGprmOp s:
                    yield return s.Source;
                    if (s.Op != 1 && s.Op != 2) yield return Operand.G(s.Register);
                    break;
                case SetStreamsOp st:
                    if (st.Audio is { } a) yield return a;
                    if (st.Subpicture is { } p) yield return p;
                    if (st.Angle is { } g) yield return g;
                    break;
                case SetNavTimerOp t: yield return t.Seconds; break;
                case SetGprmModeOp m: yield return m.Value; break;
                case SetHighlightOp h: yield return h.Value; break;
                case SetKaraokeOp k: yield return k.Value; break;
            }
        }
    }

    public IEnumerable<DvdOp> AllOps()
    {
        foreach (var o in Before) yield return o;
        foreach (var o in Guarded) yield return o;
        foreach (var o in After) yield return o;
    }
}
