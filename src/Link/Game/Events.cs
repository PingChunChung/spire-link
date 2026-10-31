using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace SpireLink.Game;

// Which event an event room is playing. The per-player copy (LocalMutableEvent) carries the generated options; the
// canonical template does not, and must not be described: generating its options on the main thread has hung the game.
internal static class Events
{
    // True once the synchronizer holds this room's own event. Right after the room is entered the list is empty
    // (ArgumentOutOfRangeException) or still holds the previous room's event, whose id differs from the canonical one.
    public static bool Ready(EventRoom room)
    {
        try { return room.LocalMutableEvent is { } local && local.Id.Entry == room.CanonicalEvent.Id.Entry; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    public static EventModel Current(EventRoom room)
    {
        try { return room.LocalMutableEvent ?? room.CanonicalEvent; }
        catch (ArgumentOutOfRangeException) { return room.CanonicalEvent; }
    }
}
