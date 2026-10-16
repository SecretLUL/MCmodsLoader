using System.Text.RegularExpressions;

namespace MCperfBooster.Core.Utils;

public static partial class ModCompatibilityChecker
{
    [GeneratedRegex(@"(?i)(?:[+\-_]mc|\+(?!kotlin))([0-9]+(?:\.[0-9]+)+)")]
    private static partial Regex McExplicitRegex();

    [GeneratedRegex(@"(?i)-fabric-([0-9]+(?:\.[0-9]+)+)-")]
    private static partial Regex McFabricInNameRegex();

    public static bool IsCompatible(string? targetMcVersion, string? dependsMinecraft, string? modVersion, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(targetMcVersion))
            return true;

        targetMcVersion = targetMcVersion.Trim();

        if (!string.IsNullOrWhiteSpace(dependsMinecraft))
        {
            return MatchesDependency(targetMcVersion, dependsMinecraft.Trim());
        }

        string? extracted = ExtractMinecraftVersion(modVersion) ?? ExtractMinecraftVersion(fileName);
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return MatchesVersionPrefixOrExact(targetMcVersion, extracted);
        }

        return true;
    }

    public static string? ExtractMinecraftVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m1 = McExplicitRegex().Match(text);
        if (m1.Success) return m1.Groups[1].Value;

        var m2 = McFabricInNameRegex().Match(text);
        if (m2.Success) return m2.Groups[1].Value;

        return null;
    }

    private static bool MatchesVersionPrefixOrExact(string targetVersion, string extractedVersion)
    {
        if (string.Equals(targetVersion, extractedVersion, StringComparison.OrdinalIgnoreCase))
            return true;

        string[] targetParts = targetVersion.Split('.');
        string[] extractedParts = extractedVersion.Split('.');

        if (targetParts.Length >= 2 && extractedParts.Length >= 2)
        {
            if (targetParts[0] == extractedParts[0] && targetParts[1] == extractedParts[1])
            {
                if (extractedParts.Length == 2 || targetParts.Length == 2)
                    return true;
                return targetParts[2] == extractedParts[2];
            }
            return false;
        }

        return false;
    }

    private static bool MatchesDependency(string targetVersion, string depends)
    {
        var orClauses = depends.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (orClauses.Length > 1)
        {
            return orClauses.Any(c => MatchesSingleDependencyClause(targetVersion, c));
        }

        return MatchesSingleDependencyClause(targetVersion, depends);
    }

    private static bool MatchesSingleDependencyClause(string targetVersion, string clause)
    {
        clause = clause.Trim();
        if (clause == "*") return true;

        if (clause.EndsWith(".x", StringComparison.OrdinalIgnoreCase) || clause.EndsWith(".*", StringComparison.OrdinalIgnoreCase))
        {
            string baseVer = clause[..^2];
            return targetVersion.StartsWith(baseVer + ".", StringComparison.OrdinalIgnoreCase) ||
                   targetVersion.Equals(baseVer, StringComparison.OrdinalIgnoreCase);
        }

        if (clause.StartsWith("~"))
        {
            string ver = clause[1..].Trim();
            string baseVer = ver.Split('-')[0];
            string[] parts = baseVer.Split('.');
            if (parts.Length >= 2)
            {
                string majorMinor = $"{parts[0]}.{parts[1]}";
                if (!targetVersion.StartsWith(majorMinor + ".", StringComparison.OrdinalIgnoreCase) &&
                    !targetVersion.Equals(majorMinor, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return MinecraftVersionComparer.Instance.Compare(targetVersion, ver) >= 0;
        }

        if (clause.StartsWith("^"))
        {
            string ver = clause[1..].Trim();
            string baseVer = ver.Split('-')[0];
            string[] parts = baseVer.Split('.');
            if (parts.Length >= 1)
            {
                string major = parts[0];
                if (!targetVersion.StartsWith(major + ".", StringComparison.OrdinalIgnoreCase) &&
                    !targetVersion.Equals(major, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return MinecraftVersionComparer.Instance.Compare(targetVersion, ver) >= 0;
        }

        var partsAnd = clause.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (partsAnd.Length > 1)
        {
            return partsAnd.All(p => MatchesSingleDependencyClause(targetVersion, p));
        }

        if (clause.StartsWith(">="))
        {
            string clean = clause[2..].Trim().TrimEnd('-');
            return MinecraftVersionComparer.Instance.Compare(targetVersion, clean) >= 0;
        }
        if (clause.StartsWith(">"))
        {
            string clean = clause[1..].Trim().TrimEnd('-');
            return MinecraftVersionComparer.Instance.Compare(targetVersion, clean) > 0;
        }
        if (clause.StartsWith("<="))
        {
            string clean = clause[2..].Trim().TrimEnd('-');
            return MinecraftVersionComparer.Instance.Compare(targetVersion, clean) <= 0;
        }
        if (clause.StartsWith("<"))
        {
            string clean = clause[1..].Trim().TrimEnd('-');
            return MinecraftVersionComparer.Instance.Compare(targetVersion, clean) < 0;
        }
        if (clause.StartsWith("="))
        {
            string clean = clause[1..].Trim().TrimEnd('-');
            return MinecraftVersionComparer.Instance.Compare(targetVersion, clean) == 0;
        }

        return MinecraftVersionComparer.Instance.Compare(targetVersion, clause.TrimEnd('-')) == 0;
    }
}
