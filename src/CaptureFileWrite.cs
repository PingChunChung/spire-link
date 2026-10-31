using System.Diagnostics;
using System.Security.Cryptography;

namespace SpireLink;

// A receipt is exposed by Publish only after a no-replace rename succeeds.
// An attempted write alone never grants ownership of the destination path.
public sealed class PublishedCaptureFile
{
    internal PublishedCaptureFile(string path) { Path = path; }
    public string Path { get; }
    public void Delete() => File.Delete(Path);
}

internal sealed class CaptureFileWrite : IDisposable
{
    internal sealed record PersistedCapture(string Sha256, double WriteMs);
    private readonly string _outputPath;
    private readonly string _temporaryPath;
    private readonly FileStream _content;
    private bool _published;

    public CaptureFileWrite(string outputPath)
    {
        _outputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(_outputPath)!;
        Directory.CreateDirectory(directory);
        _temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_outputPath)}.{Guid.NewGuid():N}.tmp");
        _content = new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous);
    }

    public Stream Content => _content;

    public static async Task<PersistedCapture> PersistAsync(string outputPath, byte[] pngBytes,
        CancellationToken cancellationToken = default, Action<PublishedCaptureFile>? onPersisted = null)
    {
        var started = Stopwatch.GetTimestamp();
        using var write = new CaptureFileWrite(outputPath);
        await write.Content.WriteAsync(pngBytes, cancellationToken).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(SHA256.HashData(pngBytes)).ToLowerInvariant();
        var published = write.Publish(cancellationToken);
        // The caller receives ownership synchronously after the successful move.
        onPersisted?.Invoke(published);
        return new PersistedCapture(sha256, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public PublishedCaptureFile Publish(CancellationToken cancellationToken = default)
    {
        if (_published) throw new InvalidOperationException("Capture already published");
        _content.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = new PublishedCaptureFile(_outputPath);
        File.Move(_temporaryPath, _outputPath, overwrite: false);
        _published = true;
        return receipt;
    }

    public void Dispose()
    {
        try { _content.Dispose(); }
        finally
        {
            if (!_published)
                try { File.Delete(_temporaryPath); }
                catch { /* Best-effort temp cleanup must preserve the original failure. */ }
        }
    }
}
