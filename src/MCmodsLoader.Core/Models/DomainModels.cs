namespace MCmodsLoader.Core.Models;

public record ModDefinition
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string FabricModId { get; init; }
    public required string Category { get; init; } // e.g. "Performance", "QoL / HUD", "Library"
    public bool IsRequired { get; init; } = true;
    public string? InstalledVersion { get; set; }
    public string? InstalledFileName { get; set; }
    public bool IsInstalled => !string.IsNullOrEmpty(InstalledFileName);
    public string? TargetVersionNumber { get; set; }
    public string? DownloadUrl { get; set; }
    public long? FileSize { get; set; }
    public string Status { get; set; } = "Pending";
}

public record FabricStatus
{
    public bool IsInstalled { get; init; }
    public string? InstalledLoaderVersion { get; init; }
    public string? ProfileName { get; init; }
    public string? LatestAvailableLoaderVersion { get; init; }
    public string StatusDescription { get; init; } = string.Empty;
}

public record MinecraftVersionInfo
{
    public required string VersionId { get; init; }
    public required string Type { get; init; } // "release", "snapshot", "unknown"
    public bool IsInstalled { get; init; }
    public bool HasFabric { get; set; }

    public override string ToString() => VersionId;
}

public record AppUpdateInfo
{
    public required string CurrentVersion { get; init; }
    public required string LatestVersion { get; init; }
    public required bool HasUpdate { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? ReleaseName { get; init; }
    public string? HtmlUrl { get; init; }
}

public record ModrinthVersionFile
{
    public required string Url { get; init; }
    public required string Filename { get; init; }
    public bool Primary { get; init; }
    public long Size { get; init; }
    public string? Sha1 { get; init; }
}

public record ModrinthVersion
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string VersionNumber { get; init; }
    public required List<string> GameVersions { get; init; }
    public required List<string> Loaders { get; init; }
    public required List<ModrinthVersionFile> Files { get; init; }
}
