using Overlay.Core.ChampSelect;

namespace Overlay.Core.Tests;

/// <summary>Flash-key normalization (SpellOrder): the LCU maps spell1→D, spell2→F.</summary>
public class SpellOrderTests
{
    private const int Flash = SpellOrder.FlashId;
    private const int Heal = 7;

    [Fact]
    public void FlashOnWrongKey_SwapsToPreferredKey()
    {
        Assert.Equal((Heal, Flash), SpellOrder.Normalize(Flash, Heal, flashOnF: true));
        Assert.Equal((Flash, Heal), SpellOrder.Normalize(Heal, Flash, flashOnF: false));
    }

    [Fact]
    public void FlashAlreadyOnPreferredKey_Unchanged()
    {
        Assert.Equal((Flash, Heal), SpellOrder.Normalize(Flash, Heal, flashOnF: false));
        Assert.Equal((Heal, Flash), SpellOrder.Normalize(Heal, Flash, flashOnF: true));
    }

    [Fact]
    public void NoFlash_PassesThrough_EitherPreference()
    {
        Assert.Equal((Heal, 14), SpellOrder.Normalize(Heal, 14, flashOnF: true));
        Assert.Equal((Heal, 14), SpellOrder.Normalize(Heal, 14, flashOnF: false));
    }
}
