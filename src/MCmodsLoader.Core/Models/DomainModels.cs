namespace MCmodsLoader.Core.Models;

public record ModDefinition
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string FabricModId { get; init; }
    public string? InstalledVersion { get; set; }
    public string? InstalledFileName { get; set; }
    public bool IsInstalled => !string.IsNullOrEmpty(InstalledFileName);
    public string? TargetVersionNumber { get; set; }
    public string? DownloadUrl { get; set; }
    public string Status { get; set; } = "Pending";
}

public record FabricStatus
{
    public bool IsInstalled { get; init; }
    public string? InstalledLoaderVersion { get; init; }
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
}

public record ModrinthVersionFile
{
    public required string Url { get; init; }
    public required string Filename { get; init; }
}
