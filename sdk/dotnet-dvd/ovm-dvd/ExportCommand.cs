namespace OvmDvd;

/// <summary>
/// export: a bundle from files ripped one per DVD title (MakeMKV, HandBrake), the whole disc's logic, with menu
/// art from ffmpeg. Titles whose file isn't there are left out (their buttons stay but do nothing). Libraries keep
/// the feature apart from the extras (Plex: "Heat (1995).mkv" beside "Featurettes/"), so one title's file can be
/// named on its own (--file 1=...), and several patterns are tried in turn.
/// </summary>
internal static class ExportCommand
{
    public static async Task<int> Run(string source, string bundle, string[] args)
    {
        var patterns = Program.Options(args, "--files");
        var named = new Dictionary<int, string>();
        foreach (var f in Program.Options(args, "--file"))
        {
            var eq = f.IndexOf('=');
            if (eq <= 0 || !int.TryParse(f[..eq], out var n))
                throw new ArgumentException($"--file needs <title>=<path>, e.g. \"1=Heat (1995).mkv\"; got \"{f}\".");
            named[n] = f[(eq + 1)..];
        }
        if (patterns.Count == 0 && named.Count == 0)
            throw new ArgumentException("export needs --files <pattern> (e.g. \"Title {n}.mkv\") or --file <title>=<path>.");
        if (patterns.FirstOrDefault(p => !p.Contains("{n}")) is { } bad)
            throw new ArgumentException($"The --files pattern \"{bad}\" needs {{n}} for the title number.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(bundle))!;

        using var disc = DvdDisc.Open(source);
        var map = DiscMapBuilder.Build(disc);
        string? FileOf(int title)
        {
            IEnumerable<string> candidates = named.TryGetValue(title, out var file) ? [file] : patterns.Select(p => p.Replace("{n}", title.ToString()));
            return candidates.FirstOrDefault(rel => File.Exists(Path.Combine(folder, rel)))?.Replace('\\', '/');
        }
        // --cells-once: the files hold each distinct cell once, as DVD Tamer's rips do.
        var set = DvdMedia.OneFilePerTitle(disc, FileOf, onceEach: args.Contains("--cells-once"));
        if (set.Media.Count == 0)
            throw new ArgumentException($"No title's file was found next to the bundle ({string.Join(", ", patterns.Concat(named.Values))} in {folder}).");

        var builder = new MenuManifestBuilder(disc, map, set.Media, set.Index, new DvdManifestOptions
        {
            Title = Program.Option(args, "--title") ?? disc.VolumeLabel,
            Season = Number(args, "--season", 0),
            Volume = Number(args, "--volume", 1),
            Generator = "ovm-dvd",
            TrackChoices = !args.Contains("--no-tracks"),
            Transitions = true,
        });

        var ffmpeg = Program.Option(args, "--ffmpeg") ?? FindOnPath("ffmpeg");
        var decoder = ffmpeg is null or "none" ? null : new FfmpegMenuDecoder(ffmpeg);
        var progress = new Progress<string>(Console.WriteLine);
        var result = await DvdExport.ToBundleAsync(builder, disc, bundle, new DvdArtOptions { Decoder = decoder }, progress);

        foreach (var w in result.Warnings) Console.WriteLine($"  warning: [{w.Code}] {w.Message}");
        Console.WriteLine(result.Written
            ? $"Wrote {result.BundlePath}: {result.Menus} menus, {set.Media.Count} titles" +
              (builder.MissingTitles.Count > 0 ? $"; titles the menus play that have no file: {string.Join(", ", builder.MissingTitles)}" : "")
            : "Not written.");
        return result.Written ? 0 : 3;
    }

    /// <summary>An optional whole-number option, at least <paramref name="min"/>.</summary>
    private static int? Number(string[] args, string name, int min)
    {
        if (Program.Option(args, name) is not { } text) return null;
        return int.TryParse(text, out var n) && n >= min ? n : throw new ArgumentException($"{name} needs a whole number, {min} or more; got \"{text}\".");
    }

    private static string? FindOnPath(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe" } : new[] { tool };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
                if (File.Exists(Path.Combine(dir, n))) return Path.Combine(dir, n);
        return null;
    }
}
