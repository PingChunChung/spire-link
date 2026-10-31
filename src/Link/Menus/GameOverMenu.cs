using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using SpireLink.Actions;

namespace SpireLink.Menus;

// The game over screen: only "main_menu" does anything. The screen's own button is clicked; when it is not clickable yet
// the screen's return method is called through Godot instead.
internal static class GameOverMenu
{
    public static Dictionary<string, object?> Select(NGameOverScreen screen, string option)
    {
        if (MenuActions.Is(option, "continue")) return Outcome.Fail("Game-over option 'continue' is not actionable. Use: main_menu");
        if (!MenuActions.Is(option, "main_menu")) return Outcome.Fail($"Unknown game over option: {option}. Use: main_menu");

        var clicked = MenuActions.ClickField(screen, "_mainMenuButton", "Returning to main menu");
        if (clicked["status"] is "ok") return clicked;
        if (!screen.HasMethod(NGameOverScreen.MethodName.ReturnToMainMenu)) return Outcome.Fail("Game-over main_menu option is not available");
        screen.Call(NGameOverScreen.MethodName.ReturnToMainMenu);
        return Outcome.Ok("Returning to main menu");
    }
}
