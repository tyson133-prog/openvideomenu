using System;
using System.Text;

namespace OpenVideoMenu.Dvd.Commands;

/// <summary>Navigation produced by a VM command. Values 1..16 match the DVD "link sub-instruction" codes.</summary>
public enum LinkCmd
{
    NoLink = 0,
    LinkTopC = 1,
    LinkNextC = 2,
    LinkPrevC = 3,
    LinkTopPG = 5,
    LinkNextPG = 6,
    LinkPrevPG = 7,
    LinkTopPGC = 9,
    LinkNextPGC = 10,
    LinkPrevPGC = 11,
    LinkGoUpPGC = 12,
    LinkTailPGC = 13,
    LinkRSM = 16,

    LinkPGCN = 100,
    LinkPTTN,
    LinkPGN,
    LinkCN,
    Exit,
    JumpTT,
    JumpVTS_TT,
    JumpVTS_PTT,
    JumpSS_FP,
    JumpSS_VMGM_MENU,
    JumpSS_VTSM,
    JumpSS_VMGM_PGC,
    CallSS_FP,
    CallSS_VMGM_MENU,
    CallSS_VTSM,
    CallSS_VMGM_PGC,
}

public readonly record struct DvdLink(LinkCmd Cmd, int Data1 = 0, int Data2 = 0, int Data3 = 0)
{
    public override string ToString() => Cmd switch
    {
        LinkCmd.LinkPGCN => $"LinkPGCN {Data1}",
        LinkCmd.LinkPTTN => $"LinkPTTN {Data1}" + Btn(Data2),
        LinkCmd.LinkPGN => $"LinkPGN {Data1}" + Btn(Data2),
        LinkCmd.LinkCN => $"LinkCN {Data1}" + Btn(Data2),
        LinkCmd.JumpTT => $"JumpTT {Data1}",
        LinkCmd.JumpVTS_TT => $"JumpVTS_TT {Data1}",
        LinkCmd.JumpVTS_PTT => $"JumpVTS_PTT {Data1}:{Data2}",
        LinkCmd.JumpSS_VMGM_MENU => $"JumpSS VMGM menu {(MenuType)Data1}",
        LinkCmd.JumpSS_VTSM => $"JumpSS VTSM vts {Data1} ttn {Data2} menu {(MenuType)Data3}",
        LinkCmd.JumpSS_VMGM_PGC => $"JumpSS VMGM PGC {Data1}",
        LinkCmd.CallSS_FP => $"CallSS FP rsm {Data1}",
        LinkCmd.CallSS_VMGM_MENU => $"CallSS VMGM menu {(MenuType)Data1} rsm {Data2}",
        LinkCmd.CallSS_VTSM => $"CallSS VTSM menu {(MenuType)Data1} rsm {Data2}",
        LinkCmd.CallSS_VMGM_PGC => $"CallSS VMGM PGC {Data1} rsm {Data2}",
        _ => Cmd.ToString() + Btn(Data1),
    };

    private static string Btn(int b) => b != 0 ? $" (button {b})" : "";
}

public enum EvalKind
{
    None,
    Goto,
    Break,
    Link,
}

public readonly record struct EvalResult(EvalKind Kind, int Line = 0, DvdLink Link = default)
{
    public static readonly EvalResult Nothing = new(EvalKind.None);
}

/// <summary>
/// Evaluates 8-byte DVD-Video navigation commands against VM registers.
/// Field layout follows the public DVD-Video command documentation (mpucoder.com "DVD-Video
/// Navigation Commands" and dvd.sourceforge.net); bit numbers count from 63 (MSB of byte 0).
/// </summary>
public static class DvdCommands
{
    private static ulong Word(byte[] c) =>
        (ulong)c[0] << 56 | (ulong)c[1] << 48 | (ulong)c[2] << 40 | (ulong)c[3] << 32 |
        (ulong)c[4] << 24 | (ulong)c[5] << 16 | (ulong)c[6] << 8 | c[7];

    /// <summary>Reads <paramref name="count"/> bits whose highest bit is <paramref name="start"/>.</summary>
    private static int Bits(ulong w, int start, int count) =>
        (int)((w >> (start - count + 1)) & ((1UL << count) - 1));

