using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>What menu art to make, and with what.</summary>
public sealed class DvdArtOptions
{
    /// <summary>Decodes the menu video: ffmpeg (<see cref="FfmpegMenuDecoder"/>) or the exporter's own; null: no backgrounds, motion or music.</summary>
    public IMenuVideoDecoder? Decoder { get; init; }

    /// <summary>What to make (all of it by default).</summary>
    public MenuArtKinds Kinds { get; init; } = MenuArtKinds.All;
}

/// <summary>A written bundle (or why there isn't one), with what the exporter should know.</summary>
/// <param name="BundlePath">The bundle, or null when it wasn't written (see <paramref name="Warnings"/>).</param>
/// <param name="Menus">Menus in the manifest.</param>
/// <param name="UnrippedButtons">Buttons that lead to titles no file holds: they stay on the menus but do nothing.</param>
public sealed record DvdExportResult(string? BundlePath, int Menus, int UnrippedButtons, DvdWarnings Warnings)
{
    public bool Written => BundlePath is not null;
}

/// <summary>Writes a disc's manifest and its menu art as an Open Video Menu bundle.</summary>
public static class DvdExport
{
    /// <summary>
    /// Makes the menu art, builds and validates the manifest, and packs it with the art into
    /// <paramref name="bundlePath"/> (written through a temporary file, so a failed export never replaces a good
    /// bundle). Media refs in the manifest are as the builder's media give them: relative to the bundle's folder.
    /// </summary>
    /// <param name="art">The art to make; null: none.</param>
    public static async Task<DvdExportResult> ToBundleAsync(MenuManifestBuilder builder, DvdDisc disc, string bundlePath, DvdArtOptions? art,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var warnings = new DvdWarnings();
        var assets = Path.Combine(Path.GetTempPath(), "ovm-dvd-" + Guid.NewGuid().ToString("N")[..8]);
        MenuArtMaker? maker = null;
        try
        {
            if (art is not null && art.Kinds != MenuArtKinds.None)
            {
                maker = new MenuArtMaker(disc, art.Decoder, assets, warnings);
                var motion = art.Kinds.HasFlag(MenuArtKinds.Motion);
                var menus = builder.Menus.ToList();
                var n = 0;
                foreach (var m in menus)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Menu art {++n} of {menus.Count}");
                    builder.Art[m] = await maker.MakeAsync(m, builder.IdOf(m), art.Kinds, ct, loop: motion ? builder.LoopContinuation(m) : null);
                }
                if (motion)
                {
                    // Transitions are made on demand while building; the decoder runs synchronously there.
                    var video = menus.FirstOrDefault()?.Video ?? new VideoAttributes();
                    builder.Transition = cells => maker.TransitionAsync(cells, video, ct).GetAwaiter().GetResult();
                }
            }

            progress?.Report("Writing the menu file");
            var manifest = builder.Build();
            warnings.AddRange(builder.Warnings);
            var check = manifest.Validate();
            foreach (var w in check.Warnings) warnings.Add(DvdWarningCode.ManifestWarning, $"{w.Path}: {w.Message}");
            if (!check.Ok)
            {
                warnings.AddRange(check.Errors.Select(e => new DvdWarning(DvdWarningCode.ManifestInvalid, $"Not written, {e.Path}: {e.Message}")));
                return new DvdExportResult(null, 0, builder.UnrippedButtons, warnings);
            }

            if (Path.GetDirectoryName(Path.GetFullPath(bundlePath)) is { } folder) Directory.CreateDirectory(folder);
            var temp = bundlePath + ".tmp";
            Directory.CreateDirectory(assets);
            var manifestFile = Path.Combine(assets, "manifest.openvideomenu.json");
            File.WriteAllText(manifestFile, CompactJson(manifest));
            var packed = Bundle.Pack(manifestFile, temp);
            warnings.AddRange(packed.Problems.Select(p => new DvdWarning(DvdWarningCode.BundleProblem, p)));
            if (!File.Exists(temp) || !Bundle.Validate(temp).Ok)
            {
                ArtProcess.TryDelete(temp);
                warnings.Add(DvdWarningCode.BundleInvalid, "The menu bundle didn't pass its own check; not written.");
                return new DvdExportResult(null, 0, builder.UnrippedButtons, warnings);
            }
            File.Move(temp, bundlePath, overwrite: true);
            return new DvdExportResult(bundlePath, manifest.Menus?.Count ?? 0, builder.UnrippedButtons, warnings);
        }
        finally
        {
            maker?.Cleanup();
            try { if (Directory.Exists(assets)) Directory.Delete(assets, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A manifest as compact JSON: a disc's logic nests deep, and indenting it would be most of the file.</summary>
    public static string CompactJson(Manifest manifest) => JsonSerializer.Serialize(manifest, DvdMenuCompiler.DeepJson) + "\n";
}
