using System.Text.Json;
using YamlDotNet.Serialization;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace GameSaveManager.Core;

public sealed class Ludusavi(AppSettings settings, string configDirectory, IProcessRunner runner, TimeSpan? accountScanTimeout = null)
{
    private Dictionary<string, object>? currentConfiguration;
    private List<string> installedTitles = [];
    public string? LastScanWarning { get; private set; }
    public string ConfigDirectory => configDirectory;
    public void Configure(IEnumerable<GameInstallation> games, IEnumerable<SaveRule> rules, IEnumerable<PathRedirect>? redirects = null, bool isolated = false)
    {
        Directory.CreateDirectory(configDirectory);
        var installations = games.Where(x => x.Present && !x.Excluded && x.SaveTitle != null).ToList();
        installedTitles = installations.Select(x => x.SaveTitle!).Distinct().ToList();
        var entries = installations.GroupBy(x => x.SaveTitle!).ToDictionary(x => x.Key, x => new Dictionary<string, object>
        {
            ["name"] = x.Key, ["integration"] = "extend", ["installDir"] = x.Select(g => Path.GetFileName(g.InstallDirectory)).Distinct().ToArray(),
            ["files"] = Array.Empty<string>(), ["registry"] = Array.Empty<string>()
        });
        foreach (var rule in rules)
        {
            if (!entries.TryGetValue(rule.Name, out var entry)) entries[rule.Name] = entry = new() { ["name"] = rule.Name, ["integration"] = "extend" };
            entry["files"] = rule.Files; entry["registry"] = rule.Registry;
        }
        var roots = installations.Select(x => new Dictionary<string, string> { ["path"] = Path.GetDirectoryName(x.InstallDirectory)!, ["store"] = "other" }).DistinctBy(x => x["path"], StringComparer.OrdinalIgnoreCase).ToList();
        if (!isolated)
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamPaths = new[] { steamKey?.GetValue("SteamPath") as string, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam") }.Where(x => x != null && Directory.Exists(x)).Cast<string>().Select(Paths.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var steam in steamPaths.ToList())
            {
                var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraries))
                    foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\""))
                    { var path = match.Groups[1].Value.Replace(@"\\", @"\"); if (Directory.Exists(path)) steamPaths.Add(Paths.Canonical(path)); }
            }
            roots.AddRange(steamPaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(x => new Dictionary<string, string> { ["path"] = x, ["store"] = "steam" }));
        }
        var config = new Dictionary<string, object>
        {
            ["roots"] = roots,
            ["customGames"] = entries.Values.ToArray(),
            ["redirects"] = (redirects ?? []).Select(x => new { kind = "restore", source = x.Source, target = x.Target }).ToArray(),
            ["backup"] = new { ignoredGames = games.Where(x => x.Excluded).Select(x => x.SaveTitle ?? x.Title).ToArray() },
            ["cloud"] = new { synchronize = false }
        };
        var yaml = new SerializerBuilder().Build().Serialize(config);
        currentConfiguration = config;
        Paths.AtomicWrite(Path.Combine(configDirectory, "config.yaml"), yaml);
        if (isolated && !File.Exists(Path.Combine(configDirectory, "manifest.yaml"))) File.WriteAllText(Path.Combine(configDirectory, "manifest.yaml"), "{}\n");
    }
    public async Task<JsonDocument> RunJsonAsync(IEnumerable<string> arguments, CancellationToken ct = default)
    {
        if (!File.Exists(settings.EnginePath)) throw new FileNotFoundException("Ludusavi is missing. Select ludusavi.exe in Settings or run scripts/bootstrap.ps1.", settings.EnginePath);
        var result = await runner.RunAsync(settings.EnginePath, new[] { "--config", configDirectory, "--no-manifest-update" }.Concat(arguments), ct);
        JsonDocument document;
        try { document = JsonDocument.Parse(result.Output); }
        catch (JsonException) { throw new IOException($"Ludusavi failed ({result.ExitCode}): {result.Error} {result.Output}"); }
        if (result.ExitCode != 0 || document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object && errors.EnumerateObject().Any())
        { var detail = document.RootElement.GetRawText(); document.Dispose(); throw new IOException("Ludusavi reported an incomplete operation: " + detail); }
        return document;
    }
    public async Task UpdateManifestAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(settings.EnginePath, ["--config", configDirectory, "manifest", "update"], ct);
        if (result.ExitCode != 0) throw new IOException("Save database update failed: " + result.Error);
    }
    public async Task<string?> ExactTitleAsync(string title, CancellationToken ct = default)
    {
        try
        {
            // The strict lookup misses punctuation differences ("2 0" vs "2.0", "and" vs "&"); the fuzzy one finds
            // them, and the key comparison keeps only a candidate that is the same title.
            foreach (var mode in new[] { "--normalized", "--fuzzy" })
            {
                using var response = await RunJsonAsync(["find", "--api", "--multiple", mode, "--", title], ct);
                if (!response.RootElement.TryGetProperty("games", out var games)) continue;
                var candidate = games.EnumerateObject().Select(x => x.Name).FirstOrDefault(x => Discovery.TitleKey(x) == Discovery.TitleKey(title));
                if (candidate != null) return candidate;
            }
        }
        catch (IOException) { }
        return null;
    }
    public async Task<JsonDocument> PreviewAsync(string repository, IEnumerable<string>? names = null, CancellationToken ct = default)
    {
        var arguments = new[] { "backup", "--preview", "--api", "--no-cloud-sync", "--path", repository, "--" };
        LastScanWarning = null;
        if (names != null || currentConfiguration == null || installedTitles.Count == 0) return await RunJsonAsync(arguments.Concat(names ?? []), ct);
        // Generic installation roots make a full-manifest scan a large cross product. Scan those
        // roots only for identified installations; still scan account/Steam saves for every game.
        using var installed = await RunJsonAsync(arguments.Concat(installedTitles), ct);
        var original = currentConfiguration["roots"];
        JsonDocument global;
        try
        {
            currentConfiguration["roots"] = ((List<Dictionary<string, string>>)original).Where(x => x["store"] != "other").ToList();
            Paths.AtomicWrite(Path.Combine(configDirectory, "config.yaml"), new SerializerBuilder().Build().Serialize(currentConfiguration));
            using var globalTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            globalTimeout.CancelAfter(accountScanTimeout ?? TimeSpan.FromSeconds(90));
            try { global = await RunJsonAsync(arguments, globalTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LastScanWarning = "Account-wide discovery exceeded 90 seconds. Results cover identified installations only; additional saves may be missing. Retry later or use Back up selected.";
                return JsonDocument.Parse(installed.RootElement.GetRawText());
            }
        }
        finally
        {
            currentConfiguration["roots"] = original;
            Paths.AtomicWrite(Path.Combine(configDirectory, "config.yaml"), new SerializerBuilder().Build().Serialize(currentConfiguration));
        }
        using (global)
        {
            var merged = JsonNode.Parse(global.RootElement.GetRawText())!.AsObject();
            var targetGames = merged["games"]?.AsObject() ?? new JsonObject(); merged["games"] = targetGames;
            if (installed.RootElement.TryGetProperty("games", out var installedGames))
            {
                foreach (var game in installedGames.EnumerateObject())
                {
                    var extra = JsonNode.Parse(game.Value.GetRawText())!.AsObject();
                    if (targetGames[game.Name] is JsonObject existing)
                    {
                        foreach (var key in new[] { "files", "registry" })
                            if (extra[key] is JsonObject additions && existing[key] is JsonObject current)
                                foreach (var entry in additions) current[entry.Key] = entry.Value?.DeepClone();
                    }
                    else targetGames[game.Name] = extra;
                }
            }
            return JsonDocument.Parse(merged.ToJsonString());
        }
    }
    public Task<JsonDocument> BackupAsync(string repository, IEnumerable<string> names, CancellationToken ct = default) => RunJsonAsync(new[] { "backup", "--api", "--force", "--no-cloud-sync", "--format", "zip", "--compression", "deflate", "--full-limit", settings.Retention.ToString(), "--differential-limit", "0", "--path", repository, "--" }.Concat(names), ct);
    public Task<JsonDocument> RestoreAsync(string repository, string game, string version, bool preview, CancellationToken ct = default) => RunJsonAsync(new[] { "restore", "--api", "--no-cloud-sync", preview ? "--preview" : "--force", "--path", repository, "--backup", version, "--", game }, ct);
    public async Task<List<BackupVersion>> VersionsAsync(string repository, CancellationToken ct = default)
    {
        using var response = await RunJsonAsync(["backups", "--api", "--path", repository], ct);
        var result = new List<BackupVersion>();
        if (response.RootElement.TryGetProperty("games", out var games))
            foreach (var game in games.EnumerateObject())
                foreach (var version in game.Value.GetProperty("backups").EnumerateArray())
                    result.Add(new(game.Name, version.GetProperty("name").GetString()!, version.GetProperty("when").GetString()!, version.GetProperty("locked").GetBoolean()));
        return result.OrderByDescending(x => x.Timestamp).ToList();
    }
    public async Task PinAsync(string repository, BackupVersion version, bool locked, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(settings.EnginePath, ["--config", configDirectory, "--no-manifest-update", "backups", "edit", "--path", repository, "--backup", version.Id, locked ? "--lock" : "--unlock", "--", version.Game], ct);
        if (result.ExitCode != 0) throw new IOException("Could not change backup pin: " + result.Error);
    }
    public static List<SaveStatus> ParseStatuses(JsonDocument document, bool preview)
    {
        var result = new List<SaveStatus>();
        if (!document.RootElement.TryGetProperty("games", out var games)) return result;
        foreach (var game in games.EnumerateObject())
        {
            var value = game.Value;
            var files = value.TryGetProperty("files", out var f) ? f.EnumerateObject().ToList() : [];
            var registry = value.TryGetProperty("registry", out var r) ? r.EnumerateObject().ToList() : [];
            var failed = files.Concat(registry).Any(x => x.Value.TryGetProperty("failed", out var f2) && f2.GetBoolean() || x.Value.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null);
            var decision = value.TryGetProperty("decision", out var d) ? d.GetString() : "";
            var changed = value.TryGetProperty("change", out var c) ? c.GetString() : "";
            var settingsOnly = files.Count > 0 && registry.Count == 0 && files.All(x => LooksLikeSettings(x.Name));
            result.Add(new()
            {
                Name = game.Name, Files = files.Select(x => x.Name).ToList(), Registry = registry.Select(x => x.Name).ToList(),
                Bytes = files.Sum(x => x.Value.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0),
                Status = failed ? "Failed" : decision == "Ignored" ? "Excluded" : decision == "Cancelled" ? "Deferred" : files.Count + registry.Count == 0 ? "No saves found" : settingsOnly ? preview ? "Settings only; saves unverified" : "Settings backed up; saves unverified" : preview ? "Ready to back up" : changed == "Same" ? "Unchanged" : "Backed up",
                Detail = failed ? value.GetRawText() : settingsOnly ? "Only configuration-like files were found. Locate your progress saves before relying on this backup." : $"{files.Count} files, {registry.Count} registry keys — inspect paths to confirm coverage"
            });
        }
        return result;
    }
    private static bool LooksLikeSettings(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/hardwaresettings/") || normalized.Contains("/saved/config/") || normalized.EndsWith("/gameusersettings.ini") || normalized.EndsWith("/display.ini") || normalized.EndsWith("/settings.lua");
    }
}