    private static ushort Reg(VmState s, int r) =>
        (r & 0x80) != 0 ? s.GetSprm(r & 0x1F) : s.Gprm[r & 0x0F];

    private static int RegOrData(VmState s, ulong w, bool immediate, int start) =>
        immediate ? Bits(w, start, 16) : Reg(s, Bits(w, start - 8, 8));

    private static bool Compare(int op, int a, int b) => op switch
    {
        1 => (a & b) != 0,
        2 => a == b,
        3 => a != b,
        4 => a >= b,
        5 => a > b,
        6 => a <= b,
        7 => a < b,
        _ => true,
    };

    // --- conditions -------------------------------------------------------

    private static bool If1(VmState s, ulong w)
    {
        var op = Bits(w, 54, 3);
        if (op == 0) return true;
        return Compare(op, Reg(s, Bits(w, 39, 8)), RegOrData(s, w, Bits(w, 55, 1) != 0, 31));
    }

    private static bool If2(VmState s, ulong w)
    {
        var op = Bits(w, 54, 3);
        if (op == 0) return true;
        return Compare(op, Reg(s, Bits(w, 15, 8)), Reg(s, Bits(w, 7, 8)));
    }

    private static bool If3(VmState s, ulong w)
    {
        var op = Bits(w, 54, 3);
        if (op == 0) return true;
        return Compare(op, s.Gprm[Bits(w, 43, 4)], RegOrData(s, w, Bits(w, 55, 1) != 0, 15));
    }

    private static bool If4(VmState s, ulong w)
    {
        var op = Bits(w, 54, 3);
        if (op == 0) return true;
        return Compare(op, s.Gprm[Bits(w, 51, 4)], RegOrData(s, w, Bits(w, 55, 1) != 0, 31));
    }

    private static bool If5(VmState s, ulong w)
    {
        var op = Bits(w, 54, 3);
        if (op == 0) return true;
        return Bits(w, 60, 1) != 0
            ? Compare(op, Reg(s, Bits(w, 31, 8)), Reg(s, Bits(w, 23, 8)))
            : Compare(op, Reg(s, Bits(w, 39, 8)), RegOrData(s, w, Bits(w, 55, 1) != 0, 31));
    }

    // --- set operations ---------------------------------------------------

    private static void SetOp(VmState s, int op, int reg, int reg2, int data)
    {
        reg &= 0x0F;
        reg2 &= 0x0F;
        var cur = s.Gprm[reg];
        switch (op)
        {
            case 1: s.Gprm[reg] = (ushort)data; break;
            case 2: // swap
                s.Gprm[reg2] = cur;
                s.Gprm[reg] = (ushort)data;
                break;
            case 3: s.Gprm[reg] = (ushort)Math.Min(cur + data, 0xFFFF); break;
            case 4: s.Gprm[reg] = (ushort)Math.Max(cur - data, 0); break;
            case 5: s.Gprm[reg] = (ushort)Math.Min(cur * data, 0xFFFF); break;
            case 6: s.Gprm[reg] = data != 0 ? (ushort)(cur / data) : (ushort)0xFFFF; break;
            case 7: s.Gprm[reg] = data != 0 ? (ushort)(cur % data) : (ushort)0xFFFF; break;
            case 8: s.Gprm[reg] = (ushort)(data > 0 ? s.NextRandom(data) : 1); break;
            case 9: s.Gprm[reg] = (ushort)(cur & data); break;
            case 10: s.Gprm[reg] = (ushort)(cur | data); break;
            case 11: s.Gprm[reg] = (ushort)(cur ^ data); break;
        }
    }

    /// <summary>Set used by type 3: register in byte 3, data in bytes 4-5 (or register in byte 5).</summary>
    private static void Set1(VmState s, ulong w, bool cond)
    {
        var op = Bits(w, 59, 4);
        var data = RegOrData(s, w, Bits(w, 60, 1) != 0, 31);
        if (cond && op != 0) SetOp(s, op, Bits(w, 35, 4), Bits(w, 19, 4), data);
    }

    /// <summary>Set used by types 4-6: register in byte 1, data in bytes 2-3 (or register in byte 3).</summary>
    private static void Set2(VmState s, ulong w, bool cond)
    {
        var op = Bits(w, 59, 4);
        var data = RegOrData(s, w, Bits(w, 60, 1) != 0, 47);
        if (cond && op != 0) SetOp(s, op, Bits(w, 51, 4), Bits(w, 35, 4), data);
    }

