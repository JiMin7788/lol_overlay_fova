using System.Windows;
using System.Windows.Controls;

namespace Overlay.Client.Views;

/// <summary>
/// The in-app manual (2026-07-25 request): one section per feature area, every string served by
/// <see cref="Localization"/> so the whole page follows the user's language setting live —
/// content rebuilds on <see cref="Localization.LanguageChanged"/>.
///
/// <para>Presented (loop 469) as a card per section after the user reported it was hard to read.
/// The old page was a flat run of headings over TextDim body text, which is the app's SECONDARY
/// colour — fine for a caption beside a number, wrong for the only text in a paragraph. Four
/// things changed, and each addresses a separate reason the page was hard to read: body text moved
/// to the primary colour, sections became separated cards instead of margin-delimited runs, the
/// text column is capped so a line does not run the full width of a wide window, and the raw
/// scrollbar became the app-wide hidden one (the last view still deviating from that convention).</para>
/// </summary>
public sealed class HelpView : UserControl
{
    private static readonly string[] Sections =
    {
        "intro", "overlay", "combo", "timers", "minimap",
        "champselect", "comp", "spells", "wards", "search", "data",
    };

    /// <summary>Roughly 70 characters at this size. Long measures are the usual reason a wall of
    /// help text is tiring: the eye loses the line it was on coming back from the right edge.</summary>
    private const double TextColumn = 680;

    private readonly StackPanel _list = new() { Margin = new Thickness(0, 0, 16, 24) };

    public HelpView()
    {
        var scroll = new ScrollViewer { Content = _list };
        scroll.SetResourceReference(StyleProperty, "HiddenScroll");
        Content = scroll;
        Rebuild();
        Localization.LanguageChanged += Rebuild;
    }

    private void Rebuild()
    {
        _list.Children.Clear();

        var title = new TextBlock
        {
            Text = Localization.L("nav.help"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _list.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = Localization.L("help.page.subtitle"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = TextColumn,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _list.Children.Add(subtitle);

        foreach (var key in Sections) _list.Children.Add(SectionCard(key));
    }

    private static UIElement SectionCard(string key)
    {
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };

        // A short accent rule instead of an icon: it marks the heading without risking a glyph
        // that renders as a box on a machine missing the font.
        var rule = new Border
        {
            Width = 3,
            Height = 15,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        rule.SetResourceReference(Border.BackgroundProperty, "Accent");
        heading.Children.Add(rule);

        var caption = new TextBlock
        {
            Text = Localization.L($"help.{key}.title"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        heading.Children.Add(caption);

        var body = new TextBlock
        {
            Text = Localization.L($"help.{key}.body"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            MaxWidth = TextColumn,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        // Primary, not TextDim: this is the content of the page, not an annotation on something else.
        body.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        var stack = new StackPanel();
        stack.Children.Add(heading);
        stack.Children.Add(body);

        var card = new Border
        {
            Child = stack,
            Padding = new Thickness(16, 14, 16, 15),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        return card;
    }
}
