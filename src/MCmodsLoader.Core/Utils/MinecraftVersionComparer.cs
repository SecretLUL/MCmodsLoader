using System.Text.RegularExpressions;

namespace MCmodsLoader.Core.Utils;

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

        var tokensX = Tokenize(x);
        var tokensY = Tokenize(y);

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
                // A number is considered newer than a prerelease/qualifier text
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

        return tokensX.Count.CompareTo(tokensY.Count);
    }

    private static List<string> Tokenize(string version)
    {
        var tokens = new List<string>();
        var matches = Regex.Matches(version.Trim(), @"([0-9]+|[a-zA-Z]+)");
        foreach (Match m in matches)
        {
            tokens.Add(m.Value);
        }
        return tokens;
    }
}
