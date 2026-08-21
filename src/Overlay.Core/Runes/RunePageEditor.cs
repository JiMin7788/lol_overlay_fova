using Overlay.Core.Lcu;

namespace Overlay.Core.Runes;

/// <summary>
/// The click-selection state machine behind the dashboard rune-page editor (M33): holds one
/// in-progress rune page and applies the client's own selection rules, so the WPF view stays a
/// dumb renderer and every rule lives here under unit tests.
///
/// Rules (mirroring the League client):
///  - primary style: one perk per row (keystone + 3 minors);
///  - secondary style: any style the catalog allows except the primary; exactly TWO perks from
///    its three minor rows, at most one per row — a same-row click replaces that row's pick, a
///    new-row click evicts the OLDEST pick once two exist;
///  - stat shards: one per row (ids repeat across rows, so shard selection is row-addressed);
///  - changing a style clears that side's perk picks (shards survive); picking the current
///    secondary as primary swaps the styles.
/// </summary>
public sealed class RunePageEditor
{
    /// <summary>Primary perk rows on a page (keystone + 3 minors).</summary>
    public const int PrimaryRows = 4;
    public const int SubPickCount = 2;
    public const int ShardRows = 3;

    public int PrimaryStyleId { get; private set; }
    public int SubStyleId { get; private set; }

    private readonly int?[] _primaryPicks = new int?[PrimaryRows];
    /// <summary>Ordered oldest-first, max <see cref="SubPickCount"/> entries.</summary>
    private readonly List<(int Row, int PerkId)> _subPicks = new();
    private readonly int?[] _shards = new int?[ShardRows];

    /// <summary>The pick for a primary row, or null.</summary>
    public int? PrimaryPick(int row) => row is >= 0 and < PrimaryRows ? _primaryPicks[row] : null;

    /// <summary>The secondary pick occupying <paramref name="row"/> (1-3), or null.</summary>
    public int? SubPick(int row)
    {
        foreach (var (r, perk) in _subPicks)
            if (r == row) return perk;
        return null;
    }

    public int? Shard(int row) => row is >= 0 and < ShardRows ? _shards[row] : null;

    public bool IsComplete =>
        PrimaryStyleId > 0 && SubStyleId > 0
        && _primaryPicks.All(p => p.HasValue)
        && _subPicks.Count == SubPickCount
        && _shards.All(s => s.HasValue);

    // ── construction ───────────────────────────────────────────────────────────────

    /// <summary>Builds an editor from an existing page, tolerantly: perk ids that don't belong
    /// to the page's styles per the catalog are simply left unselected.</summary>
    public static RunePageEditor FromPage(RunePage page)
    {
        var ed = new RunePageEditor();
        ed.PrimaryStyleId = page.PrimaryStyleId;
        ed.SubStyleId = page.SubStyleId;

        var primary = RuneCatalog.GetStyle(page.PrimaryStyleId);
        var sub = RuneCatalog.GetStyle(page.SubStyleId);
        foreach (int perkId in page.PerkIds)
        {
            if (primary is not null && ed.TryPlacePrimary(primary, perkId)) continue;
            if (sub is not null && ed.TryPlaceSub(sub, perkId)) continue;
            ed.TryPlaceShard(primary, perkId);
        }
        return ed;
    }

    private bool TryPlacePrimary(RuneStyleInfo style, int perkId)
    {
        for (int row = 0; row < style.PerkRows.Count && row < PrimaryRows; row++)
            if (_primaryPicks[row] is null && style.PerkRows[row].Contains(perkId))
            {
                _primaryPicks[row] = perkId;
                return true;
            }
        return false;
    }

    private bool TryPlaceSub(RuneStyleInfo style, int perkId)
    {
        if (_subPicks.Count >= SubPickCount) return false;
        for (int row = 1; row < style.PerkRows.Count; row++)
            if (SubPick(row) is null && style.PerkRows[row].Contains(perkId))
            {
                _subPicks.Add((row, perkId));
                return true;
            }
        return false;
    }

