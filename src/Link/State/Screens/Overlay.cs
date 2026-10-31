using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace SpireLink.State.Screens;

// Group D. The "overlay" field for any overlay screen no other reader handles (keeps clients from soft-locking).
// Contract: {screen_type (the node's type name), message: "An overlay (<type>) is active. It may require manual interaction
// in-game."}.
internal static class OverlayScreen
{
    public static Dictionary<string, object?> Read(IOverlayScreen screen)
    {
        var type = screen.GetType().Name;
        return new()
        {
            ["screen_type"] = type,
            ["message"] = $"An overlay ({type}) is active. It may require manual interaction in-game."
        };
    }
}
