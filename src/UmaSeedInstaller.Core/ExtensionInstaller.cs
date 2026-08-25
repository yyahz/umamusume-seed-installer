using System.Text.Json;

namespace UmaSeedInstaller.Core;

public sealed class ExtensionInstaller
{
    private readonly InstallLayout _layout;

    public ExtensionInstaller(InstallLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public Version? GetInstalledVersion()
    {
        var manifestPath = Path.Combine(_layout.ExtensionDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            var version = document.RootElement.GetProperty("version").GetString();
            return Version.TryParse(version, out var parsed) ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public InstallResult Install(string verifiedZipPath, Version expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedZipPath);
        ArgumentNullException.ThrowIfNull(expectedVersion);
        EnsureSafeLayout();
        ExtensionPackage.Inspect(verifiedZipPath, expectedVersion);

        Directory.CreateDirectory(_layout.BaseDirectory);
        Directory.CreateDirectory(_layout.BackupDirectory);
        Directory.CreateDirectory(_layout.WorkDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(_layout.WorkDirectory, operationId);
        var incomingDirectory = Path.Combine(_layout.BaseDirectory, $"Extension.incoming-{operationId}");
        string? backupDirectory = null;
        var previousVersion = GetInstalledVersion();
        var movedExisting = false;
        var activatedIncoming = false;

        try
        {
            ExtensionPackage.ExtractVerified(verifiedZipPath, stagingDirectory);
            Directory.Move(stagingDirectory, incomingDirectory);

            if (Directory.Exists(_layout.ExtensionDirectory))
            {
                RejectReparsePoint(_layout.ExtensionDirectory);
                var versionLabel = previousVersion?.ToString() ?? "unknown";
                backupDirectory = Path.Combine(
                    _layout.BackupDirectory,
                    $"v{versionLabel}-{DateTime.Now:yyyyMMdd-HHmmssfff}");
                Directory.Move(_layout.ExtensionDirectory, backupDirectory);
                movedExisting = true;
            }

            Directory.Move(incomingDirectory, _layout.ExtensionDirectory);
            activatedIncoming = true;
            var installed = GetInstalledVersion();
            if (installed != expectedVersion)
            {
                throw new InvalidDataException("更新完成后的版本复核失败。");
            }

            return new InstallResult(
                installed,
                _layout.ExtensionDirectory,
                backupDirectory,
                previousVersion is not null);
        }
        catch
        {
            if (activatedIncoming && Directory.Exists(_layout.ExtensionDirectory))
            {
                Directory.Delete(_layout.ExtensionDirectory, recursive: true);
            }

            if (movedExisting && backupDirectory is not null && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, _layout.ExtensionDirectory);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            TryDeleteDirectory(incomingDirectory);
        }
    }

    private void EnsureSafeLayout()
    {
        var baseRoot = Path.GetFullPath(_layout.BaseDirectory);
        foreach (var path in new[]
                 {
                     _layout.ExtensionDirectory,
                     _layout.BackupDirectory,
                     _layout.WorkDirectory
                 })
        {
            var resolved = Path.GetFullPath(path);
            var prefix = baseRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"安装路径超出助手目录：{resolved}");
            }
        }

        if (Directory.Exists(baseRoot))
        {
            RejectReparsePoint(baseRoot);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"拒绝操作链接或联接目录：{path}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
