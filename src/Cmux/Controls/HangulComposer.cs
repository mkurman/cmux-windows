namespace Cmux.Controls;

/// <summary>
/// Software Hangul syllable composer. Receives a stream of Hangul Compatibility
/// Jamo (U+3131–U+3163) and composes them into Hangul Syllables (U+AC00–U+D7A3).
///
/// On a raw WPF FrameworkElement the OS IME fails to maintain composition state,
/// delivering decomposed jamo instead of composed syllables. This class
/// reimplements the composition algorithm so the terminal receives correct Korean text.
/// </summary>
public sealed class HangulComposer
{
    // ── Composition state ───────────────────────────────────────────────
    private int _cho = -1;  // 초성 index (0-18), -1 = empty
    private int _jung = -1; // 중성 index (0-20), -1 = empty
    private int _jong = -1; // 종성 index (1-27), -1 = empty (0 = no 종성 in syllable formula)

    // ── Hangul Compatibility Jamo → index tables ────────────────────────

    // 초성 (initial consonants): 19 entries
    // ㄱ ㄲ ㄴ ㄷ ㄸ ㄹ ㅁ ㅂ ㅃ ㅅ ㅆ ㅇ ㅈ ㅉ ㅊ ㅋ ㅌ ㅍ ㅎ
    private static readonly char[] ChoJamo =
    [
        'ㄱ','ㄲ','ㄴ','ㄷ','ㄸ','ㄹ','ㅁ','ㅂ','ㅃ','ㅅ',
        'ㅆ','ㅇ','ㅈ','ㅉ','ㅊ','ㅋ','ㅌ','ㅍ','ㅎ'
    ];

    // 중성 (medial vowels): 21 entries
    // ㅏ ㅐ ㅑ ㅒ ㅓ ㅔ ㅕ ㅖ ㅗ ㅘ ㅙ ㅚ ㅛ ㅜ ㅝ ㅞ ㅟ ㅠ ㅡ ㅢ ㅣ
    private static readonly char[] JungJamo =
    [
        'ㅏ','ㅐ','ㅑ','ㅒ','ㅓ','ㅔ','ㅕ','ㅖ','ㅗ','ㅘ',
        'ㅙ','ㅚ','ㅛ','ㅜ','ㅝ','ㅞ','ㅟ','ㅠ','ㅡ','ㅢ','ㅣ'
    ];

    // 종성 (final consonants): indices 1-27 (0 = no final)
    // (none) ㄱ ㄲ ㄳ ㄴ ㄵ ㄶ ㄷ ㄹ ㄺ ㄻ ㄼ ㄽ ㄾ ㄿ ㅀ ㅁ ㅂ ㅄ ㅅ ㅆ ㅇ ㅈ ㅊ ㅋ ㅌ ㅍ ㅎ
    private static readonly char[] JongJamo =
    [
        '\0','ㄱ','ㄲ','ㄳ','ㄴ','ㄵ','ㄶ','ㄷ','ㄹ','ㄺ',
        'ㄻ','ㄼ','ㄽ','ㄾ','ㄿ','ㅀ','ㅁ','ㅂ','ㅄ','ㅅ',
        'ㅆ','ㅇ','ㅈ','ㅊ','ㅋ','ㅌ','ㅍ','ㅎ'
    ];

    // 종성 → 초성 mapping (a 종성 jamo can also serve as the next 초성)
    // Maps 종성 index to 초성 index, or -1 if not possible (compound 종성)
    private static readonly int[] JongToChoIndex =
    [
        -1, // 0: no final
         0, // 1: ㄱ → ㄱ(0)
         1, // 2: ㄲ → ㄲ(1)
        -1, // 3: ㄳ (compound)
         2, // 4: ㄴ → ㄴ(2)
        -1, // 5: ㄵ (compound)
        -1, // 6: ㄶ (compound)
         3, // 7: ㄷ → ㄷ(3)
         5, // 8: ㄹ → ㄹ(5)
        -1, // 9: ㄺ (compound)
        -1, //10: ㄻ (compound)
        -1, //11: ㄼ (compound)
        -1, //12: ㄽ (compound)
        -1, //13: ㄾ (compound)
        -1, //14: ㄿ (compound)
        -1, //15: ㅀ (compound)
         6, //16: ㅁ → ㅁ(6)
         7, //17: ㅂ → ㅂ(7)
        -1, //18: ㅄ (compound)
         9, //19: ㅅ → ㅅ(9)
        10, //20: ㅆ → ㅆ(10)
        11, //21: ㅇ → ㅇ(11)
        12, //22: ㅈ → ㅈ(12)
        14, //23: ㅊ → ㅊ(14)
        15, //24: ㅋ → ㅋ(15)
        16, //25: ㅌ → ㅌ(16)
        17, //26: ㅍ → ㅍ(17)
        18, //27: ㅎ → ㅎ(18)
    ];