    // --- links ------------------------------------------------------------

    private static EvalResult LinkSub(ulong w, bool cond)
    {
        var button = Bits(w, 15, 6);
        var op = Bits(w, 4, 5);
        if (op is 0 or 4 or 8 or 14 or 15 || op > 16 || !cond) return EvalResult.Nothing;
        return new EvalResult(EvalKind.Link, Link: new DvdLink((LinkCmd)op, button));
    }

    private static EvalResult LinkInstr(ulong w, bool cond)
    {
        switch (Bits(w, 51, 4))
        {
            case 1: return LinkSub(w, cond);
            case 4: return cond ? L(LinkCmd.LinkPGCN, Bits(w, 14, 15)) : EvalResult.Nothing;
            case 5: return cond ? L(LinkCmd.LinkPTTN, Bits(w, 9, 10), Bits(w, 15, 6)) : EvalResult.Nothing;
            case 6: return cond ? L(LinkCmd.LinkPGN, Bits(w, 6, 7), Bits(w, 15, 6)) : EvalResult.Nothing;
            case 7: return cond ? L(LinkCmd.LinkCN, Bits(w, 7, 8), Bits(w, 15, 6)) : EvalResult.Nothing;
            default: return EvalResult.Nothing;
        }
    }

    private static EvalResult JumpInstr(ulong w, bool cond)
    {
        if (!cond) return EvalResult.Nothing;
        switch (Bits(w, 51, 4))
        {
            case 1: return L(LinkCmd.Exit);
            case 2: return L(LinkCmd.JumpTT, Bits(w, 22, 7));
            case 3: return L(LinkCmd.JumpVTS_TT, Bits(w, 22, 7));
            case 5: return L(LinkCmd.JumpVTS_PTT, Bits(w, 22, 7), Bits(w, 41, 10));
            case 6:
                return Bits(w, 23, 2) switch
                {
                    0 => L(LinkCmd.JumpSS_FP),
                    1 => L(LinkCmd.JumpSS_VMGM_MENU, Bits(w, 19, 4)),
                    2 => L(LinkCmd.JumpSS_VTSM, Bits(w, 31, 8), Bits(w, 39, 8), Bits(w, 19, 4)),
                    _ => L(LinkCmd.JumpSS_VMGM_PGC, Bits(w, 46, 15)),
                };
            case 8:
                return Bits(w, 23, 2) switch
                {
                    0 => L(LinkCmd.CallSS_FP, Bits(w, 31, 8)),
                    1 => L(LinkCmd.CallSS_VMGM_MENU, Bits(w, 19, 4), Bits(w, 31, 8)),
                    2 => L(LinkCmd.CallSS_VTSM, Bits(w, 19, 4), Bits(w, 31, 8)),
                    _ => L(LinkCmd.CallSS_VMGM_PGC, Bits(w, 46, 15), Bits(w, 31, 8)),
                };
            default: return EvalResult.Nothing;
        }
    }

    private static EvalResult L(LinkCmd c, int d1 = 0, int d2 = 0, int d3 = 0) =>
        new(EvalKind.Link, Link: new DvdLink(c, d1, d2, d3));

    // --- system set -------------------------------------------------------

    private static EvalResult SystemSet(VmState s, ulong w, bool cond)
    {
        var imm = Bits(w, 60, 1) != 0;
        switch (Bits(w, 59, 4))
        {
            case 1: // SetSTN: audio, subpicture, angle
                for (var i = 1; i <= 3; i++)
                {
                    var b = Bits(w, 63 - (2 + i) * 8, 8);
                    if ((b & 0x80) == 0) continue;
                    var v = imm ? b & 0x7F : s.Gprm[b & 0x0F];
                    if (cond) s.SetSprm(i, (ushort)v);
                }
                break;
            case 2: // SetNVTMR
                {
                    var t = RegOrData(s, w, imm, 47);
                    var pgcn = Bits(w, 15, 16);
                    if (cond) { s.SetSprm(9, (ushort)t); s.SetSprm(10, (ushort)pgcn); }
                    break;
                }
            case 3: // SetGPRMMD
                {
                    var v = RegOrData(s, w, imm, 47);
                    if (cond) s.Gprm[Bits(w, 19, 4)] = (ushort)v;
                    break;
                }
            case 4: // SetAMXMD
                {
                    var v = RegOrData(s, w, imm, 31);
                    if (cond) s.SetSprm(11, (ushort)v);
                    break;
                }
            case 6: // SetHL_BTNN
                {
                    var v = RegOrData(s, w, imm, 31);
                    if (cond) s.SetSprm(8, (ushort)v);
                    break;
                }
        }
        return Bits(w, 51, 4) != 0 ? LinkInstr(w, cond) : EvalResult.Nothing;
    }

