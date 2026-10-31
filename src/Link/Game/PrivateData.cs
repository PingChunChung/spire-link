using System.Collections;
using System.Reflection;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace SpireLink.Game;

// The "private_state" and "internal_data" fields: a model's own private scalars, read as they are. This is the one place
// the state reads private fields in bulk; nothing here is computed, and whatever cannot be read is left out.
internal static class PrivateData
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // Numeric and bool fields of `model`'s type and its bases up to (not including) `stopBefore`, snake_case keyed.
    // Auto-property backing fields take the property's name. Null when there is nothing to report.
    public static Dictionary<string, object?>? Of(object model, Type stopBefore)
    {
        var result = new Dictionary<string, object?>();
        for (var type = model.GetType(); type != null && type != stopBefore && type != typeof(object); type = type.BaseType)
            foreach (var field in type.GetFields(Declared))
            {
                if (!IsScalar(field.FieldType)) continue;
                var name = SnakeCase(PropertyName(field.Name).TrimStart('_'));
                if (name.Length == 0 || result.ContainsKey(name)) continue;
                try { result[name] = field.GetValue(model); } catch { }
            }
        return result.Count > 0 ? result : null;
    }

    // All fields of a plain data object (a power's internal data), each exported through Plain.
    public static Dictionary<string, object?>? Fields(object? data)
    {
        if (data == null) return null;
        var result = new Dictionary<string, object?>();
        foreach (var field in data.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            try { result[SnakeCase(field.Name.TrimStart('_'))] = Plain(field.GetValue(data), 0); } catch { }
        return result.Count > 0 ? result : null;
    }

    // Scalars and enums as they are, creatures as combat ids, cards as id and upgrade level, collections as lists of at most 30
    // (a dictionary as {key, value} pairs), three levels deep; anything else is null.
    public static object? Plain(object? value, int depth)
    {
        if (value == null || depth > 3) return null;
        switch (value)
        {
            case bool or int or long or decimal or float or double or string: return value;
            case Enum e: return e.ToString();
            case Creature creature: return creature.CombatId is uint id ? (int?)id : null;
            case CardModel card: return new Dictionary<string, object?> { ["id"] = card.Id.Entry, ["upgrade_level"] = card.CurrentUpgradeLevel };
            case IDictionary dictionary:
            {
                var list = new List<object?>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (list.Count >= 30) break;
                    list.Add(new Dictionary<string, object?> { ["key"] = Plain(entry.Key, depth + 1), ["value"] = Plain(entry.Value, depth + 1) });
                }
                return list;
            }
            case IEnumerable items:
            {
                var list = new List<object?>();
                foreach (var item in items)
                {
                    if (list.Count >= 30) break;
                    list.Add(Plain(item, depth + 1));
                }
                return list;
            }
            default: return null;
        }
    }

    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            bool boundary = char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
            if (boundary) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool IsScalar(Type type)
        => type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(decimal) || type == typeof(float) || type == typeof(double);

    private static string PropertyName(string fieldName)
        => fieldName.StartsWith('<') && fieldName.Contains(">k__BackingField") ? fieldName[1..fieldName.IndexOf('>')] : fieldName;
}
