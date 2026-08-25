using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace UmaSeedInstaller.Core;

public sealed class ReleaseAsset
{
    public ReleaseAsset(string name, Uri downloadUrl, long size, string sha256)
    {
        Name = name;
        DownloadUrl = downloadUrl;
        Size = size;
        Sha256 = sha256;
    }

    public string Name { get; }
    public Uri DownloadUrl { get; }
    public long Size { get; }
    public string Sha256 { get; }
}

public sealed class ExtensionRelease
{
    public ExtensionRelease(string tagName, Version version, ReleaseAsset asset, Uri releasePage)
    {
        TagName = tagName;
        Version = version;
        Asset = asset;
        ReleasePage = releasePage;
    }

    public string TagName { get; }
    public Version Version { get; }
    public ReleaseAsset Asset { get; }
    public Uri ReleasePage { get; }
}

public sealed class ExtensionManifest
{
    public ExtensionManifest(int manifestVersion, string name, Version version)
    {
        ManifestVersion = manifestVersion;
        Name = name;
        Version = version;
    }

    public int ManifestVersion { get; }
    public string Name { get; }
    public Version Version { get; }
}

public sealed class InstallResult
{
    public InstallResult(Version installedVersion, string extensionDirectory, string? backupDirectory, bool wasUpgrade)
    {
        InstalledVersion = installedVersion;
        ExtensionDirectory = extensionDirectory;
        BackupDirectory = backupDirectory;
        WasUpgrade = wasUpgrade;
    }

    public Version InstalledVersion { get; }
    public string ExtensionDirectory { get; }
    public string? BackupDirectory { get; }
    public bool WasUpgrade { get; }
}

public sealed class BrowserInfo
{
    public BrowserInfo(string id, string displayName, string executablePath, string managementUrl, string processName)
    {
        Id = id;
        DisplayName = displayName;
        ExecutablePath = executablePath;
        ManagementUrl = managementUrl;
        ProcessName = processName;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string ExecutablePath { get; }
    public string ManagementUrl { get; }
    public string ProcessName { get; }
}

public sealed class InstallLayout
{
    public InstallLayout(string baseDirectory)
    {
        Guard.NotBlank(baseDirectory, nameof(baseDirectory));
        BaseDirectory = Path.GetFullPath(baseDirectory);
        ExtensionDirectory = Path.Combine(BaseDirectory, "Extension");
        BackupDirectory = Path.Combine(BaseDirectory, "Backups");
        WorkDirectory = Path.Combine(BaseDirectory, "Work");
    }

    public string BaseDirectory { get; }
    public string ExtensionDirectory { get; }
    public string BackupDirectory { get; }
    public string WorkDirectory { get; }

    public static InstallLayout CreateDefault() =>
        new InstallLayout(Path.Combine(GetLocalAppData(), "UmaSeedSearcher"));

    public static InstallLayout CreateLegacy() =>
        new InstallLayout(Path.Combine(GetLocalAppData(), "Songe", "UmaSeedSearcher"));

    public bool MigrateFrom(InstallLayout legacyLayout)
    {
        Guard.NotNull(legacyLayout, nameof(legacyLayout));
        if (string.Equals(BaseDirectory, legacyLayout.BaseDirectory, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(legacyLayout.BaseDirectory))
        {
            return false;
        }

        if (Directory.Exists(BaseDirectory) || File.Exists(BaseDirectory))
        {
            throw new IOException("新安装目录已经存在，无法自动迁移：" + BaseDirectory);
        }

        Guard.RejectReparsePoint(legacyLayout.BaseDirectory, "拒绝迁移链接或联接目录：");
        var targetParent = Path.GetDirectoryName(BaseDirectory)
            ?? throw new InvalidOperationException("无法确定新安装目录的父目录。");
        Directory.CreateDirectory(targetParent);
        Guard.RejectReparsePoint(targetParent, "拒绝迁移到链接或联接目录：");
        Directory.Move(legacyLayout.BaseDirectory, BaseDirectory);
        return true;
    }

    private static string GetLocalAppData()
    {
        var value = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("无法确定当前用户的 LocalAppData 目录。")
            : value;
    }
}

public sealed class GitHubReleaseClient
{
    private const long MaximumAssetBytes = 20L * 1024 * 1024;
    private static readonly Uri LatestReleaseApi = new(
        "https://api.github.com/repos/yyahz/umamusume-seed-optimizer/releases/latest");
    private static readonly Regex ReleaseAssetNameRegex = new(
        @"^umamusume-seed-optimizer-v(\d+(?:\.\d+){1,3})\.zip$",
        RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient;

    public GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UmaSeedInstaller", "0.1.13"));
        }
    }

