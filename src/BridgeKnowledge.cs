using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using SpireLink.Game;

namespace SpireLink;

public static partial class SpireLinkMod
{
    private sealed record KnowledgeCatalog(
        List<Dictionary<string, object?>> Cards,
        Dictionary<string, Dictionary<string, object?>> CardsById,
        List<Dictionary<string, object?>> Keywords,
        Dictionary<string, Dictionary<string, object?>> KeywordsById,
        string Hash,
        string AssemblyName,
        string AssemblyVersion,
        string AssemblyBuild,
        string Locale,
        long BuildMilliseconds,
        int ExcludedInternalTestPowers);

    // Built and read only on the Godot main thread by the HTTP dispatcher's
    // RunOnMainThread callback. The immutable-after-build dictionaries avoid
    // repeatedly cloning and localizing every registered game model.
    private static KnowledgeCatalog? _knowledgeCatalog;
    private static readonly Type? _cardKeywordExtensions = typeof(CardKeyword).Assembly.GetType(
        "MegaCrit.Sts2.Core.Entities.Cards.CardKeywordExtensions");
    private static readonly MethodInfo? _cardKeywordTitle = _cardKeywordExtensions?.GetMethod("GetTitle");
    private static readonly MethodInfo? _cardKeywordDescription = _cardKeywordExtensions?.GetMethod("GetDescription");

