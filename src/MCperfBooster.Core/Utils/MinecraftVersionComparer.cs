namespace MCperfBooster.Core.Utils;

/// <summary>
/// Compares Minecraft and Fabric version strings semantically and numerically,
/// preventing string-sorting bugs (such as "1.9" sorting higher than "1.21.4").
/// </summary>
public class MinecraftVersionComparer : IComparer<string>
{
    public static readonly MinecraftVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        string xTrim = x.Trim();
        string yTrim = y.Trim();

        string xBase = xTrim;
        string? xPre = null;
        int xHyphen = xTrim.IndexOf('-');
        if (xHyphen >= 0)
        {
            xBase = xTrim[..xHyphen];
            xPre = xTrim[(xHyphen + 1)..];
        }

        string yBase = yTrim;
        string? yPre = null;
        int yHyphen = yTrim.IndexOf('-');
        if (yHyphen >= 0)
        {
            yBase = yTrim[..yHyphen];
            yPre = yTrim[(yHyphen + 1)..];
        }

        int baseCmp = CompareTokens(Tokenize(xBase), Tokenize(yBase));
        if (baseCmp != 0) return baseCmp;

        if (xPre == null && yPre != null) return 1;
        if (xPre != null && yPre == null) return -1;
        if (xPre == null && yPre == null) return 0;

        return CompareTokens(Tokenize(xPre!), Tokenize(yPre!));
    }

    private static int CompareTokens(List<string> tokensX, List<string> tokensY)
    {
        int minLen = Math.Min(tokensX.Count, tokensY.Count);
        for (int i = 0; i < minLen; i++)
        {
            var tx = tokensX[i];
            var ty = tokensY[i];

            bool isNumX = long.TryParse(tx, out long nx);
            bool isNumY = long.TryParse(ty, out long ny);

            if (isNumX && isNumY)
            {
                int cmp = nx.CompareTo(ny);
                if (cmp != 0) return cmp;
            }
            else if (isNumX && !isNumY)
            {
                return 1;
            }
            else if (!isNumX && isNumY)
            {
                return -1;
            }
            else
            {
                int cmp = string.Compare(tx, ty, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }

        // Missing parts count as zero, as in SemVer and in Fabric's own check: mods declare
        // "26.2.0" for the game that calls itself 26.2. "1.21" still sorts below "1.21.1".
        var longer = tokensX.Count > tokensY.Count ? tokensX : tokensY;
        for (int i = minLen; i < longer.Count; i++)
        {
            if (!long.TryParse(longer[i], out long part) || part != 0)
                return tokensX.Count.CompareTo(tokensY.Count);
        }

        return 0;
    }

    private static List<string> Tokenize(string version)
    {
        var span = version.AsSpan().Trim();
        var tokens = new List<string>();
        int i = 0;
        while (i < span.Length)
        {
            if (char.IsAsciiDigit(span[i]))
            {
                int start = i;
                while (i < span.Length && char.IsAsciiDigit(span[i])) i++;
                tokens.Add(span[start..i].ToString());
            }
            else if (char.IsAsciiLetter(span[i]))
            {
                int start = i;
                while (i < span.Length && char.IsAsciiLetter(span[i])) i++;
                tokens.Add(span[start..i].ToString());
            }
            else
            {
                i++;
            }
        }
        return tokens;
    }
}
