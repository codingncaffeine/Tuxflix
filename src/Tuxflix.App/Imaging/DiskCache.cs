using System.Security.Cryptography;
using System.Text;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Imaging;

/// <summary>
/// Artwork kept on disk between runs, so a library opens with its posters already there.
/// </summary>
/// <remarks>
/// One file per image and size, named by a hash of the request, in 256 subfolders. Writes go to
/// a sibling file and are renamed into place, so a crash never leaves half a picture. Every few
/// hundred writes the folder is measured and the least recently used files are removed until it
/// is back under its budget; reading a file marks it as used.
/// </remarks>
internal sealed class DiskCache
{
    private const long Budget = 1024L * 1024 * 1024;
    private const long TrimTo = 800L * 1024 * 1024;
    private const int WritesBetweenTrims = 300;

    private readonly string _root;
    private int _writes;
    private int _trimming;

    public DiskCache(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public byte[]? Read(string key)
    {
        var file = FileFor(key);
        try
        {
            if (!File.Exists(file)) return null;
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
            return File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string key, byte[] bytes)
    {
        var file = FileFor(key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".part";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Artwork could not be cached on disk: {ex.Message}");
            return;
        }

        if (Interlocked.Increment(ref _writes) % WritesBetweenTrims == 0) _ = Task.Run(Trim);
    }

    private void Trim()
    {
        if (Interlocked.Exchange(ref _trimming, 1) == 1) return;
        try
        {
            var files = new DirectoryInfo(_root).EnumerateFiles("*.img", SearchOption.AllDirectories).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= Budget) return;

            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= TrimTo) break;
                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"The artwork cache could not be trimmed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _trimming, 0);
        }
    }

    private string FileFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_root, hash[..2], hash + ".img");
    }
}
