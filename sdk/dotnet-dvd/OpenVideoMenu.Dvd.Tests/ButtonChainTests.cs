namespace OpenVideoMenu.Dvd.Tests;

/// <summary>Which title a button plays when its title runs on into others: the last one with real content.</summary>
public class ButtonChainTests
{
    private static (ButtonTarget Target, DiscMap Map) FirstButton(TitleChain chain)
    {
        using var disc = SyntheticDisc.Open(chain: chain);
        var map = DiscMapBuilder.Build(disc);
        return (map.StartMenu!.Buttons[0].Target, map);
    }

    [Fact]
    public void An_extra_followed_by_an_empty_title_is_the_buttons_title()
    {
        var (target, map) = FirstButton(TitleChain.ExtraThenStub);
        Assert.Equal(1, target.Landing!.Ttn);
        Assert.Empty(target.PassedThrough);
        Assert.Equal(new[] { 2 }, target.FollowedBy.Select(t => t.Ttn));
        Assert.Equal(Reachability.OnMenu, map.GetTitle(1)!.Reach);
        Assert.NotNull(map.GetTitle(2)!.JunkReason);
    }

    [Fact]
    public void A_warning_before_the_film_still_credits_the_film()
    {
        var (target, map) = FirstButton(TitleChain.PrerollThenFilm);
        Assert.Equal(2, target.Landing!.Ttn);
        Assert.Equal(new[] { 1 }, target.PassedThrough.Select(t => t.Ttn));
        Assert.Empty(target.FollowedBy);
        Assert.Null(map.GetTitle(1)!.JunkReason);
    }

    [Fact]
    public void A_chain_of_nothing_but_junk_keeps_its_last_title()
    {
        var (target, _) = FirstButton(TitleChain.AllJunk);
        Assert.Equal(2, target.Landing!.Ttn);
        Assert.Equal(new[] { 1 }, target.PassedThrough.Select(t => t.Ttn));
        Assert.Empty(target.FollowedBy);
    }

    [Fact]
    public void An_empty_title_that_hands_back_to_another_menu_leads_to_that_menu()
    {
        // The stub, then a clip without buttons, then a menu that waits for the viewer.
        var (target, map) = FirstButton(TitleChain.StubToOtherMenu);
        Assert.Equal(TargetKind.Menu, target.Kind);
        Assert.Equal(2, target.Menu!.Pgcn);
        Assert.Equal(2, target.Menu.Buttons.Count);
        Assert.Equal(new[] { 1 }, target.PassedThrough.Select(t => t.Ttn));
        Assert.Equal(Reachability.OffMenu, map.GetTitle(1)!.Reach);
        // The other menu's first button plays title 2: on a menu now.
        Assert.Equal(Reachability.OnMenu, map.GetTitle(2)!.Reach);
    }

    [Fact]
    public void An_empty_title_that_returns_to_the_same_menu_stays_the_buttons_title()
    {
        var (target, _) = FirstButton(TitleChain.StubBackToMenu);
        Assert.Equal(TargetKind.Title, target.Kind);
        Assert.Equal(1, target.Landing!.Ttn);
    }
}
