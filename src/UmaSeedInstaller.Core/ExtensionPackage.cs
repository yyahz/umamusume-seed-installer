using System.IO.Compression;
using System.Text.Json;

namespace UmaSeedInstaller.Core;

public static class ExtensionPackage
{
    private const int MaximumEntries = 100;
    private const long MaximumEntryBytes = 12L * 1024 * 1024;
    private const long MaximumExpandedBytes = 50L * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".js", ".png", ".md"
    };

    public static ExtensionManifest Inspect(string zipPath, Version expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(expectedVersion);
        using var archive = ZipFile.OpenRead(zipPath);
        ValidateEntries(archive);
        var manifestEntry = archive.Entries.SingleOrDefault(
            entry => string.Equals(NormalizeEntryName(entry.FullName), "manifest.json", StringComparison.Ordinal));
        if (manifestEntry is null)
        {
            throw new InvalidDataException("扩展包根目录缺少 manifest.json。");
        }

        using var stream = manifestEntry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var manifestVersion = root.GetProperty("manifest_version").GetInt32();
        var name = root.GetProperty("name").GetString() ?? string.Empty;
        var versionText = root.GetProperty("version").GetString() ?? string.Empty;
        if (manifestVersion != 3)
        {
            throw new InvalidDataException($"只接受 Manifest V3 扩展，实际为 V{manifestVersion}。");
        }

        if (!name.Contains("种马搜索器", StringComparison.Ordinal))
        {
            throw new InvalidDataException("扩展包名称与种马搜索器不匹配。");
        }

        if (!Version.TryParse(versionText, out var version) || version != expectedVersion)
        {
            throw new InvalidDataException(
                $"扩展包版本不匹配：期望 {expectedVersion}，实际 {versionText}。");
        }

        return new ExtensionManifest(manifestVersion, name, version);
    }

    public static void ExtractVerified(string zipPath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        using var archive = ZipFile.OpenRead(zipPath);
        ValidateEntries(archive);
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryName(entry.FullName);
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destinationRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            EnsureDescendant(destinationRoot, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries)
        {
            throw new InvalidDataException($"扩展包文件数量异常：{archive.Entries.Count}。");
        }

        long expandedBytes = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryName(entry.FullName);
            if (string.IsNullOrWhiteSpace(normalized)
                || normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Contains(':', StringComparison.Ordinal)
                || normalized.Split('/').Any(segment => segment is ".." or "."))
            {
                throw new InvalidDataException($"扩展包包含不安全路径：{entry.FullName}");
            }

            if (!names.Add(normalized))
            {
                throw new InvalidDataException($"扩展包包含重复路径：{normalized}");
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException($"扩展包文件大小异常：{normalized}");
            }

            expandedBytes += entry.Length;
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("扩展包解压后体积超过安全限制。");
            }

            var fileName = Path.GetFileName(normalized);
            var extension = Path.GetExtension(fileName);
            if (!AllowedExtensions.Contains(extension)
                && !string.Equals(fileName, "LICENSE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"扩展包包含不允许的文件类型：{normalized}");
            }
        }
    }

    private static string NormalizeEntryName(string name) => name.Replace('\\', '/');

    private static void EnsureDescendant(string root, string path)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"拒绝解压到目标目录之外：{path}");
        }
    }
}
