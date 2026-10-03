using System.Collections.Generic;

namespace OpenVideoMenu.Dvd;

/// <summary>What a warning is about, for tools that sort, count or hide them.</summary>
public enum DvdWarningCode
{
    /// <summary>Part of the disc couldn't be read (a damaged or truncated image).</summary>
    DiscRead,

    /// <summary>First play ends somewhere other than a menu.</summary>
    FirstPlayNoMenu,

    /// <summary>A title points at a title set or chapter table that isn't there.</summary>
    MissingTitleSet,

    /// <summary>The translation stopped following a path (a loop or a dead end on the disc).</summary>
    PathStopped,

    /// <summary>A random-order PGC has a cell command the format can't attach to a random play.</summary>
    RandomCellCommand,

    /// <summary>A shuffle PGC plays only some of its programs; the export shuffles them all.</summary>
    ShufflePartial,

    /// <summary>Conditions inside titles are decided when playback starts.</summary>
    ApproximatedConditions,

    /// <summary>The disc has no menus with buttons.</summary>
    NoMenus,

    /// <summary>Something a resumed title does that its resume point can't carry.</summary>
    ResumeLoss,

    /// <summary>A file's chapter holds more than one of the disc's chapters.</summary>
    ChapterAmbiguous,

    /// <summary>The video decoder couldn't make a file, or couldn't run.</summary>
    ArtDecoder,

    /// <summary>No video decoder: plain backgrounds, no motion or music.</summary>
    NoDecoder,

    /// <summary>A menu's video couldn't be read from the disc.</summary>
    MenuVideoUnreadable,

    /// <summary>A menu's background picture couldn't be made.</summary>
    MenuPictureFailed,

    /// <summary>The manifest's validation warned about something.</summary>
    ManifestWarning,

    /// <summary>The manifest didn't validate; nothing was written.</summary>
    ManifestInvalid,

    /// <summary>Packing the bundle reported a problem.</summary>
    BundleProblem,

    /// <summary>The bundle didn't pass its own check; nothing was written.</summary>
    BundleInvalid,
}

/// <summary>A warning: what it's about, and a sentence for people.</summary>
public sealed record DvdWarning(DvdWarningCode Code, string Message)
{
    public override string ToString() => Message;
}

/// <summary>A list of warnings, added to by code and message.</summary>
public sealed class DvdWarnings : List<DvdWarning>
{
    public void Add(DvdWarningCode code, string message) => Add(new DvdWarning(code, message));

    /// <summary>The messages alone.</summary>
    public IEnumerable<string> Messages()
    {
        foreach (var w in this) yield return w.Message;
    }
}
