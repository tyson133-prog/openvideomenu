namespace OpenVideoMenu.Dvd.Machine;

/// <summary>
/// What differs between DVD players and a disc's commands can read (the system registers, SPRMs): the viewer's
/// languages, parental settings, region, and what the player and TV can show and play. The defaults are an
/// English-speaking, region 1, unrestricted player on a 16:9 TV.
/// </summary>
public sealed record DvdPlayerSettings
{
    public static DvdPlayerSettings Default { get; } = new();

    /// <summary>Preferred menu language, ISO 639-1 (SPRM 0).</summary>
    public string MenuLanguage { get; init; } = "en";

    /// <summary>Preferred audio language (SPRM 16).</summary>
    public string AudioLanguage { get; init; } = "en";

    /// <summary>Preferred subtitle language (SPRM 18).</summary>
    public string SubtitleLanguage { get; init; } = "en";

    /// <summary>Parental country, ISO 3166-1 alpha-2 (SPRM 12).</summary>
    public string ParentalCountry { get; init; } = "US";

    /// <summary>Parental level, 1-8, or 15 for no restriction (SPRM 13).</summary>
    public int ParentalLevel { get; init; } = 15;

    /// <summary>Player region, 1-8 (SPRM 20).</summary>
    public int Region { get; init; } = 1;

    /// <summary>Player configuration for video (SPRM 14): 0x0C00 = a 16:9 display.</summary>
    public ushort VideoConfiguration { get; init; } = 0x0C00;

    /// <summary>Audio the player can decode (SPRM 15).</summary>
    public ushort AudioCapabilities { get; init; } = 0x7CFC;

    /// <summary>A two-letter code as a register holds it (first letter in the high byte), lower-cased.</summary>
    public static ushort Code(string twoLetters) =>
        twoLetters is { Length: >= 2 } t ? (ushort)(char.ToLowerInvariant(t[0]) << 8 | char.ToLowerInvariant(t[1])) : (ushort)0;

    private static ushort Upper(string twoLetters) =>
        twoLetters is { Length: >= 2 } t ? (ushort)(char.ToUpperInvariant(t[0]) << 8 | char.ToUpperInvariant(t[1])) : (ushort)0;

    /// <summary>Puts these settings into a state's system registers.</summary>
    public void ApplyTo(VmState s)
    {
        s.Sprm[0] = Code(MenuLanguage);
        s.Sprm[12] = Upper(ParentalCountry);
        s.Sprm[13] = (ushort)ParentalLevel;
        s.Sprm[14] = VideoConfiguration;
        s.Sprm[15] = AudioCapabilities;
        s.Sprm[16] = Code(AudioLanguage);
        s.Sprm[18] = Code(SubtitleLanguage);
        s.Sprm[20] = (ushort)Region;
    }
}

/// <summary>A step of the DVD machine, for observers (tracing, tools).</summary>
public enum VmStepKind
{
    EnterPgc,
    PlayProgram,
    PlayCell,
    EndCell,
    EndPgc,
}

/// <summary>One step the DVD machine takes, with the state as it is then (read it, don't keep or change it).</summary>
public sealed record VmStep(VmStepKind Kind, VmState State);
