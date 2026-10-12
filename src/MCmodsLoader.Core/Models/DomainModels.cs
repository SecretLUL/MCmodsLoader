namespace MCmodsLoader.Core.Models;

public record ModDefinition
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string FabricModId { get; init; }
    public string? InstalledVersion { get; set; }
    public string? InstalledFileName { get; set; }
    public bool IsCompatible { get; set; } = true;
    public bool IsInstalled => !string.IsNullOrEmpty(InstalledFileName) && IsCompatible;
    public string? TargetVersionNumber { get; set; }
    public string? DownloadUrl { get; set; }
    public string? TargetSha1 { get; set; }
    public string? TargetSha512 { get; set; }

    /// <summary>
    /// Further jars of the same mod found next to <see cref="InstalledFileName"/>; they are removed on the next install.
    /// </summary>
    public IReadOnlyList<string> DuplicateFileNames { get; set; } = Array.Empty<string>();

    public string Status { get; set; } = "Pending";
}

public record FabricStatus
{
    public bool IsInstalled { get; init; }
    public string? InstalledLoaderVersion { get; init; }

    /// <summary>The newest installed version id, "fabric-loader-&lt;loader&gt;-&lt;mc&gt;".</summary>
    public string? InstalledVersionId { get; init; }

    /// <summary>
    /// True if a launcher profile points at an installed Fabric loader for this game
    /// version. Fabric can be installed without one, e.g. after the profile was deleted.
    /// </summary>
    public bool HasLauncherProfile { get; init; }

    /// <summary>The launcher's name for that profile; null when there is none.</summary>
    public string? ProfileName { get; init; }
}

public record MinecraftVersionInfo
{
    public required string VersionId { get; init; }
    public bool IsInstalled { get; init; }

    public override string ToString() => VersionId;
}

public record AppUpdateInfo
{
    public required string CurrentVersion { get; init; }
    public required string LatestVersion { get; init; }
    public required bool HasUpdate { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ReleaseName { get; init; }
    public string? HtmlUrl { get; init; }

    /// <summary>
    /// Lower-case hex SHA-256 of the release asset, taken from the asset's "digest"
    /// field. Null when GitHub did not publish one; the download is then only checked
    /// for being a non-empty PE image.
    /// </summary>
    public string? DownloadSha256 { get; init; }
}

public record ModrinthVersionFile
{
    public required string Url { get; init; }
    public required string Filename { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha512 { get; init; }
    public string? ProjectId { get; init; }
    public string? VersionNumber { get; init; }
    public IReadOnlyList<ModrinthDependency> Dependencies { get; init; } = Array.Empty<ModrinthDependency>();
}

/// <summary>A dependency of a Modrinth version; <see cref="DependencyType"/> is required, optional, incompatible or embedded.</summary>
public sealed record ModrinthDependency(string? ProjectId, string? VersionId, string DependencyType);

/// <summary>A Modrinth project. <see cref="Loaders"/> is null or empty when unknown.</summary>
public sealed record ModrinthProject(string Id, string Slug, string Title, IReadOnlyList<string>? Loaders = null);

public sealed record ModIssue(string ModName, string Reason);
