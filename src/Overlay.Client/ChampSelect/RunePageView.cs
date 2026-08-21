using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Overlay.Core.Lcu;
using Overlay.Core.Runes;

namespace Overlay.Client.ChampSelect;

/// <summary>
/// The League-style rune page rendered from the live CommunityDragon catalog (M33 dashboard
/// rune editor): primary tree (style picker + keystone row + 3 minor rows), secondary tree
/// (style picker + its 3 minor rows, two picks), and the 3 stat-shard rows — every icon loaded
/// via <see cref="DDragonIconProvider.LoadRuneIconAsync"/>, every selection rule delegated to
/// <see cref="RunePageEditor"/> (unit-tested in Core; this control only renders and forwards
/// clicks).
///
/// <para>Clicking any rune edits the page IN PLACE and raises <see cref="PageEdited"/> once the
/// page is complete — the panel behind it live-applies through the LCU. Unselected runes render
/// dimmed, the selected one full-opacity with an accent ring, mirroring the client's own page.</para>
/// </summary>
public sealed class RunePageView : UserControl
{
    private RunePageEditor? _editor;
    private readonly StackPanel _root;

    /// <summary>Raised with the complete page after every edit that leaves the page valid.</summary>
    public event Action<RunePage>? PageEdited;

    /// <summary>The page as currently shown, or null while empty/incomplete — the panel's
    /// client-sync poller compares against this before overwriting the view (2026-07-26).</summary>
    public RunePage? CurrentPage => _editor?.ToPage();

    public RunePageView()
    {
        _root = new StackPanel { Orientation = Orientation.Horizontal };
        Content = _root;
    }

    /// <summary>Renders <paramref name="page"/> for editing. Null collapses the view.</summary>
    public void ShowPage(RunePage? page)
    {
        if (page is null || RuneCatalog.Styles.Count == 0)
        {
            _editor = null;
            _root.Children.Clear();
            Visibility = Visibility.Collapsed;
            return;
        }
        _editor = RunePageEditor.FromPage(page);
        Visibility = Visibility.Visible;
        Render();
    }

    private void Edited()
    {
        Render();
        if (_editor?.ToPage() is { } page) PageEdited?.Invoke(page);
    }

    // ── rendering ───────────────────────────────────────────────────────────────

    private void Render()
    {
        _root.Children.Clear();
        if (_editor is null) return;

        var primary = RuneCatalog.GetStyle(_editor.PrimaryStyleId);
        var sub = RuneCatalog.GetStyle(_editor.SubStyleId);
        if (primary is null || sub is null) return;

        _root.Children.Add(PrimaryColumn(primary));
        _root.Children.Add(SubColumn(primary, sub));
    }

    private UIElement PrimaryColumn(RuneStyleInfo primary)
    {
        var col = NewColumn();

        // Style picker: all five styles; the active one highlighted.
        var styles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var s in RuneCatalog.Styles)
            styles.Children.Add(IconButton(s.IconPath, s.Name, 24, s.Id == primary.Id,
                () => { _editor!.SelectPrimaryStyle(s.Id); Edited(); }));
        col.Children.Add(styles);

