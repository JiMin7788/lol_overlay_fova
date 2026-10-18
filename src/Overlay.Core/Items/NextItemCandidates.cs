using Overlay.Core.ChampionDb;

namespace Overlay.Core.Items;

/// <summary>
/// (M43) The items worth ranking as "what next": finished (builds into nothing), not boots, at
/// least 2,200 gold, sold on Summoner's Rift, not owned, and adding something the damage engine
/// models — AD, AP, lethality, armor/magic pen, or a curated item proc. Defensive and utility items
/// are left out rather than ranked at zero.
/// </summary>
public static class NextItemCandidates
{
    public const int MinGold = 2200;

    public static IReadOnlyList<string> For(IEnumerable<int> ownedItemIds, StaticGameData? staticData)
    {
        var owned = new HashSet<string>(ownedItemIds.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var result = new List<string>();
        foreach (var item in ItemRepository.GetAll())
        {
            if (!item.AvailableOnSummonersRift || item.IsBoots || item.BuildsInto.Count > 0
                || item.GoldTotal < MinGold || owned.Contains(item.Id)) continue;
            var extra = int.TryParse(item.Id, out int id) ? staticData?.GetItem(id) : null;
            bool damage = item.Stats.Ad > 0 || item.Stats.Ap > 0 || ItemEffectDb.Get(item.Id) is not null
                || extra is { Lethality: > 0 } or { FlatMagicPen: > 0 } or { PercentArmorPen: > 0 } or { PercentMagicPen: > 0 };
            if (damage) result.Add(item.Id);
        }
        return result;
    }
}
