using Godot;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group C. The "crystal_sphere" field (the divination minigame overlay).
// Contract: {instructions_title?, instructions_description?, grid_width, grid_height, cells: [{x, y, is_hidden,
// is_clickable, is_highlighted, is_hovered, item_type?, is_good?}] (row-major), clickable_cells: [{x, y}],
// revealed_items: [{item_type, x, y, width, height, is_good}], tool ("big"/"small"/"none"), can_use_big_tool,
// can_use_small_tool, divinations_left_text?, can_proceed}.
// Cells are the screen's NCrystalSphereCell nodes, one per grid entity; a cell can be clicked while its fog is still
// there. An item spans several cells and is listed once, in the order its cells first appear in the tree. The tool
// buttons hide when the minigame ends and the proceed button appears.
internal static class CrystalSphereScreen
{
    public static Dictionary<string, object?> Read(NCrystalSphereScreen screen)
    {
        var state = new Dictionary<string, object?>();
        if (Text.OfLabel(screen.GetNodeOrNull<Control>("%InstructionsTitle")) is { } title) state["instructions_title"] = title;
        if (Text.OfLabel(screen.GetNodeOrNull<Control>("%InstructionsDescription")) is { } text) state["instructions_description"] = text;

        var cells = Nodes.All<NCrystalSphereCell>(screen).ToList();
        state["grid_width"] = cells.Count == 0 ? 0 : cells.Max(c => c.Entity.X) + 1;
        state["grid_height"] = cells.Count == 0 ? 0 : cells.Max(c => c.Entity.Y) + 1;

        var grid = new List<Dictionary<string, object?>>();
        var clickable = new List<Dictionary<string, object?>>();
        foreach (var cell in cells.OrderBy(c => c.Entity.Y).ThenBy(c => c.Entity.X))
        {
            var entity = cell.Entity;
            bool canClick = entity.IsHidden && cell.Visible;
            var entry = new Dictionary<string, object?>
            {
                ["x"] = entity.X,
                ["y"] = entity.Y,
                ["is_hidden"] = entity.IsHidden,
                ["is_clickable"] = canClick,
                ["is_highlighted"] = entity.IsHighlighted,
                ["is_hovered"] = entity.IsHovered
            };
            if (!entity.IsHidden && entity.Item is { } item)
            {
                entry["item_type"] = item.GetType().Name;
                entry["is_good"] = item.IsGood;
            }
            grid.Add(entry);
            if (canClick) clickable.Add(new() { ["x"] = entity.X, ["y"] = entity.Y });
        }
        state["cells"] = grid;
        state["clickable_cells"] = clickable;
        state["revealed_items"] = cells.Where(c => !c.Entity.IsHidden && c.Entity.Item != null)
            .Select(c => c.Entity.Item!).Distinct().Select(Item).ToList();

        var big = screen.GetNodeOrNull<Control>("%BigDivinationButton");
        var small = screen.GetNodeOrNull<Control>("%SmallDivinationButton");
        state["tool"] = Selected(big) ? "big" : Selected(small) ? "small" : "none";
        state["can_use_big_tool"] = big?.Visible == true;
        state["can_use_small_tool"] = small?.Visible == true;
        if (Text.OfLabel(screen.GetNodeOrNull<Control>("%DivinationsLeft")) is { } left) state["divinations_left_text"] = left;
        state["can_proceed"] = ProceedButton(screen) != null;
        return state;
    }

    private static Dictionary<string, object?> Item(CrystalSphereItem item) => new()
    {
        ["item_type"] = item.GetType().Name,
        ["x"] = item.Position.X,
        ["y"] = item.Position.Y,
        ["width"] = item.Size.X,
        ["height"] = item.Size.Y,
        ["is_good"] = item.IsGood
    };

    // The divination button whose outline shows is the selected tool.
    private static bool Selected(Control? button) => button?.GetNodeOrNull<Control>("%Outline")?.Visible == true;

    // The proceed button when it can be clicked: %ProceedButton, else any enabled proceed button on the screen.
    public static NProceedButton? ProceedButton(NCrystalSphereScreen screen)
    {
        if (screen.GetNodeOrNull<NProceedButton>("%ProceedButton") is { } named && Ui.Enabled(named)) return named;
        return Nodes.All<NProceedButton>(screen).FirstOrDefault(b => Ui.Enabled(b));
    }
}
