using System.Text.RegularExpressions;

namespace OpenVideoMenu;

public sealed partial class Manifest
{
    private static readonly Regex UrlScheme = new("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Every menu asset the manifest names (backgrounds, highlight overlays, menu music, transitions,
    /// chapter thumbnails), as written, sorted. Media refs (the film, extras, episodes) are not
    /// assets. Absolute URLs are left out: only paths relative to the manifest, which are what a
    /// bundle holds and what servers allow. Mirrors menuAssets() in packages/core/src/assets.ts.
    /// </summary>
    public IReadOnlyList<string> MenuAssets()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && !UrlScheme.IsMatch(path) && !path.StartsWith('/'))
            {
                found.Add(path);
            }
        }

        void Walk(MenuAction? action)
        {
            switch (action)
            {
                case GotoAction g:
                    Add(g.Transition);
                    break;
                case PlayAction p:
                    Walk(p.After);
                    WalkCues(p.Cues);
                    break;
                case PlayAllAction pa:
                    Walk(pa.After);
                    foreach (var item in pa.Items)
                    {
                        WalkCues(item.Cues);
                    }

                    break;
                case SetAction s:
                    Walk(s.Then);
                    break;
                case ResumeAction r:
                    Walk(r.Otherwise);
                    break;
                case IfAction i:
                    Walk(i.Then);
                    Walk(i.Else);
                    break;
                case DoAction d:
                    d.Steps.ForEach(Walk);
                    break;
            }
        }

        void WalkButtons(IEnumerable<Button> buttons)
        {
            foreach (var button in buttons)
            {
                Add(button.States?.Normal?.Image);
                Add(button.States?.Selected?.Image);
                Add(button.States?.Activated?.Image);
                foreach (var extra in button.States?.Extensions?.Values ?? Enumerable.Empty<System.Text.Json.JsonElement>())
                {
                    if (extra.ValueKind == System.Text.Json.JsonValueKind.Object && extra.TryGetProperty("image", out var image) &&
                        image.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        Add(image.GetString());
                    }
                }

                Walk(button.Action);
            }
        }

        void WalkCues(IEnumerable<Cue>? cues)
        {
            foreach (var cue in cues ?? [])
            {
                switch (cue)
                {
                    case PointCue point:
                        Walk(point.Action);
                        break;
                    case Overlay overlay:
                        WalkButtons(overlay.Buttons);
                        break;
                }
            }
        }

        foreach (var menu in Menus?.Values ?? Enumerable.Empty<Menu>())
        {
            Add(menu.Background?.Image);
            Add(menu.Background?.Video?.Src);
            Add(menu.Background?.Audio?.Src);
            Walk(menu.Back);
            Walk(menu.Timeout?.Action);
            WalkButtons(menu.Buttons);
        }

        Walk(FirstPlay);
        foreach (var action in Actions?.Values ?? [])
        {
            Walk(action);
        }
        foreach (var group in Fallback)
        {
            foreach (var item in group.Items)
            {
                Walk(item.Action);
            }
        }

        foreach (var media in Media.Values)
        {
            foreach (var chapter in media.Chapters ?? [])
            {
                Add(chapter.Thumbnail);
            }

            WalkCues(media.Cues);
        }

        return [.. found];
    }

    /// <summary>
    /// Menu assets that aren't in <paramref name="directory"/> (the folder the loose manifest is
    /// in). Readers show menus without missing art as best they can, so these are warnings.
    /// </summary>
    public IReadOnlyList<string> MissingAssets(string directory) =>
        MenuAssets().Where(a => !File.Exists(Path.Combine(directory, a.Replace('/', Path.DirectorySeparatorChar)))).ToList();
}
