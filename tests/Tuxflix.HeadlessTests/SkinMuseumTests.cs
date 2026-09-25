using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tuxflix.App.Classic;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The Winamp Skin Museum inside the app, against a stand-in for its API and store: only safe
/// skins from the museum's own store are listed, and a skin is added only when it is the file the
/// museum names and a classic skin.
/// </summary>
public sealed class SkinMuseumTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "museum-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly byte[] _skin = SkinZip(withMain: true);
    private readonly byte[] _notASkin = SkinZip(withMain: false);

    public SkinMuseumTests()
    {
        HeadlessSkia.Ensure();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task OnlySafeSkinsFromTheMuseumsOwnStoreAreListed()
    {
        using var museum = new SkinMuseum(new Stand(this));
        var found = await museum.SearchAsync("chrome", 0, 30, TestContext.Current.CancellationToken);
        Assert.Equal(["Chrome Deluxe", "Not A Skin"], found.Select(s => s.Name));
        var (browsed, total) = await museum.BrowseAsync(0, 30, TestContext.Current.CancellationToken);
        Assert.Equal(93603, total);
        Assert.Single(browsed);
    }

    [Fact]
    public async Task ASkinIsAddedOnlyWhenItIsTheMuseumsFileAndASkin()
    {
        using var museum = new SkinMuseum(new Stand(this));
        using var library = new SkinLibrary(Path.Combine(_folder, "skins"));
        var cancel = TestContext.Current.CancellationToken;
        var found = await museum.SearchAsync("chrome", 0, 30, cancel);

        var name = await museum.AddAsync(found[0], library, cancel);
        Assert.Equal("Chrome_Deluxe.wsz", name);
        Assert.Equal(_skin, await File.ReadAllBytesAsync(Path.Combine(library.Folder, name), cancel));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(library.Folder, ".museum")));

        // A file that is not the one the museum names, and a zip with no main window: neither joins the library.
        var wrong = found[0] with { Md5 = new string('0', 32) };
        await Assert.ThrowsAsync<InvalidDataException>(() => museum.AddAsync(wrong, library, cancel));
        await Assert.ThrowsAsync<InvalidDataException>(() => museum.AddAsync(found[1], library, cancel));
        Assert.Equal(["Chrome_Deluxe.wsz"], Directory.EnumerateFiles(library.Folder, "*.wsz").Select(Path.GetFileName));
    }

    [Fact]
    public async Task TheWindowsModelPagesSearchesAndAdds()
    {
        using var library = new SkinLibrary(Path.Combine(_folder, "skins"));
        var added = new List<string>();
        var model = new SkinMuseumViewModel(new SkinMuseum(new Stand(this)), library, added.Add);
        await model.StartAsync();
        Assert.Single(model.Skins);
        Assert.True(model.CanLoadMore);
        Assert.StartsWith("93,603 skins", model.Status, StringComparison.Ordinal);

        model.SearchText = "chrome";
        await model.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, model.Skins.Count);
        await model.Skins[0].AddCommand.ExecuteAsync(null);
        Assert.True(model.Skins[0].IsAdded);
        Assert.False(model.Skins[0].AddCommand.CanExecute(null));
        Assert.Equal(["Chrome_Deluxe.wsz"], added);
        model.Dispose();
    }

    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    private static byte[] SkinZip(bool withMain)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(withMain ? "main.bmp" : "readme.txt");
            using var stream = entry.Open();
            stream.Write(withMain ? Bmp(275, 116) : "not a skin"u8.ToArray());
        }

        return memory.ToArray();
    }

    private static byte[] Bmp(int width, int height)
    {
        var row = ((width * 3) + 3) & ~3;
        var bytes = new byte[54 + (row * height)];
        "BM"u8.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), bytes.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(10), 54);
        BitConverter.TryWriteBytes(bytes.AsSpan(14), 40);
        BitConverter.TryWriteBytes(bytes.AsSpan(18), width);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), height);
        BitConverter.TryWriteBytes(bytes.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), (short)24);
        return bytes;
    }

    /// <summary>A stand-in for the museum: its API answers, its store serves pictures and skins.</summary>
    private sealed class Stand(SkinMuseumTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri == SkinMuseum.Api)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var good = test._skin;
                var bad = test._notASkin;
                string Node(string md5, string file, bool nsfw = false, string host = "r2.webampskins.org") =>
                    $$"""{"md5":"{{md5}}","filename":"{{file}}","screenshot_url":"https://{{host}}/screenshots/{{md5}}.png","download_url":"https://{{host}}/skins/{{md5}}.wsz","nsfw":{{(nsfw ? "true" : "false")}}}""";
                var json = body.Contains("search_skins", StringComparison.Ordinal)
                    ? $$$"""{"data":{"search_skins":[{{{Node(Md5(good), "Chrome_Deluxe.wsz")}}},{{{Node(Md5(bad), "Not_A_Skin.wsz")}}},{{{Node(new string('a', 32), "Racy.wsz", nsfw: true)}}},{{{Node(new string('b', 32), "Elsewhere.wsz", host: "example.org")}}}]}}"""
                    : $$$$"""{"data":{"skins":{"count":93603,"nodes":[{{{{Node(Md5(good), "Chrome_Deluxe.wsz")}}}}]}}}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            if (uri.Host == "r2.webampskins.org" && uri.AbsolutePath.StartsWith("/skins/", StringComparison.Ordinal))
            {
                var md5 = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                var bytes = md5 == Md5(test._skin) ? test._skin : md5 == Md5(test._notASkin) ? test._notASkin : null;
                return bytes is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }

            // Pictures: none here; the window shows the skin without one.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
