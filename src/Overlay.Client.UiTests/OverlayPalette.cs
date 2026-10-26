using System.Drawing;

namespace Overlay.Client.UiTests;

/// <summary>
/// Copies of the <c>src/Overlay.Client/Hud/OverlayHost.cs</c> colours the screen checks look for
/// (ARGB). The test project cannot reference Overlay.Client, so each value cites its source line;
/// <c>HarnessTests.OverlayPalette_CitedLinesStillHoldTheValues</c> fails when a cited line no longer
/// holds the value.
/// </summary>
public static class OverlayPalette
{
    public const uint CardBackground = 0xE6181B22; // OverlayHost.cs:1063
    public const uint AccentGold = 0xFFC8AA6E;     // OverlayHost.cs:1068
    public const uint OverlayPanelBg = 0xE60B0F16; // OverlayHost.cs:2314
    public const uint PanelBorder = 0xFF2A2F3A;    // OverlayHost.cs:2316
    public const uint RingNormal = 0xFF3B4250;     // OverlayHost.cs:2318
    public const uint RingDead = 0xFF3A4150;       // OverlayHost.cs:2319
    public const uint NameDim = 0xFF8B929F;        // OverlayHost.cs:2321
    public const uint RespawnText = 0xFFCDD3DC;    // OverlayHost.cs:2324
    public const uint HpBarBg = 0xFF07090D;        // OverlayHost.cs:2330
    public const uint HpBarBorder = 0xFF6B7280;    // OverlayHost.cs:2331
    public const uint HpKillRed = 0xFFE23327;      // OverlayHost.cs:2332
    public const uint MaxBoxBorder = 0xFF3A2420;   // OverlayHost.cs:2336
    public const uint MinBoxBorder = 0xFF3A3220;   // OverlayHost.cs:2337
    public const uint SlotP = 0xFFC8AA6E;          // OverlayHost.cs:2342
    public const uint SlotQ = 0xFF3B82F6;          // OverlayHost.cs:2342
    public const uint SlotW = 0xFF22C55E;          // OverlayHost.cs:2342
    public const uint SlotE = 0xFFA855F7;          // OverlayHost.cs:2343
    public const uint SlotR = 0xFFF87171;          // OverlayHost.cs:2343
    public const uint SlotA = 0xFF64748B;          // OverlayHost.cs:2343

    /// <summary>Every copied value with its name and OverlayHost.cs line, for the citation check.</summary>
    public static IReadOnlyList<(string Name, uint Argb, int Line)> Citations { get; } = new[]
    {
        (nameof(CardBackground), CardBackground, 1063),
        (nameof(AccentGold), AccentGold, 1068),
        (nameof(OverlayPanelBg), OverlayPanelBg, 2314),
        (nameof(PanelBorder), PanelBorder, 2316),
        (nameof(RingNormal), RingNormal, 2318),
        (nameof(RingDead), RingDead, 2319),
        (nameof(NameDim), NameDim, 2321),
        (nameof(RespawnText), RespawnText, 2324),
        (nameof(HpBarBg), HpBarBg, 2330),
        (nameof(HpBarBorder), HpBarBorder, 2331),
        (nameof(HpKillRed), HpKillRed, 2332),
        (nameof(MaxBoxBorder), MaxBoxBorder, 2336),
        (nameof(MinBoxBorder), MinBoxBorder, 2337),
        (nameof(SlotP), SlotP, 2342),
        (nameof(SlotQ), SlotQ, 2342),
        (nameof(SlotW), SlotW, 2342),
        (nameof(SlotE), SlotE, 2343),
        (nameof(SlotR), SlotR, 2343),
        (nameof(SlotA), SlotA, 2343),
    };

    /// <summary>The colour as drawn opaque (alpha dropped). A translucent card colour blends with
    /// what lies under it, so compare those with a tolerance.</summary>
    public static Color Opaque(uint argb) => Color.FromArgb(255, (int)(argb >> 16) & 0xFF, (int)(argb >> 8) & 0xFF, (int)argb & 0xFF);
}
