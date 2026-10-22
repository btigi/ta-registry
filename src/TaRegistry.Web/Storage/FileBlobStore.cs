using System.Security.Cryptography;
using TaRegistry.Core;

namespace TaRegistry.Web.Storage;

public sealed class FileBlobStore(StorageLayout layout)
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public async Task<string> SavePackageAsync(Stream input, string extension, CancellationToken cancellationToken)
    {
        var sha = await SaveHashedAsync(input, layout.Packages, extension, cancellationToken);
        return sha;
    }

    public async Task<string> SavePictureAsync(Stream input, CancellationToken cancellationToken)
    {
        var sha = await SaveHashedAsync(input, layout.Pictures, "png", cancellationToken);
        var path = PicturePath(sha);
        var info = new FileInfo(path);
        if (info.Length > 2 * 1024 * 1024 || !IsPng(path))
        {
            File.Delete(path);
            throw new InvalidOperationException("Pictures must be PNG files of at most 2 MiB.");
        }

        return sha;
    }

    public string PackagePath(string sha256, string kind) =>
        Path.Combine(layout.Packages, PackageKinds.FileName(sha256, kind));

    public string PicturePath(string sha256) => Path.Combine(layout.Pictures, sha256 + ".png");

    public string KeyPath(string keyId) => Path.Combine(layout.Keys, keyId + ".key");

    public void DeleteIfUnreferenced(string path) 
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsPng(string path)
    {
        Span<byte> magic = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        return stream.Read(magic) == 8 && magic.SequenceEqual(PngMagic);
    }

    private async Task<string> SaveHashedAsync(Stream input, string directory, string extension, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(layout.Temp, Guid.NewGuid().ToString("n") + ".partial");
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    hasher.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            var destination = Path.Combine(directory, sha + "." + extension);
            if (File.Exists(destination))
                File.Delete(temp);
            else
                File.Move(temp, destination);

            return sha;
        }
        catch
        {
            if (File.Exists(temp))
                File.Delete(temp);
            throw;
        }
    }
}