    public async Task<ExtensionRelease> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var requestUri = new UriBuilder(LatestReleaseApi)
        {
            Query = "cache_bust=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MaxAge = TimeSpan.Zero
        };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var root = Json.AsObject(new JavaScriptSerializer().DeserializeObject(json), "GitHub Release");
        var tagName = Json.String(root, "tag_name");
        if (!TryParseTagVersion(tagName, out var version))
        {
            throw new InvalidDataException("GitHub 最新版本标签无效：" + tagName);
        }

        var releasePage = Json.HttpsUri(root, "html_url", "github.com");
        ReleaseAsset? selected = null;
        foreach (var value in Json.Array(root, "assets"))
        {
            var asset = Json.AsObject(value, "Release asset");
            var name = Json.String(asset, "name");
            var match = ReleaseAssetNameRegex.Match(name);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var assetVersion)
                || !assetVersion.Equals(version))
            {
                continue;
            }

            var downloadUrl = Json.HttpsUri(asset, "browser_download_url", "github.com");
            var size = Json.Long(asset, "size");
            if (size <= 0 || size > MaximumAssetBytes)
            {
                throw new InvalidDataException("Release 文件大小异常：" + size + " 字节。");
            }

            if (!TryNormalizeSha256(Json.StringOrEmpty(asset, "digest"), out var sha256))
            {
                throw new InvalidDataException("Release 未提供有效的 SHA256 摘要，已拒绝下载。");
            }

            selected = new ReleaseAsset(name, downloadUrl, size, sha256);
            break;
        }

        return selected is null
            ? throw new InvalidDataException("Release " + tagName + " 中没有找到匹配的扩展 ZIP。")
            : new ExtensionRelease(tagName, version, selected, releasePage);
    }

    public async Task DownloadVerifiedAsync(
        ReleaseAsset asset,
        string destinationPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(asset, nameof(asset));
        Guard.NotBlank(destinationPath, nameof(destinationPath));
        if (asset.DownloadUrl.Scheme != Uri.UriSchemeHttps
            || !string.Equals(asset.DownloadUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("仅允许从 github.com 通过 HTTPS 下载发布包。");
        }

        using var response = await _httpClient.GetAsync(
            asset.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var declaredLength = response.Content.Headers.ContentLength;
        if (!declaredLength.HasValue || declaredLength.Value <= 0 || declaredLength.Value > MaximumAssetBytes)
        {
            throw new InvalidDataException("下载响应的文件大小异常。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = SHA256.Create();
        var buffer = new byte[81920];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumAssetBytes || total > asset.Size)
                {
                    throw new InvalidDataException("下载内容超过 Release 声明大小。");
                }

                hash.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                progress?.Report((int)Math.Min(100, total * 100 / asset.Size));
            }

            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }
        catch
        {
            output.Close();
            File.Delete(destinationPath);
            throw;
        }

        if (total != asset.Size || hash.Hash is null
            || !Crypto.FixedTimeEquals(hash.Hash, Crypto.FromHex(asset.Sha256)))
        {
            output.Close();
            File.Delete(destinationPath);
            throw new InvalidDataException("下载文件的大小或 SHA256 校验失败，文件已删除。");
        }

        progress?.Report(100);
    }

    public static bool TryParseTagVersion(string tagName, out Version version)
    {
        var value = tagName.StartsWith("v", StringComparison.Ordinal) ? tagName.Substring(1) : tagName;
        return Version.TryParse(value, out version);
    }

    public static bool TryNormalizeSha256(string digest, out string sha256)
    {
        var value = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest.Substring(7)
            : digest;
        if (value.Length == 64 && value.All(Uri.IsHexDigit))
        {
            sha256 = value.ToLowerInvariant();
            return true;
        }

        sha256 = string.Empty;
        return false;
    }
}

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
        Guard.NotBlank(zipPath, nameof(zipPath));
        Guard.NotNull(expectedVersion, nameof(expectedVersion));
        using var archive = ZipFile.OpenRead(zipPath);
        ValidateEntries(archive);
        var manifestEntry = archive.Entries.SingleOrDefault(
            entry => string.Equals(Normalize(entry.FullName), "manifest.json", StringComparison.Ordinal));
        if (manifestEntry is null)
        {
            throw new InvalidDataException("扩展包根目录缺少 manifest.json。");
        }

        string json;
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
        {
            json = reader.ReadToEnd();
        }

        var root = Json.AsObject(new JavaScriptSerializer().DeserializeObject(json), "manifest.json");
        var manifestVersion = Json.Int(root, "manifest_version");
        var name = Json.String(root, "name");
        var versionText = Json.String(root, "version");
        if (manifestVersion != 3)
        {
            throw new InvalidDataException("只接受 Manifest V3 扩展，实际为 V" + manifestVersion + "。");
        }

        if (name.IndexOf("种马搜索器", StringComparison.Ordinal) < 0)
        {
            throw new InvalidDataException("扩展包名称与种马搜索器不匹配。");
        }

        if (!Version.TryParse(versionText, out var version) || !version.Equals(expectedVersion))
        {
            throw new InvalidDataException("扩展包版本不匹配：期望 " + expectedVersion + "，实际 " + versionText + "。");
        }

        return new ExtensionManifest(manifestVersion, name, version);
    }

    public static void ExtractVerified(string zipPath, string destinationDirectory)
    {
        Guard.NotBlank(destinationDirectory, nameof(destinationDirectory));
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(zipPath);
        ValidateEntries(archive);
        foreach (var entry in archive.Entries)
        {
            var normalized = Normalize(entry.FullName);
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            Guard.EnsureDescendant(root, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
        }
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("扩展包文件数量异常：" + archive.Entries.Count + "。");
        }

        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = Normalize(entry.FullName);
            if (string.IsNullOrWhiteSpace(normalized)
                || normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.IndexOf(':') >= 0
                || normalized.Split('/').Any(segment => segment == ".." || segment == "."))
            {
                throw new InvalidDataException("扩展包包含不安全路径：" + entry.FullName);
            }

            if (!names.Add(normalized))
            {
                throw new InvalidDataException("扩展包包含重复路径：" + normalized);
            }

            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException("扩展包文件大小异常：" + normalized);
            }

            expanded += entry.Length;
            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException("扩展包解压后体积超过安全限制。");
            }

            var fileName = Path.GetFileName(normalized);
            if (!AllowedExtensions.Contains(Path.GetExtension(fileName))
                && !string.Equals(fileName, "LICENSE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("扩展包包含不允许的文件类型：" + normalized);
            }
        }
    }

    private static string Normalize(string value) => value.Replace('\\', '/');
}

