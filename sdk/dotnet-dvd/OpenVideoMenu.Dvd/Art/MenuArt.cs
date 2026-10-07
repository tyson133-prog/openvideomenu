using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenVideoMenu.Dvd.Art;

/// <summary>Art made for one menu, as paths relative to the manifest ("menus/…").</summary>
public sealed class MenuArtSet
{
    public string? Image { get; set; }
    public string? Video { get; set; }
    public string? Audio { get; set; }
    public double? ButtonsAt { get; set; }

    /// <summary>Seconds into <see cref="Video"/> where the loop starts (after an intro cell).</summary>
    public double? LoopStart { get; set; }

    /// <summary>
    /// The cell holds when its video ends (a still time, timed or infinite): the video and the music play once
    /// and the last frame stays, as on the disc. False: the cell runs on, so the menu's video loops.
    /// </summary>
    public bool PlaysOnce { get; set; }
    public Dictionary<int, (string? Selected, string? Activated)> Buttons { get; } = new();

    /// <summary>
    /// The highlights of each set of buttons the menu changes to while its video plays, in the order of the
    /// highlights <see cref="MenuArtMaker.MakeAsync"/> was given.
    /// </summary>
    public List<Dictionary<int, (string? Selected, string? Activated)>> SetButtons { get; } = new();
}

/// <summary>Which kinds of menu art to make.</summary>
[Flags]
public enum MenuArtKinds
{
    None = 0,

    /// <summary>A background picture per menu (a still menu's frame, a motion menu's poster).</summary>
    Stills = 1,

    /// <summary>The disc's own button highlights, selected and activated (decoded here; no video decoder needed).</summary>
    Highlights = 2,

    /// <summary>Motion menus' video with their sound, intro and loop, and the transition clips between menus.</summary>
    Motion = 4,

    /// <summary>The music of still menus.</summary>
    Music = 8,

    All = Stills | Highlights | Motion | Music,
}

/// <summary>Whether a decoder made its file, and if not, why (null: no reason to give).</summary>
public readonly record struct DecodeResult(bool Ok, string? Error = null)
{
    public static DecodeResult Done { get; } = new(true);
}

/// <summary>
/// Turns a menu's MPEG-2 program stream (its cells' sectors, written to a file) into the files a manifest
/// points at. The package uses ffmpeg (<see cref="FfmpegMenuDecoder"/>); an exporter can bring its own decoder.
/// </summary>
public interface IMenuVideoDecoder
{
    /// <summary>A picture (JPEG) of the frame <paramref name="at"/> seconds in; null: the last frame (a still menu).</summary>
    Task<DecodeResult> StillAsync(string vob, string output, double? at, CancellationToken ct);

    /// <summary>A video clip (MP4, H.264 + AAC) of the whole stream, shown at the menu's shape.</summary>
    Task<DecodeResult> ClipAsync(string vob, string output, VideoAttributes video, CancellationToken ct);

    /// <summary>The stream's first audio track (M4A, AAC).</summary>
    Task<DecodeResult> AudioAsync(string vob, string output, CancellationToken ct);
}

/// <summary>Menu video through ffmpeg.</summary>
public sealed class FfmpegMenuDecoder : IMenuVideoDecoder
{
    private readonly string _ffmpeg;

    /// <param name="ffmpeg">The ffmpeg executable.</param>
    public FfmpegMenuDecoder(string ffmpeg) => _ffmpeg = ffmpeg;

    /// <summary>The frame shape ffmpeg should flag (storage pixels stay as they are; buttons use them).</summary>
    private static string Dar(VideoAttributes v) => v.IsWide ? "16/9" : "4/3";

    public Task<DecodeResult> StillAsync(string vob, string output, double? at, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-fflags", "+genpts" };
        if (at is { } t) args.AddRange(new[] { "-ss", t.ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", vob, "-map", "0:v:0", "-vf", "yadif=deint=interlaced,setsar=1" });
        // A still menu is its last frame: keep overwriting the one output image.
        args.AddRange(at is null ? new[] { "-update", "1" } : new[] { "-frames:v", "1" });
        args.AddRange(new[] { "-q:v", "2", output });
        return RunAsync(args, output, ct);
    }

    public Task<DecodeResult> ClipAsync(string vob, string output, VideoAttributes video, CancellationToken ct) =>
        RunAsync(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-fflags", "+genpts", "-i", vob,
            "-map", "0:v:0", "-map", "0:a:0?", "-vf", $"yadif,setdar={Dar(video)}",
            // The spec asks for a few MB per menu: CRF 23 still looks like the disc at DVD size.
            "-c:v", "libx264", "-preset", "slow", "-crf", "23", "-pix_fmt", "yuv420p", "-profile:v", "main",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-movflags", "+faststart", output,
        }, output, ct);

    public Task<DecodeResult> AudioAsync(string vob, string output, CancellationToken ct) =>
        RunAsync(new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-fflags", "+genpts", "-i", vob,
            "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-movflags", "+faststart", output,
        }, output, ct);

