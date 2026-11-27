using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Tundy;

/// <summary>
/// Hides the game's own chat bubble that a jukebox-link tell causes.
///
/// With the game's chat bubbles on for tells, every tell puts a bubble over the people in it. Tundy hides its link
/// tells from chat, so that bubble shows the person's previous message instead, and it looks like they said it twice.
/// Only the two players in a link tell ever see it, and both run Tundy, so each one hides its own copy here: for a
/// moment after a link tell (sent or received), the next new bubble over either player is hidden. The chat log
/// itself is never touched.
///
/// The game keeps its chat bubbles in the screen-log agent (up to 10 at a time) and draws them in the "_MiniTalk"
/// window; this reads that list and hides the matching bubble's part of the window.
/// </summary>
public sealed unsafe class BubbleGuard : IDisposable
{
    /// <summary>How long after a link tell a new bubble still counts as caused by it.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1.5);

    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly Func<bool> enabled;

    private sealed class Seen
    {
        public required string? Name { get; init; }
        public required bool Self { get; init; }
        public required DateTime First { get; init; }
        public bool Hide { get; set; }
    }

    // Bubbles in the game's list right now (by the game's bubble id), and whether we're hiding them.
    private readonly Dictionary<int, Seen> seen = new();
    // Link tells waiting for their bubble: who it would show over (null = you), and when.
    private readonly List<(string? Name, DateTime At)> expected = new();
    // Bubble window slots we hid, so they can be shown again once the slot holds a real bubble.
    private readonly HashSet<int> hiddenSlots = new();

    public BubbleGuard(IAddonLifecycle lifecycle, IFramework framework, IObjectTable objects, Func<bool> enabled)
    {
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.objects = objects;
        this.enabled = enabled;
        framework.Update += OnUpdate;
        lifecycle.RegisterListener(AddonEvent.PreDraw, "_MiniTalk", OnPreDraw);
    }

    /// <summary>
    /// A link tell went to or came from <paramref name="otherName"/>: the next new bubble over them, and the next one
    /// over you, in the next moment, are hidden. Called on the game's thread.
    /// </summary>
    public void LinkTell(string? otherName)
    {
        if (!enabled()) return;
        var now = DateTime.Now;
        expected.RemoveAll(e => now - e.At > Window);
        expected.Add((null, now));
        if (!string.IsNullOrWhiteSpace(otherName)) expected.Add((otherName, now));

        // The game may already have made the bubble before the tell reached Tundy.
        Scan();
        foreach (var s in seen.Values.Where(s => !s.Hide && now - s.First < Window))
            TryMatch(s, now);
    }

    private void TryMatch(Seen s, DateTime now)
    {
        var i = expected.FindIndex(e => now - e.At <= Window
            && (e.Name == null ? s.Self : !s.Self && s.Name != null && Names.Same(e.Name, s.Name)));
        if (i < 0) return;
        expected.RemoveAt(i);
        s.Hide = true;
        Plugin.Log.Debug($"Hiding a chat bubble from a jukebox tell{(s.Self ? " (over you)" : "")}.");
    }

    private void OnUpdate(IFramework _)
    {
        if (!enabled()) expected.Clear();
        if (seen.Count == 0 && expected.Count == 0 && hiddenSlots.Count == 0 && !AnyBubbles()) return;
        Scan();
    }

    private static bool AnyBubbles()
    {
        var agent = AgentScreenLog.Instance();
        return agent != null && agent->BalloonQueue.LongCount > 0;
    }

    /// <summary>Reads the game's bubble list: notes new bubbles, matches them to link tells, forgets closed ones.</summary>
    private void Scan()
    {
        var agent = AgentScreenLog.Instance();
        if (agent == null) return;
        var count = agent->BalloonQueue.LongCount;
        if (count < 0 || count > 64) return; // not a list we understand

        var now = DateTime.Now;
        var self = objects.LocalPlayer?.Address ?? nint.Zero;
        var present = new HashSet<int>();
        for (long i = 0; i < count; i++)
        {
            ref var b = ref agent->BalloonQueue[i];
            present.Add(b.BalloonId);
            if (seen.ContainsKey(b.BalloonId)) continue;

            var isSelf = b.Character != null && (nint)b.Character == self;
            string? name = null;
            if (b.Character != null)
            {
                try { name = ((GameObject*)b.Character)->NameString; } catch { /* unreadable */ }
            }
            var s = new Seen { Name = name, Self = isSelf, First = now };
            seen[b.BalloonId] = s;

            // A bubble with the link text itself is always one of ours.
            var hasLinkText = false;
            try { hasLinkText = b.OriginalText.ToString().Contains(JukeboxProtocol.Prefix.Trim(), StringComparison.Ordinal); }
            catch { /* unreadable */ }
            if (hasLinkText && enabled()) s.Hide = true;
            else if (enabled()) TryMatch(s, now);
        }
        foreach (var id in seen.Keys.Where(id => !present.Contains(id)).ToList()) seen.Remove(id);
        expected.RemoveAll(e => now - e.At > Window);
    }

    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        if (seen.Count == 0 && hiddenSlots.Count == 0) return;
        Apply((AddonMiniTalk*)args.Addon.Address, releaseAll: false);
    }

    /// <summary>Hides the window slots that show one of our bubbles; shows a slot again once it holds a real one.</summary>
    /// <param name="releaseAll">Hide nothing (when Tundy unloads): every slot we hid that holds a bubble is shown.</param>
    private void Apply(AddonMiniTalk* addon, bool releaseAll)
    {
        var agent = AgentScreenLog.Instance();
        if (addon == null || agent == null) return;
        var count = agent->BalloonQueue.LongCount;
        if (count < 0 || count > 64) return;

        var hide = new HashSet<int>();
        var used = new HashSet<int>();
        for (long i = 0; i < count; i++)
        {
            ref var b = ref agent->BalloonQueue[i];
            var slot = SlotOf(agent, b.BalloonId, b.Slot);
            if (slot < 0) continue;
            if (!releaseAll && seen.TryGetValue(b.BalloonId, out var s) && s.Hide) hide.Add(slot);
            else used.Add(slot);
        }

        for (var slot = 0; slot < addon->TalkBubbles.Length; slot++)
        {
            var node = (AtkResNode*)addon->TalkBubbles[slot].ComponentNode;
            if (node == null) continue;
            if (hide.Contains(slot) && !used.Contains(slot))
            {
                node->ToggleVisibility(false);
                hiddenSlots.Add(slot);
            }
            else if (used.Contains(slot) && hiddenSlots.Remove(slot))
            {
                node->ToggleVisibility(true); // that slot now holds a real bubble
            }
        }
    }

    /// <summary>The window slot showing a bubble: from the agent's slot list, else the bubble's own slot number.</summary>
    private static int SlotOf(AgentScreenLog* agent, int balloonId, byte ownSlot)
    {
        for (var i = 0; i < agent->BalloonSlots.Length; i++)
        {
            var s = agent->BalloonSlots[i];
            if (s.Available == 0 && s.Id == balloonId) return i;
        }
        return ownSlot < 10 ? ownSlot : -1;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        lifecycle.UnregisterListener(AddonEvent.PreDraw, "_MiniTalk", OnPreDraw);
        if (hiddenSlots.Count == 0) return;
        // Leave no real bubble hidden.
        try { Apply((AddonMiniTalk*)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("_MiniTalk"), releaseAll: true); }
        catch { /* game closing */ }
        hiddenSlots.Clear();
    }
}