    // --- entry point ------------------------------------------------------

    /// <summary>Evaluates one command, mutating <paramref name="s"/> and returning any control transfer.</summary>
    public static EvalResult Evaluate(byte[] cmd, VmState s)
    {
        if (cmd is null || cmd.Length < 8) return EvalResult.Nothing;
        var w = Word(cmd);

        switch (Bits(w, 63, 3))
        {
            case 0:
                {
                    var cond = If1(s, w);
                    switch (Bits(w, 51, 4))
                    {
                        case 1: return cond ? new EvalResult(EvalKind.Goto, Bits(w, 7, 8)) : EvalResult.Nothing;
                        case 2: return cond ? new EvalResult(EvalKind.Break) : EvalResult.Nothing;
                        case 3:
                            if (!cond) return EvalResult.Nothing;
                            s.SetSprm(13, (ushort)Bits(w, 11, 4));
                            return new EvalResult(EvalKind.Goto, Bits(w, 7, 8));
                        default: return EvalResult.Nothing;
                    }
                }
            case 1:
                // Links keep their operands in bytes 6-7, so their condition uses bytes 3-5;
                // jumps keep operands in bytes 2-5, so their condition compares the registers in bytes 6-7.
                return Bits(w, 60, 1) != 0 ? JumpInstr(w, If2(s, w)) : LinkInstr(w, If1(s, w));
            case 2:
                return SystemSet(s, w, If2(s, w));
            case 3:
                {
                    var cond = If3(s, w);
                    Set1(s, w, cond);
                    return Bits(w, 51, 4) != 0 ? LinkInstr(w, cond) : EvalResult.Nothing;
                }
            case 4:
                {
                    Set2(s, w, true);
                    return LinkSub(w, If4(s, w));
                }
            case 5:
                {
                    var cond = If5(s, w);
                    Set2(s, w, cond);
                    return LinkSub(w, cond);
                }
            case 6:
                {
                    var cond = If5(s, w);
                    Set2(s, w, cond);
                    return LinkSub(w, true);
                }
            default:
                return EvalResult.Nothing;
        }
    }

    /// <summary>
    /// The command links to the resume point (LinkRSM), whatever its condition says: "if the film was
    /// played (g7 != 0), resume it" reads as a resume button even while the simulation hasn't played it.
    /// </summary>
    public static bool IsResumeLink(byte[] cmd)
    {
        if (cmd is null || cmd.Length < 8) return false;
        var w = Word(cmd);
        var type = Bits(w, 63, 3);
        var linkInstr = type == 1 && Bits(w, 60, 1) == 0 || type is 2 or 3;
        if (linkInstr ? Bits(w, 51, 4) != 1 : type is not (4 or 5 or 6)) return false;
        return Bits(w, 4, 5) == (int)LinkCmd.LinkRSM;
    }

    // --- decoding (for translating programs rather than running them) ------

    private static Operand RegOrDataOperand(ulong w, bool immediate, int start) =>
        immediate ? Operand.Imm(Bits(w, start, 16)) : Operand.Reg(Bits(w, start - 8, 8));

    private static DvdCondition? Cond(ulong w, Operand left, Operand right)
    {
        var op = Bits(w, 54, 3);
        return op == 0 ? null : new DvdCondition(left, op, right);
    }

    private static DvdCondition? Cond1(ulong w) =>
        Cond(w, Operand.Reg(Bits(w, 39, 8)), RegOrDataOperand(w, Bits(w, 55, 1) != 0, 31));

    private static DvdCondition? Cond2(ulong w) =>
        Cond(w, Operand.Reg(Bits(w, 15, 8)), Operand.Reg(Bits(w, 7, 8)));

