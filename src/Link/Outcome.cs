namespace SpireLink;

// The result body an action returns to the HTTP layer: {"status": "ok", "message": ...} plus optional fields,
// or {"status": "error", "error": ...}. Postconditions, not this body, decide whether the game really changed.
internal static class Outcome
{
    public static Dictionary<string, object?> Ok(string message, params (string Key, object? Value)[] extra)
    {
        var body = new Dictionary<string, object?> { ["status"] = "ok", ["message"] = message };
        foreach (var (key, value) in extra) body[key] = value;
        return body;
    }

    public static Dictionary<string, object?> Fail(string error, params (string Key, object? Value)[] extra)
    {
        var body = new Dictionary<string, object?> { ["status"] = "error", ["error"] = error };
        foreach (var (key, value) in extra) body[key] = value;
        return body;
    }
}