    private async Task<DecodeResult> RunAsync(IEnumerable<string> args, string output, CancellationToken ct)
    {
        var errors = new List<string>();
        int code;
        try
        {
            code = await ArtProcess.RunAsync(_ffmpeg, args, _ => { }, l => { if (errors.Count < 5) errors.Add(l); }, ct);
        }
        catch (ArtToolException ex)
        {
            return new DecodeResult(false, ex.Message);
        }
        if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 0) return DecodeResult.Done;
        ArtProcess.TryDelete(output);
        return new DecodeResult(false, errors.Count > 0 ? $"ffmpeg ({Path.GetFileName(output)}): {errors[0]}" : null);
    }
}

/// <summary>
/// Makes menu art from the disc's menu cells: a video decoder turns the MPEG-2 into still frames, motion
/// loops, music and transition clips; the highlight PNGs come from the package's own subpicture decoder.
/// Everything is written under <c>assetRoot/menus/</c>.
/// </summary>
public sealed class MenuArtMaker
{
    public const string Folder = "menus";

    private readonly DvdDisc _disc;
    private readonly IMenuVideoDecoder? _decoder;
    private readonly string _assetRoot;
    private readonly string _work;
    private readonly DvdWarnings _warnings;
    private readonly Dictionary<string, string?> _transitions = new();
    private bool _toldNoDecoder;

    /// <param name="decoder">Decodes the menu video; null: no backgrounds, motion or music (highlights are still made).</param>
    public MenuArtMaker(DvdDisc disc, IMenuVideoDecoder? decoder, string assetRoot, DvdWarnings warnings)
    {
        _disc = disc;
        _decoder = decoder;
        _assetRoot = assetRoot;
        _warnings = warnings;
        _work = Path.Combine(assetRoot, ".work");
        Directory.CreateDirectory(Path.Combine(assetRoot, Folder));
        Directory.CreateDirectory(_work);
    }

    /// <summary>The art maker with ffmpeg as its video decoder.</summary>
    public MenuArtMaker(DvdDisc disc, string ffmpeg, string assetRoot, DvdWarnings warnings)
        : this(disc, new FfmpegMenuDecoder(ffmpeg), assetRoot, warnings)
    {
    }

    /// <summary>Runs the decoder, recording why it failed when it says.</summary>
    private async Task<bool> Decoded(Task<DecodeResult> work)
    {
        var r = await work;
        if (!r.Ok && r.Error is { Length: > 0 } e) _warnings.Add(DvdWarningCode.ArtDecoder, e);
        return r.Ok;
    }

