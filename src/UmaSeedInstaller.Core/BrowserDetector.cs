using System.Diagnostics;

namespace UmaSeedInstaller.Core;

public static class BrowserDetector
{
    public const string ToolboxUrl = "https://game.bilibili.com/tool/pd";

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
            new BrowserInfo("360se", "360 安全浏览器", Path.Combine(roaming, "360se6", "Application", "360se.exe"), "se://extensions/", "360se"),
            new BrowserInfo("360x", "360 极速浏览器 X", Path.Combine(local, "360ChromeX", "Chrome", "Application", "360ChromeX.exe"), "chrome://extensions/", "360ChromeX"),
            new BrowserInfo("360", "360 极速浏览器", Path.Combine(local, "360Chrome", "Chrome", "Application", "360chrome.exe"), "chrome://extensions/", "360chrome")
        };

        return candidates
            .Where(browser => File.Exists(browser.ExecutablePath))
            .GroupBy(browser => browser.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public static bool IsRunning(BrowserInfo browser) =>
        Process.GetProcessesByName(browser.ProcessName).Length > 0;

    public static void Open(BrowserInfo browser, string url)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Process.Start(new ProcessStartInfo
        {
            FileName = browser.ExecutablePath,
            Arguments = QuoteArgument(url),
            UseShellExecute = false
        });
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
