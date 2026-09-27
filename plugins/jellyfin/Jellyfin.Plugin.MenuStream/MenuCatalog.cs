using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MenuStream;

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

public sealed record CatalogMedia(string Id, string? Kind, string? Label, JsonElement Ref, string? Path);

/// <summary>
/// The manifests under the libraries' folders, and the exact set of files that may be served: the
/// manifests plus the menu assets they name. Nothing else, in particular no media, is reachable.
/// Mirrors packages/asset-server/src/catalog.ts.
/// </summary>
public sealed class MenuCatalog
{
    public const string ManifestSuffix = ".menustream.json";
    public const string FormatVersion = "0.2";
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions IndexJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILibraryManager _libraries;
    private readonly ILogger<MenuCatalog> _log;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly Dictionary<string, (DateTime Modified, CatalogEntry Entry)> _cache = new();

    private volatile Snapshot? _snapshot;
    private Task? _background;

    public MenuCatalog(ILibraryManager libraries, ILogger<MenuCatalog> log)
    {
        _libraries = libraries;
        _log = log;
    }

    /// <summary>What one scan found. Replaced as a whole, so readers never see a half-built catalog.</summary>
    private sealed record Snapshot(string[] Roots, List<CatalogEntry> Entries, HashSet<string> Allowed, string Json, string ETag, DateTime ScannedAt);

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

