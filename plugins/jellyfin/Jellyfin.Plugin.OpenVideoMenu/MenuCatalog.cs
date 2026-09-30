using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using OpenVideoMenu;

namespace Jellyfin.Plugin.OpenVideoMenu;

/// <summary>One manifest in the index: the same shape the standalone asset server sends (API v1).</summary>
public sealed record CatalogEntry
{
    public required string Id { get; init; }
    public required int Root { get; init; }
    public required string Path { get; init; }
    public required string Url { get; init; }
    public required bool Valid { get; init; }
    public List<string>? Errors { get; init; }
    public string? Version { get; init; }
    public JsonElement? Title { get; init; }
    public JsonElement? Source { get; init; }
    public bool HasMenus { get; init; }
    public List<CatalogMedia> Media { get; init; } = [];
    public required string Modified { get; init; }

    /// <summary>Menu assets, relative to the root: the allowlist. Not sent in the index.</summary>
    [JsonIgnore]
    public List<string> Assets { get; init; } = [];
}

/// <summary>A media item of a manifest. <paramref name="Ref"/> is null for media made of <paramref name="Segments"/> (Level 2).</summary>
public sealed record CatalogMedia(string Id, string? Kind, string? Label, JsonElement? Ref, string? Path, List<CatalogAngle>? Angles = null, List<string>? Segments = null);

/// <summary>Level 2: another angle of a media item as a file (angle 2, 3, …), with its ref and path like the media's own.</summary>
public sealed record CatalogAngle(JsonElement Ref, string? Path);