    // Compound 종성 split: compound index → (left 종성, right 초성)
    // Used when a vowel follows a compound 종성, splitting it.
    private static readonly Dictionary<int, (int leftJong, int rightCho)> CompoundJongSplit = new()
    {
        [3]  = (1, 9),   // ㄳ → ㄱ(jong:1) + ㅅ(cho:9)
        [5]  = (4, 12),  // ㄵ → ㄴ(jong:4) + ㅈ(cho:12)
        [6]  = (4, 18),  // ㄶ → ㄴ(jong:4) + ㅎ(cho:18)
        [9]  = (8, 0),   // ㄺ → ㄹ(jong:8) + ㄱ(cho:0)
        [10] = (8, 6),   // ㄻ → ㄹ(jong:8) + ㅁ(cho:6)
        [11] = (8, 7),   // ㄼ → ㄹ(jong:8) + ㅂ(cho:7)
        [12] = (8, 9),   // ㄽ → ㄹ(jong:8) + ㅅ(cho:9)
        [13] = (8, 16),  // ㄾ → ㄹ(jong:8) + ㅌ(cho:16)
        [14] = (8, 17),  // ㄿ → ㄹ(jong:8) + ㅍ(cho:17)
        [15] = (8, 18),  // ㅀ → ㄹ(jong:8) + ㅎ(cho:18)
        [18] = (17, 9),  // ㅄ → ㅂ(jong:17) + ㅅ(cho:9)
    };

    // Compound 종성 composition: (left 종성, right 종성) → compound index
    private static readonly Dictionary<(int, int), int> CompoundJongMap = new()
    {
        [(1, 19)]  = 3,  // ㄱ+ㅅ = ㄳ
        [(4, 22)]  = 5,  // ㄴ+ㅈ = ㄵ
        [(4, 27)]  = 6,  // ㄴ+ㅎ = ㄶ
        [(8, 1)]   = 9,  // ㄹ+ㄱ = ㄺ
        [(8, 16)]  = 10, // ㄹ+ㅁ = ㄻ
        [(8, 17)]  = 11, // ㄹ+ㅂ = ㄼ
        [(8, 19)]  = 12, // ㄹ+ㅅ = ㄽ
        [(8, 25)]  = 13, // ㄹ+ㅌ = ㄾ
        [(8, 26)]  = 14, // ㄹ+ㅍ = ㄿ
        [(8, 27)]  = 15, // ㄹ+ㅎ = ㅀ
        [(17, 19)] = 18, // ㅂ+ㅅ = ㅄ
    };

