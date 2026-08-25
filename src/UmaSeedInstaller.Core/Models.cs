namespace UmaSeedInstaller.Core;

public sealed record ReleaseAsset(
    string Name,
    Uri DownloadUrl,
    long Size,
    string Sha256);

public sealed record ExtensionRelease(
    string TagName,
    Version Version,
    ReleaseAsset Asset,
    Uri ReleasePage);

public sealed record ExtensionManifest(
    int ManifestVersion,
    string Name,
    Version Version);

public sealed record InstallResult(
    Version InstalledVersion,
    string ExtensionDirectory,
    string? BackupDirectory,
    bool WasUpgrade);

public sealed record BrowserInfo(
    string Id,
    string DisplayName,
    string ExecutablePath,
    string ManagementUrl,
    string ProcessName);

