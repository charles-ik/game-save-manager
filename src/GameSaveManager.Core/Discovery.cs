using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GameSaveManager.Core;

public sealed class Discovery
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "redist", "_redist", "_commonredist", "redistributables", "support", "__installer", "installer", "directx", "dotnet", ".git", "node_modules", "shortcuts", "tools", "mod uploader", "mod-uploader", "model_editor", "resources", "engine" };
    private static readonly HashSet<string> BinaryDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "release", "retail", "game", "app" };
    private static readonly Regex NonGame = new(@"^(unins.*|uninstall.*|setup.*|install.*|vcredist.*|vc_redist.*|dxsetup.*|crash.*|unitycrash.*|ue4prereq.*|dotnet.*|cef.*|notification_helper|unrealCEFSubProcess)$", RegexOptions.IgnoreCase);

    public static string NormalizeTitle(string value)
    {
        value = Regex.Replace(value, @"(?i)(?:[. _-]+)(?:v\d|build[. _]*\d).*$", "");
        value = Regex.Replace(value, @"(?i)[. _-]+(?:incl[. _]+)?all[. _]+dlcs?$", "");
        return Regex.Replace(value.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim(' ', '-');
    }
    public static string TitleKey(string value) => Regex.Replace(NormalizeTitle(value), @"[^\p{L}\p{N}]", "").ToLowerInvariant();

    public List<GameInstallation> Scan(IEnumerable<string> configuredRoots, Action<string> warning, CancellationToken cancellationToken = default)
    {
        var roots = Paths.DistinctRoots(configuredRoots);
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var changes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { warning($"Unavailable game folder: {root}"); continue; }
            Walk(root, root, groups, changes, warning, cancellationToken);
        }
        // A nested executable below a launchable game is an alternate/helper, not a second installation.
        foreach (var child in groups.Keys.OrderByDescending(x => x.Length).ToList())
        {
            var parent = groups.Keys.Where(x => !Paths.Equal(x, child) && Paths.Within(child, x)).OrderBy(x => x.Length).FirstOrDefault();
            if (parent == null) continue;
            groups[parent].AddRange(groups[child]); changes[parent] = new[] { changes[parent], changes[child] }.Max(); groups.Remove(child);
        }
        var results = new List<GameInstallation>();
        foreach (var (directory, executables) in groups)
        {
            var title = NormalizeTitle(Path.GetFileName(directory));
            var candidates = executables.OrderBy(x => x.Length).ToList();
            var preferred = candidates.Where(x => TitleKey(Path.GetFileNameWithoutExtension(x)) == TitleKey(title)).ToList();
            var selected = preferred.Count == 1 ? preferred[0] : candidates.Count == 1 ? candidates[0] : "";
            var settled = changes[directory] <= DateTimeOffset.UtcNow.AddMinutes(-2);
            results.Add(new()
            {
                Title = title, InstallDirectory = directory, Executable = selected,
                ExecutableCandidates = candidates, LastChangeUtc = changes[directory],
                Status = settled ? "Needs review" : "Waiting for files",
                Reason = !settled ? "Files changed within the last two minutes." : selected.Length == 0 ? "Choose the correct launch executable." : "Confirm game identity or wait for database matching."
            });
        }
        return results.OrderBy(x => x.Title).ToList();
    }
    private static DateTimeOffset Walk(string directory, string root, Dictionary<string, List<string>> groups, Dictionary<string, DateTimeOffset> changes, Action<string> warning, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || IgnoredDirectories.Contains(info.Name) || info.Name.StartsWith('.') || info.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) || info.Name.StartsWith("_Update", StringComparison.OrdinalIgnoreCase)) return DateTimeOffset.MinValue;
            var files = info.GetFiles();
            var executables = files.Where(x => x.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !NonGame.IsMatch(Path.GetFileNameWithoutExtension(x.Name))).ToList();
            foreach (var executable in executables)
            {
                var install = info;
                while (BinaryDirectories.Contains(install.Name) && install.Parent != null && !Paths.Equal(install.FullName, root)) install = install.Parent;
                // An executable immediately under a library root is a standalone installation.
                var key = install.FullName;
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                group.Add(executable.FullName);
                var latest = files.Select(x => x.LastWriteTimeUtc).Append(info.LastWriteTimeUtc).Max();
                if (!changes.TryGetValue(key, out var previous) || latest > previous) changes[key] = latest;
            }
            var subtreeChange = new DateTimeOffset(files.Select(x => x.LastWriteTimeUtc).Append(info.LastWriteTimeUtc).Max());
            foreach (var child in info.GetDirectories()) subtreeChange = new[] { subtreeChange, Walk(child.FullName, root, groups, changes, warning, ct) }.Max();
            foreach (var key in groups.Keys.Where(x => Paths.Within(directory, x))) if (changes[key] < subtreeChange) changes[key] = subtreeChange;
            return subtreeChange;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning($"Cannot scan {directory}: {ex.Message}"); }
        return DateTimeOffset.MinValue;
    }
    public static bool ExecutableSupportsTitle(GameInstallation game)
    {
        if (game.Executable.Length == 0) return false;
        try
        {
            var metadata = FileVersionInfo.GetVersionInfo(game.Executable);
            return new[] { metadata.ProductName, metadata.FileDescription }.Any(x => !string.IsNullOrWhiteSpace(x) && TitleKey(x) == TitleKey(game.Title));
        }
        catch { return false; }
    }
}
