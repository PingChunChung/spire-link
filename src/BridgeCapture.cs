using System.Diagnostics;
using Godot;

namespace SpireLink;

public sealed record CaptureResult(
    string Path,
    int Width,
    int Height,
    string Sha256,
    double WaitForFrameMs,
    double ReadbackMs,
    double EncodeMs,
    double WriteMs,
    long StateVersion,
    string Reason);

public static class BridgeCapture
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(2);

    public static async Task<CaptureResult> CaptureAsync(
        string outputPath,
        string reason,
        long stateVersion,
        CancellationToken cancellationToken = default,
        Action<PublishedCaptureFile>? onPersisted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var fullOutputPath = System.IO.Path.GetFullPath(outputPath);
        using var timeoutSource = new CancellationTokenSource(FrameTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var request = new FrameCaptureRequest(linkedSource.Token);

        try
        {
            await SpireLinkMod.RunOnMainThread(request.Register)
                .WaitAsync(linkedSource.Token)
                .ConfigureAwait(false);
            var frame = await request.Task.WaitAsync(linkedSource.Token).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var encoded = await Task.Run(
                    () => Encode(frame, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            var persisted = await Task.Run(
                    () => CaptureFileWrite.PersistAsync(fullOutputPath, encoded.PngBytes, cancellationToken, onPersisted),
                    cancellationToken)
                .ConfigureAwait(false);

            return new CaptureResult(
                fullOutputPath,
                frame.Width,
                frame.Height,
                persisted.Sha256,
                frame.WaitForFrameMs,
                frame.ReadbackMs,
                encoded.EncodeMs,
                persisted.WriteMs,
                stateVersion,
                reason);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await request.CancelAndDetachAsync(CleanupWait).ConfigureAwait(false);
            throw new TimeoutException($"Viewport capture did not receive frame_post_draw within {FrameTimeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            await request.CancelAndDetachAsync(CleanupWait).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await request.DetachAsync(CleanupWait).ConfigureAwait(false);
            request.Dispose();
        }
    }

    private static EncodedCapture Encode(FrameCapture frame, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var pngBytes = BridgePng.EncodeRgba8(
            frame.RgbaBytes,
            frame.Width,
            frame.Height,
            cancellationToken);
        return new EncodedCapture(pngBytes, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private sealed class FrameCaptureRequest : IDisposable
    {
        private readonly CancellationToken _cancellationToken;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly TaskCompletionSource<FrameCapture> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _waitStarted;
        private int _registered;
        private int _terminal;

        public FrameCaptureRequest(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _cancellationRegistration = cancellationToken.Register(
                static state => ((FrameCaptureRequest)state!).CancellationRequested(),
                this);
        }

        public Task<FrameCapture> Task => _completion.Task;

        public void Register()
        {
            if (_cancellationToken.IsCancellationRequested || Volatile.Read(ref _terminal) != 0)
            {
                Cancel();
                return;
            }

            _waitStarted = Stopwatch.GetTimestamp();
            RenderingServer.Singleton.FramePostDraw += OnFramePostDraw;
            Volatile.Write(ref _registered, 1);

            if (_cancellationToken.IsCancellationRequested)
            {
                Cancel();
                DetachOnMainThread();
            }
        }

        public async Task CancelAndDetachAsync(TimeSpan wait)
        {
            Cancel();
            await DetachAsync(wait).ConfigureAwait(false);
        }

        public async Task DetachAsync(TimeSpan wait)
        {
            if (Volatile.Read(ref _registered) == 0)
            {
                return;
            }

            try
            {
                await SpireLinkMod.RunOnMainThread(DetachOnMainThread)
                    .WaitAsync(wait)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The queued main-thread detach still runs when the game loop resumes.
            }
        }

        public void Dispose()
        {
            _cancellationRegistration.Dispose();
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _terminal, 1, 0) == 0)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
        }

        private void CancellationRequested()
        {
            Cancel();
            _ = DetachAsync(CleanupWait);
        }

        private void OnFramePostDraw()
        {
            DetachOnMainThread();
            if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
            {
                return;
            }

            Image? image = null;
            try
            {
                var waitForFrameMs = Stopwatch.GetElapsedTime(_waitStarted).TotalMilliseconds;
                var readbackStarted = Stopwatch.GetTimestamp();
                var sceneTree = (SceneTree)Engine.GetMainLoop();
                image = sceneTree.Root.GetTexture().GetImage();
                var width = image.GetWidth();
                var height = image.GetHeight();
                if (image.GetFormat() != Image.Format.Rgba8)
                {
                    image.Convert(Image.Format.Rgba8);
                }

                var rgbaBytes = image.GetData();
                var readbackMs = Stopwatch.GetElapsedTime(readbackStarted).TotalMilliseconds;
                _completion.TrySetResult(new FrameCapture(
                    rgbaBytes,
                    width,
                    height,
                    waitForFrameMs,
                    readbackMs));
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                image?.Dispose();
            }
        }

        private void DetachOnMainThread()
        {
            if (Interlocked.Exchange(ref _registered, 0) != 0)
            {
                RenderingServer.Singleton.FramePostDraw -= OnFramePostDraw;
            }
        }
    }

    private sealed record FrameCapture(
        byte[] RgbaBytes,
        int Width,
        int Height,
        double WaitForFrameMs,
        double ReadbackMs);

    private sealed record EncodedCapture(byte[] PngBytes, double EncodeMs);
}
