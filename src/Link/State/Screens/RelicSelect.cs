using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group D. The "relic_select" field (choose one relic).
// Contract: {prompt: "Choose a relic.", relics: [Relics.Choice + actionable (screen not complete and holder enabled);
// index counts every holder, those without a relic skipped], can_skip}.
internal static class RelicSelectScreen
{
    public static Dictionary<string, object?> Read(NChooseARelicSelection screen)
    {
        bool open = Ui.ChoiceOpen(screen);
        var relics = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in Nodes.All<NRelicBasicHolder>(screen))
        {
            int slot = index++;
            if (holder.Relic?.Model is not { } relic) continue;
            var entry = Relics.Choice(relic, slot);
            entry["actionable"] = open && Ui.Enabled(holder);
            relics.Add(entry);
        }
        return new()
        {
            ["prompt"] = "Choose a relic.",
            ["relics"] = relics,
            ["can_skip"] = open && Ui.Enabled(Ui.SkipButton(screen))
        };
    }
}
