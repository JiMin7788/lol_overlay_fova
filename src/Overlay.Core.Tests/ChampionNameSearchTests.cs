using Overlay.Core.ChampionDb;

namespace Overlay.Core.Tests;

public class ChampionNameSearchTests
{
    [Theory]
    [InlineData("가렌")]
    [InlineData("렌")]
    [InlineData("garen")]
    [InlineData("ㄱㄹ")]     // initials only
    [InlineData("ㄱ")]
    [InlineData("가ㄹ")]     // syllable + initial
    [InlineData("갈")]       // IME mid-composition of 가렌
    [InlineData("rkfps")]    // 가렌 typed with the IME off
    [InlineData("RKFPS")]
    [InlineData("  가렌 ")]
    [InlineData("")]
    public void FindsGaren(string query) => Assert.True(ChampionNameSearch.Matches("가렌", "Garen", query));

    [Theory]
    [InlineData("ㄴ")]
    [InlineData("ㄹㄱ")]
    [InlineData("갇")]       // ㄷ final: no syllable of 가렌 starts with ㄷ
    [InlineData("rkfp1")]
    public void RejectsOthers(string query) => Assert.False(ChampionNameSearch.Matches("가렌", "Garen", query));

    [Theory]
    [InlineData("잭스", "wortm")]
    [InlineData("쐐기", "Thorl")]      // doubled initial uses the shifted key
    [InlineData("Ab가", "Abrk")]      // non-Hangul passes through
    public void ToQwerty(string text, string keys) => Assert.Equal(keys, ChampionNameSearch.ToQwerty(text));

    [Fact]
    public void ShiftedKeysMatchUnshifted()
        => Assert.True(ChampionNameSearch.Matches("쓰레쉬", "Thresh", "tmfptnl"));

    [Fact]
    public void InitialsWorkMidName()
        => Assert.True(ChampionNameSearch.Matches("미스 포츈", "MissFortune", "ㅍㅊ"));
}
