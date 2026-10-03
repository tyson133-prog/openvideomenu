using System;
using System.Collections.Generic;

namespace OpenVideoMenu.Dvd.Structure;

/// <summary>DVD-Video VM domains.</summary>
public enum DvdDomain
{
    FirstPlay,
    VmgMenu,
    VtsMenu,
    Title,
    Stop,
}

/// <summary>Menu type carried by entry PGCs in a PGCI_UT language unit.</summary>
public enum MenuType : byte
{
    None = 0,
    Title = 2,
    Root = 3,
    Subpicture = 4,
    Audio = 5,
    Angle = 6,
    Chapter = 7,
}

public sealed class VideoAttributes
{
    public bool IsPal { get; init; }
    public bool IsWide { get; init; }
    public int Width { get; init; } = 720;
    public int Height { get; init; } = 480;

    public static VideoAttributes Parse(byte b0, byte b1)
    {
        var pal = ((b0 >> 4) & 3) == 1;
        var wide = ((b0 >> 2) & 3) == 3;
        var res = (b1 >> 3) & 7;
        var fullHeight = pal ? 576 : 480;
        var (w, h) = res switch
        {
            1 => (704, fullHeight),
            2 => (352, fullHeight),
            3 => (352, fullHeight / 2),
            _ => (720, fullHeight),
        };
        return new VideoAttributes { IsPal = pal, IsWide = wide, Width = w, Height = h };
    }

    public override string ToString() => $"{Width}x{Height} {(IsPal ? "PAL" : "NTSC")} {(IsWide ? "16:9" : "4:3")}";
}

public sealed record AudioStreamInfo(int Index, string Language, string Codec, int Channels);

public sealed record SubpictureStreamInfo(int Index, string Language);

public sealed class CellPlayback
{
    /// <summary>0 = not in block, 1 = first, 2 = middle, 3 = last.</summary>
    public int BlockMode { get; init; }

    /// <summary>1 = angle block.</summary>
    public int BlockType { get; init; }

    public byte StillTime { get; init; }

    /// <summary>1-based index into the PGC's cell command list; 0 = none.</summary>
    public int CellCommandNumber { get; init; }

    public TimeSpan Duration { get; init; }
    public uint FirstSector { get; init; }
    public uint LastVobuStartSector { get; init; }
    public uint LastSector { get; init; }

    public bool IsAngleBlock => BlockType == 1 && BlockMode != 0;
}

/// <summary>A program chain (menu, first-play, or title).</summary>
public sealed class Pgc
{
    public int ProgramCount { get; init; }
    public int CellCount { get; init; }
    public TimeSpan Duration { get; init; }
    public uint ProhibitedUserOps { get; init; }
    public int NextPgc { get; init; }
    public int PrevPgc { get; init; }
    public int GoUpPgc { get; init; }
    public byte StillTime { get; init; }
    public byte PlaybackMode { get; init; }

    public IReadOnlyList<byte[]> PreCommands { get; init; } = Array.Empty<byte[]>();
    public IReadOnlyList<byte[]> PostCommands { get; init; } = Array.Empty<byte[]>();
    public IReadOnlyList<byte[]> CellCommands { get; init; } = Array.Empty<byte[]>();

    /// <summary>Entry cell number (1-based) for each program (index = PGN - 1).</summary>
    public IReadOnlyList<int> ProgramMap { get; init; } = Array.Empty<int>();

    public IReadOnlyList<CellPlayback> Cells { get; init; } = Array.Empty<CellPlayback>();

    /// <summary>16 palette entries (0x00YYCrCb), used by subpictures.</summary>
    public IReadOnlyList<uint> Palette { get; init; } = Array.Empty<uint>();

    // Set by the containing table:
    public bool IsEntry { get; internal set; }
    public MenuType MenuType { get; internal set; }

    /// <summary>For VTS title PGCs: the VTS_TTN in the category byte (0 when not an entry PGC).</summary>
    public int VtsTitleNumber { get; internal set; }

    /// <summary>Byte offset in the IFO; identical offsets mean the same physical PGC.</summary>
    public int IfoOffset { get; init; }

    /// <summary>Returns the program (1-based) that contains the given cell (1-based).</summary>
    public int ProgramOfCell(int cell)
    {
        var pg = 1;
        for (var i = 0; i < ProgramMap.Count; i++)
            if (ProgramMap[i] <= cell) pg = i + 1;
        return pg;
    }

    /// <summary>Start time of a program within this PGC.</summary>
    public TimeSpan ProgramStart(int pgn)
    {
        if (pgn < 1 || pgn > ProgramMap.Count) return TimeSpan.Zero;
        var entryCell = ProgramMap[pgn - 1];
        var t = TimeSpan.Zero;
        for (var c = 1; c < entryCell && c <= Cells.Count; c++)
        {
            var cell = Cells[c - 1];
            // Only count the first angle of an angle block.
            if (cell.IsAngleBlock && cell.BlockMode != 1) continue;
            t += cell.Duration;
        }
        return t;
    }
}

public sealed class MenuLanguageUnit
{
    public string Language { get; init; } = "";
    public List<Pgc> Pgcs { get; } = new();
}

public sealed class TitleSearchEntry
{
    public int TitleNumber { get; init; }
    public byte PlaybackType { get; init; }
    public int AngleCount { get; init; }
    public int ChapterCount { get; init; }
    public int Vtsn { get; init; }
    public int VtsTtn { get; init; }
}

public readonly record struct PttEntry(int Pgcn, int Pgn);

public sealed class VmgInfo
{
    public int TitleSetCount { get; init; }
    public Pgc? FirstPlay { get; init; }
    public List<TitleSearchEntry> Titles { get; } = new();
    public List<MenuLanguageUnit> Menus { get; } = new();
    public VideoAttributes MenuVideo { get; init; } = new();
}

public sealed class VtsInfo
{
    public int Vtsn { get; init; }
    public List<MenuLanguageUnit> Menus { get; } = new();
    public List<Pgc> TitlePgcs { get; } = new();

    /// <summary>PTT table: index = VTS_TTN - 1, then chapter - 1.</summary>
    public List<List<PttEntry>> Ptts { get; } = new();

    public VideoAttributes MenuVideo { get; init; } = new();
    public VideoAttributes TitleVideo { get; init; } = new();
    public List<AudioStreamInfo> Audio { get; } = new();
    public List<SubpictureStreamInfo> Subpictures { get; } = new();
}
