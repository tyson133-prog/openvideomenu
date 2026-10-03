namespace OvmDvd;

/// <summary>ovm-dvd: the OpenVideoMenu.Dvd package's layers as commands.</summary>
internal static class Program
{
    private const string Usage = """
        ovm-dvd: DVD to Open Video Menu

          map <disc> [--verbose]            menus, every button's target, the title inventory; --verbose adds commands
          nav <disc> vmgm|vtsm|title <vtsn> <pgcn> <cell>
                                            a cell's navigation packs and highlight status
          disasm <hex>...                   8-byte commands as text (e.g. 3002000000050000)
          export <disc> <bundle.ovmz> --files <pattern>... [--file <n>=<path>]... [--ffmpeg <path>|none]
                 [--no-tracks] [--title <name>]
                                            a bundle from files ripped one per DVD title; a pattern names each
                                            title's file relative to the bundle, {n} = the title number
                                            ("Title {n}.mkv"); with several, the first whose file exists wins;
                                            --file names one title's file (the feature as "Heat (1995).mkv")
          verify <disc> [--walks n] [--depth n] [--seed n] [--lang xx] [--json <out>] [--quiet]
                                            export the whole disc and play it against the disc; one VERIFY line
          verify-all <folder>... [--out <dir>] [--parallel n] [--timeout s] [--walks n] [--depth n]
                                            verify every ISO and VIDEO_TS under a folder, each in its own process;
                                            a summary at the end
          stats | scan | uops [--verbose] | modes | sets <disc>
                                            one-line diagnostics of what a disc uses

        A disc is an ISO image or a VIDEO_TS folder (or the folder above it). Discs must be decrypted.
        Traces to stderr: OVM_DVD_TRACE=walks,vm,compile (any of them).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        try
        {
            return args[0] switch
            {
                "map" => DiscCommands.Map(Arg(args, 1), args.Contains("--verbose")),
                "nav" => DiscCommands.Nav(Arg(args, 1), args.Skip(2).ToArray()),
                "disasm" => DiscCommands.Disasm(args.Skip(1)),
                "export" => await ExportCommand.Run(Arg(args, 1), Arg(args, 2), args),
                "verify" => VerifyCommand.Run(Arg(args, 1), args),
                "verify-all" => await VerifyAllCommand.Run(args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList(), args),
                "stats" => Diagnostics.Stats(Arg(args, 1)),
                "scan" => Diagnostics.Scan(Arg(args, 1)),
                "uops" => Diagnostics.Uops(Arg(args, 1), args.Contains("--verbose")),
                "modes" => Diagnostics.Modes(Arg(args, 1)),
                "sets" => Diagnostics.Sets(Arg(args, 1)),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or DvdFormatException or ArgumentException)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}\n\n{Usage}");
        return 1;
    }

    private static string Arg(string[] args, int i) =>
        i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException("Missing argument. Run ovm-dvd --help.");

    /// <summary>The value after an option (--walks 200), or null.</summary>
    public static string? Option(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    /// <summary>Every value of an option given several times (--files a --files b), in order.</summary>
    public static List<string> Options(string[] args, string name) =>
        [.. args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1])];

    public static int Option(string[] args, string name, int fallback) =>
        int.TryParse(Option(args, name), out var v) ? v : fallback;

    /// <summary>Whether a trace (walks, vm, compile) is on in OVM_DVD_TRACE.</summary>
    public static bool Traces(string what) =>
        (Environment.GetEnvironmentVariable("OVM_DVD_TRACE") ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(what, StringComparer.OrdinalIgnoreCase);
}
