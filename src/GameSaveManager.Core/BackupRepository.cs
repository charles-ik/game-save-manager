using System.IO.Compression;
using System.Security.Cryptography;

namespace GameSaveManager.Core;

public sealed class BackupRepository
{
    private const string CatalogName = "gsm-integrity.json";
    public static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source)) return;
        foreach (var file in EnumerateSafeFiles(source))
        {
            var target = Paths.SafeChild(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
    public static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked files are not allowed in backup repositories: " + entry.FullName);
            if (entry is DirectoryInfo) { foreach (var child in EnumerateSafeFiles(entry.FullName)) yield return child; }
            else yield return entry.FullName;
        }
    }
    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public static void ValidateArchives(string repository)
    {
        foreach (var file in EnumerateSafeFiles(repository).Where(x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            using var archive = ZipFile.OpenRead(file);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains("../", StringComparison.Ordinal) || Path.IsPathRooted(entry.FullName)) throw new InvalidDataException("Unsafe path inside backup archive.");
                using var stream = entry.Open(); stream.CopyTo(Stream.Null);
            }
        }
    }
    public static void Verify(string repository)
    {
        if (!Directory.Exists(repository)) throw new DirectoryNotFoundException(repository);
        var catalogPath = Path.Combine(repository, CatalogName);
        if (!File.Exists(catalogPath)) throw new InvalidDataException("This folder has no verified Game Save Manager backup catalog. Select its Saves or Safety repository.");
        foreach (var (relative, expected) in Json.Read<Dictionary<string, string>>(File.ReadAllText(catalogPath)))
        {
            var file = Paths.SafeChild(repository, relative);
            if (!File.Exists(file) || !string.Equals(Hash(file), expected, StringComparison.Ordinal)) throw new InvalidDataException("Backup file is unavailable or damaged: " + relative);
        }
        ValidateArchives(repository);
    }
    public static void Publish(string staged, string destination)
    {
        ValidateArchives(staged);
        Directory.CreateDirectory(destination);
        var catalogPath = Path.Combine(destination, CatalogName);
        var old = File.Exists(catalogPath) ? Json.Read<Dictionary<string, string>>(File.ReadAllText(catalogPath)) : [];
        var files = EnumerateSafeFiles(staged).Where(x => Path.GetFileName(x) != CatalogName).ToList();
        var catalog = files.ToDictionary(x => Path.GetRelativePath(staged, x), Hash, StringComparer.OrdinalIgnoreCase);
        // Payloads precede manifests. A failure leaves old payloads available and is never reported as success.
        foreach (var file in files.OrderBy(x => x.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            var relative = Path.GetRelativePath(staged, file);
            var target = Paths.SafeChild(destination, relative);
            if (File.Exists(target) && Hash(target) == catalog[relative]) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".gsm-publishing";
            File.Copy(file, temporary, true);
            if (Hash(temporary) != catalog[relative]) throw new IOException("Published file failed verification: " + relative);
            File.Move(temporary, target, true);
        }
        Paths.AtomicWrite(catalogPath, Json.Write(catalog));
        foreach (var obsolete in old.Keys.Except(catalog.Keys, StringComparer.OrdinalIgnoreCase)) File.Delete(Paths.SafeChild(destination, obsolete));
    }
}