        for (int row = 0; row < primary.PerkRows.Count && row < RunePageEditor.PrimaryRows; row++)
        {
            int r = row;
            double size = row == 0 ? 40 : 30; // keystones larger, like the client
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            foreach (int perkId in primary.PerkRows[row])
                line.Children.Add(PerkIcon(perkId, size, _editor!.PrimaryPick(r) == perkId,
                    () => { if (_editor!.SelectPrimaryPerk(r, perkId)) Edited(); }));
            col.Children.Add(line);
        }
        return col;
    }

    private UIElement SubColumn(RuneStyleInfo primary, RuneStyleInfo sub)
    {
        var col = NewColumn();

        var styles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var s in RuneCatalog.Styles)
        {
            if (s.Id == primary.Id) continue; // the client hides the primary from the sub picker
            styles.Children.Add(IconButton(s.IconPath, s.Name, 20, s.Id == sub.Id,
                () => { _editor!.SelectSubStyle(s.Id); Edited(); }));
        }
        col.Children.Add(styles);

        for (int row = 1; row < sub.PerkRows.Count; row++)
        {
            int r = row;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            foreach (int perkId in sub.PerkRows[row])
                line.Children.Add(PerkIcon(perkId, 26, _editor!.SubPick(r) == perkId,
                    () => { if (_editor!.SelectSubPerk(r, perkId)) Edited(); }));
            col.Children.Add(line);
        }

        // Stat shards live UNDER the secondary tree, same as the client's own page layout
        // (2026-07-25 feedback; was a third column).
        for (int row = 0; row < primary.StatRows.Count && row < RunePageEditor.ShardRows; row++)
        {
            int r = row;
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, row == 0 ? 10 : 2, 0, 2),
            };
            foreach (int perkId in primary.StatRows[row])
                line.Children.Add(PerkIcon(perkId, 20, _editor!.Shard(r) == perkId,
                    () => { if (_editor!.SelectShard(r, perkId)) Edited(); }));
            col.Children.Add(line);
        }
        return col;
    }

    private static StackPanel NewColumn() => new()
    {
        Orientation = Orientation.Vertical,
        Margin = new Thickness(0, 0, 18, 0),
        VerticalAlignment = VerticalAlignment.Top,
    };

    private UIElement PerkIcon(int perkId, double size, bool selected, Action onClick)
    {
        var perk = RuneCatalog.GetPerk(perkId);
        string tooltip = perk is null ? perkId.ToString()
            : string.IsNullOrEmpty(perk.Value.Desc) ? perk.Value.Name
            : perk.Value.Name + "\n" + perk.Value.Desc;
        return IconButton(perk?.IconPath ?? "", tooltip, size, selected, onClick);
    }

    /// <summary>Warms the icon cache for the ENTIRE catalog (styles, runes, shards) so the first
    /// champ-select render doesn't pay ~70 sequential CDN fetches — call fire-and-forget at app
    /// start; icons land on disk and in memory before the page is ever shown.</summary>
    public static async System.Threading.Tasks.Task PrefetchIconsAsync()
    {
        var paths = new List<string>();
        foreach (var s in RuneCatalog.Styles)
        {
            paths.Add(s.IconPath);
            foreach (var row in s.PerkRows)
                foreach (int id in row)
                    if (RuneCatalog.GetPerk(id) is { } p) paths.Add(p.IconPath);
            foreach (var row in s.StatRows)
                foreach (int id in row)
                    if (RuneCatalog.GetPerk(id) is { } p) paths.Add(p.IconPath);
        }
        // Modest parallelism: fast enough (~70 small PNGs), gentle on the CDN.
        using var throttle = new System.Threading.SemaphoreSlim(6);
        var tasks = paths.Distinct().Select(async path =>
        {
            await throttle.WaitAsync();
            try { await DDragonIconProvider.LoadRuneIconAsync(path); }
            finally { throttle.Release(); }
        });
        await System.Threading.Tasks.Task.WhenAll(tasks);
    }

    /// <summary>A clickable rune/style icon: dimmed when unselected, accent-ringed when selected;
    /// tooltip carries the localized name. Icons stream in asynchronously and never throw.</summary>
    private UIElement IconButton(string iconPath, string name, double size, bool selected, Action onClick)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        _ = SetIconAsync(image, iconPath);

        var ring = new Border
        {
            Width = size + 6,
            Height = size + 6,
            CornerRadius = new CornerRadius((size + 6) / 2),
            BorderThickness = new Thickness(selected ? 2 : 0),
            BorderBrush = selected ? (Brush)Application.Current.FindResource("Accent") : Brushes.Transparent,
            Margin = new Thickness(2, 0, 2, 0),
            Child = image,
            Opacity = selected ? 1.0 : 0.35,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent, // hit-test the full circle, not just icon pixels
        };
        if (!string.IsNullOrEmpty(name))
        {
            // Explicit ToolTip + service knobs: the plain ToolTip-property form did not show on
            // these rebuilt-per-render Borders in the live run (2026-07-25 feedback item 3).
            ToolTipService.SetToolTip(ring, new ToolTip
            {
                Content = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 },
            });
            ToolTipService.SetInitialShowDelay(ring, 300);
            ToolTipService.SetShowDuration(ring, 15000);
            ToolTipService.SetShowOnDisabled(ring, true);
        }
        ring.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        ring.MouseEnter += (_, _) => { if (!selected) ring.Opacity = 0.7; };
        ring.MouseLeave += (_, _) => { if (!selected) ring.Opacity = 0.35; };
        return ring;
    }

    private static async System.Threading.Tasks.Task SetIconAsync(Image image, string iconPath)
    {
        var src = await DDragonIconProvider.LoadRuneIconAsync(iconPath);
        if (src is not null) image.Source = src;
    }
}