    // Compound 중성 composition: (left, right) → compound vowel index
    private static readonly Dictionary<(int, int), int> CompoundJungMap = new()
    {
        [(8, 0)]   = 9,  // ㅗ+ㅏ = ㅘ
        [(8, 1)]   = 10, // ㅗ+ㅐ = ㅙ
        [(8, 20)]  = 11, // ㅗ+ㅣ = ㅚ
        [(13, 4)]  = 14, // ㅜ+ㅓ = ㅝ
        [(13, 5)]  = 15, // ㅜ+ㅔ = ㅞ
        [(13, 20)] = 16, // ㅜ+ㅣ = ㅟ
        [(18, 20)] = 19, // ㅡ+ㅣ = ㅢ
    };

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Feed a character into the composer. Returns the string to send to ConPTY.
    /// May return empty string (character buffered), one syllable, or multiple
    /// characters (flushed buffer + new state).
    /// </summary>
    public string Feed(char c)
    {
        // Already-composed Hangul syllable — decompose and buffer it so the
        // next input character can modify it (e.g. add 종성). The OS IME on a
        // raw FrameworkElement sends some syllables pre-composed (e.g. "느")
        // but a following consonant should become its 종성 (e.g. "느"+ㄴ → "는").
        if (c is >= '\uAC00' and <= '\uD7A3')
        {
            var flushed = Flush();
            int offset = c - 0xAC00;
            _cho = offset / (21 * 28);
            _jung = (offset / 28) % 21;
            int jong = offset % 28;
            _jong = jong > 0 ? jong : -1;
            return flushed;
        }

        int choIdx = GetChoIndex(c);
        int jungIdx = GetJungIndex(c);

        // Not a Hangul jamo? Flush and pass through.
        if (choIdx < 0 && jungIdx < 0)
        {
            var flushed = Flush();
            return flushed + c;
        }

        // ── Consonant input ─────────────────────────────────────────
        if (choIdx >= 0 && jungIdx < 0)
        {
            int jongIdx = GetJongIndexFromCho(choIdx);

            // State: empty → start new 초성
            if (_cho < 0)
            {
                _cho = choIdx;
                return "";
            }

            // State: 초성 only → flush previous 초성, start new
            if (_jung < 0)
            {
                string prev = ChoJamo[_cho].ToString();
                _cho = choIdx;
                _jung = -1;
                _jong = -1;
                return prev;
            }

            // State: 초성+중성 (no 종성) → try adding as 종성
            if (_jong < 0)
            {
                if (jongIdx >= 0)
                {
                    _jong = jongIdx;
                    return "";
                }
                // Can't be 종성 → flush syllable, start new 초성
                string syllable = ComposeSyllable();
                _cho = choIdx;
                _jung = -1;
                _jong = -1;
                return syllable;
            }

            // State: 초성+중성+종성 → try compound 종성
            if (jongIdx >= 0 && CompoundJongMap.TryGetValue((_jong, jongIdx), out int compound))
            {
                _jong = compound;
                return "";
            }

            // Can't compound → flush syllable, start new 초성
            {
                string syllable = ComposeSyllable();
                _cho = choIdx;
                _jung = -1;
                _jong = -1;
                return syllable;
            }
        }

        // ── Vowel input ─────────────────────────────────────────────
        if (jungIdx >= 0)
        {
            // State: empty → output bare vowel
            if (_cho < 0)
            {
                var flushed = Flush();
                return flushed + JungJamo[jungIdx];
            }

            // State: 초성 only → add 중성
            if (_jung < 0)
            {
                _jung = jungIdx;
                return "";
            }

            // State: 초성+중성 (no 종성) → try compound vowel
            if (_jong < 0)
            {
                if (CompoundJungMap.TryGetValue((_jung, jungIdx), out int compound))
                {
                    _jung = compound;
                    return "";
                }
                // Can't compound → flush syllable, start new state with vowel
                string syllable = ComposeSyllable();
                _cho = -1;
                _jung = -1;
                _jong = -1;
                return syllable + JungJamo[jungIdx];
            }

            // State: 초성+중성+종성 → split 종성: left stays, right becomes new 초성 + this vowel
            if (CompoundJongSplit.TryGetValue(_jong, out var split))
            {
                // Compound 종성: split it
                _jong = split.leftJong;
                string syllable = ComposeSyllable();
                _cho = split.rightCho;
                _jung = jungIdx;
                _jong = -1;
                return syllable;
            }
            else
            {
                // Simple 종성: move it to next 초성
                int nextCho = JongToChoIndex[_jong];
                if (nextCho >= 0)
                {
                    _jong = -1; // Remove 종성 from current syllable
                    string syllable = ComposeSyllable();
                    _cho = nextCho;
                    _jung = jungIdx;
                    _jong = -1;
                    return syllable;
                }
                else
                {
                    // Shouldn't happen, but flush everything
                    string syllable = ComposeSyllable();
                    _cho = -1;
                    _jung = -1;
                    _jong = -1;
                    return syllable + JungJamo[jungIdx];
                }
            }
        }

        // Fallback (shouldn't reach here)
        return Flush() + c;
    }

    /// <summary>
    /// Flush any buffered composition state. Call this when a non-composable
    /// event occurs (Enter, space, focus lost, etc.).
    /// </summary>
    public string Flush()
    {
        if (_cho < 0) return "";

        string result;
        if (_jung < 0)
        {
            // Only 초성 → output as bare consonant jamo
            result = ChoJamo[_cho].ToString();
        }
        else
        {
            result = ComposeSyllable();
        }

        _cho = -1;
        _jung = -1;
        _jong = -1;
        return result;
    }

    /// <summary>Whether there is an active composition.</summary>
    public bool IsComposing => _cho >= 0;

    // ── Private helpers ─────────────────────────────────────────────────

    private string ComposeSyllable()
    {
        if (_cho < 0 || _jung < 0) return "";
        int jongValue = _jong >= 0 ? _jong : 0;
        int code = 0xAC00 + (_cho * 21 + _jung) * 28 + jongValue;
        return ((char)code).ToString();
    }

    private static int GetChoIndex(char c)
    {
        for (int i = 0; i < ChoJamo.Length; i++)
            if (ChoJamo[i] == c) return i;
        return -1;
    }

    private static int GetJungIndex(char c)
    {
        for (int i = 0; i < JungJamo.Length; i++)
            if (JungJamo[i] == c) return i;
        return -1;
    }

    /// <summary>
    /// Given a 초성 index, return the corresponding 종성 index (or -1).
    /// Not all 초성 can be used as 종성 (e.g. ㄸ, ㅃ, ㅉ cannot).
    /// </summary>
    private static int GetJongIndexFromCho(int choIdx)
    {
        char c = ChoJamo[choIdx];
        for (int i = 1; i < JongJamo.Length; i++)
            if (JongJamo[i] == c) return i;
        return -1;
    }
}
