using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UmaSeedInstaller.Core;

public sealed partial class GitHubReleaseClient
{
    private const long MaximumAssetBytes = 20L * 1024 * 1024;
    private static readonly Uri LatestReleaseApi = new(
        "https://api.github.com/repos/yyahz/umamusume-seed-optimizer/releases/latest");
    private readonly HttpClient _httpClient;

    public GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("UmaSeedInstaller", "0.1.9"));
        }
    }

    public async Task<ExtensionRelease> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var requestUri = new UriBuilder(LatestReleaseApi)
        {
            Query = $"cache_bust={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
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
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseTagVersion(tagName, out var version))
        {
            throw new InvalidDataException($"GitHub 最新版本标签无效：{tagName}");
        }

        var releasePageText = root.GetProperty("html_url").GetString();
        if (!Uri.TryCreate(releasePageText, UriKind.Absolute, out var releasePage)
            || !string.Equals(releasePage.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub Release 页面地址无效。");
        }

        ReleaseAsset? selected = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            var match = ReleaseAssetNameRegex().Match(name);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var assetVersion)
                || assetVersion != version)
            {
                continue;
            }

            var downloadText = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(downloadText, UriKind.Absolute, out var downloadUrl)
                || !string.Equals(downloadUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Release 下载地址不是受信任的 GitHub HTTPS 地址。");
            }

            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumAssetBytes)
            {
                throw new InvalidDataException($"Release 文件大小异常：{size} 字节。");
            }

            var digest = asset.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString() ?? string.Empty
                : string.Empty;
            if (!TryNormalizeSha256(digest, out var sha256))
            {
                throw new InvalidDataException("Release 未提供有效的 SHA256 摘要，已拒绝下载。");
            }

            selected = new ReleaseAsset(name, downloadUrl, size, sha256);
            break;
        }

        return selected is null
            ? throw new InvalidDataException($"Release {tagName} 中没有找到匹配的扩展 ZIP。")
            : new ExtensionRelease(tagName, version, selected, releasePage);
    }

    public async Task DownloadVerifiedAsync(
        ReleaseAsset asset,
        string destinationPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!string.Equals(asset.DownloadUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
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
        if (declaredLength is > MaximumAssetBytes || declaredLength is <= 0)
        {
            throw new InvalidDataException("下载响应的文件大小异常。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumAssetBytes || total > asset.Size)
                {
                    throw new InvalidDataException("下载内容超过 Release 声明大小。");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report((int)Math.Min(100, total * 100 / asset.Size));
            }
        }
        catch
        {
            output.Close();
            File.Delete(destinationPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (total != asset.Size)
        {
            output.Close();
            File.Delete(destinationPath);
            throw new InvalidDataException($"下载大小不匹配：应为 {asset.Size}，实际为 {total}。");
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(asset.Sha256)))
        {
            output.Close();
            File.Delete(destinationPath);
            throw new InvalidDataException("下载文件的 SHA256 校验失败，文件已删除。");
        }

        progress?.Report(100);
    }

    public static bool TryParseTagVersion(string tagName, out Version version)
    {
        var value = tagName.StartsWith('v') ? tagName[1..] : tagName;
        return Version.TryParse(value, out version!);
    }

    public static bool TryNormalizeSha256(string digest, out string sha256)
    {
        var value = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..]
            : digest;
        if (value.Length == 64 && value.All(Uri.IsHexDigit))
        {
            sha256 = value.ToLowerInvariant();
            return true;
        }

        sha256 = string.Empty;
        return false;
    }

    [GeneratedRegex(@"^umamusume-seed-optimizer-v(\d+(?:\.\d+){1,3})\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseAssetNameRegex();
}