    private static DvdCondition? Cond5(ulong w) => Bits(w, 60, 1) != 0
        ? Cond(w, Operand.Reg(Bits(w, 31, 8)), Operand.Reg(Bits(w, 23, 8)))
        : Cond(w, Operand.Reg(Bits(w, 39, 8)), RegOrDataOperand(w, Bits(w, 55, 1) != 0, 31));

    private static DvdOp? LinkOpOf(EvalResult r) => r.Kind == EvalKind.Link ? new LinkOp(r.Link) : null;

    /// <summary>Set used by type 3: register in byte 3, data in bytes 4-5 (or register in byte 5).</summary>
    private static DvdOp? Set1Op(ulong w)
    {
        var op = Bits(w, 59, 4);
        return op == 0 ? null : new SetGprmOp(Bits(w, 35, 4), op, RegOrDataOperand(w, Bits(w, 60, 1) != 0, 31), Bits(w, 19, 4));
    }

    /// <summary>Set used by types 4-6: register in byte 1, data in bytes 2-3 (or register in byte 3).</summary>
    private static DvdOp? Set2Op(ulong w)
    {
        var op = Bits(w, 59, 4);
        return op == 0 ? null : new SetGprmOp(Bits(w, 51, 4), op, RegOrDataOperand(w, Bits(w, 60, 1) != 0, 47), Bits(w, 35, 4));
    }

    private static List<DvdOp> Ops(params DvdOp?[] ops)
    {
        var list = new List<DvdOp>(ops.Length);
        foreach (var o in ops) if (o is not null) list.Add(o);
        return list;
    }

    private static DvdOp? SystemSetOp(ulong w)
    {
        var imm = Bits(w, 60, 1) != 0;
        switch (Bits(w, 59, 4))
        {
            case 1:
                {
                    Operand? Stream(int i)
                    {
                        var b = Bits(w, 63 - (2 + i) * 8, 8);
                        if ((b & 0x80) == 0) return null;
                        return imm ? Operand.Imm(b & 0x7F) : Operand.G(b & 0x0F);
                    }
                    return new SetStreamsOp(Stream(1), Stream(2), Stream(3));
                }
            case 2: return new SetNavTimerOp(RegOrDataOperand(w, imm, 47), Bits(w, 15, 16));
            case 3: return new SetGprmModeOp(Bits(w, 19, 4), RegOrDataOperand(w, imm, 47), Bits(w, 23, 1) != 0);
            case 4: return new SetKaraokeOp(RegOrDataOperand(w, imm, 31));
            case 6: return new SetHighlightOp(RegOrDataOperand(w, imm, 31));
            default: return null;
        }
    }

    /// <summary>
    /// Decodes a command into its effects, mirroring <see cref="Evaluate"/> exactly (the same fields, the
    /// same order of testing and setting), so a program can be translated instead of run.
    /// </summary>
    public static DvdInstruction Decode(byte[] cmd)
    {
        if (cmd is null || cmd.Length < 8) return DvdInstruction.Nop;
        var w = Word(cmd);
        switch (Bits(w, 63, 3))
        {
            case 0:
                {
                    DvdOp? flow = Bits(w, 51, 4) switch
                    {
                        1 => new GotoOp(Bits(w, 7, 8)),
                        2 => new BreakOp(),
                        3 => null,
                        _ => null,
                    };
                    var guarded = Bits(w, 51, 4) == 3
                        ? Ops(new SetParentalOp(Bits(w, 11, 4)), new GotoOp(Bits(w, 7, 8)))
                        : Ops(flow);
                    return new DvdInstruction { Condition = Cond1(w), Guarded = guarded };
                }
            case 1:
                return Bits(w, 60, 1) != 0
                    ? new DvdInstruction { Condition = Cond2(w), Guarded = Ops(LinkOpOf(JumpInstr(w, true))) }
                    : new DvdInstruction { Condition = Cond1(w), Guarded = Ops(LinkOpOf(LinkInstr(w, true))) };
            case 2:
                return new DvdInstruction
                {
                    Condition = Cond2(w),
                    Guarded = Ops(SystemSetOp(w), Bits(w, 51, 4) != 0 ? LinkOpOf(LinkInstr(w, true)) : null),
                };
            case 3:
                return new DvdInstruction
                {
                    Condition = Cond(w, Operand.G(Bits(w, 43, 4)), RegOrDataOperand(w, Bits(w, 55, 1) != 0, 15)),
                    Guarded = Ops(Set1Op(w), Bits(w, 51, 4) != 0 ? LinkOpOf(LinkInstr(w, true)) : null),
                };
            case 4:
                return new DvdInstruction
                {
                    Before = Ops(Set2Op(w)),
                    Condition = Cond(w, Operand.G(Bits(w, 51, 4)), RegOrDataOperand(w, Bits(w, 55, 1) != 0, 31)),
                    Guarded = Ops(LinkOpOf(LinkSub(w, true))),
                };
            case 5:
                return new DvdInstruction { Condition = Cond5(w), Guarded = Ops(Set2Op(w), LinkOpOf(LinkSub(w, true))) };
            case 6:
                return new DvdInstruction { Condition = Cond5(w), Guarded = Ops(Set2Op(w)), After = Ops(LinkOpOf(LinkSub(w, true))) };
            default:
                return DvdInstruction.Nop;
        }
    }

