using System.Text;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Champion search that never makes a Korean player switch IME or finish a syllable. A query
/// matches a champion when it is found, case-insensitively, in any of:
/// <list type="bullet">
/// <item>the Korean name ("가렌") or the id ("Garen");</item>
/// <item>the Korean name with initial consonants (초성) standing in for syllables — "ㄱㄹ" and
/// "가ㄹ" both find 가렌;</item>
/// <item>the same, while the IME is still composing: "갈" (the moment before "가렌") is read as
/// "가ㄹ";</item>
/// <item>the Korean name typed on a QWERTY keyboard with the IME off — "잭스" comes out as
/// "wortm", matched by rendering the name to its 2-beolsik keystrokes.</item>
/// </list>
/// </summary>
public static class ChampionNameSearch
{
    // Compatibility jamo for the 19 initial consonants, in Unicode composition order.
    private const string ChoJamo = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    // Compatibility jamo for the 27 final consonants (index 0 = none).
    private const string JongJamo = "\0ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

    // 2-beolsik (두벌식) keystrokes for each jamo, in Unicode composition order. Doubled jamo use
    // their shifted key (ㄲ→R); matching is case-insensitive so an un-shifted "r" still finds it.
    private static readonly string[] ChoKeys = "r,R,s,e,E,f,a,q,Q,t,T,d,w,W,c,z,x,v,g".Split(',');
    private static readonly string[] JungKeys =
        "k,o,i,O,j,p,u,P,h,hk,ho,hl,y,n,nj,np,nl,b,m,ml,l".Split(',');
    private static readonly string[] JongKeys =
        ",r,R,rt,s,sw,sg,e,f,fr,fa,fq,ft,fx,fv,fg,a,q,qt,t,T,d,w,c,z,x,v,g".Split(',');

    public static bool Matches(string koreanName, string id, string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return true;
        if (koreanName.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (ContainsWithInitials(koreanName, needle)) return true;
        if (SplitTrailingFinal(needle) is { } composing && ContainsWithInitials(koreanName, composing)) return true;
        return ToQwerty(koreanName).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Substring match where a lone initial consonant in <paramref name="needle"/> matches
    /// any syllable starting with it.</summary>
    private static bool ContainsWithInitials(string text, string needle)
    {
        for (int start = 0; start + needle.Length <= text.Length; start++)
        {
            int k = 0;
            while (k < needle.Length && CharMatches(text[start + k], needle[k])) k++;
            if (k == needle.Length) return true;
        }
        return false;
    }

    private static bool CharMatches(char c, char q)
    {
        if (char.ToUpperInvariant(c) == char.ToUpperInvariant(q)) return true;
        int cho = ChoJamo.IndexOf(q);
        if (cho < 0) return false;
        int s = c - 0xAC00;
        return s is >= 0 and < 11172 && s / 588 == cho;
    }

    /// <summary>"갈" → "가ㄹ": the last syllable's final consonant, when it can also start a
    /// syllable, is what the IME shows just before the next syllable takes it. Null otherwise.</summary>
    private static string? SplitTrailingFinal(string needle)
    {
        int s = needle[^1] - 0xAC00;
        if (s is < 0 or >= 11172 || s % 28 == 0) return null;
        char jong = JongJamo[s % 28];
        if (ChoJamo.IndexOf(jong) < 0) return null;
        return needle[..^1] + (char)(0xAC00 + s - s % 28) + jong;
    }

    /// <summary>The QWERTY keys a two-set keyboard produces for <paramref name="text"/>, leaving
    /// non-Hangul characters as-is. A fuzzy aid for IME-off typing, not a transliteration.</summary>
    public static string ToQwerty(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (char ch in text)
        {
            int i = ch - 0xAC00;
            if (i is >= 0 and < 11172)
                sb.Append(ChoKeys[i / 588]).Append(JungKeys[(i % 588) / 28]).Append(JongKeys[i % 28]);
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