    /// <param name="kinds">What to make.</param>
    /// <param name="loop">
    /// A menu cell this one runs on into by itself, with the same buttons (a motion menu's intro, then
    /// its loop): the clip is both, looping from the second.
    /// </param>
    /// <param name="sets">The highlights of the button sets the menu changes to while its video plays (their
    /// pictures come from the subpicture showing then); null: none.</param>
    public async Task<MenuArtSet> MakeAsync(MenuNode menu, string id, MenuArtKinds kinds, CancellationToken ct, MenuNode? loop = null,
        IReadOnlyList<HighlightInfo>? sets = null)
    {
        var decoder = _decoder;
        if (decoder is null && (kinds & (MenuArtKinds.Stills | MenuArtKinds.Motion | MenuArtKinds.Music)) != 0 && !_toldNoDecoder)
        {
            _toldNoDecoder = true;
            _warnings.Add(DvdWarningCode.NoDecoder, "No video decoder: the menus have plain backgrounds, without motion or music.");
        }
        var art = new MenuArtSet();
        var cellBytes = _disc.ReadMenuCell(menu.Domain, menu.Vtsn, menu.Pgcn, menu.Cell, menu.MenuUnit);
        if (cellBytes is null)
        {
            _warnings.Add(DvdWarningCode.MenuVideoUnreadable, $"{id}: couldn't read the menu's video from the disc.");
            return art;
        }
        var pgc = _disc.GetPgc(menu.Domain, menu.Vtsn, menu.Pgcn, menu.MenuUnit);
        var cell = pgc?.Cells[menu.Cell - 1];
        var vob = Path.Combine(_work, id + ".vob");
        await File.WriteAllBytesAsync(vob, cellBytes, ct);

        var seconds = cell?.Duration.TotalSeconds ?? 0;
        // One rule for every cell: motion is a video with more than a picture or two (a still is sometimes
        // the same picture repeated briefly), whatever its length and whatever follows it.
        var isStill = cell is null || MenuStreams.CountPictures(cellBytes, upTo: 3) < 3;
        var buttonsAt = menu.Highlight.ButtonsAt.TotalSeconds;
        // What follows the video: a hold (still time) keeps the last frame; otherwise the cell runs on and the
        // video loops. With an intro running into a loop cell, the loop cell decides.
        var lastCell = loop is null ? cell : _disc.GetPgc(loop.Domain, loop.Vtsn, loop.Pgcn, loop.MenuUnit)?.Cells.ElementAtOrDefault(loop.Cell - 1);
        art.PlaysOnce = lastCell is { StillTime: not 0 };

        // The background: a still menu shows its last frame, and so does motion that plays once and holds;
        // a looping motion menu's poster is the frame the buttons appear on.
        // Backgrounds are video frames: JPEG (the spec allows it) is a tenth of a PNG's size. Highlights stay PNG for alpha.
        var image = $"{Folder}/{id}.jpg";
        var at = isStill || art.PlaysOnce && loop is null ? (double?)null : Math.Clamp(buttonsAt > 0 ? buttonsAt : 1, 0, Math.Max(0, seconds - 0.2));
        if (decoder is not null && kinds.HasFlag(MenuArtKinds.Stills))
        {
            if (await Decoded(decoder.StillAsync(vob, Path.Combine(_assetRoot, image), at, ct))) art.Image = image;
            else _warnings.Add(DvdWarningCode.MenuPictureFailed, $"{id}: ffmpeg couldn't decode the menu picture.");
        }

        if (decoder is not null)
        {
            if (!isStill && kinds.HasFlag(MenuArtKinds.Motion))
            {
                var video = $"{Folder}/{id}.mp4";
                var source = vob;
                if (loop is not null && _disc.ReadMenuCell(loop.Domain, loop.Vtsn, loop.Pgcn, loop.Cell, loop.MenuUnit) is { } loopBytes)
                {
                    source = Path.Combine(_work, id + "-loop.vob");
                    await using (var fs = File.Create(source))
                    {
                        await fs.WriteAsync(cellBytes, ct);
                        await fs.WriteAsync(loopBytes, ct);
                    }
                    art.LoopStart = Math.Round(seconds, 2);
                }
                if (await Decoded(decoder.ClipAsync(source, Path.Combine(_assetRoot, video), menu.Video, ct))) art.Video = video;
                else art.LoopStart = null;
                if (source != vob) ArtProcess.TryDelete(source);
                if (buttonsAt > 0.2) art.ButtonsAt = Math.Round(buttonsAt, 2);
            }
            else if (isStill && kinds.HasFlag(MenuArtKinds.Music) && seconds > 0 && MenuStreams.HasAudio(cellBytes))
            {
                var audio = $"{Folder}/{id}.m4a";
                if (await Decoded(decoder.AudioAsync(vob, Path.Combine(_assetRoot, audio), ct))) art.Audio = audio;
            }
        }

        if (kinds.HasFlag(MenuArtKinds.Highlights) && pgc is not null)
        {
            Highlights(menu, id, cellBytes, pgc.Palette, art);
            for (var i = 0; i < (sets?.Count ?? 0); i++)
            {
                var hl = sets![i];
                var spu = cell is null ? null : MenuStreams.SubpictureAt(cellBytes, (int)(hl.NavSector - cell.FirstSector), Rects(hl.Buttons));
                art.SetButtons.Add(spu is null ? new() : Draw(hl, hl.Buttons, spu, pgc.Palette, $"{id}-s{i + 1}"));
            }
        }
        ArtProcess.TryDelete(vob);
        return art;
    }

    private void Highlights(MenuNode menu, string id, byte[] cell, IReadOnlyList<uint> palette, MenuArtSet art)
    {
        var spu = MenuStreams.ReadSubpicture(cell);
        if (spu is null) return;
        foreach (var (n, states) in Draw(menu.Highlight, menu.Buttons.Select(bn => bn.Button), spu, palette, id))
            art.Buttons[n] = states;
    }

    private static List<(int X0, int Y0, int X1, int Y1)> Rects(IEnumerable<DvdButton> buttons) =>
        buttons.Where(b => b.HasArea).Select(b => (b.X0, b.Y0, b.X1, b.Y1)).ToList();