    internal static object BuildCardKnowledge(string? ids, string? query, int offset, int limit)
    {
        var catalog = GetKnowledgeCatalog();
        var requested = ParseKnowledgeIds(ids);
        var selected = requested == null
            ? catalog.Cards
            : requested.Where(catalog.CardsById.ContainsKey).Select(id => catalog.CardsById[id]).ToList();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            selected = selected.Where(card =>
                Contains(card, "id", term) || Contains(card, "name", term)
                || Contains(card, "type", term) || Contains(card, "rarity", term)
                || card["base"] is Dictionary<string, object?> baseVariant
                    && Contains(baseVariant, "description", term)
                || card["upgraded"] is Dictionary<string, object?> upgradedVariant
                    && Contains(upgradedVariant, "description", term)).ToList();
        }
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var page = selected.Skip(safeOffset).Take(safeLimit)
            .Select(card => requested == null ? CardSummary(card) : card).ToList();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["scope"] = "all_registered_cards",
            ["mode"] = requested == null ? "summary" : "detail",
            ["query"] = query,
            ["offset"] = safeOffset,
            ["limit"] = safeLimit,
            ["total"] = selected.Count,
            ["items"] = page,
            ["missing_ids"] = requested?.Where(id => !catalog.CardsById.ContainsKey(id)).ToList(),
            ["catalog"] = CatalogMetadata(catalog)
        };
    }

    internal static object BuildKeywordKnowledge(string? ids)
    {
        var catalog = GetKnowledgeCatalog();
        var requested = ParseKnowledgeIds(ids);
        var selected = requested == null
            ? catalog.Keywords
            : requested.Where(catalog.KeywordsById.ContainsKey).Select(id => catalog.KeywordsById[id]).ToList();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["scope"] = "registered_card_keywords_and_gameplay_powers",
            ["total"] = selected.Count,
            ["items"] = selected,
            ["missing_ids"] = requested?.Where(id => !catalog.KeywordsById.ContainsKey(id)).ToList(),
            ["catalog"] = CatalogMetadata(catalog)
        };
    }

    private static KnowledgeCatalog GetKnowledgeCatalog()
    {
        var locale = Godot.TranslationServer.GetLocale();
        if (_knowledgeCatalog is { } cached && cached.Locale == locale) return cached;
        var timer = Stopwatch.StartNew();
        var assembly = typeof(CardModel).Assembly;
        var assemblyName = assembly.GetName().Name ?? "sts2";
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "unknown";
        var assemblyBuild = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assemblyVersion;

        var keywordRows = new List<Dictionary<string, object?>>();
        foreach (var keyword in Enum.GetValues<CardKeyword>().Where(value => value != CardKeyword.None))
        {
            keywordRows.Add(new Dictionary<string, object?>
            {
                ["id"] = keyword.ToString().ToUpperInvariant(),
                ["kind"] = "card_keyword",
                ["name"] = Text.Of(() => _cardKeywordTitle?.Invoke(null, new object[] { keyword }))
                    ?? keyword.ToString(),
                ["description"] = Text.Of(() => _cardKeywordDescription?.Invoke(null, new object[] { keyword }))
            });
        }
        int excludedInternalTestPowers = 0;
        foreach (var power in ModelDb.AllPowers)
        {
            var id = Text.Of(() => power.Id.Entry);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (id.StartsWith("MOCK_", StringComparison.Ordinal)) { excludedInternalTestPowers++; continue; }
            keywordRows.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["kind"] = "power",
                ["name"] = Text.Of(() => power.Title),
                ["description"] = Text.Of(() => power.Description)
                    ?? Text.Of(() => power.DumbHoverTip.Description)
            });
        }
        keywordRows = keywordRows.OrderBy(row => row["id"] as string, StringComparer.Ordinal).ToList();
        var keywordsById = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        var keywordIdsByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in keywordRows)
        {
            if (row["description"] is string text)
                row["description"] = KnowledgeText.NormalizeEnergyIcons(text);
            var id = (string)row["id"]!;
            keywordsById.TryAdd(id, row);
            if (row["name"] is string name && !string.IsNullOrWhiteSpace(name))
            {
                if (!keywordIdsByName.TryGetValue(name, out var refs))
                    keywordIdsByName[name] = refs = new List<string>();
                refs.Add(id);
            }
        }

        var cardRows = new List<Dictionary<string, object?>>();
        foreach (var card in ModelDb.AllCards)
        {
            var id = Text.Of(() => card.Id.Entry);
            if (string.IsNullOrWhiteSpace(id)) continue;
            var baseVariant = BuildKnowledgeCardVariant(card, "base", keywordIdsByName);
            var upgraded = Cards.Upgraded(card);
            var upgradedVariant = upgraded == null ? null
                : BuildKnowledgeCardVariant(upgraded, "upgraded", keywordIdsByName);
            cardRows.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = Text.Of(() => card.Title),
                ["type"] = card.Type.ToString(),
                ["rarity"] = card.Rarity.ToString(),
                ["is_upgradable"] = card.IsUpgradable,
                ["max_upgrade_level"] = card.MaxUpgradeLevel,
                ["base"] = baseVariant,
                ["upgraded"] = upgradedVariant,
                ["upgraded_preview_available"] = upgradedVariant != null,
                ["effect_representation"] = "game_display_text"
            });
        }
        cardRows = cardRows.OrderBy(row => row["id"] as string, StringComparer.Ordinal).ToList();
        var cardsById = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in cardRows) cardsById.TryAdd((string)row["id"]!, row);
        var hashInput = JsonSerializer.Serialize(new object[] { assemblyName, assemblyBuild, locale, cardRows, keywordRows });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();
        timer.Stop();
        var result = new KnowledgeCatalog(cardRows, cardsById, keywordRows, keywordsById,
            hash, assemblyName, assemblyVersion, assemblyBuild, locale, timer.ElapsedMilliseconds, excludedInternalTestPowers);
        // ModelDb may not be initialized during early boot; retry on a later request.
        if (cardRows.Count > 0) _knowledgeCatalog = result;
        return result;
    }

    private static Dictionary<string, object?> BuildKnowledgeCardVariant(CardModel card, string variant,
        Dictionary<string, List<string>> keywordIdsByName)
    {
        // Registered templates have no pile. Hand would make InCombat depend on
        // the room where this lazy cache was first built; None keeps it neutral.
        var row = Knowledge.CardVariant(card, variant, PileType.None);
        if (row["description"] is string description)
            row["description"] = KnowledgeText.NormalizeEnergyIcons(description);
        if (row["keywords"] is List<Dictionary<string, object?>> tips)
            foreach (var tip in tips)
                if (tip.GetValueOrDefault("description") is string tipText)
                    tip["description"] = KnowledgeText.NormalizeEnergyIcons(tipText);
        row["target_type"] = card.TargetType.ToString();
        var keywordIds = card.Keywords.Select(keyword => keyword.ToString().ToUpperInvariant())
            .Where(id => id != "NONE").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        row["keyword_ids"] = keywordIds;
        var references = new SortedSet<string>(keywordIds, StringComparer.Ordinal);
        if (row["keywords"] is List<Dictionary<string, object?>> hoverTips)
            foreach (var tip in hoverTips)
                if (tip.GetValueOrDefault("name") is string name && keywordIdsByName.TryGetValue(name, out var ids))
                    foreach (var id in ids) references.Add(id);
        row["keyword_references"] = references.ToList();
        return row;
    }

    private static Dictionary<string, object?> CardSummary(Dictionary<string, object?> card)
    {
        var baseVariant = (Dictionary<string, object?>)card["base"]!;
        return new Dictionary<string, object?>
        {
            ["id"] = card["id"], ["name"] = card["name"], ["type"] = card["type"],
            ["rarity"] = card["rarity"], ["cost"] = baseVariant["cost"],
            ["target_type"] = baseVariant["target_type"], ["is_upgradable"] = card["is_upgradable"]
        };
    }

    private static Dictionary<string, object?> CatalogMetadata(KnowledgeCatalog catalog) => new()
    {
        ["game_assembly"] = catalog.AssemblyName,
        ["game_assembly_version"] = catalog.AssemblyVersion,
        ["game_assembly_build"] = catalog.AssemblyBuild,
        ["locale"] = catalog.Locale,
        ["card_description_context"] = "out_of_combat_template",
        ["text_tokens"] = new Dictionary<string, string> { ["[energy]"] = "one_energy" },
        ["catalog_hash"] = catalog.Hash,
        ["catalog_build_ms"] = catalog.BuildMilliseconds,
        ["card_count"] = catalog.Cards.Count,
        ["keyword_count"] = catalog.Keywords.Count,
        ["excluded_internal_test_powers"] = catalog.ExcludedInternalTestPowers
    };

    private static bool Contains(Dictionary<string, object?> row, string key, string query)
        => row.GetValueOrDefault(key) is string value
            && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static List<string>? ParseKnowledgeIds(string? ids)
        => ids == null ? null : ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