    /// <summary>The value of an operand in a state.</summary>
    public static int ValueOf(Operand o, VmState s) => o.Kind switch
    {
        OperandKind.Immediate => o.Value,
        OperandKind.Gprm => s.Gprm[o.Value & 0x0F],
        _ => s.GetSprm(o.Value),
    };

    public static bool Holds(DvdCondition? c, VmState s) => c is null || Compare(c.Op, ValueOf(c.Left, s), ValueOf(c.Right, s));

    /// <summary>Runs a decoded command; the same result as <see cref="Evaluate"/> on its bytes.</summary>
    public static EvalResult Execute(DvdInstruction ins, VmState s)
    {
        var result = EvalResult.Nothing;
        void Run(DvdOp op)
        {
            switch (op)
            {
                case SetGprmOp g: SetOp(s, g.Op, g.Register, g.SwapWith, ValueOf(g.Source, s)); break;
                case SetStreamsOp st:
                    if (st.Audio is { } a) s.SetSprm(1, (ushort)ValueOf(a, s));
                    if (st.Subpicture is { } p) s.SetSprm(2, (ushort)ValueOf(p, s));
                    if (st.Angle is { } n) s.SetSprm(3, (ushort)ValueOf(n, s));
                    break;
                case SetNavTimerOp t: s.SetSprm(9, (ushort)ValueOf(t.Seconds, s)); s.SetSprm(10, (ushort)t.Pgcn); break;
                case SetGprmModeOp m: s.Gprm[m.Register] = (ushort)ValueOf(m.Value, s); break;
                case SetHighlightOp h: s.SetSprm(8, (ushort)ValueOf(h.Value, s)); break;
                case SetKaraokeOp k: s.SetSprm(11, (ushort)ValueOf(k.Value, s)); break;
                case SetParentalOp pml: s.SetSprm(13, (ushort)pml.Level); break;
                case GotoOp go: if (result.Kind == EvalKind.None) result = new EvalResult(EvalKind.Goto, go.Line); break;
                case BreakOp: if (result.Kind == EvalKind.None) result = new EvalResult(EvalKind.Break); break;
                case LinkOp l: if (result.Kind == EvalKind.None) result = new EvalResult(EvalKind.Link, Link: l.Link); break;
            }
        }
        foreach (var op in ins.Before) Run(op);
        if (Holds(ins.Condition, s))
            foreach (var op in ins.Guarded) Run(op);
        foreach (var op in ins.After) Run(op);
        return result;
    }

    // --- disassembly (diagnostics only) ------------------------------------

    private static readonly string[] CmpOps = { "", "&", "==", "!=", ">=", ">", "<=", "<" };
    private static readonly string[] SetOps = { "", "=", "<->", "+=", "-=", "*=", "/=", "%=", "rnd", "&=", "|=", "^=" };

    private static string RegName(int r) => (r & 0x80) != 0 ? $"s{r & 0x1F}" : $"g{r & 0x0F}";

    private static string RoD(ulong w, bool imm, int start) =>
        imm ? Bits(w, start, 16).ToString() : RegName(Bits(w, start - 8, 8));

