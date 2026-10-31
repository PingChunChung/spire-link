using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group C. The "rewards" field (the combat reward list).
// Contract: {items: [{index, type ("gold"/"potion"/"relic"/"card"/"special_card"/"card_removal"/lower-cased type name),
// description, gold_amount?, potion_id?, potion_name?, potion_description?}] (enabled buttons with a reward only),
// can_proceed}.
// A claimed reward's button is disabled and drops out of the list, so indexes are positions in the remaining list, not
// in the original reward set.
internal static class RewardsScreen
{
    public static Dictionary<string, object?> Read(NRewardsScreen screen)
    {
        var items = new List<Dictionary<string, object?>>();
        foreach (var button in Claimable(screen))
        {
            var reward = button.Reward!;
            var item = new Dictionary<string, object?>
            {
                ["index"] = items.Count,
                ["type"] = TypeName(reward),
                ["description"] = Text.Of(() => reward.Description)
            };
            switch (reward)
            {
                case GoldReward gold:
                    item["gold_amount"] = gold.Amount;
                    break;
                case PotionReward { Potion: { } potion }:
                    item["potion_id"] = potion.Id.Entry;
                    item["potion_name"] = Text.Of(() => potion.Title);
                    item["potion_description"] = Text.Of(() => potion.DynamicDescription);
                    break;
            }
            items.Add(item);
        }
        return new()
        {
            ["items"] = items,
            ["can_proceed"] = Nodes.First<NProceedButton>(screen)?.IsEnabled ?? false
        };
    }

    // The enabled reward buttons that still hold a reward, in tree order: the "index" of items and of claim_reward is a
    // position in this list.
    public static List<NRewardButton> Claimable(NRewardsScreen screen)
        => Nodes.All<NRewardButton>(screen).Where(b => b.IsEnabled && b.Reward != null).ToList();

    // The reward's kind as the client names it; an unknown reward class is its own name in lower case.
    public static string TypeName(Reward reward) => reward switch
    {
        GoldReward => "gold",
        PotionReward => "potion",
        RelicReward => "relic",
        CardReward => "card",
        SpecialCardReward => "special_card",
        CardRemovalReward => "card_removal",
        _ => reward.GetType().Name.ToLower()
    };
}
