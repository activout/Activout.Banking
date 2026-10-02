namespace Activout.Banking;

/// <summary>Platform configuration and data directories, or a single explicit directory for portable use.</summary>
public sealed record BankPaths(string ConfigDirectory, string DataDirectory)
{
    public string DatabasePath => Path.Combine(DataDirectory, "bank.db");
    public string SyncLockPath => Path.Combine(DataDirectory, "sync.lock");
    public string TlsDirectory => Path.Combine(ConfigDirectory, "tls");

    public static BankPaths Resolve(string? dataDirOverride)
    {
        if (!string.IsNullOrEmpty(dataDirOverride))
        {
            var dir = Path.GetFullPath(dataDirOverride);
            return new BankPaths(dir, dir);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return new BankPaths(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Activout.Banking"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Activout.Banking"));
        }

        if (OperatingSystem.IsMacOS())
        {
            var dir = Path.Combine(home, "Library", "Application Support", "Activout.Banking");
            return new BankPaths(dir, dir);
        }

        return new BankPaths(
            Path.Combine(XdgDirectory("XDG_CONFIG_HOME", Path.Combine(home, ".config")), "activout-banking"),
            Path.Combine(XdgDirectory("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), "activout-banking"));
    }

    private static string XdgDirectory(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        // XDG requires absolute paths; relative values are ignored.
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : fallback;
    }

    /// <summary>Creates a directory readable only by the owner where the platform supports Unix modes.</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        // ponytail: Unix modes only; Windows relies on the per-user profile ACLs, see docs/setup.md.
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>True when group or others can read the file (Unix only).</summary>
    public static bool IsReadableByOthers(string path) =>
        !OperatingSystem.IsWindows() &&
        (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0;
}
