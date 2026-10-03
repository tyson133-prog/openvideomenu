using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Tests;

/// <summary>The package end to end on <see cref="SyntheticDisc"/>: read, map, run, translate, verify, bundle.</summary>
public class SyntheticDiscTests
{
    [Fact]
    public void Reads_the_structure()
    {
        using var disc = SyntheticDisc.Open();
        Assert.Equal(2, disc.Vmg.Titles.Count);
        var menu = disc.GetHighlight(DvdDomain.VmgMenu, 0, 1, 1);
        Assert.NotNull(menu);
        Assert.Equal(3, menu!.Buttons.Count);
        Assert.Equal(Convert.FromHexString(SyntheticDisc.JumpTitle2), menu.Buttons[1].Command);
        Assert.Equal(1200, (int)Math.Round(disc.GetPgc(DvdDomain.Title, 1, 2)!.Cells[0].Duration.TotalSeconds));
    }

    [Fact]
    public void Maps_every_button_to_its_title()
    {
        using var disc = SyntheticDisc.Open();
        var map = DiscMapBuilder.Build(disc);
        Assert.NotNull(map.StartMenu);
        var targets = map.StartMenu!.Buttons.Take(2).Select(b => b.Target.Landing?.Ttn).ToList();
        Assert.Equal(new int?[] { 1, 2 }, targets);
    }

    [Fact]
    public void The_machine_plays_a_title_and_resumes_it()
    {
        using var disc = SyntheticDisc.Open();
        var vm = new DvdVm(disc);
        var menu = vm.StartFromFirstPlay();
        Assert.Equal(VmStopKind.Menu, menu.Kind);

        var title = vm.ActivateButton(menu.State, menu.Highlight!.Buttons[1]);
        Assert.Equal(VmStopKind.Title, title.Kind);
        Assert.Equal(2, title.Landing!.Ttn);

        // The title ends: its post commands call the menu, keeping a resume point.
        var back = vm.ContinueAfterTitle(title.State);
        Assert.Equal(VmStopKind.Menu, back.Kind);
        Assert.NotNull(back.State.Resume);
    }

    [Fact]
    public void Player_settings_reach_the_registers()
    {
        var s = new VmState(new DvdPlayerSettings { MenuLanguage = "fr", Region = 2, ParentalLevel = 4 });
        Assert.Equal(DvdPlayerSettings.Code("fr"), s.GetSprm(0));
        Assert.Equal(2, s.GetSprm(20));
        Assert.Equal(4, s.GetSprm(13));
    }

    [Fact]
    public void The_observer_sees_every_step()
    {
        using var disc = SyntheticDisc.Open();
        // The state is live: an observer that keeps it records a copy.
        var steps = new List<(VmStepKind Kind, DvdDomain Domain)>();
        var vm = new DvdVm(disc) { Observer = s => steps.Add((s.Kind, s.State.Domain)) };
        vm.StartFromFirstPlay();
        Assert.Contains((VmStepKind.EnterPgc, DvdDomain.FirstPlay), steps);
        Assert.Contains((VmStepKind.PlayCell, DvdDomain.VmgMenu), steps);
    }

    private static (Manifest Manifest, MenuManifestBuilder Builder) Export(DvdDisc disc, ManifestPasses passes = ManifestPasses.All)
    {
        var set = DvdMedia.OneFilePerTitle(disc, t => $"Title {t}.mkv");
        var builder = new MenuManifestBuilder(disc, DiscMapBuilder.Build(disc), set.Media, set.Index, new DvdManifestOptions { Title = "Synthetic", Passes = passes });
        return (builder.Build(), builder);
    }

    [Fact]
    public void Exports_a_valid_manifest_that_plays_the_files()
    {
        using var disc = SyntheticDisc.Open();
        var (manifest, builder) = Export(disc);
        Assert.True(manifest.Validate().Ok, string.Join("; ", manifest.Validate().Errors.Select(e => e.Message)));
        Assert.Equal(3, manifest.Level);
        Assert.Single(manifest.Menus!);
        Assert.Equal(3, manifest.Menus!.Values.Single().Buttons.Count);
        Assert.Equal("Title 1.mkv", manifest.Media["t1"].Ref?.ToString());
        Assert.Equal(0, builder.UnrippedButtons);
    }

    [Fact]
    public void A_partial_export_leaves_buttons_to_missing_titles_inert()
    {
        using var disc = SyntheticDisc.Open();
        var set = DvdMedia.OneFilePerTitle(disc, t => t == 1 ? "Title 1.mkv" : null);
        var builder = new MenuManifestBuilder(disc, DiscMapBuilder.Build(disc), set.Media, set.Index);
        var manifest = builder.Build();
        Assert.True(manifest.Validate().Ok);
        Assert.False(manifest.Media.ContainsKey("t2"));
        Assert.Contains(2, builder.MissingTitles);
    }

    [Theory]
    [InlineData(ManifestPasses.All)]
    [InlineData(ManifestPasses.None)]
    public void Verifies_against_the_disc(ManifestPasses passes)
    {
        using var disc = SyntheticDisc.Open();
        var report = MenuVerifier.Run(disc, DiscMapBuilder.Build(disc), new MenuVerifyOptions { Walks = 40, Depth = 12 });
        Assert.Empty(report.ValidationErrors);
        Assert.True(report.Problems.Count == 0, string.Join(" | ", report.Problems.Take(3)));
        Assert.Equal(0, report.Mismatches);
        Assert.True(report.Matches > 40);

        // The same check over a manifest made outside the verifier (its menus found by their x-dvd).
        var set = DvdMedia.OneFilePerTitle(disc, t => $"t{t}.mkv", (v, p) => $"v{v}p{p}.mkv");
        var external = new MenuManifestBuilder(disc, null, set.Media, set.Index, new DvdManifestOptions { Passes = passes }).Build();
        var again = MenuVerifier.Run(disc, null, new MenuVerifyOptions { Walks = 40, Depth = 12, Export = new VerifyExport(external, set) });
        Assert.Equal(0, again.Mismatches);
        Assert.True(again.Matches > 40);
    }

    [Fact]
    public void Verifies_another_navigator_through_the_adapter()
    {
        using var disc = SyntheticDisc.Open();
        var calls = 0;
        var report = MenuVerifier.Run(disc, null, new MenuVerifyOptions
        {
            Walks = 10,
            Depth = 8,
            Navigator = (m, s, e) => { calls++; return Navigator.Step(m, s, e); },
        });
        Assert.Equal(0, report.Mismatches);
        Assert.True(calls > 10);
    }

    [Fact]
    public async Task Writes_a_bundle_without_art()
    {
        using var disc = SyntheticDisc.Open();
        var (_, builder) = Export(disc);
        var folder = Path.Combine(Path.GetTempPath(), "ovm-dvd-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var result = await DvdExport.ToBundleAsync(builder, disc, Path.Combine(folder, "Synthetic.ovmz"), art: null);
            Assert.True(result.Written, string.Join("; ", result.Warnings));
            Assert.True(Bundle.Validate(result.BundlePath!).Ok);
            Assert.Equal(1, result.Menus);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Art_without_a_decoder_still_makes_highlights_and_says_so()
    {
        using var disc = SyntheticDisc.Open();
        var (_, builder) = Export(disc);
        var folder = Path.Combine(Path.GetTempPath(), "ovm-dvd-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var result = await DvdExport.ToBundleAsync(builder, disc, Path.Combine(folder, "Synthetic.ovmz"), new DvdArtOptions { Decoder = null });
            Assert.True(result.Written);
            Assert.Contains(result.Warnings, w => w.Code == DvdWarningCode.NoDecoder);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
