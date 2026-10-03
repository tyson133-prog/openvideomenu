using System.Diagnostics;

namespace OvmDvd;

/// <summary>verify: exports the whole disc and plays it against the disc's own machine. One VERIFY line.</summary>
internal static class VerifyCommand
{
    public static int Run(string source, string[] args)
    {
        var clock = Stopwatch.StartNew();
        using var disc = DvdDisc.Open(source);
        var map = DiscMapBuilder.Build(disc);
        var report = MenuVerifier.Run(disc, map, new MenuVerifyOptions
        {
            Walks = Program.Option(args, "--walks", 300),
            Depth = Program.Option(args, "--depth", 25),
            Seed = Program.Option(args, "--seed", 7),
            MenuLanguage = Program.Option(args, "--lang") ?? "en",
            WalkTrace = Program.Traces("walks") ? Console.Error.WriteLine : null,
            VmTrace = Program.Traces("vm")
                ? step => Console.Error.WriteLine($"    vm {step.Kind} {step.State.Domain} vts{step.State.Vtsn} pgc{step.State.Pgcn} c{step.State.Cell} g0={step.State.Gprm[0]} g3={step.State.Gprm[3]}")
                : null,
            CompileTrace = Program.Traces("compile") ? Console.Error.WriteLine : null,
        });

        if (Program.Option(args, "--json") is { } json)
        {
            var (media, index, _) = MenuVerifier.WholeDisc(disc);
            File.WriteAllText(json, new MenuManifestBuilder(disc, map, media, index).Build().ToJson());
        }

        if (!args.Contains("--quiet"))
        {
            foreach (var w in report.Warnings.Take(20)) Console.WriteLine($"  warning: [{w.Code}] {w.Message}");
            foreach (var e in report.ValidationErrors.Take(20)) Console.WriteLine($"  INVALID: {e}");
            foreach (var p in report.Problems.Take(15)) Console.WriteLine($"  MISMATCH: {p}");
        }
        var simulated = map.Menus.Select(m => $"{m.Domain}:{m.Vtsn}:{m.Pgcn}:{m.Cell}").Distinct().Count();
        Console.WriteLine($"VERIFY {(report.ValidationErrors.Count > 0 ? "INVALID" : report.Mismatches == 0 ? "OK" : "DIFF")} " +
                          $"menus={report.Menus} (map {simulated}) buttons={report.Buttons} steps={report.Steps} matched={report.Matches} " +
                          $"mismatches={report.Mismatches} visited={report.MenusVisited.Count} nodes={report.ActionNodes} " +
                          $"kb={report.ManifestBytes / 1024} random={report.UsesRandom} ms={clock.ElapsedMilliseconds} :: {source}");
        return report.ValidationErrors.Count > 0 ? 3 : report.Mismatches > 0 ? 4 : 0;
    }
}
