namespace Cmux.Core.Terminal;

/// <summary>
/// Provides Unicode character width calculation (wcwidth equivalent).
/// Full-width CJK characters return 2; most others return 1.
/// </summary>
public static class UnicodeWidth
{
    /// <summary>
    /// Returns the column width of a character: 2 for full-width / wide, 1 otherwise.
    /// </summary>
    public static int GetWidth(char c)
    {
        int cp = c;
        return IsWide(cp) ? 2 : 1;
    }

    /// <summary>
    /// Returns true if the codepoint occupies two terminal columns.
    /// Covers CJK Unified Ideographs, Hangul, Katakana/Hiragana, fullwidth forms, etc.
    /// Based on Unicode East Asian Width property (W and F categories).
    /// </summary>
    private static bool IsWide(int cp)
    {
        // Fullwidth Forms (FF01-FF60, FFE0-FFE6)
        if (cp >= 0xFF01 && cp <= 0xFF60) return true;
        if (cp >= 0xFFE0 && cp <= 0xFFE6) return true;

        // CJK Radicals Supplement (2E80-2EFF)
        if (cp >= 0x2E80 && cp <= 0x2EFF) return true;
        // Kangxi Radicals (2F00-2FDF)
        if (cp >= 0x2F00 && cp <= 0x2FDF) return true;
        // Ideographic Description Characters (2FF0-2FFF)
        if (cp >= 0x2FF0 && cp <= 0x2FFF) return true;

        // CJK Symbols and Punctuation (3000-303F)
        if (cp >= 0x3000 && cp <= 0x303F) return true;
        // Hiragana (3040-309F)
        if (cp >= 0x3040 && cp <= 0x309F) return true;
        // Katakana (30A0-30FF)
        if (cp >= 0x30A0 && cp <= 0x30FF) return true;
        // Bopomofo (3100-312F)
        if (cp >= 0x3100 && cp <= 0x312F) return true;
        // Hangul Compatibility Jamo (3130-318F)
        if (cp >= 0x3130 && cp <= 0x318F) return true;
        // Kanbun (3190-319F)
        if (cp >= 0x3190 && cp <= 0x319F) return true;
        // Bopomofo Extended (31A0-31BF)
        if (cp >= 0x31A0 && cp <= 0x31BF) return true;
        // CJK Strokes (31C0-31EF)
        if (cp >= 0x31C0 && cp <= 0x31EF) return true;
        // Katakana Phonetic Extensions (31F0-31FF)
        if (cp >= 0x31F0 && cp <= 0x31FF) return true;
        // Enclosed CJK Letters and Months (3200-32FF)
        if (cp >= 0x3200 && cp <= 0x32FF) return true;
        // CJK Compatibility (3300-33FF)
        if (cp >= 0x3300 && cp <= 0x33FF) return true;
        // CJK Unified Ideographs Extension A (3400-4DBF)
        if (cp >= 0x3400 && cp <= 0x4DBF) return true;
        // CJK Unified Ideographs (4E00-9FFF)
        if (cp >= 0x4E00 && cp <= 0x9FFF) return true;

        // Yi Syllables and Radicals (A000-A4CF)
        if (cp >= 0xA000 && cp <= 0xA4CF) return true;

        // Hangul Jamo (1100-115F) — initial consonants
        if (cp >= 0x1100 && cp <= 0x115F) return true;
        // Hangul Jamo Extended-A (A960-A97C)
        if (cp >= 0xA960 && cp <= 0xA97C) return true;

        // Hangul Syllables (AC00-D7AF)
        if (cp >= 0xAC00 && cp <= 0xD7AF) return true;

        // CJK Compatibility Ideographs (F900-FAFF)
        if (cp >= 0xF900 && cp <= 0xFAFF) return true;

        // CJK Compatibility Forms (FE30-FE4F)
        if (cp >= 0xFE30 && cp <= 0xFE4F) return true;

        return false;
    }
}
