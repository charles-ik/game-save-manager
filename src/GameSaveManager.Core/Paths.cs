namespace GameSaveManager.Core;

public static class Paths
{
    public static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Equal(string a, string b) => string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);
    public static bool Within(string candidate, string root) => Equal(candidate, root) || Canonical(candidate).StartsWith(Canonical(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static List<string> DistinctRoots(IEnumerable<string> roots)
    {
        var sorted = roots.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length);
        var result = new List<string>();
        foreach (var path in sorted) if (!result.Any(root => Within(path, root))) result.Add(path);
        return result;
    }
    public static string SafeChild(string root, string relative)
    {
        var candidate = Canonical(Path.Combine(root, relative));
        if (!Within(candidate, root) || Equal(candidate, root)) throw new InvalidDataException("Path escapes its parent directory.");
        return candidate;
    }
    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
    public static void DeleteOwnedDirectory(string path, string parent)
    {
        if (!Within(path, parent) || Equal(path, parent)) throw new InvalidOperationException("Refusing to remove directory outside the work area.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
