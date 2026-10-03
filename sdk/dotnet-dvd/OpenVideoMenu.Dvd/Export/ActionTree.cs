using System;
using System.Collections.Generic;
using System.Linq;
using OpenVideoMenu;

namespace OpenVideoMenu.Dvd.Export;

/// <summary>Walking action trees: an action's children, and every action under one (optionally into named actions).</summary>
public static class ActionTree
{
    public static IEnumerable<MenuAction?> Children(MenuAction a) => a switch
    {
        DoAction d => d.Steps,
        IfAction i => new[] { i.Then, i.Else },
        SetAction s => new[] { s.Then },
        PlayAction p => new[] { p.After }.Concat(p.Cues?.OfType<PointCue>().Select(c => (MenuAction?)c.Action) ?? Array.Empty<MenuAction?>())
            .Concat(p.Cues?.OfType<Overlay>().SelectMany(o => o.Buttons.Select(b => (MenuAction?)b.Action)) ?? Array.Empty<MenuAction?>()),
        PlayAllAction pa => new[] { pa.After },
        ResumeAction r => new[] { r.Otherwise },
        SaveResumeAction sr => new[] { sr.After }.Concat(new[] { sr.Item }.Concat(sr.Queue ?? new()).Where(i => i is not null)
            .SelectMany(i => ItemActions(i!))),
        RequestParentalAction q => new[] { q.Then, q.Else },
        TimerAction t => new[] { t.Action },
        _ => Array.Empty<MenuAction?>(),
    };

    /// <summary>The actions a play item holds: its point cues' and its overlays' buttons.</summary>
    public static IEnumerable<MenuAction?> ItemActions(PlayItem i) =>
        (i.Cues?.OfType<PointCue>().Select(c => (MenuAction?)c.Action) ?? Array.Empty<MenuAction?>())
        .Concat(i.Cues?.OfType<Overlay>().SelectMany(o => o.Buttons.Select(b => (MenuAction?)b.Action)) ?? Array.Empty<MenuAction?>());

    /// <summary>The action and everything under it, depth first.</summary>
    public static IEnumerable<MenuAction> Walk(MenuAction? a)
    {
        if (a is null) yield break;
        var stack = new Stack<MenuAction>();
        stack.Push(a);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            yield return x;
            foreach (var c in Children(x).Reverse())
                if (c is not null) stack.Push(c);
        }
    }

    /// <summary>Like <see cref="Walk"/>, also going into the named actions a <c>run</c> reaches (each once).</summary>
    public static IEnumerable<MenuAction> WalkRuns(MenuAction? a, IReadOnlyDictionary<string, MenuAction>? named)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<MenuAction>();
        if (a is not null) stack.Push(a);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            yield return x;
            if (x is RunAction run && named is not null && seen.Add(run.Action) && named.TryGetValue(run.Action, out var body))
                stack.Push(body);
            foreach (var c in Children(x).Reverse())
                if (c is not null) stack.Push(c);
        }
    }
}
