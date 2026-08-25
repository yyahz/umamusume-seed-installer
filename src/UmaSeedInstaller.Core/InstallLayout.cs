namespace UmaSeedInstaller.Core;

public sealed class InstallLayout
{
    public InstallLayout(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        BaseDirectory = Path.GetFullPath(baseDirectory);
        ExtensionDirectory = Path.Combine(BaseDirectory, "Extension");
        BackupDirectory = Path.Combine(BaseDirectory, "Backups");
        WorkDirectory = Path.Combine(BaseDirectory, "Work");
    }

    public string BaseDirectory { get; }

    public string ExtensionDirectory { get; }

    public string BackupDirectory { get; }

    public string WorkDirectory { get; }

    public static InstallLayout CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法确定当前用户的 LocalAppData 目录。");
        }

        return new InstallLayout(Path.Combine(localAppData, "Songe", "UmaSeedSearcher"));
    }
}

