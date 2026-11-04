using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit;

internal static class CurseForgeService
{
    private const string BaseUrl = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;
    private const int ModsClassId = 6;
    private const int ModpacksClassId = 4471;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORBIT", "curseforge.key.dpapi");

    public sealed record Project(int Id, string Name, string Summary, int MainFileId);

    public static bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

    public static void SaveApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            if (File.Exists(KeyPath)) File.Delete(KeyPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, protectedBytes);
    }

    private static string? GetApiKey()
    {
        if (!File.Exists(KeyPath)) return null;
        try
        {
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The saved CurseForge API key cannot be decrypted for this Windows account. Please enter it again in ORBIT Settings.");
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string path)
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("CurseForge needs an approved API key. Open ORBIT Settings and add your key from https://console.curseforge.com/.");

        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await Http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("CurseForge rejected this API key (403). Check that the key is valid and approved for third-party launcher use.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("CurseForge rate limit reached. Wait a moment and try again.");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public static async Task<IReadOnlyList<Project>> SearchAsync(string query, bool modpack, string? gameVersion)
    {
        var classId = modpack ? ModpacksClassId : ModsClassId;
        var parameters = new List<string>
        {
            "gameId=" + MinecraftGameId,
            "classId=" + classId,
            "pageSize=30",
            "index=0",
            "sortField=2",
            "sortOrder=desc"
        };
        if (!string.IsNullOrWhiteSpace(query)) parameters.Add("searchFilter=" + Uri.EscapeDataString(query));
        if (!string.IsNullOrWhiteSpace(gameVersion)) parameters.Add("gameVersion=" + Uri.EscapeDataString(gameVersion));
        using var document = await GetJsonAsync("/mods/search?" + string.Join("&", parameters));
        return document.RootElement.GetProperty("data").EnumerateArray().Select(item => new Project(
            item.GetProperty("id").GetInt32(),
            item.GetProperty("name").GetString() ?? "Unknown project",
            item.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "",
            item.TryGetProperty("mainFileId", out var mainFile) ? mainFile.GetInt32() : 0
        )).ToList();
    }

    private static async Task<JsonElement> GetFileAsync(int modId, int fileId)
    {
        using var document = await GetJsonAsync($"/mods/{modId}/files/{fileId}");
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ResolveDownloadUrlAsync(int modId, int fileId, JsonElement file)
    {
        if (file.TryGetProperty("downloadUrl", out var direct) && direct.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(direct.GetString()))
            return direct.GetString()!;

        using var document = await GetJsonAsync($"/mods/{modId}/files/{fileId}/download-url");
        var url = document.RootElement.GetProperty("data").GetString();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("CurseForge has disabled direct distribution for this file.");
        return url;
    }

    public static async Task InstallModAsync(Project project, string gameVersion, string modsFolder)
    {
        using var files = await GetJsonAsync($"/mods/{project.Id}/files?gameVersion={Uri.EscapeDataString(gameVersion)}&pageSize=50&index=0");
        var candidates = files.RootElement.GetProperty("data").EnumerateArray()
            .Where(file => !file.TryGetProperty("isAvailable", out var available) || available.GetBoolean())
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No available CurseForge file for {project.Name} supports Minecraft {gameVersion}.");

        var file = candidates[0];
        var fileId = file.GetProperty("id").GetInt32();
        var filename = file.GetProperty("fileName").GetString() ?? $"{project.Id}-{fileId}.jar";
        var url = await ResolveDownloadUrlAsync(project.Id, fileId, file);
        await DownloadAsync(url, Path.Combine(modsFolder, Path.GetFileName(filename)));
    }

    public static async Task InstallModpackAsync(Project project, string gameVersion, string instanceFolder)
    {
        using var files = await GetJsonAsync($"/mods/{project.Id}/files?gameVersion={Uri.EscapeDataString(gameVersion)}&pageSize=50&index=0");
        var candidates = files.RootElement.GetProperty("data").EnumerateArray()
            .Where(file => !file.TryGetProperty("isAvailable", out var available) || available.GetBoolean())
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No available CurseForge modpack file for {project.Name} supports Minecraft {gameVersion}.");

        var file = candidates[0];
        var fileId = file.GetProperty("id").GetInt32();
        var archiveUrl = await ResolveDownloadUrlAsync(project.Id, fileId, file);
        var archivePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await DownloadAsync(archiveUrl, archivePath);
            await InstallModpackArchiveAsync(archivePath, instanceFolder);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }

    private static async Task InstallModpackArchiveAsync(string archivePath, string instanceFolder)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("This download is not a standard CurseForge modpack export (manifest.json is missing).");
        using var stream = manifestEntry.Open();
        using var manifest = await JsonDocument.ParseAsync(stream);
        var root = manifest.RootElement;
        var minecraft = root.GetProperty("minecraft");
        var gameVersion = minecraft.GetProperty("version").GetString() ?? "";
        var loaders = minecraft.TryGetProperty("modLoaders", out var loaderArray)
            ? loaderArray.EnumerateArray().Select(x => x.GetProperty("id").GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
            : Array.Empty<string>();

        var modsFolder = Path.Combine(instanceFolder, "mods");
        Directory.CreateDirectory(modsFolder);
        var files = root.GetProperty("files").EnumerateArray().ToList();
        var downloaded = 0;
        foreach (var dependency in files)
        {
            var required = !dependency.TryGetProperty("required", out var req) || req.GetBoolean();
            var modId = dependency.GetProperty("projectID").GetInt32();
            var fileId = dependency.GetProperty("fileID").GetInt32();
            try
            {
                var file = await GetFileAsync(modId, fileId);
                var filename = file.GetProperty("fileName").GetString() ?? $"{modId}-{fileId}.jar";
                var url = await ResolveDownloadUrlAsync(modId, fileId, file);
                await DownloadAsync(url, Path.Combine(modsFolder, Path.GetFileName(filename)));
                downloaded++;
            }
            catch when (!required) { }
        }

        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = entry.FullName["overrides/".Length..];
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var destination = SafeDestination(instanceFolder, relative);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }

        Directory.CreateDirectory(instanceFolder);
        var metadata = new
        {
            name = root.TryGetProperty("name", out var name) ? name.GetString() : "CurseForge Modpack",
            gameVersion,
            loaders,
            source = "CurseForge",
            downloadedMods = downloaded,
            totalMods = files.Count
        };
        await File.WriteAllTextAsync(Path.Combine(instanceFolder, "orbit-instance.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SafeDestination(string root, string relative)
    {
        var basePath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe path in CurseForge modpack archive.");
        return fullPath;
    }

    private static async Task DownloadAsync(string url, string destination)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target);
    }
}