    /// <summary>
    /// Each button's selected and activated pictures, as a player draws them: the subpicture's pixels under the
    /// button in the highlight's colours. Files are <c>{prefix}-b{n}-{state}.png</c>.
    /// </summary>
    private Dictionary<int, (string? Selected, string? Activated)> Draw(HighlightInfo hl, IEnumerable<DvdButton> buttons, Subpicture spu,
        IReadOnlyList<uint> palette, string prefix)
    {
        var drawn = new Dictionary<int, (string?, string?)>();
        if (hl.ColorTables.Count < 3) return drawn;
        foreach (var b in buttons)
        {
            var table = hl.ColorTables[Math.Clamp(b.ColorTable - 1, 0, 2)];
            string? Save(uint word, string state)
            {
                using var img = HighlightRenderer.Render(spu, b, word, palette);
                if (img is null) return null;
                var rel = $"{Folder}/{prefix}-b{b.Number}-{state}.png";
                HighlightRenderer.SavePng(img, Path.Combine(_assetRoot, rel));
                return rel;
            }
            var selected = Save(table.Select, "selected");
            var activated = Save(table.Action, "activated");
            if (selected is not null || activated is not null) drawn[b.Number] = (selected, activated);
        }
        return drawn;
    }

    private readonly Dictionary<(int Vtsn, uint Nav), Dictionary<int, (string? Selected, string? Activated)>> _titleHighlights = new();

    /// <summary>
    /// The pictures of buttons shown over a title's video (an overlay), drawn from the title's subpicture at the
    /// navigation pack that carries them, with the title's palette. Made once per highlight.
    /// </summary>
    public Dictionary<int, (string? Selected, string? Activated)> TitleHighlights(int vtsn, int pgcn, int cellNumber, HighlightInfo hl)
    {
        if (_titleHighlights.TryGetValue((vtsn, hl.NavSector), out var done)) return done;
        var drawn = new Dictionary<int, (string?, string?)>();
        var pgc = _disc.GetPgc(DvdDomain.Title, vtsn, pgcn);
        var cell = pgc?.Cells.ElementAtOrDefault(cellNumber - 1);
        if (pgc is not null && cell is not null)
        {
            // The subpicture is sent near its buttons' navigation pack, before it or in the video unit it starts.
            var first = Math.Max(cell.FirstSector, hl.NavSector > 1024 ? hl.NavSector - 1024 : 0);
            var last = Math.Min(cell.LastSector, hl.NavSector + 1024);
            if (_disc.ReadTitleSectors(vtsn, first, last) is { } bytes &&
                MenuStreams.SubpictureAt(bytes, (int)(hl.NavSector - first), Rects(hl.Buttons)) is { } spu)
                drawn = Draw(hl, hl.Buttons, spu, pgc.Palette, $"vts{vtsn}-nav{hl.NavSector}");
        }
        return _titleHighlights[(vtsn, hl.NavSector)] = drawn;
    }

    /// <summary>A clip of the button-less cells that play before a menu (a page transition), made once per cell run.</summary>
    public async Task<string?> TransitionAsync(IReadOnlyList<MenuCellRef> cells, VideoAttributes video, CancellationToken ct)
    {
        if (cells.Count == 0 || _decoder is null) return null;
        var key = string.Join("+", cells.Select(c => $"{c.Domain}-{c.Vtsn}-{c.Pgcn}-{c.Cell}"));
        if (_transitions.TryGetValue(key, out var done)) return done;
        _transitions[key] = null;

        var seconds = cells.Sum(c => _disc.GetPgc(c.Domain, c.Vtsn, c.Pgcn, c.Unit)?.Cells.ElementAtOrDefault(c.Cell - 1)?.Duration.TotalSeconds ?? 0);
        if (seconds < 0.5 || seconds > 180) return null;
        var parts = cells.Select(c => _disc.ReadMenuCell(c.Domain, c.Vtsn, c.Pgcn, c.Cell, c.Unit)).ToList();
        if (parts.Any(p => p is null)) return null;

        var name = $"transition-{_transitions.Count}";
        var vob = Path.Combine(_work, name + ".vob");
        await using (var fs = File.Create(vob))
            foreach (var p in parts) await fs.WriteAsync(p!, ct);
        var rel = $"{Folder}/{name}.mp4";
        var ok = await Decoded(_decoder.ClipAsync(vob, Path.Combine(_assetRoot, rel), video, ct));
        ArtProcess.TryDelete(vob);
        return _transitions[key] = ok ? rel : null;
    }

    public void Cleanup()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
