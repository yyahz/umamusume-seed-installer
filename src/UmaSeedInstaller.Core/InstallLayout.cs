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
        var localAppData = GetLocalAppData();
        return new InstallLayout(Path.Combine(localAppData, "UmaSeedSearcher"));
    }

    public static InstallLayout CreateLegacy()
    {
        var localAppData = GetLocalAppData();
        return new InstallLayout(Path.Combine(localAppData, "Songe", "UmaSeedSearcher"));
    }

    public bool MigrateFrom(InstallLayout legacyLayout)
    {
        ArgumentNullException.ThrowIfNull(legacyLayout);
        if (string.Equals(BaseDirectory, legacyLayout.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Directory.Exists(legacyLayout.BaseDirectory))
        {
            return false;
        }

        if (Directory.Exists(BaseDirectory) || File.Exists(BaseDirectory))
        {
            throw new IOException($"新安装目录已经存在，无法自动迁移：{BaseDirectory}");
        }

        RejectReparsePoint(legacyLayout.BaseDirectory);
        var targetParent = Path.GetDirectoryName(BaseDirectory)
            ?? throw new InvalidOperationException("无法确定新安装目录的父目录。");
        Directory.CreateDirectory(targetParent);
        RejectReparsePoint(targetParent);
        Directory.Move(legacyLayout.BaseDirectory, BaseDirectory);
        return true;
    }

    private static string GetLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法确定当前用户的 LocalAppData 目录。");
        }

        return localAppData;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"拒绝迁移链接或联接目录：{path}");
        }
    }
}
