using OpenVideoMenu.Dvd.Export;

namespace OpenVideoMenu.Dvd.Tests;

/// <summary>
/// Chapters on video a rip holds once: a chapter is a place in the title's list of cells, so a title that lists
/// one clip twice plays it twice (a composite) and its chapters land at distinct times.
/// </summary>
public class CompositeChapterTests
{
    private static (OpenVideoMenu.Manifest Manifest, MenuManifestBuilder Builder) Export(bool repeatChapter, Action<DvdMediaSet>? change = null)
    {
        using var disc = SyntheticDisc.Open(repeatCell: true, repeatChapter: repeatChapter);
        var set = DvdMedia.OneFilePerTitle(disc, t => $"t{t}.mkv", onceEach: true);
        change?.Invoke(set);
        var builder = new MenuManifestBuilder(disc, DiscMapBuilder.Build(disc), set.Media, set.Index);
        var manifest = builder.Build();
        Assert.True(manifest.Validate().Ok, string.Join("; ", manifest.Validate().Errors.Select(e => $"{e.Path}: {e.Message}")));
        return (manifest, builder);
    }

    private static OpenVideoMenu.MediaEntry Composite(OpenVideoMenu.Manifest m) =>
        m.Media.Values.Single(e => e.Segments is { Count: 2 } s && s.All(x => x.Media == "t2"));

    [Fact]
    public void A_chapter_on_the_repeated_clip_is_a_distinct_point_of_the_composite()
    {
        var (manifest, _) = Export(repeatChapter: true);
        Assert.Equal(new[] { 0, 300.3 }, Composite(manifest).Chapters!.Select(c => c.Time));
    }

    [Fact]
    public void Listing_a_clip_again_without_a_chapter_adds_none()
    {
        // Title 2 is one chapter: the second listing isn't a chapter, however many segments play it.
        var (manifest, _) = Export(repeatChapter: false);
        Assert.Null(Composite(manifest).Chapters);
    }

    [Fact]
    public void A_files_chapter_marks_that_go_back_are_left_out_with_a_warning()
    {
        // A ripper that marks every disc chapter on the one copy: chapter 2 at the same time as chapter 1.
        var (manifest, builder) = Export(repeatChapter: true, set =>
            set.Media["t2"].Chapters = new List<OpenVideoMenu.Chapter> { new() { Time = 0 }, new() { Time = 0 }, new() { Time = 120 } });
        Assert.Equal(new[] { 0.0, 120 }, manifest.Media["t2"].Chapters!.Select(c => c.Time));
        Assert.Contains(builder.Warnings, w => w.Code == DvdWarningCode.ChaptersNotForward && w.Message.Contains("chapters 2"));
    }
}
