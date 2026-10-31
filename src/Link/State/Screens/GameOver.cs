using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;

namespace SpireLink.State.Screens;

// Group D. The "game_over" field. Contract: {message: "Run ended.", options: ["main_menu"]}.
internal static class GameOverScreen
{
    public static Dictionary<string, object?> Read(NGameOverScreen screen) => new()
    {
        ["message"] = "Run ended.",
        ["options"] = new List<string> { "main_menu" }
    };
}
