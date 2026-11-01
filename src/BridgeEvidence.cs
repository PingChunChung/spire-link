using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace SpireLink;

public sealed class EvidenceJournal : IDisposable
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions StateJson = new(CompactJson)
    {
        WriteIndented = true
    };

    private readonly string _sessionId;
    private readonly string _eventsPath;
    private readonly string _statesDirectory;
    private readonly Channel<JournalCommand> _commands;
    private readonly ConcurrentDictionary<long, byte> _stateVersions = new();
    private readonly object _enqueueLock = new();
    private readonly Task _writerTask;
    private string? _lastError;
    private long _sequence;
    private long _dropped;
    private int _disposed;

    // The writer queue is bounded: when the disk cannot keep up, new events and states are dropped and counted instead of
    // piling up in memory. Writes never block the game thread.
    public const int DefaultQueueCapacity = 4096;

    public EvidenceJournal(string rootDirectory, string sessionId, int queueCapacity = DefaultQueueCapacity)
        : this(rootDirectory, sessionId, queueCapacity, startWriter: true) { }

    // startWriter false leaves the queue unread (tests of the bound); nothing is ever written then.
    internal EvidenceJournal(string rootDirectory, string sessionId, int queueCapacity, bool startWriter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));

        _sessionId = sessionId;
        var root = Path.GetFullPath(rootDirectory);
        var sessionDirectoryName = CreateSessionDirectoryName(sessionId);
        SessionDirectory = Path.Combine(root, "sessions", sessionDirectoryName);
        _eventsPath = Path.Combine(SessionDirectory, "events.jsonl");
        _statesDirectory = Path.Combine(SessionDirectory, "states");

        _commands = Channel.CreateBounded<JournalCommand>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait // TryWrite returns false when full; nothing ever waits
        });
        _writerTask = startWriter ? Task.Run(WriterLoopAsync) : Task.CompletedTask;
    }

    public string SessionDirectory { get; }

    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Events and states that were not queued because the writer queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Deletes the oldest session folders under <paramref name="rootDirectory"/>/sessions so that at most <paramref name="keep"/>
    /// remain (by last write time; the current session is never counted or deleted). Only folders named like a session folder
    /// are touched. Returns the names of the deleted folders. <paramref name="keep"/> of 0 or less keeps everything.
    /// </summary>
    public static IReadOnlyList<string> PruneSessions(string rootDirectory, int keep, string? currentSessionDirectory = null)
    {
        var deleted = new List<string>();
        if (keep <= 0) return deleted;
        var sessions = Path.Combine(Path.GetFullPath(rootDirectory), "sessions");
        if (!Directory.Exists(sessions)) return deleted;
        var current = currentSessionDirectory == null ? null : Path.GetFullPath(currentSessionDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var candidates = new DirectoryInfo(sessions).GetDirectories()
            .Where(d => IsSessionDirectoryName(d.Name) && !string.Equals(d.FullName.TrimEnd(Path.DirectorySeparatorChar), current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.LastWriteTimeUtc).ToList();
        foreach (var old in candidates.Skip(keep))
        {
            try { old.Delete(recursive: true); deleted.Add(old.Name); }
            catch (Exception) { /* a folder in use stays; the next launch tries again */ }
        }
        return deleted;
    }

    // "<prefix>-<12 hex>" as CreateSessionDirectoryName produces it.
    private static bool IsSessionDirectoryName(string name)
    {
        int dash = name.LastIndexOf('-');
        if (dash < 1 || name.Length - dash - 1 != 12) return false;
        return name[(dash + 1)..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    public void Record(string eventType, object payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                ReportError("Evidence event was not queued because eventType was empty.");
                return;
            }

            var payloadElement = JsonSerializer.SerializeToElement(payload, CompactJson);
            string? enqueueError = null;

            lock (_enqueueLock)
            {
                if (_disposed != 0)
                {
                    enqueueError = $"Evidence event '{eventType}' was not queued because the journal is disposed.";
                }
                else
                {
                    var sequence = ++_sequence;
                    var envelope = new EventEnvelope(
                        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        Stopwatch.GetTimestamp(),
                        Stopwatch.Frequency,
                        _sessionId,
                        sequence,
                        eventType,
                        payloadElement);

                    if (!_commands.Writer.TryWrite(new EventCommand(envelope)))
                    {
                        Interlocked.Increment(ref _dropped);
                        enqueueError = $"Evidence event '{eventType}' (seq {sequence}) was dropped: {QueueState()} ({Dropped} dropped so far).";
                    }
                }
            }

            if (enqueueError is not null)
            {
                ReportError(enqueueError);
            }
        }
        catch (Exception exception)
        {
            ReportError($"Evidence event '{eventType}' could not be serialized or queued: {exception.Message}");
        }
    }

    public void SaveState(long version, string stateHash, JsonElement state)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(stateHash))
            {
                ReportError($"State version {version} was not queued because stateHash was empty.");
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                ReportError($"State version {version} was not queued because the journal is disposed.");
                return;
            }

            var stateCopy = state.Clone();
            if (!_stateVersions.TryAdd(version, 0))
            {
                return;
            }

            if (!_commands.Writer.TryWrite(new StateCommand(version, stateHash, stateCopy)))
            {
                _stateVersions.TryRemove(version, out _);
                Interlocked.Increment(ref _dropped);
                ReportError($"State version {version} was dropped: {QueueState()} ({Dropped} dropped so far).");
            }
        }
        catch (Exception exception)
        {
            ReportError($"State version {version} could not be copied or queued: {exception.Message}");
        }
    }

    public void Dispose()
    {
        lock (_enqueueLock)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            _commands.Writer.TryComplete();
        }

        try
        {
            if (!_writerTask.Wait(TimeSpan.FromSeconds(5)))
            {
                ReportError("Evidence journal did not finish flushing within 5 seconds.");
            }
        }
        catch (Exception exception)
        {
            ReportError($"Evidence journal flush failed: {exception.GetBaseException().Message}");
        }
    }

    private async Task WriterLoopAsync()
    {
        StreamWriter? eventWriter = null;
        try
        {
            Directory.CreateDirectory(SessionDirectory);
            Directory.CreateDirectory(_statesDirectory);

            var stream = new FileStream(
                _eventsPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            eventWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    switch (command)
                    {
                        case EventCommand eventCommand:
                            await WriteEventAsync(eventWriter, eventCommand.Envelope).ConfigureAwait(false);
                            break;
                        case StateCommand stateCommand:
                            await WriteStateAsync(stateCommand).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception exception)
                {
                    if (command is StateCommand failedState)
                    {
                        _stateVersions.TryRemove(failedState.Version, out _);
                    }

                    ReportError($"Evidence writer failed for {command.Description}: {exception.Message}");
                }
            }

            await eventWriter.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError($"Evidence writer stopped: {exception.Message}");
            _commands.Writer.TryComplete(exception);
            while (_commands.Reader.TryRead(out var command))
            {
                ReportError($"Evidence writer did not persist {command.Description}.");
            }
        }
        finally
        {
            if (eventWriter is not null)
            {
                await eventWriter.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteEventAsync(StreamWriter writer, EventEnvelope envelope)
    {
        var line = JsonSerializer.Serialize(envelope, CompactJson);
        await writer.WriteLineAsync(line).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteStateAsync(StateCommand command)
    {
        var finalPath = Path.Combine(
            _statesDirectory,
            $"{command.Version.ToString(CultureInfo.InvariantCulture)}.json");
        if (File.Exists(finalPath))
        {
            return;
        }

        var temporaryPath = Path.Combine(
            _statesDirectory,
            $".{command.Version.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new StateEnvelope(_sessionId, command.Version, command.StateHash, command.State);
            var json = JsonSerializer.Serialize(document, StateJson);
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception)
            {
                ReportError($"Temporary state file cleanup failed: {exception.Message}");
            }
        }
    }

    private string QueueState() => _commands.Reader.Completion.IsCompleted ? "the writer has stopped" : "the writer queue is full";

    private void ReportError(string message)
    {
        Volatile.Write(ref _lastError, message);
        try
        {
            Console.Error.WriteLine($"[Spire Link evidence] {message}");
        }
        catch
        {
            // Evidence failures must never destabilize the game process.
        }
    }

    private static string CreateSessionDirectoryName(string sessionId)
    {
        var safePrefix = new string(sessionId
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .Take(48)
            .ToArray())
            .Trim('-');
        if (safePrefix.Length == 0)
        {
            safePrefix = "session";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..12]
            .ToLowerInvariant();
        return $"{safePrefix}-{hash}";
    }

    private abstract record JournalCommand(string Description);

    private sealed record EventCommand(EventEnvelope Envelope)
        : JournalCommand($"event seq {Envelope.Sequence}");

    private sealed record StateCommand(long Version, string StateHash, JsonElement State)
        : JournalCommand($"state version {Version}");

    private sealed record EventEnvelope(
        [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
        [property: JsonPropertyName("monotonic_ticks")] long MonotonicTicks,
        [property: JsonPropertyName("monotonic_frequency")] long MonotonicFrequency,
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("seq")] long Sequence,
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("payload")] JsonElement Payload);

    private sealed record StateEnvelope(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("version")] long Version,
        [property: JsonPropertyName("state_hash")] string StateHash,
        [property: JsonPropertyName("state")] JsonElement State);
}
