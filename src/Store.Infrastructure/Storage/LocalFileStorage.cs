using Microsoft.Extensions.Options;
using Store.Application.Abstractions;

namespace Store.Infrastructure.Storage;

public sealed class FileStorageOptions
{
    public const string Section = "FileStorage";

    /// <summary>Directory files are written to. Relative paths resolve against the app's content root.</summary>
    public string RootPath { get; set; } = "uploads";

    /// <summary>URL prefix the directory is served under.</summary>
    public string RequestPath { get; set; } = "/uploads";
}

/// <summary>
/// Stores files on local disk under random names. Fine for a single server; swap for a blob
/// storage implementation of <see cref="IFileStorage"/> when running more than one instance.
/// </summary>
internal sealed class LocalFileStorage(IOptions<FileStorageOptions> options) : IFileStorage
{
    private readonly FileStorageOptions _options = options.Value;

    public async Task<string> SaveAsync(Stream content, string extension, string folder, CancellationToken cancellationToken)
    {
        var safeFolder = string.Join('/', folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(IsSafeSegment));
        var directory = Path.Combine(_options.RootPath, safeFolder);
        Directory.CreateDirectory(directory);

        var fileName = $"{Guid.CreateVersion7():N}{extension}";
        var path = Path.Combine(directory, fileName);

        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        return $"{_options.RequestPath.TrimEnd('/')}/{safeFolder}/{fileName}";
    }

    public Task DeleteAsync(string url, CancellationToken cancellationToken)
    {
        var prefix = _options.RequestPath.TrimEnd('/') + "/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Task.CompletedTask; // not ours (e.g. an external URL); nothing to delete
        }

        var relative = url[prefix.Length..];
        var root = Path.GetFullPath(_options.RootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));

        // Never delete outside the storage root, whatever the URL says.
        if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    private static bool IsSafeSegment(string segment) =>
        segment is not ("." or "..") && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