public sealed class ExtensionInstaller
{
    private readonly InstallLayout _layout;

    public ExtensionInstaller(InstallLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public Version? GetInstalledVersion()
    {
        var path = Path.Combine(_layout.ExtensionDirectory, "manifest.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var root = Json.AsObject(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)), "manifest.json");
            return Version.TryParse(Json.String(root, "version"), out var version) ? version : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
        {
            return null;
        }
    }

    public InstallResult Install(string verifiedZipPath, Version expectedVersion)
    {
        Guard.NotBlank(verifiedZipPath, nameof(verifiedZipPath));
        Guard.NotNull(expectedVersion, nameof(expectedVersion));
        EnsureSafeLayout();
        ExtensionPackage.Inspect(verifiedZipPath, expectedVersion);
        Directory.CreateDirectory(_layout.BaseDirectory);
        Directory.CreateDirectory(_layout.BackupDirectory);
        Directory.CreateDirectory(_layout.WorkDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(_layout.WorkDirectory, operationId);
        var incoming = Path.Combine(_layout.BaseDirectory, "Extension.incoming-" + operationId);
        string? backup = null;
        var previous = GetInstalledVersion();
        var movedExisting = false;
        var activatedIncoming = false;

        try
        {
            ExtensionPackage.ExtractVerified(verifiedZipPath, staging);
            Directory.Move(staging, incoming);
            if (Directory.Exists(_layout.ExtensionDirectory))
            {
                Guard.RejectReparsePoint(_layout.ExtensionDirectory, "拒绝操作链接或联接目录：");
                backup = Path.Combine(
                    _layout.BackupDirectory,
                    "v" + (previous?.ToString() ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
                Directory.Move(_layout.ExtensionDirectory, backup);
                movedExisting = true;
            }

            Directory.Move(incoming, _layout.ExtensionDirectory);
            activatedIncoming = true;
            var installed = GetInstalledVersion();
            if (installed is null || !installed.Equals(expectedVersion))
            {
                throw new InvalidDataException("更新完成后的版本复核失败。");
            }

            return new InstallResult(installed, _layout.ExtensionDirectory, backup, previous is not null);
        }
        catch
        {
            if (activatedIncoming && Directory.Exists(_layout.ExtensionDirectory))
            {
                Directory.Delete(_layout.ExtensionDirectory, true);
            }

            if (movedExisting && backup is not null && Directory.Exists(backup))
            {
                Directory.Move(backup, _layout.ExtensionDirectory);
            }

            throw;
        }
        finally
        {
            Guard.TryDeleteDirectory(staging);
            Guard.TryDeleteDirectory(incoming);
        }
    }

    private void EnsureSafeLayout()
    {
        var root = Path.GetFullPath(_layout.BaseDirectory);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var value in new[] { _layout.ExtensionDirectory, _layout.BackupDirectory, _layout.WorkDirectory })
        {
            if (!Path.GetFullPath(value).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安装路径超出助手目录：" + value);
            }
        }

        if (Directory.Exists(root))
        {
            Guard.RejectReparsePoint(root, "拒绝操作链接或联接目录：");
        }
    }
}

public static class BrowserDetector
{
    public const string ToolboxUrl = "https://game.bilibili.com/tool/pd";
    public const string Safe360ManagementUrl = "chrome://extensions/";

    public static IReadOnlyList<BrowserInfo> DetectInstalled()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            new BrowserInfo("chrome", "Google Chrome", Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"), "chrome://extensions/", "chrome"),
            new BrowserInfo("chrome", "Google Chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"), "chrome://extensions/", "chrome"),
            new BrowserInfo("chrome", "Google Chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"), "chrome://extensions/", "chrome"),
            new BrowserInfo("edge", "Microsoft Edge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"), "edge://extensions/", "msedge"),
            new BrowserInfo("edge", "Microsoft Edge", Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"), "edge://extensions/", "msedge"),
            new BrowserInfo("360se", "360 安全浏览器", Path.Combine(roaming, "360se6", "Application", "360se.exe"), Safe360ManagementUrl, "360se"),
            new BrowserInfo("360x", "360 极速浏览器 X", Path.Combine(local, "360ChromeX", "Chrome", "Application", "360ChromeX.exe"), "chrome://extensions/", "360ChromeX"),
            new BrowserInfo("360", "360 极速浏览器", Path.Combine(local, "360Chrome", "Chrome", "Application", "360chrome.exe"), "chrome://extensions/", "360chrome")
        };

        return candidates.Where(browser => File.Exists(browser.ExecutablePath))
            .GroupBy(browser => browser.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public static bool IsRunning(BrowserInfo browser)
    {
        var processes = Process.GetProcessesByName(browser.ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static void Open(BrowserInfo browser, string url)
    {
        Guard.NotNull(browser, nameof(browser));
        Guard.NotBlank(url, nameof(url));
        Process.Start(new ProcessStartInfo
        {
            FileName = browser.ExecutablePath,
            Arguments = "\"" + url.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false
        });
    }
}

internal static class Json
{
    public static Dictionary<string, object> AsObject(object? value, string label) =>
        value as Dictionary<string, object>
        ?? throw new InvalidDataException(label + " 的 JSON 结构无效。");

    public static object[] Array(Dictionary<string, object> root, string name) =>
        root.TryGetValue(name, out var value) && value is object[] array
            ? array
            : throw new InvalidDataException("JSON 缺少数组字段：" + name);

    public static string String(Dictionary<string, object> root, string name) =>
        root.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidDataException("JSON 缺少文本字段：" + name);

    public static string StringOrEmpty(Dictionary<string, object> root, string name) =>
        root.TryGetValue(name, out var value) && value is string text ? text : string.Empty;

    public static int Int(Dictionary<string, object> root, string name) => checked((int)Long(root, name));

    public static long Long(Dictionary<string, object> root, string name)
    {
        if (!root.TryGetValue(name, out var value) || value is null)
        {
            throw new InvalidDataException("JSON 缺少数字字段：" + name);
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("JSON 数字字段无效：" + name, exception);
        }
    }

    public static Uri HttpsUri(Dictionary<string, object> root, string name, string trustedHost)
    {
        var text = String(root, name);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, trustedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("JSON 地址字段不受信任：" + name);
        }

        return uri;
    }
}

internal static class Crypto
{
    public static byte[] FromHex(string value)
    {
        if (value.Length % 2 != 0)
        {
            throw new FormatException("十六进制字符串长度无效。");
        }

        var result = new byte[value.Length / 2];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = Convert.ToByte(value.Substring(index * 2, 2), 16);
        }

        return result;
    }

    public static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        var difference = left.Length ^ right.Length;
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            difference |= left[index] ^ right[index];
        }

        return difference == 0;
    }
}

internal static class Guard
{
    public static void NotNull(object? value, string name)
    {
        if (value is null)
        {
            throw new ArgumentNullException(name);
        }
    }

    public static void NotBlank(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("值不能为空。", name);
        }
    }

    public static void RejectReparsePoint(string path, string prefix)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(prefix + path);
        }
    }

    public static void EnsureDescendant(string root, string path)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("拒绝解压到目标目录之外：" + path);
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
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
