using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OvmDvd;

/// <summary>
/// verify-all: every ISO (and VIDEO_TS folder) under a folder, each verified in its own process (a disc that
/// hangs or runs out of memory can't take the others with it), a few at a time. Each disc's full output goes
/// to its own log; the end is a summary: results, problems grouped by kind, and export sizes.
/// </summary>
internal static class VerifyAllCommand
{
    public static async Task<int> Run(List<string> folders, string[] args)
    {
        if (folders.Count == 0) throw new ArgumentException("verify-all needs a folder.");
        var parallel = Math.Max(1, Program.Option(args, "--parallel", 3));
        var timeout = TimeSpan.FromSeconds(Program.Option(args, "--timeout", 600));
        var walks = Program.Option(args, "--walks", 300); // as verify: 200 missed Matrix Revolutions D2's resume bug
        var depth = Program.Option(args, "--depth", 25);
        var outDir = Program.Option(args, "--out") ?? Path.Combine(Path.GetTempPath(), "ovm-dvd-verify-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Path.Combine(outDir, "discs"));

        var discs = folders.SelectMany(Discs).ToList();
        await File.WriteAllLinesAsync(Path.Combine(outDir, "discs.txt"), discs);
        Console.WriteLine($"{discs.Count} discs under {string.Join(", ", folders)}; logs in {outDir}");

        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find ovm-dvd itself.");
        var results = new string[discs.Count];
        var next = -1;
        var done = 0;
        async Task Worker()
        {
            while (true)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= discs.Count) return;
                var log = Path.Combine(outDir, "discs", $"{i + 1}.txt");
                results[i] = await VerifyOne(self, discs[i], log, walks, depth, timeout);
                var n = Interlocked.Increment(ref done);
                Console.WriteLine($"[{n}/{discs.Count}] {results[i]}");
            }
        }
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => Worker()));

        var summary = Summarize(outDir, discs, results);
        Console.WriteLine();
        Console.WriteLine(summary);
        await File.WriteAllTextAsync(Path.Combine(outDir, "summary.txt"), summary);
        return results.All(r => r.StartsWith("VERIFY OK", StringComparison.Ordinal)) ? 0 : 4;
    }

    /// <summary>ISOs, and VIDEO_TS folders not inside an ISO's folder tree, sorted.</summary>
    private static IEnumerable<string> Discs(string folder)
    {
        var isos = Directory.EnumerateFiles(folder, "*.iso", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true });
        var videoTs = Directory.EnumerateDirectories(folder, "VIDEO_TS", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true })
            .Where(d => File.Exists(Path.Combine(d, "VIDEO_TS.IFO")) || File.Exists(Path.Combine(d, "VIDEO_TS.BUP")));
        return isos.Concat(videoTs).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> VerifyOne(string self, string disc, string log, int walks, int depth, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(self) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "verify", disc, "--walks", walks.ToString(), "--depth", depth.ToString() }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        string last;
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            var text = await stdout + await stderr;
            await File.WriteAllTextAsync(log, text);
            last = text.Split('\n').Select(l => l.TrimEnd()).LastOrDefault(l => l.StartsWith("VERIFY ", StringComparison.Ordinal))
                   ?? $"VERIFY ERROR {text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0)} :: {disc}";
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            last = $"VERIFY TIMEOUT :: {disc}";
            await File.WriteAllTextAsync(log, last);
        }
        return last;
    }

    private static string Summarize(string outDir, List<string> discs, string[] results)
    {
        var lines = new List<string>();
        var counts = results.GroupBy(r => r.Split(' ').ElementAtOrDefault(1) ?? "?").OrderByDescending(g => g.Count());
        lines.Add("Results: " + string.Join(", ", counts.Select(g => $"{g.Key} {g.Count()}")));

        // Problems grouped by kind: the first mismatch of each disc, numbers taken out.
        var groups = new Dictionary<string, List<string>>();
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i].StartsWith("VERIFY OK", StringComparison.Ordinal)) continue;
            var log = File.Exists(Path.Combine(outDir, "discs", $"{i + 1}.txt")) ? File.ReadAllText(Path.Combine(outDir, "discs", $"{i + 1}.txt")) : "";
            var first = Regex.Match(log, @"(?:MISMATCH|INVALID): (.*)").Groups[1].Value;
            var kind = first.Length > 0 ? Regex.Replace(first, @"\d+", "N") : results[i].Split(' ').ElementAtOrDefault(1) ?? "?";
            kind = kind.Length > 100 ? kind[..100] : kind;
            if (!groups.TryGetValue(kind, out var list)) groups[kind] = list = new();
            list.Add($"{discs[i]}  (log {i + 1}.txt)");
        }
        foreach (var (kind, list) in groups.OrderByDescending(g => g.Value.Count))
        {
            lines.Add($"{list.Count} x {kind}");
            lines.AddRange(list.Take(8).Select(d => "    " + d));
        }

        var kb = results.Select(r => Regex.Match(r, @"\bkb=(\d+)")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).OrderBy(k => k).ToList();
        if (kb.Count > 0)
            lines.Add($"Export sizes: median {kb[kb.Count / 2]} KB, 90th percentile {kb[(int)(kb.Count * 0.9)]} KB, largest {kb[^1]} KB, total {kb.Sum() / 1024} MB");
        return string.Join(Environment.NewLine, lines);
    }
}
