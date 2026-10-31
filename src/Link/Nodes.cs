using System.Reflection;
using Godot;

namespace SpireLink;

// Scene-tree queries shared by every screen reader and action. Nodes can be freed between frames,
// so every access tolerates a disposed or queued-for-deletion node and treats it as absent.
internal static class Nodes
{
    public static SceneTree? Tree => Engine.GetMainLoop() as SceneTree;
    public static Node? Root => Tree?.Root;

    public static bool Live(Node? node)
    {
        try { return node != null && GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion(); }
        catch (ObjectDisposedException) { return false; }
    }

    public static bool Visible(CanvasItem? node)
    {
        try { return Live(node) && node!.Visible && node.IsVisibleInTree(); }
        catch (ObjectDisposedException) { return false; }
    }

    // Depth-first in child order, the start node included.
    public static IEnumerable<T> All<T>(Node? start) where T : Node
    {
        if (!Live(start)) yield break;
        var stack = new Stack<Node>();
        stack.Push(start!);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!Live(node)) continue;
            if (node is T match) yield return match;
            Godot.Collections.Array<Node> children;
            try { children = node.GetChildren(); }
            catch (ObjectDisposedException) { continue; }
            for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    public static T? First<T>(Node? start) where T : Node => All<T>(start).FirstOrDefault();

    // The first node of type T in the tree, if it is visible; a hidden first node hides the screen. Readers and
    // actions detect menu screens with this, so both see the same node.
    public static T? Shown<T>(Node? root) where T : CanvasItem
        => First<T>(root) is { } node && Visible(node) ? node : null;

    // Grid holders reorder their children on focus (MoveToFront), so screen order comes from position: rows top to bottom, then left to right.
    public static List<T> ByPosition<T>(IEnumerable<T> controls) where T : Control
        => controls.OrderBy(c => c.GlobalPosition.Y).ThenBy(c => c.GlobalPosition.X).ToList();

    // Private engine fields the game exposes no accessor for; null when the field is missing in this build.
    public static object? Field(object? owner, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = owner?.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, flags) is { } field) return field.GetValue(owner);
        return null;
    }
}
