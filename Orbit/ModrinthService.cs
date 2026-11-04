using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Orbit;

internal static class ModrinthService
{
    private static readonly HttpClient Http = new();
    static ModrinthService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("ORBIT/0.3.0");

    public static async Task<JsonDocument> SearchAsync(string query, string type)
    {
        var facets = JsonSerializer.Serialize(new[] { new[] { "project_type:" + type } });
        var url = "https://api.modrinth.com/v2/search?limit=30&facets=" + Uri.EscapeDataString(facets) + "&query=" + Uri.EscapeDataString(query);
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public static async Task<JsonDocument> VersionsAsync(string id)
    {
        using var response = await Http.GetAsync("https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(id) + "/version");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public static async Task DownloadAsync(string url, string destination)
    {
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target);
    }

    public static async Task InstallPackAsync(string packFile, string folder)
    {
        using var zip = ZipFile.OpenRead(packFile);
        var indexEntry = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("Not a Modrinth .mrpack file.");
        using var stream = indexEntry.Open();
        using var index = await JsonDocument.ParseAsync(stream);
        var root = index.RootElement;
        var deps = root.GetProperty("dependencies");
        var gameVersion = deps.GetProperty("minecraft").GetString() ?? "";
        var loaderInfo = deps.EnumerateObject().Where(p => p.Name != "minecraft").Select(p => p.Name + ":" + p.Value.GetString()).ToArray();
        Directory.CreateDirectory(folder);
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString() ?? "";
            var destination = SafeDestination(folder, relative);
            var downloads = file.GetProperty("downloads").EnumerateArray().ToList();
            if (downloads.Count == 0) continue;
            var url = downloads[0].GetString();
            if (!string.IsNullOrWhiteSpace(url)) await DownloadAsync(url, destination);
        }
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = entry.FullName.Substring("overrides/".Length);
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var destination = SafeDestination(folder, relative);
            if (entry.FullName.EndsWith("/")) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }
        var metadata = new { name = root.GetProperty("name").GetString(), gameVersion, loaders = loaderInfo, source = "Modrinth" };
        await File.WriteAllTextAsync(Path.Combine(folder, "orbit-instance.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SafeDestination(string root, string relative)
    {
        var basePath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe path in modpack.");
        return fullPath;
    }
}