    private void TryPlaceShard(RuneStyleInfo? style, int perkId)
    {
        var rows = style?.StatRows;
        if (rows is null) return;
        for (int row = 0; row < rows.Count && row < ShardRows; row++)
            if (_shards[row] is null && rows[row].Contains(perkId))
            {
                _shards[row] = perkId;
                return;
            }
    }

    /// <summary>The page in LCU order (4 primary + 2 secondary + 3 shards), or null while
    /// incomplete — a live apply must never send a half-built page.</summary>
    public RunePage? ToPage()
    {
        if (!IsComplete) return null;
        var perkIds = new List<int>(9);
        perkIds.AddRange(_primaryPicks.Select(p => p!.Value));
        // LCU keeps sub perks in ROW order regardless of click order.
        perkIds.AddRange(_subPicks.OrderBy(p => p.Row).Select(p => p.PerkId));
        perkIds.AddRange(_shards.Select(s => s!.Value));
        return new RunePage { PrimaryStyleId = PrimaryStyleId, SubStyleId = SubStyleId, PerkIds = perkIds };
    }

    // ── clicks ─────────────────────────────────────────────────────────────────────

    /// <summary>Selects a primary style. Picking the current secondary swaps the two styles.
    /// Any style change clears the affected side's perk picks (shards survive).</summary>
    public void SelectPrimaryStyle(int styleId)
    {
        if (styleId == PrimaryStyleId || RuneCatalog.GetStyle(styleId) is null) return;
        if (styleId == SubStyleId) SubStyleId = PrimaryStyleId;
        PrimaryStyleId = styleId;
        Array.Clear(_primaryPicks);
        _subPicks.Clear(); // sub picks belonged to a page whose identity just changed
    }

    /// <summary>Selects a secondary style; the primary itself and catalog-disallowed styles are
    /// rejected.</summary>
    public void SelectSubStyle(int styleId)
    {
        if (styleId == SubStyleId || styleId == PrimaryStyleId) return;
        if (RuneCatalog.GetStyle(styleId) is null) return;
        var primary = RuneCatalog.GetStyle(PrimaryStyleId);
        if (primary is not null && primary.AllowedSubStyles.Count > 0
            && !primary.AllowedSubStyles.Contains(styleId)) return;
        SubStyleId = styleId;
        _subPicks.Clear();
    }

    /// <summary>Sets the primary pick for <paramref name="row"/>; the perk must belong to that
    /// row of the primary style.</summary>
    public bool SelectPrimaryPerk(int row, int perkId)
    {
        var style = RuneCatalog.GetStyle(PrimaryStyleId);
        if (style is null || row < 0 || row >= Math.Min(style.PerkRows.Count, PrimaryRows)) return false;
        if (!style.PerkRows[row].Contains(perkId)) return false;
        _primaryPicks[row] = perkId;
        return true;
    }

    /// <summary>Sets a secondary pick in <paramref name="row"/> (1-3 of the secondary style):
    /// same-row picks replace, a third distinct row evicts the oldest pick.</summary>
    public bool SelectSubPerk(int row, int perkId)
    {
        var style = RuneCatalog.GetStyle(SubStyleId);
        if (style is null || row < 1 || row >= style.PerkRows.Count) return false;
        if (!style.PerkRows[row].Contains(perkId)) return false;

        int existing = _subPicks.FindIndex(p => p.Row == row);
        if (existing >= 0) _subPicks[existing] = (row, perkId);
        else
        {
            if (_subPicks.Count >= SubPickCount) _subPicks.RemoveAt(0); // evict oldest
            _subPicks.Add((row, perkId));
        }
        return true;
    }

    /// <summary>Sets the shard for <paramref name="row"/> (row-addressed because shard ids repeat
    /// across rows).</summary>
    public bool SelectShard(int row, int perkId)
    {
        var style = RuneCatalog.GetStyle(PrimaryStyleId);
        var rows = style?.StatRows;
        if (rows is null || row < 0 || row >= Math.Min(rows.Count, ShardRows)) return false;
        if (!rows[row].Contains(perkId)) return false;
        _shards[row] = perkId;
        return true;
    }
}
