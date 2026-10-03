using System;
using System.Collections.Generic;
using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>The passes that make a manifest smaller without changing what a player does (all on by default).</summary>
[Flags]
public enum ManifestPasses
{
    None = 0,

    /// <summary>Drop register sets nothing ever reads.</summary>
    DropUnreadSets = 1,

    /// <summary>Write once on a media entry the keys and prohibitions every play of it repeats.</summary>
    HoistToMedia = 2,

    /// <summary>Merge named actions that are the same.</summary>
    MergeIdentical = 4,

    /// <summary>Merge tests whose branches agree, and share logic written out in many places as named actions.</summary>
    ShareRepeated = 8,

    All = DropUnreadSets | HoistToMedia | MergeIdentical | ShareRepeated,
}

/// <summary>What a media file holds, as far as naming the menus that play it goes.</summary>
public enum MediaRole
{
    Feature,
    Episode,
    Extra,
}

/// <summary>
/// What a caller tells the manifest builder beyond the disc and its files: names, labels, roles, the fallback
/// list, and which parts of the disc's behaviour the files can carry. Everything is optional; the defaults
/// describe the disc from its structure alone.
/// </summary>
public sealed class DvdManifestOptions
{
    /// <summary>The disc's title and year, and catalogue ids ("tmdb", "tvdb").</summary>
    public string? Title { get; init; }
    public int? Year { get; init; }
    public Dictionary<string, string>? Ids { get; init; }

    /// <summary>The manifest's <c>source.generator</c>.</summary>
    public string Generator { get; init; } = "OpenVideoMenu.Dvd";

    /// <summary>A menu's heading as read on screen (DVD Tamer: OCR), already cleaned; null when unknown.</summary>
    public Func<MenuNode, string?>? MenuHeading { get; init; }

    /// <summary>A button's caption as read on screen, already cleaned; null when unknown.</summary>
    public Func<MenuNode, int, string?>? ButtonLabel { get; init; }

    /// <summary>What a media id holds, for naming menus by what their buttons play ("Episodes", "Special Features").</summary>
    public Func<string, MediaRole?>? RoleOf { get; init; }

    /// <summary>The fallback list (Level 0); default: every media as one list of titles.</summary>
    public Func<List<FallbackGroup>>? Fallback { get; init; }

    /// <summary>Write the disc's menus and logic (false: the fallback list only).</summary>
    public bool Menus { get; init; } = true;

    /// <summary>The files keep the disc's audio and subtitle tracks in order, so the menus' language choices can pick them.</summary>
    public bool TrackChoices { get; init; }

    /// <summary>Menu clips in front of menus (lead-in cells) are made into transitions.</summary>
    public bool Transitions { get; init; }

    /// <summary>How many navigation packs of a title cell to read for buttons over the film (null: all).</summary>
    public int? TitleButtonVobus { get; init; }

    /// <summary>Called with a line for each step of the translation (debugging a disc); null for none.</summary>
    public Action<string>? CompileTrace { get; init; }

    /// <summary>The size passes to run; turn them off to see the translation as it comes out of the compiler.</summary>
    public ManifestPasses Passes { get; init; } = ManifestPasses.All;
}
