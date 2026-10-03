namespace OvmDvd;

/// <summary>
/// export: a bundle from files ripped one per DVD title (MakeMKV, HandBrake), the whole disc's logic, with menu
/// art from ffmpeg. Titles whose file isn't there are left out (their buttons stay but do nothing).
/// </summary>
internal static class ExportCommand
{
    public static async Task<int> Run(string source, string bundle, string[] args)
    {
        var pattern = Program.Option(args, "--files") ?? throw new ArgumentException("export needs --files <pattern>, e.g. \"Title {n}.mkv\".");
        if (!pattern.Contains("{n}")) throw new ArgumentException("The --files pattern needs {n} for the title number.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(bundle))!;

        using var disc = DvdDisc.Open(source);
        var map = DiscMapBuilder.Build(disc);
        string? FileOf(int title)
        {
            var rel = pattern.Replace("{n}", title.ToString());
            return File.Exists(Path.Combine(folder, rel)) ? rel.Replace('\\', '/') : null;
        }
        var set = DvdMedia.OneFilePerTitle(disc, FileOf);
        if (set.Media.Count == 0) throw new ArgumentException($"No title's file was found next to the bundle ({pattern} in {folder}).");

        var builder = new MenuManifestBuilder(disc, map, set.Media, set.Index, new DvdManifestOptions
        {
            Title = Program.Option(args, "--title") ?? disc.VolumeLabel,
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

    private static string? FindOnPath(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe" } : new[] { tool };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
                if (File.Exists(Path.Combine(dir, n))) return Path.Combine(dir, n);
        return null;
    }
}
