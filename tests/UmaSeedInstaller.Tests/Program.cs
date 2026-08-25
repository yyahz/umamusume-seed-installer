using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using UmaSeedInstaller.Core;

var tests = new (string Name, Action Run)[]
{
    ("版本标签解析", TestVersionParsing),
    ("SHA256 摘要解析", TestDigestParsing),
    ("GitHub 最新版请求禁用缓存", TestLatestRequestDisablesCaching),
    ("合法扩展包检查与安装", TestValidPackageAndInstall),
    ("更新保留备份", TestUpgradeKeepsBackup),
    ("旧安装目录完整迁移", TestLegacyDirectoryMigration),
    ("拒绝 ZIP 路径穿越", TestZipTraversalRejected),
    ("无效包不破坏已有安装", TestInvalidPackagePreservesInstall)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await TestPublicLatestReleaseAsync();
        Console.WriteLine("PASS GitHub 最新 Release 下载与校验");
    }
    catch (Exception exception)
    {
        failures.Add($"GitHub 最新 Release 下载与校验: {exception.Message}");
        Console.WriteLine($"FAIL GitHub 最新 Release 下载与校验: {exception}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"共 {failures.Count} 项失败：");
    failures.ForEach(Console.Error.WriteLine);
    return 1;
}

Console.WriteLine($"全部 {tests.Length + (args.Contains("--integration", StringComparer.OrdinalIgnoreCase) ? 1 : 0)} 项测试通过。");
return 0;

static void TestVersionParsing()
{
    Assert(GitHubReleaseClient.TryParseTagVersion("v0.10.7", out var prefixed));
    AssertEqual(new Version(0, 10, 7), prefixed);
    Assert(GitHubReleaseClient.TryParseTagVersion("1.2.3", out var plain));
    AssertEqual(new Version(1, 2, 3), plain);
    Assert(!GitHubReleaseClient.TryParseTagVersion("release-latest", out _));
}

static void TestDigestParsing()
{
    var digest = new string('A', 64);
    Assert(GitHubReleaseClient.TryNormalizeSha256($"sha256:{digest}", out var normalized));
    AssertEqual(digest.ToLowerInvariant(), normalized);
    Assert(!GitHubReleaseClient.TryNormalizeSha256("sha256:1234", out _));
}

static void TestLatestRequestDisablesCaching()
{
    const string responseJson = """
        {
          "tag_name": "v1.2.3",
          "html_url": "https://github.com/yyahz/umamusume-seed-optimizer/releases/tag/v1.2.3",
          "assets": [
            {
              "name": "umamusume-seed-optimizer-v1.2.3.zip",
              "browser_download_url": "https://github.com/yyahz/umamusume-seed-optimizer/releases/download/v1.2.3/umamusume-seed-optimizer-v1.2.3.zip",
              "size": 1,
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
          ]
        }
        """;
    using var handler = new CallbackHandler(request =>
    {
        Assert(request.RequestUri?.Query.IndexOf("cache_bust=", StringComparison.Ordinal) >= 0);
        Assert(request.Headers.CacheControl?.NoCache == true);
        Assert(request.Headers.CacheControl?.NoStore == true);
        Assert(request.Headers.Pragma.Any(value => value.Name == "no-cache"));
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };
    });
    using var http = new HttpClient(handler);
    var release = new GitHubReleaseClient(http).GetLatestAsync().GetAwaiter().GetResult();
    AssertEqual(new Version(1, 2, 3), release.Version);
}

static void TestValidPackageAndInstall()
{
    WithTemporaryDirectory(root =>
    {
        var zip = Path.Combine(root, "extension.zip");
        CreatePackage(zip, "1.2.3", ("main.js", "console.log('ok');"));
        var manifest = ExtensionPackage.Inspect(zip, new Version(1, 2, 3));
        AssertEqual(3, manifest.ManifestVersion);
        var layout = new InstallLayout(Path.Combine(root, "install"));
        var result = new ExtensionInstaller(layout).Install(zip, new Version(1, 2, 3));
        AssertEqual(new Version(1, 2, 3), result.InstalledVersion);
        Assert(File.Exists(Path.Combine(layout.ExtensionDirectory, "main.js")));
    });
}

