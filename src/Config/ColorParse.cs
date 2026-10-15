using System;
using System.Globalization;

namespace ImmersiveWakes.Config;

// Colours of the config: "#RRGGBB", with each colour's alpha apart in wakeColorAlphas, because ConfigLib's colour
// picker knows no alpha (T06.08); "#AARRGGBB", Wakes' form and the file's before T06.08, is read too. ColorUtil.Hex2Int
// throws a FormatException on a typo, and the mod loader rethrows that one from StartClientSide and takes the client
// down (FINDINGS "Harmony, хоткеи, команды, отладка"), so the config parses its colours here and never throws.
static class ColorParse
{
    // '#' and six or eight hex digits, either case, spaces around them allowed; alpha is the leading pair of eight
    // digits and null for six. Wakes parses any run of hex digits and reads "#RRGGBB" as alpha 0; here it has none.
    public static bool TryParse(string? text, out uint rgb, out int? alpha)
    {
        rgb = 0;
        alpha = null;
        if (text == null) return false;
        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s.Length is not (7 or 9) || s[0] != '#') return false;
        foreach (char c in s[1..])
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        if (!uint.TryParse(s[1..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return false;
        rgb = value & 0xffffff;
        if (s.Length == 9) alpha = (int)(value >> 24);
        return true;
    }

    // Six digits in upper case, as ColorUtil.Int2Hex writes them from ConfigLib's window: a colour saved there unchanged
    // reads back as the same text, and the mod leaves the file alone.
    public static string FormatRgb(uint rgb) => "#" + (rgb & 0xffffff).ToString("X6", CultureInfo.InvariantCulture);
}
