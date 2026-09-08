using System.Diagnostics;
using System.Text.RegularExpressions;

namespace StalkerTrainer;

internal sealed record InstallationInfo(string Root, string? BuildId, bool HasExecutable)
{
    internal const string ExpectedBuild = "24963344";
    internal const string DefaultRoot = @"F:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl";
    internal const string ProcessName = "Stalker2-Win64-Shipping";
    internal const string RelativeExe = @"Stalker2\Binaries\Win64\Stalker2-Win64-Shipping.exe";

    public static InstallationInfo Inspect(string root)
    {
        root = Path.GetFullPath(root.Trim().Trim('"'));
        var steamApps = Directory.GetParent(root)?.Parent?.FullName;
        var manifest = steamApps is null ? "" : Path.Combine(steamApps, "appmanifest_1643320.acf");
        string? buildId = null;
        if (File.Exists(manifest))
        {
            var text = File.ReadAllText(manifest);
            buildId = Regex.Match(text, "\"buildid\"\\s+\"(\\d+)\"").Groups[1].Value;
        }
        return new(root, buildId, File.Exists(Path.Combine(root, RelativeExe)));
    }

    public static Process? FindRunning(string root)
    {
        var expected = Path.GetFullPath(Path.Combine(root, RelativeExe));
        Process? found = null;
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            try
            {
                if (found is null && !process.HasExited && process.MainWindowHandle != 0 &&
                    string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    found = process;
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // A process may exit, or Windows may deny its executable path.
            }
            process.Dispose();
        }
        return found;
    }
}