/// <summary>
/// The manifests under the libraries' folders, and the exact set of files that may be served: the
/// manifests plus the menu assets they name. Nothing else, in particular no media, is reachable.
/// Mirrors packages/asset-server/src/catalog.ts.
/// </summary>
public sealed class MenuCatalog
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions IndexJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILibraryManager _libraries;
    private readonly ILogger<MenuCatalog> _log;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly Dictionary<string, (DateTime Modified, CatalogEntry Entry, IReadOnlyDictionary<string, BundleMember>? Members)> _cache = new();

    private volatile Snapshot? _snapshot;
    private Task? _background;

    public MenuCatalog(ILibraryManager libraries, ILogger<MenuCatalog> log)
    {
        _libraries = libraries;
        _log = log;
    }

    /// <summary>What one scan found. Replaced as a whole, so readers never see a half-built catalog.</summary>
    private sealed record Snapshot(
        string[] Roots,
        List<CatalogEntry> Entries,
        HashSet<string> Allowed,
        Dictionary<string, (string File, IReadOnlyDictionary<string, BundleMember> Members)> Bundles,
        string Json,
        string ETag,
        DateTime ScannedAt);

    /// <summary>An allowed file: the whole of <see cref="File"/>, or for a bundle member the slice holding it.</summary>
    public sealed record ResolvedFile(string File, string Name, long Start = 0, long? Length = null);

    /// <summary>
    /// The latest scan. The first call waits for a scan; later calls never wait, and a stale catalog is
    /// refreshed in the background.
    /// </summary>
    public async Task<(IReadOnlyList<CatalogEntry> Entries, string Json, string ETag)> Current(CancellationToken cancel)
    {
        var s = _snapshot ?? await ScanAsync(cancel).ConfigureAwait(false);
        if (DateTime.UtcNow - s.ScannedAt > Stale && (_background is null || _background.IsCompleted))
        {
            _background = Task.Run(() => ScanAsync(CancellationToken.None));
        }

        return (s.Entries, s.Json, s.ETag);
    }

    /// <summary>Number of manifests found so far (0 before the first scan).</summary>
    public int Count => _snapshot?.Entries.Count ?? 0;

    /// <summary>
    /// An allowed file, or null. <paramref name="path"/> is root-relative with forward slashes. A bundle
    /// member ("…/X.ovmz/menus/a.jpg") comes back as the bundle file plus the slice holding the member.
    /// </summary>
    public async Task<ResolvedFile?> ResolveFile(int root, string path, CancellationToken cancel)
    {
        var s = _snapshot ?? await ScanAsync(cancel).ConfigureAwait(false);
        var normalized = NormalizePosix(path);
        if (normalized is null || !s.Allowed.Contains($"{root}:{normalized}") || root < 0 || root >= s.Roots.Length)
        {
            return null;
        }

        var segments = normalized.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!Format.IsBundleFileName(segments[i]))
            {
                continue;
            }

            if (!s.Bundles.TryGetValue($"{root}:{string.Join('/', segments[..(i + 1)])}", out var bundle)
                || !bundle.Members.TryGetValue(string.Join('/', segments[(i + 1)..]), out var member))
            {
                return null;
            }

            return new ResolvedFile(bundle.File, member.Name, Bundle.DataOffset(bundle.File, member), member.Size);
        }

        var baseDir = s.Roots[root];
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, normalized.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var rel = System.IO.Path.GetRelativePath(baseDir, full);
        return rel != "." && !rel.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(rel) ? new ResolvedFile(full, full) : null;
    }

    /// <summary>Rescan now (e.g. after a Jellyfin library scan).</summary>
    public Task RescanAsync(CancellationToken cancel) => ScanAsync(cancel);

    /// <summary>Scan the library folders. Scans run one at a time.</summary>
    private async Task<Snapshot> ScanAsync(CancellationToken cancel)
    {
        await _scanLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var roots = _libraries.GetVirtualFolders()
                .SelectMany(f => f.Locations ?? [])
                .Where(Directory.Exists)
                .Select(p => System.IO.Path.GetFullPath(p))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

            var entries = new List<CatalogEntry>();
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            var bundles = new Dictionary<string, (string File, IReadOnlyDictionary<string, BundleMember> Members)>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var r = 0; r < roots.Length; r++)
            {
                var found = FindManifests(roots[r]).ToList();
                // A folder with both X.ovmz and X.openvideomenu.json: the bundle is the one to use (spec §2.1).
                var bundleBases = found.Where(Format.IsBundleFileName).Select(f => f[..^Format.BundleExtension.Length]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var rel in found.Where(f => Format.IsBundleFileName(f) || !bundleBases.Contains(f[..^Format.ManifestSuffix.Length])))
                {
                    var key = $"{r}:{roots[r]}:{rel}";
                    seen.Add(key);
                    var file = System.IO.Path.Combine(roots[r], rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    DateTime modified;
                    try
                    {
                        modified = File.GetLastWriteTimeUtc(file);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    var loaded = _cache.TryGetValue(key, out var hit) && hit.Modified == modified ? (hit.Entry, hit.Members) : Load(r, rel, file, modified);
                    _cache[key] = (modified, loaded.Item1, loaded.Item2);
                    var entry = loaded.Item1;
                    entries.Add(entry);
                    if (loaded.Item2 is { } members)
                    {
                        bundles[$"{r}:{rel}"] = (file, members);
                        if (entry.Valid)
                        {
                            allowed.Add($"{r}:{rel}/{Format.BundleManifestName}");
                        }
                    }
                    else
                    {
                        allowed.Add($"{r}:{rel}");
                    }

                    foreach (var asset in entry.Assets)
                    {
                        allowed.Add($"{r}:{asset}");
                    }
                }
            }

            foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _cache.Remove(stale);
            }

            var json = JsonSerializer.Serialize(new { api = "1", format = Format.Version, roots = roots.Length, manifests = entries }, IndexJson);
            var etag = "\"" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json)))[..20].ToLowerInvariant() + "\"";
            var snapshot = new Snapshot(roots, entries, allowed, bundles, json, etag, DateTime.UtcNow);
            _snapshot = snapshot;
            _log.LogInformation("Open Video Menu: {Count} manifest(s) in {Roots} library folder(s)", entries.Count, roots.Length);
            return snapshot;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Manifests and bundles under <paramref name="root"/>, as forward-slash relative paths. Hidden folders are skipped.</summary>
    private IEnumerable<string> FindManifests(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*" + Format.ManifestSuffix, options)
                .Concat(Directory.EnumerateFiles(root, "*" + Format.BundleExtension, options))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Open Video Menu: couldn't read {Root}: {Message}", root, e.Message);
            yield break;
        }

        foreach (var f in files.Order(StringComparer.Ordinal))
        {
            var rel = System.IO.Path.GetRelativePath(root, f).Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (rel.Split('/').Any(s => s.StartsWith('.') || s == "@eaDir"))
            {
                continue;
            }

            yield return rel;
        }
    }

    /// <summary>
    /// A manifest's catalog entry, and for a bundle its members. Media refs resolve against the folder
    /// holding the manifest (or the bundle); assets against the manifest's own location, which for a
    /// bundle is inside it ("…/X.ovmz/menus/a.jpg"), as if the bundle were a folder.
    /// </summary>
    private (CatalogEntry Entry, IReadOnlyDictionary<string, BundleMember>? Members) Load(int root, string path, string file, DateTime modified)
    {
        var isBundle = Format.IsBundleFileName(path);
        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{root}:{path}")))[..16].ToLowerInvariant();
        var url = $"/v1/files/{root}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}" + (isBundle ? "/" + Format.BundleManifestName : string.Empty);
        CatalogEntry Invalid(params string[] errors) => new() { Id = id, Root = root, Path = path, Url = url, Valid = false, Errors = [.. errors], Modified = modified.ToString("O") };

        IReadOnlyDictionary<string, BundleMember>? members = null;
        string text;
        try
        {
            if (isBundle)
            {
                var (found, readErrors) = Bundle.Read(file);
                members = found;
                var bundleErrors = readErrors.ToList();
                if (!found.TryGetValue(Format.BundleManifestName, out var manifest))
                {
                    bundleErrors.Add($"No {Format.BundleManifestName} at the bundle's root.");
                }

                if (bundleErrors.Count > 0)
                {
                    return (Invalid([.. bundleErrors]), members);
                }

                text = Encoding.UTF8.GetString(Bundle.ReadMember(file, manifest!));
            }
            else
            {
                text = File.ReadAllText(file);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (Invalid($"/: Unreadable: {e.Message}"), members);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            return (Invalid($"/: Not valid JSON: {e.Message}"), members);
        }

        using (doc)
        {
            var m = doc.RootElement;
            var result = Manifest.Validate(m);
            if (!result.Ok)
            {
                return (Invalid([.. result.Errors.Select(e => e.ToString())]), members);
            }

            var version = result.Manifest!.Version;
            var media = m.GetProperty("media");
            var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
            string? InRoot(string rel) => NormalizePosix(dir.Length == 0 ? rel : $"{dir}/{rel}");
            var assetDir = isBundle ? path : dir;
            string? AssetInRoot(string rel) => NormalizePosix(assetDir.Length == 0 ? rel : $"{assetDir}/{rel}");

            var hasMenus = m.TryGetProperty("menus", out var menus) && menus.ValueKind == JsonValueKind.Object && menus.EnumerateObject().Any();
            return (new CatalogEntry
            {
                Id = id,
                Root = root,
                Path = path,
                Url = url,
                Valid = true,
                Version = version,
                Title = m.TryGetProperty("title", out var t) ? t.Clone() : null,
                Source = m.TryGetProperty("source", out var src) ? src.Clone() : null,
                HasMenus = hasMenus,
                Media = media.EnumerateObject().Select(p =>
                {
                    JsonElement? r = p.Value.TryGetProperty("ref", out var refEl) ? refEl.Clone() : null;
                    var relPath = r is { ValueKind: JsonValueKind.String } s && IsRelative(s.GetString()!) ? InRoot(s.GetString()!) : null;
                    List<CatalogAngle>? angles = null;
                    if (p.Value.TryGetProperty("angles", out var anglesEl) && anglesEl.ValueKind == JsonValueKind.Array)
                    {
                        angles = [.. anglesEl.EnumerateArray().Select(a => new CatalogAngle(
                            a.Clone(), a.ValueKind == JsonValueKind.String && IsRelative(a.GetString()!) ? InRoot(a.GetString()!) : null))];
                    }

                    List<string>? segments = null;
                    if (p.Value.TryGetProperty("segments", out var segEl) && segEl.ValueKind == JsonValueKind.Array)
                    {
                        segments = [.. segEl.EnumerateArray().Select(seg => seg.TryGetProperty("media", out var id) ? id.GetString() ?? "" : "")];
                    }

                    return new CatalogMedia(p.Name, Str(p.Value, "kind"), Str(p.Value, "label"), r, relPath, angles, segments);
                }).ToList(),
                Assets = result.Manifest.MenuAssets().Select(AssetInRoot).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList(),
                Modified = modified.ToString("O"),
            }, members);
        }
    }

    private static readonly Regex UrlScheme = new("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsRelative(string s) => !UrlScheme.IsMatch(s) && !s.StartsWith('/');

    /// <summary>Collapse "." and ".." in a forward-slash path; null if it escapes its root.</summary>
    private static string? NormalizePosix(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg is "" or ".")
            {
                continue;
            }

            if (seg == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(seg);
            }
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Rescan manifests after every Jellyfin library scan, so new menus show up without waiting.</summary>
public sealed class RescanAfterLibraryScan(MenuCatalog catalog) : ILibraryPostScanTask
{
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken) => catalog.RescanAsync(cancellationToken);
}
