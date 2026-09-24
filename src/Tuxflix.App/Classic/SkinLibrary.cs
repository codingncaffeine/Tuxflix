using System.Net.Http.Headers;
using System.Security.Cryptography;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Classic;

/// <summary>A skin in the library: its file name in the skins folder and the name the menu shows.</summary>
public sealed record SkinFile(string FileName, string Label, bool IsBase);

/// <summary>
/// The classic skins on this computer: a folder of <c>.wsz</c> files, Winamp's base skin among them.
/// </summary>
/// <remarks>
/// The base skin is Winamp's own artwork, which Tuxflix may not ship. It is fetched once from the
/// Winamp Skin Museum and kept only if its SHA-256 matches the one pinned here. Every other skin a
/// person chooses is copied into the folder, so it stays after the original moves. A skin that
/// leaves a sheet out borrows it from the base skin, as Winamp did, so the base skin stays loaded.
/// Everything here touches files or the network and runs on workers, never on the UI thread.
/// </remarks>
public sealed class SkinLibrary : IDisposable
{
    public const string BaseFileName = "base-2.91.wsz";

    /// <summary>Larger than any classic skin; a bigger file is not one.</summary>
    public const long MaxSkinBytes = 16 * 1024 * 1024;

    private const string BaseSha256 = "0166fb878cd41de07e1cc51067525ccd3f055b94dc3c227d61124fe528788f6f";

    /// <summary>The museum names every skin by its MD5.</summary>
    private static readonly Uri BaseSource = new("https://r2.webampskins.org/skins/5e4f10275dcb1fb211d4a8b4f1bda236.wsz");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClassicSkin? _base;

    public SkinLibrary(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    /// <summary>Every skin in the folder, the base skin first, then by name.</summary>
    public Task<IReadOnlyList<SkinFile>> ListAsync() => Task.Run<IReadOnlyList<SkinFile>>(() =>
    {
        if (!Directory.Exists(Folder)) return [];
        return [.. Directory.EnumerateFiles(Folder)
            .Where(IsSkinFile)
            .Select(path => Path.GetFileName(path))
            .Select(name => new SkinFile(name, name == BaseFileName ? "Base skin" : Path.GetFileNameWithoutExtension(name), name == BaseFileName))
            .OrderBy(s => s.IsBase ? 0 : 1)
            .ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)];
    });

    /// <summary>
    /// Loads a skin by its file name in the folder, or the base skin for none. A skin that cannot
    /// be read falls back to the base skin; with no base skin either (offline on the first run) the
    /// result is an empty skin, which draws plain shapes.
    /// </summary>
    public async Task<ClassicSkin> LoadAsync(string? fileName, CancellationToken cancellation = default)
    {
        var fallback = await BaseAsync(cancellation);
        if (fileName is null || fileName == BaseFileName) return fallback ?? ClassicSkin.Empty();

        var path = Path.Combine(Folder, Path.GetFileName(fileName));
        try
        {
            return await Task.Run(() => ClassicSkin.Load(path, fallback), cancellation);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"Classic skin {fileName} could not be opened ({ex.Message}); using the base skin.");
            return fallback ?? ClassicSkin.Empty();
        }
    }

    /// <summary>
    /// Copies a skin into the folder and returns its file name there. Throws
    /// <see cref="InvalidDataException"/> when the file is not a classic skin.
    /// </summary>
    public Task<string> ImportAsync(string source) => Task.Run(() =>
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("The skin file is gone.", source);
        if (info.Length > MaxSkinBytes) throw new InvalidDataException("That file is too large to be a classic skin.");

        // A classic skin is a zip holding at least its main window.
        using (var probe = ClassicSkin.Load(source, fallback: null))
        {
            if (probe.Sheet(ClassicSprites.Main) is null) throw new InvalidDataException("That file is not a classic skin: it has no main window.");
        }

        Directory.CreateDirectory(Folder);
        var name = Path.GetFileNameWithoutExtension(info.Name);
        var target = Path.Combine(Folder, name + ".wsz");
        if (Path.GetFullPath(target) == Path.GetFullPath(source)) return Path.GetFileName(target);
        for (var n = 2; File.Exists(target); n++)
        {
            // The same skin again is the same file; another skin of the same name gets a number.
            if (SameContent(target, source)) return Path.GetFileName(target);
            target = Path.Combine(Folder, $"{name} ({n}).wsz");
        }

        File.Copy(source, target);
        Log.Info($"Classic skin {info.Name} added to the library.");
        return Path.GetFileName(target);
    });

    /// <summary>Lets go of a skin this library loaded; the base skin stays, as others borrow from it.</summary>
    public void Release(ClassicSkin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        if (!ReferenceEquals(skin, _base)) skin.Dispose();
    }

    public void Dispose()
    {
        _base?.Dispose();
        _base = null;
        _gate.Dispose();
    }

    private static bool IsSkinFile(string path) =>
        path.EndsWith(".wsz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool SameContent(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && Sha256(a) == Sha256(b);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The base skin, fetched on first use and loaded once.</summary>
    private async Task<ClassicSkin?> BaseAsync(CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            if (_base is not null) return _base;
            var path = await Task.Run(() => FetchBaseAsync(cancellation), cancellation);
            if (path is null) return null;
            _base = await Task.Run(() => ClassicSkin.Load(path, fallback: null), cancellation);
            return _base;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn($"The base classic skin could not be opened ({ex.Message}).");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> FetchBaseAsync(CancellationToken cancellation)
    {
        var path = Path.Combine(Folder, BaseFileName);
        if (File.Exists(path) && Sha256(path) == BaseSha256) return path;

        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = MaxSkinBytes,
            };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tuxflix", BuildInfo.Version));
            var bytes = await http.GetByteArrayAsync(BaseSource, cancellation);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (hash != BaseSha256)
            {
                Log.Warn($"The base classic skin from the museum did not match its pinned hash ({hash}); not used.");
                return null;
            }

            Directory.CreateDirectory(Folder);
            var partial = path + ".part";
            await File.WriteAllBytesAsync(partial, bytes, cancellation);
            File.Move(partial, path, overwrite: true);
            Log.Info("The base classic skin was fetched from the Winamp Skin Museum.");
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The base classic skin could not be fetched ({ex.Message}).");
            return null;
        }
    }
}