static void TestUpgradeKeepsBackup()
{
    WithTemporaryDirectory(root =>
    {
        var first = Path.Combine(root, "first.zip");
        var second = Path.Combine(root, "second.zip");
        CreatePackage(first, "1.0.0", ("main.js", "old"));
        CreatePackage(second, "1.1.0", ("main.js", "new"));
        var layout = new InstallLayout(Path.Combine(root, "install"));
        var installer = new ExtensionInstaller(layout);
        installer.Install(first, new Version(1, 0, 0));
        var result = installer.Install(second, new Version(1, 1, 0));
        Assert(result.WasUpgrade);
        Assert(result.BackupDirectory is not null);
        AssertEqual("old", File.ReadAllText(Path.Combine(result.BackupDirectory!, "main.js")));
        AssertEqual("new", File.ReadAllText(Path.Combine(layout.ExtensionDirectory, "main.js")));
    });
}

static void TestLegacyDirectoryMigration()
{
    WithTemporaryDirectory(root =>
    {
        var legacy = new InstallLayout(Path.Combine(root, "Publisher", "UmaSeedSearcher"));
        var current = new InstallLayout(Path.Combine(root, "UmaSeedSearcher"));
        Directory.CreateDirectory(legacy.ExtensionDirectory);
        Directory.CreateDirectory(legacy.BackupDirectory);
        File.WriteAllText(Path.Combine(legacy.ExtensionDirectory, "manifest.json"), Manifest("1.0.0"));
        File.WriteAllText(Path.Combine(legacy.BackupDirectory, "marker.txt"), "backup");

        Assert(current.MigrateFrom(legacy));
        Assert(!Directory.Exists(legacy.BaseDirectory));
        Assert(File.Exists(Path.Combine(current.ExtensionDirectory, "manifest.json")));
        AssertEqual("backup", File.ReadAllText(Path.Combine(current.BackupDirectory, "marker.txt")));
        Assert(!current.MigrateFrom(legacy));
    });
}

static void TestZipTraversalRejected()
{
    WithTemporaryDirectory(root =>
    {
        var zip = Path.Combine(root, "unsafe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", Manifest("1.0.0"));
            WriteEntry(archive, "../outside.js", "bad");
        }

        AssertThrows<InvalidDataException>(() => ExtensionPackage.Inspect(zip, new Version(1, 0, 0)));
    });
}

static void TestInvalidPackagePreservesInstall()
{
    WithTemporaryDirectory(root =>
    {
        var valid = Path.Combine(root, "valid.zip");
        var invalid = Path.Combine(root, "invalid.zip");
        CreatePackage(valid, "1.0.0", ("main.js", "keep-me"));
        CreatePackage(invalid, "2.0.0", ("payload.exe", "not allowed"));
        var layout = new InstallLayout(Path.Combine(root, "install"));
        var installer = new ExtensionInstaller(layout);
        installer.Install(valid, new Version(1, 0, 0));
        AssertThrows<InvalidDataException>(() => installer.Install(invalid, new Version(2, 0, 0)));
        AssertEqual("keep-me", File.ReadAllText(Path.Combine(layout.ExtensionDirectory, "main.js")));
        AssertEqual(new Version(1, 0, 0), installer.GetInstalledVersion());
    });
}

static async Task TestPublicLatestReleaseAsync()
{
    await WithTemporaryDirectoryAsync(async root =>
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var client = new GitHubReleaseClient(http);
        var release = await client.GetLatestAsync();
        var package = Path.Combine(root, release.Asset.Name);
        await client.DownloadVerifiedAsync(release.Asset, package);
        var manifest = ExtensionPackage.Inspect(package, release.Version);
        AssertEqual(release.Version, manifest.Version);
        var layout = new InstallLayout(Path.Combine(root, "install"));
        var installed = new ExtensionInstaller(layout).Install(package, release.Version);
        AssertEqual(release.Version, installed.InstalledVersion);
    });
}

static void CreatePackage(string path, string version, params (string Name, string Content)[] files)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    WriteEntry(archive, "manifest.json", Manifest(version));
    foreach (var file in files)
    {
        WriteEntry(archive, file.Name, file.Content);
    }
}

static string Manifest(string version) =>
    $$"""
    {"manifest_version":3,"name":"种马搜索器","version":"{{version}}"}
    """;

static void WriteEntry(ZipArchive archive, string name, string content)
{
    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
    using var stream = entry.Open();
    using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    writer.Write(content);
}

static void WithTemporaryDirectory(Action<string> action)
{
    var root = Path.Combine(Path.GetTempPath(), $"UmaSeedInstallerTests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        action(root);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task WithTemporaryDirectoryAsync(Func<string, Task> action)
{
    var root = Path.Combine(Path.GetTempPath(), $"UmaSeedInstallerTests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        await action(root);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void Assert(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("断言失败。");
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"断言失败：期望 {expected}，实际 {actual}。");
    }
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"断言失败：预期抛出 {typeof(TException).Name}。");
}

sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(callback(request));
}
