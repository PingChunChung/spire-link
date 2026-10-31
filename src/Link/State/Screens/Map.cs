using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group B. The "map" field.
// Contract: {current_position?: {col, row, type}, visited: [{col, row, type}], next_options: [{index, col, row, type,
// leads_to?: [{col, row, type}]}] (travelable NMapPoints by column), nodes: [{col, row, type, children: [[col, row]...]}]
// (start, grid points, boss, second boss; boss nodes also carry id/name when the encounter is known),
// boss: {col, row, id?, name?}, bosses: [boss, second boss?]}.
internal static class MapScreen
{
    public static Dictionary<string, object?> Read(RunState run)
    {
        var map = run.Map;
        var visited = run.VisitedMapCoords;
        var state = new Dictionary<string, object?>();
        if (visited.Count > 0) state["current_position"] = Coord(map, visited[^1]);
        state["visited"] = visited.Select(coord => Coord(map, coord)).ToList();
        state["next_options"] = NextOptions();

        // The boss encounter throws until the act has generated its rooms; the second one is plain nullable.
        EncounterModel? first = null;
        try { first = run.Act.BossEncounter; } catch { }
        var second = run.Act.SecondBossEncounter;

        var nodes = new List<Dictionary<string, object?>> { Node(map.StartingMapPoint) };
        nodes.AddRange(map.GetAllMapPoints().Select(point => Node(point)));
        nodes.Add(Node(map.BossMapPoint, first));
        var bosses = new List<Dictionary<string, object?>> { Boss(map.BossMapPoint, first) };
        if (map.SecondBossMapPoint is { } secondPoint)
        {
            nodes.Add(Node(secondPoint, second));
            bosses.Add(Boss(secondPoint, second));
        }
        state["nodes"] = nodes;
        state["boss"] = bosses[0];
        state["bosses"] = bosses;
        return state;
    }

    // The map screen's points the player can travel to right now, left to right (a stable sort, so nodes in one column
    // keep tree order). The "index" of next_options and of choose_map_node is a position in this list. Empty without a
    // map screen.
    public static List<NMapPoint> Travelable()
        => Nodes.All<NMapPoint>(NMapScreen.Instance)
            .Where(node => node.State == MapPointState.Travelable && node.Point != null)
            .OrderBy(node => node.Point!.coord.col)
            .ToList();

    // Each travelable point with the points it leads to.
    private static List<Dictionary<string, object?>> NextOptions()
    {
        var options = new List<Dictionary<string, object?>>();
        foreach (var point in Travelable().Select(node => node.Point!))
        {
            var option = new Dictionary<string, object?>
            {
                ["index"] = options.Count,
                ["col"] = point.coord.col,
                ["row"] = point.coord.row,
                ["type"] = point.PointType.ToString()
            };
            var leadsTo = Children(point).Select(Coord).ToList();
            if (leadsTo.Count > 0) option["leads_to"] = leadsTo;
            options.Add(option);
        }
        return options;
    }

    private static Dictionary<string, object?> Coord(MapPoint point) => new()
    {
        ["col"] = point.coord.col,
        ["row"] = point.coord.row,
        ["type"] = point.PointType.ToString()
    };

    // A visited coordinate; its type is null when the point is no longer on the map.
    private static Dictionary<string, object?> Coord(ActMap map, MapCoord coord) => new()
    {
        ["col"] = coord.col,
        ["row"] = coord.row,
        ["type"] = map.GetPoint(coord)?.PointType.ToString()
    };

    private static Dictionary<string, object?> Node(MapPoint point, EncounterModel? boss = null)
    {
        var node = new Dictionary<string, object?>
        {
            ["col"] = point.coord.col,
            ["row"] = point.coord.row,
            ["type"] = point.PointType.ToString(),
            ["children"] = Children(point).Select(child => new List<int> { child.coord.col, child.coord.row }).ToList()
        };
        Identify(node, boss);
        return node;
    }

    private static Dictionary<string, object?> Boss(MapPoint point, EncounterModel? boss)
    {
        var entry = new Dictionary<string, object?> { ["col"] = point.coord.col, ["row"] = point.coord.row };
        Identify(entry, boss);
        return entry;
    }

    // id and name of a known boss encounter; nothing when the id is blank, no name when the title is blank.
    private static void Identify(Dictionary<string, object?> target, EncounterModel? boss)
    {
        var id = boss?.Id?.Entry;
        if (string.IsNullOrWhiteSpace(id)) return;
        target["id"] = id;
        var name = Text.Of(() => boss?.Title);
        if (!string.IsNullOrWhiteSpace(name)) target["name"] = name;
    }

    private static IEnumerable<MapPoint> Children(MapPoint point) => point.Children.OrderBy(child => child.coord.col);
}
