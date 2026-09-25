using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tuxflix.App.Classic;

/// <summary>A skin in the Winamp Skin Museum: its file's MD5 (the museum's name for it), its file name, its picture and its file.</summary>
public sealed record MuseumSkin(string Md5, string FileName, Uri Screenshot, Uri Download)
{
    /// <summary>The name to show: the file's, without its extension or underscores.</summary>
    public string Name => Path.GetFileNameWithoutExtension(FileName).Replace('_', ' ').Trim();
}

/// <summary>
/// The Winamp Skin Museum (skins.webamp.org), read through its public GraphQL: browse it in the
/// museum's own order, search it, fetch a skin's picture, and add a skin to the library.
/// </summary>
/// <remarks>
/// Only the museum's own files are fetched: a skin whose picture or file is anywhere but the
/// museum's store is left out. Skins the museum marks as not safe for work are left out too. A
/// skin added is checked twice: its bytes must hash to the MD5 the museum names it by, and the
/// library must find a classic skin in them (a zip with a main window, under the size cap).
/// </remarks>
public sealed class SkinMuseum : IDisposable
{
    public static readonly Uri Api = new("https://api.webamp.org/graphql");
    private const string Store = "r2.webampskins.org";
    private const string Fields = "md5 filename screenshot_url download_url nsfw";

    private readonly HttpClient _http;

    public SkinMuseum(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = SkinLibrary.MaxSkinBytes,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tuxflix", BuildInfo.Version));
    }

    /// <summary>The museum in its own order, <paramref name="count"/> from <paramref name="offset"/>, and how many it holds.</summary>
    public async Task<(IReadOnlyList<MuseumSkin> Skins, int Total)> BrowseAsync(int offset, int count, CancellationToken cancellation)
    {
        using var answer = await QueryAsync(
            $"query Browse($first: Int!, $offset: Int!) {{ skins(first: $first, offset: $offset, sort: MUSEUM) {{ count nodes {{ {Fields} }} }} }}",
            new Dictionary<string, object> { ["first"] = count, ["offset"] = offset },
            cancellation).ConfigureAwait(false);
        var skins = answer.RootElement.GetProperty("data").GetProperty("skins");
        return (Parse(skins.GetProperty("nodes")), skins.TryGetProperty("count", out var total) && total.TryGetInt32(out var n) ? n : 0);
    }

    /// <summary>The museum's skins that match <paramref name="query"/>, best first.</summary>
    public async Task<IReadOnlyList<MuseumSkin>> SearchAsync(string query, int offset, int count, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        using var answer = await QueryAsync(
            $"query Search($query: String!, $first: Int!, $offset: Int!) {{ search_skins(query: $query, first: $first, offset: $offset) {{ {Fields} }} }}",
            new Dictionary<string, object> { ["query"] = query.Trim(), ["first"] = count, ["offset"] = offset },
            cancellation).ConfigureAwait(false);
        return Parse(answer.RootElement.GetProperty("data").GetProperty("search_skins"));
    }

    /// <summary>A skin's picture as the museum made it (its main window, equalizer and playlist), PNG.</summary>
    public Task<byte[]> ScreenshotAsync(MuseumSkin skin, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(skin);
        return _http.GetByteArrayAsync(skin.Screenshot, cancellation);
    }

    /// <summary>Fetches a skin, checks it is the file the museum names, and adds it to <paramref name="library"/>; answers its file name there.</summary>
    public async Task<string> AddAsync(MuseumSkin skin, SkinLibrary library, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(library);
        var bytes = await _http.GetByteArrayAsync(skin.Download, cancellation).ConfigureAwait(false);
#pragma warning disable CA5351 // Not a protection: the museum names each skin by its file's MD5, and this checks the file is that one.
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
#pragma warning restore CA5351
        if (md5 != skin.Md5) throw new InvalidDataException("The skin that came is not the one the museum lists.");

        // Under its museum name, so the library names it the same; the library checks it is a skin.
        var folder = Path.Combine(library.Folder, ".museum");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, SafeName(skin) + ".wsz");
        await File.WriteAllBytesAsync(file, bytes, cancellation).ConfigureAwait(false);
        try
        {
            return await library.ImportAsync(file).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The museum's skins from a GraphQL list: the safe ones, whose files are in the museum's own store.</summary>
    internal static IReadOnlyList<MuseumSkin> Parse(JsonElement nodes)
    {
        var skins = new List<MuseumSkin>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("nsfw", out var nsfw) && nsfw.ValueKind == JsonValueKind.True) continue;
            var md5 = Text(node, "md5");
            var name = Text(node, "filename");
            if (md5 is not { Length: 32 } || !md5.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(name)) continue;
            if (!InStore(Text(node, "screenshot_url"), out var screenshot) || !InStore(Text(node, "download_url"), out var download)) continue;
            skins.Add(new MuseumSkin(md5.ToLowerInvariant(), name, screenshot, download));
        }

        return skins;
    }

    private async Task<JsonDocument> QueryAsync(string query, Dictionary<string, object> variables, CancellationToken cancellation)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["query"] = query, ["variables"] = variables }, MuseumJson.Default.DictionaryStringObject);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Api, content, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
        }
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool InStore(string? url, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host == Store && (uri = parsed) is not null;
    }

    // A file name the library can keep: the museum's, less anything a path could take for a folder.
    private static string SafeName(MuseumSkin skin)
    {
        var name = new string([.. Path.GetFileNameWithoutExtension(skin.FileName).Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' ? c : '_')]).Trim('.', ' ');
        return name.Length > 0 ? name[..Math.Min(name.Length, 80)] : skin.Md5;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class MuseumJson : System.Text.Json.Serialization.JsonSerializerContext;
