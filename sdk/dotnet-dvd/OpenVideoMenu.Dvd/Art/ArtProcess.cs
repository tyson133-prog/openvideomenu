using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenVideoMenu.Dvd.Art;

/// <summary>A tool the art maker runs (ffmpeg) couldn't start.</summary>
internal sealed class ArtToolException(string message) : Exception(message);

/// <summary>Runs the art maker's command-line tool, feeding its output to callbacks line by line; kills it on cancel.</summary>
internal static class ArtProcess
{
    public static async Task<int> RunAsync(string exe, IEnumerable<string> args, Action<string> onStdout, Action<string> onStderr, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ArtToolException($"Couldn't start {Path.GetFileName(exe)}: {ex.Message}");
        }
        proc.StandardInput.Close();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });

        var err = Pump(proc.StandardError, onStderr, ct);
        await Pump(proc.StandardOutput, onStdout, ct);
        await err;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private static async Task Pump(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null) onLine(line);
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