    /// <summary>Human-readable form of a command, for CLI dumps and logs.</summary>
    public static string Disassemble(byte[] cmd)
    {
        if (cmd is null || cmd.Length < 8) return "(invalid)";
        var w = Word(cmd);
        var hex = BitConverter.ToString(cmd).Replace("-", "");
        var sb = new StringBuilder();
        var op = Bits(w, 54, 3);
        var type = Bits(w, 63, 3);

        string Cond(Func<string> lhs, Func<string> rhs) => op == 0 ? "" : $"if ({lhs()} {CmpOps[op]} {rhs()}) ";

        var probe = new VmState();
        switch (type)
        {
            case 0:
                sb.Append(Cond(() => RegName(Bits(w, 39, 8)), () => RoD(w, Bits(w, 55, 1) != 0, 31)));
                sb.Append(Bits(w, 51, 4) switch
                {
                    0 => "Nop",
                    1 => $"Goto {Bits(w, 7, 8)}",
                    2 => "Break",
                    3 => $"SetTmpPML {Bits(w, 11, 4)}, Goto {Bits(w, 7, 8)}",
                    _ => "?special",
                });
                break;
            case 1:
                if (Bits(w, 60, 1) != 0)
                    sb.Append(Cond(() => RegName(Bits(w, 15, 8)), () => RegName(Bits(w, 7, 8))));
                else
                    sb.Append(Cond(() => RegName(Bits(w, 39, 8)), () => RoD(w, Bits(w, 55, 1) != 0, 31)));
                var lr = Bits(w, 60, 1) != 0 ? JumpInstr(w, true) : LinkInstr(w, true);
                sb.Append(lr.Kind == EvalKind.Link ? lr.Link.ToString() : "?link");
                break;
            case 2:
                sb.Append(Cond(() => RegName(Bits(w, 15, 8)), () => RegName(Bits(w, 7, 8))));
                var imm = Bits(w, 60, 1) != 0;
                sb.Append(Bits(w, 59, 4) switch
                {
                    1 => "SetSTN",
                    2 => $"SetNVTMR {RoD(w, imm, 47)} pgc {Bits(w, 15, 16)}",
                    3 => $"SetGPRMMD g{Bits(w, 19, 4)} = {RoD(w, imm, 47)}",
                    4 => $"SetAMXMD {RoD(w, imm, 31)}",
                    6 => $"SetHL_BTNN {RoD(w, imm, 31)}",
                    _ => "?sysset",
                });
                if (Bits(w, 51, 4) != 0) { var l2 = LinkInstr(w, true); if (l2.Kind == EvalKind.Link) sb.Append("; ").Append(l2.Link); }
                break;
            case 3:
                sb.Append(Cond(() => $"g{Bits(w, 43, 4)}", () => RoD(w, Bits(w, 55, 1) != 0, 15)));
                if (Bits(w, 59, 4) != 0)
                    sb.Append($"g{Bits(w, 35, 4)} {SetOps[Math.Min(Bits(w, 59, 4), 11)]} {RoD(w, Bits(w, 60, 1) != 0, 31)}");
                if (Bits(w, 51, 4) != 0) { var l3 = LinkInstr(w, true); if (l3.Kind == EvalKind.Link) sb.Append("; ").Append(l3.Link); }
                break;
            case 4:
            case 5:
            case 6:
                if (Bits(w, 59, 4) != 0)
                    sb.Append($"g{Bits(w, 51, 4)} {SetOps[Math.Min(Bits(w, 59, 4), 11)]} {RoD(w, Bits(w, 60, 1) != 0, 47)}; ");
                sb.Append(type == 4
                    ? Cond(() => $"g{Bits(w, 51, 4)}", () => RoD(w, Bits(w, 55, 1) != 0, 31))
                    : Cond(() => Bits(w, 60, 1) != 0 ? RegName(Bits(w, 31, 8)) : RegName(Bits(w, 39, 8)),
                           () => Bits(w, 60, 1) != 0 ? RegName(Bits(w, 23, 8)) : RoD(w, Bits(w, 55, 1) != 0, 31)));
                var l4 = LinkSub(w, true);
                sb.Append(l4.Kind == EvalKind.Link ? l4.Link.ToString() : "");
                break;
            default:
                sb.Append("?unknown");
                break;
        }
        return $"{hex}  {sb.ToString().Trim()}";
    }
}