    /// <summary>Absolute path of an allowed file, or null. <paramref name="path"/> is root-relative with forward slashes.</summary>
    public async Task<string?> ResolveFile(int root, string path, CancellationToken cancel)
    {
        var s = _snapshot ?? await ScanAsync(cancel).ConfigureAwait(false);
        var normalized = NormalizePosix(path);
        if (normalized is null || !s.Allowed.Contains($"{root}:{normalized}") || root < 0 || root >= s.Roots.Length)
        {
            return null;
        }

        var baseDir = s.Roots[root];
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, normalized.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var rel = System.IO.Path.GetRelativePath(baseDir, full);
        return rel != "." && !rel.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(rel) ? full : null;
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
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var r = 0; r < roots.Length; r++)
            {
                foreach (var rel in FindManifests(roots[r]))
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

                    var entry = _cache.TryGetValue(key, out var hit) && hit.Modified == modified ? hit.Entry : Load(r, rel, file, modified);
                    _cache[key] = (modified, entry);
                    entries.Add(entry);
                    allowed.Add($"{r}:{rel}");
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

            var json = JsonSerializer.Serialize(new { api = "1", format = FormatVersion, roots = roots.Length, manifests = entries }, IndexJson);
            var etag = "\"" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json)))[..20].ToLowerInvariant() + "\"";
            var snapshot = new Snapshot(roots, entries, allowed, json, etag, DateTime.UtcNow);
            _snapshot = snapshot;
            _log.LogInformation("MenuStream: {Count} manifest(s) in {Roots} library folder(s)", entries.Count, roots.Length);
            return snapshot;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Manifest files under <paramref name="root"/>, as forward-slash relative paths. Hidden folders are skipped.</summary>
    private IEnumerable<string> FindManifests(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*" + ManifestSuffix, options).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("MenuStream: couldn't read {Root}: {Message}", root, e.Message);
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

    private CatalogEntry Load(int root, string path, string file, DateTime modified)
    {
        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{root}:{path}")))[..16].ToLowerInvariant();
        var url = $"/v1/files/{root}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
        CatalogEntry Invalid(params string[] errors) => new() { Id = id, Root = root, Path = path, Url = url, Valid = false, Errors = [.. errors], Modified = modified.ToString("O") };

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return Invalid($"/: Not valid JSON or unreadable: {e.Message}");
        }

        using (doc)
        {
            var m = doc.RootElement;
            var errors = new List<string>();
            if (m.ValueKind != JsonValueKind.Object)
            {
                return Invalid("/: A manifest must be a JSON object.");
            }

            if (Str(m, "format") != "menustream")
            {
                errors.Add("/format: must be \"menustream\"");
            }

            var version = Str(m, "version");
            if (version is null || !version.StartsWith("0.", StringComparison.Ordinal))
            {
                errors.Add($"/version: this server reads 0.x manifests, not \"{version}\"");
            }

            if (!m.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object || !media.EnumerateObject().Any())
            {
                errors.Add("/media: at least one media item is required");
            }

            if (!m.TryGetProperty("fallback", out var fallback) || fallback.ValueKind != JsonValueKind.Array || fallback.GetArrayLength() == 0)
            {
                errors.Add("/fallback: a fallback list is required");
            }

            if (errors.Count > 0)
            {
                return Invalid([.. errors]);
            }

            var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
            string? InRoot(string rel) => NormalizePosix(dir.Length == 0 ? rel : $"{dir}/{rel}");

            var hasMenus = m.TryGetProperty("menus", out var menus) && menus.ValueKind == JsonValueKind.Object && menus.EnumerateObject().Any();
            return new CatalogEntry
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
                    var r = p.Value.TryGetProperty("ref", out var refEl) ? refEl.Clone() : default;
                    var relPath = r.ValueKind == JsonValueKind.String && IsRelative(r.GetString()!) ? InRoot(r.GetString()!) : null;
                    return new CatalogMedia(p.Name, Str(p.Value, "kind"), Str(p.Value, "label"), r, relPath);
                }).ToList(),
                Assets = MenuAssets(m).Select(InRoot).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList(),
                Modified = modified.ToString("O"),
            };
        }
    }

    /// <summary>
    /// Every menu asset a manifest names (backgrounds, highlight overlays, menu music, transitions,
    /// chapter thumbnails). Media refs are not assets. Mirrors menuAssets() in packages/core/src/assets.ts.
    /// </summary>
    public static IEnumerable<string> MenuAssets(JsonElement manifest)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Add(JsonElement? el)
        {
            if (el is { ValueKind: JsonValueKind.String } v && v.GetString() is { Length: > 0 } s && IsRelative(s))
            {
                found.Add(s);
            }
        }

        void WalkAction(JsonElement action)
        {
            if (action.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = Str(action, "type");
            if (type == "goto")
            {
                Add(Prop(action, "transition"));
            }

            if (type is "play" or "playAll" && action.TryGetProperty("after", out var after))
            {
                WalkAction(after);
            }

            if (type == "set" && action.TryGetProperty("then", out var then))
            {
                WalkAction(then);
            }
        }

        if (manifest.TryGetProperty("menus", out var menus) && menus.ValueKind == JsonValueKind.Object)
        {
            foreach (var menu in menus.EnumerateObject().Select(p => p.Value))
            {
                if (menu.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.Object)
                {
                    Add(Prop(bg, "image"));
                    if (bg.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
                    {
                        Add(Prop(video, "src"));
                    }

                    if (bg.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object)
                    {
                        Add(Prop(audio, "src"));
                    }
                }

                if (menu.TryGetProperty("buttons", out var buttons) && buttons.ValueKind == JsonValueKind.Array)
                {
                    foreach (var button in buttons.EnumerateArray())
                    {
                        if (button.TryGetProperty("states", out var states) && states.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var state in states.EnumerateObject())
                            {
                                if (state.Value.ValueKind == JsonValueKind.Object)
                                {
                                    Add(Prop(state.Value, "image"));
                                }
                            }
                        }

                        if (button.TryGetProperty("action", out var action))
                        {
                            WalkAction(action);
                        }
                    }
                }
            }
        }

        if (manifest.TryGetProperty("firstPlay", out var firstPlay))
        {
            WalkAction(firstPlay);
        }

        if (manifest.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in media.EnumerateObject())
            {
                if (item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chapter in chapters.EnumerateArray())
                    {
                        if (chapter.ValueKind == JsonValueKind.Object)
                        {
                            Add(Prop(chapter, "thumbnail"));
                        }
                    }
                }
            }
        }

        return found;
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

    private static JsonElement? Prop(JsonElement el, string name) => el.TryGetProperty(name, out var v) ? v : null;

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Rescan manifests after every Jellyfin library scan, so new menus show up without waiting.</summary>
public sealed class RescanAfterLibraryScan(MenuCatalog catalog) : ILibraryPostScanTask
{
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken) => catalog.RescanAsync(cancellationToken);
}
