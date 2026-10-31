using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireLink;

[ModInitializer("Initialize")]
public static partial class SpireLinkMod
{
    private static long _transformSeenAt = -100000;
    public const string Version = "0.3.0";
    public const string ModName = "SpireLink";
    // The config and the next_run spec beside the DLL end in .cfg, not .json: the game's mod loader reads every .json under
    // mods/ as a manifest and logs each one that has no id. Their content is JSON.
    public const string ConfigFileName = ModName + ".cfg";
    public const string NextRunFileName = ModName + ".next_run.cfg";
    private static string DefaultDataDirectory => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), ModName);
    public const int DefaultPort = 15528;
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    internal static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private static readonly SnapshotStore Snapshots = new();
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);
    private static readonly RunCaptureBudget CaptureBudget = new();
    private static ReceiptStore<RequestEntry> Requests = new(16384, e => e.Work.IsCompleted);
    private static readonly ConcurrentDictionary<string, long> Counters = new();
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<Dictionary<string, double>>> Metrics = new();
    private static readonly CancellationTokenSource Stopping = new();
    private static HttpListener? _listener;
    private static BridgeConfiguration _configuration = new();
    private static EvidenceJournal? _journal;
    private static long _lastSample;
    private static readonly HashSet<string> MajorEvents = new();
    private static readonly HashSet<string> BlockedEvents = new();
    private static bool _wasLowHp;
    private static string? _lastError;
    private static string? _buildHash;
    private static string? _journalRunId;
    private static string _runObservationOrigin = "observed";
    // Main-thread-owned in-flight mutation guard also survives a settle timeout.
    private static long _pendingBeforeVersion;
    private static long _pendingLastVersion;
    private static long _pendingStableSince;
    private static AgentAction? _pendingAction;
    private static AgentSnapshot? _pendingSnapshot;
    private static string? _pendingRequestId;
    private static long _pendingStarted;
    private static JsonElement? _pendingDecision;
    private static long _controlRevision;
    private static long _lastInteractionVersion;
    private static long _lastInteractionChange = Stopwatch.GetTimestamp();

    private sealed class BridgeConfiguration
    {
        public int Port { get; set; } = DefaultPort;
        public string ApiKey { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        public string DataDirectory { get; set; } = DefaultDataDirectory;
        public bool AutoCapture { get; set; } = true;
        public int MaxCaptures { get; set; } = 100;
        public int ObserveIntervalMs { get; set; } = 250;
        public int SettleStableMs { get; set; } = 80;
        public string AnimationMode { get; set; } = "keep";
        public double TimeScale { get; set; } = 1.0;
        // Frame rate cap (0 keeps the engine default, which spins as fast as it can). Lower means less CPU per instance.
        public int MaxFps { get; set; } = 0;
        // Godot sleeps between frames when idle (OS.LowProcessorUsageMode).
        public bool LowProcessor { get; set; } = false;
        public string NonInteractive { get; set; } = "off";
        public string TraceFile { get; set; } = "";
        public int ReceiptCapacity { get; set; } = 16384;
    }

    private sealed class RequestEntry(string fingerprint, Task<object> work)
    {
        public string Fingerprint { get; } = fingerprint;
        public Task<object> Work = work;
        public ReceiptResolution Resolution { get; } = new();
        public object Present(object result) => Resolution.Apply(result, _jsonOptions);
    }
    private sealed class ApiFailure(int status, string code, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }

    public static void Initialize()
    {
        try
        {
            string directory = Path.GetDirectoryName(typeof(SpireLinkMod).Assembly.Location)!;
            _buildHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SpireLinkMod).Assembly.Location))).ToLowerInvariant();
            string configPath = Path.Combine(directory, ConfigFileName);
            if (File.Exists(configPath))
                _configuration = JsonSerializer.Deserialize<BridgeConfiguration>(File.ReadAllText(configPath), _jsonOptions)
                    ?? throw new InvalidDataException("Invalid config");
            else
                File.WriteAllText(configPath, JsonSerializer.Serialize(_configuration, _jsonOptions));
            if (_configuration.Port is < 1024 or > 65535 || _configuration.ApiKey.Length < 24)
                throw new InvalidDataException("Invalid port or api_key (minimum 24 characters)");
            _configuration.ObserveIntervalMs = Math.Clamp(_configuration.ObserveIntervalMs, 5, 2000);
            _configuration.SettleStableMs = Math.Clamp(_configuration.SettleStableMs, 0, 500);
            _configuration.MaxCaptures = Math.Clamp(_configuration.MaxCaptures, 0, 1000);
            _configuration.ReceiptCapacity = Math.Clamp(_configuration.ReceiptCapacity, 1024, 65536);
            Requests = new(_configuration.ReceiptCapacity, e => e.Work.IsCompleted);
            _journal = new EvidenceJournal(_configuration.DataDirectory, SessionId);
            InstallCardGridGuard();
            InstallPunchOffGuard();
            StartMainThreadWatchdog();
            if (!string.IsNullOrEmpty(_configuration.TraceFile))
            {
                AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace("UNHANDLED " + e.ExceptionObject);
                TaskScheduler.UnobservedTaskException += (_, e) => Trace("UNOBSERVED " + e.Exception);
                Trace("trace_started");
            }
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(ProcessMainThreadQueue));
            tree.Root.TreeExiting += Shutdown;
            TryApplyAnimationMode(_configuration.AnimationMode); // throws on an unknown name; the save manager may not exist yet, ProcessMainThreadQueue keeps the setting pinned
            _configuration.TimeScale = Math.Clamp(_configuration.TimeScale, 0.5, 8.0);
            if (Math.Abs(_configuration.TimeScale - 1.0) > 1e-9) Engine.TimeScale = _configuration.TimeScale;
            if (_configuration.MaxFps > 0) Engine.MaxFps = Math.Clamp(_configuration.MaxFps, 5, 240);
            if (_configuration.LowProcessor) OS.LowProcessorUsageMode = true;
            // The game's own AutoSlayer bot flips this switch: waits, pauses and per-frame action pacing are skipped.
            // "all" crashes the game when a shop room loads, so "combat" limits the fast path to fights.
            // Choosing a card to transform (Neow's New Leaf) hangs the game in the fast path, so that screen runs normally.
            // The confirm closes that screen and the transform animation then runs on the fast path, which hangs the main thread
            // (148 confirm_selection requests in six hours of ro-hunt8/9 never answered, the game was killed each time). So the
            // normal path also covers a few seconds after the screen was last on top.
            if (_configuration.NonInteractive == "all")
                MegaCrit.Sts2.Core.Helpers.NonInteractiveMode.AutoSlayerCheck = () =>
                {
                    long now = System.Environment.TickCount64;
                    if (MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Peek() is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen)
                    {
                        Interlocked.Exchange(ref _transformSeenAt, now);
                        return false;
                    }
                    return now - Interlocked.Read(ref _transformSeenAt) > 6000;
                };
            else if (_configuration.NonInteractive == "noshop")
                // Everything except the merchant room, whose load crashes the game in this mode.
                MegaCrit.Sts2.Core.Helpers.NonInteractiveMode.AutoSlayerCheck = () =>
                    MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom
                        is not MegaCrit.Sts2.Core.Rooms.MerchantRoom && MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance == null;
            else if (_configuration.NonInteractive == "combat")
                MegaCrit.Sts2.Core.Helpers.NonInteractiveMode.AutoSlayerCheck =
                    () => MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress == true;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_configuration.Port}/");
            _listener.Start();
            _ = Task.Run(ServerLoop);
            _journal.Record("bridge_started", new { version = Version, build_sha256 = _buildHash, port = _configuration.Port, time_scale = _configuration.TimeScale, non_interactive = _configuration.NonInteractive,
                game_assembly_version = typeof(MegaCrit.Sts2.Core.Runs.RunManager).Assembly.GetName().Version?.ToString() });
            GD.Print($"[Spire Link] v{Version} listening on 127.0.0.1:{_configuration.Port}; API key is in {configPath}");
        }
        catch (Exception ex) { _lastError = ex.Message; GD.PrintErr($"[Spire Link] Initialize failed: {ex}"); }
    }

    private static void Shutdown()
    {
        if (Stopping.IsCancellationRequested) return;
        Stopping.Cancel();
        try { _listener?.Close(); } catch { }
        _journal?.Record("bridge_stopped", new { });
        _journal?.Dispose();
    }

    private static void ProcessMainThreadQueue()
    {
        BeatMainThread();
        if (_configuration.AnimationMode != "keep")
        {
            try { TryApplyAnimationMode(_configuration.AnimationMode); } catch (Exception ex) { _lastError = ex.Message; }
        }
        int processed = 0;
        while (processed++ < 8 && _mainThreadQueue.TryDequeue(out var action))
            try { action(); } catch (Exception ex) { GD.PrintErr($"[Spire Link] Dispatch: {ex}"); }
        if (!Stopping.IsCancellationRequested && (_configuration.AutoCapture || Volatile.Read(ref _pendingBeforeVersion) != 0)
            && Stopwatch.GetElapsedTime(_lastSample).TotalMilliseconds >= _configuration.ObserveIntervalMs)
        {
            try { SampleState(); }
            catch (Exception ex) { _lastError = ex.Message; }
        }
    }

    internal static Task<T> RunOnMainThread<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        var invocation = new QueuedInvocation<T>(() =>
        {
            Stopping.Token.ThrowIfCancellationRequested();
            return func();
        }, cancellationToken);
        _mainThreadQueue.Enqueue(invocation.Run);
        return invocation.Task;
    }
    internal static Task RunOnMainThread(Action action, CancellationToken cancellationToken = default)
        => RunOnMainThread(() => { action(); return true; }, cancellationToken);

    // Crash forensics: with trace_file set, every sample and unhandled exception is appended and flushed, so the last
    // line shows what the process was doing when it died.
    private static readonly object TraceLock = new();
    private static void Trace(string line)
    {
        if (string.IsNullOrEmpty(_configuration.TraceFile)) return;
        try { lock (TraceLock) File.AppendAllText(_configuration.TraceFile, $"{DateTime.UtcNow:HH:mm:ss.fff} {line}{System.Environment.NewLine}"); }
        catch { }
    }

    private static AgentSnapshot SampleState()
    {
        Dictionary<string, object?> state;
        Trace("sample_start");
        try { state = ReadGameState(); Trace("sample_end " + (state.GetValueOrDefault("state_type") as string)); }
        catch (Exception ex) when (ex is NullReferenceException or ObjectDisposedException)
        {
            Count("transient_observation_error");
            if (_lastError != ex.Message)
                _journal?.Record("observation_not_ready", new { error_type = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace });
            _lastError = ex.Message;
            state = new() { ["state_type"] = "transition", ["reason"] = "game_data_not_ready", ["observation_error"] = ex.GetType().Name };
        }
        var snapshot = Snapshots.Publish(state, InteractionReady(state));
        CaptureBudget.Observe(snapshot.State);
        if (_lastInteractionVersion != snapshot.Version)
        {
            _lastInteractionVersion = snapshot.Version;
            _lastInteractionChange = Stopwatch.GetTimestamp();
        }
        if (_pendingBeforeVersion != 0)
        {
            if (_pendingLastVersion != snapshot.Version || !snapshot.Ready)
            {
                _pendingLastVersion = snapshot.Version;
                _pendingStableSince = Stopwatch.GetTimestamp();
            }
            if (snapshot.Version != _pendingBeforeVersion && snapshot.Ready
                && Elapsed(_pendingStableSince) >= _configuration.SettleStableMs
                && ReachedPostcondition(_pendingAction, _pendingSnapshot, snapshot))
            {
                ResolvePending(snapshot);
            }
        }
        _lastSample = Stopwatch.GetTimestamp();
        ObserveMajorEvents(snapshot);
        var interaction = DiagnosticsStatus(snapshot);
        if (interaction.Status is "blocked" or "unsupported"
            && BlockedEvents.Add($"{snapshot.StateHash}:{_pendingRequestId}:{interaction.Reason}"))
        {
            _journal?.SaveState(snapshot.Version, snapshot.StateHash, snapshot.State);
            _journal?.Record("interaction_blocked", new { state_version = snapshot.Version, interaction.Status,
                reason = interaction.Reason, request_id = _pendingRequestId, retry_allowed = false });
            if (_configuration.AutoCapture) _ = AutoCapture(snapshot, interaction.Reason, "diagnostic");
        }
        return snapshot;
    }

    private static async Task<AgentSnapshot> TimedSnapshot(Dictionary<string, double> timing)
    {
        long queued = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await RunOnMainThread(() =>
        {
            timing["queue_wait_ms"] = Elapsed(queued);
            long build = Stopwatch.GetTimestamp();
            var state = SampleState();
            timing["state_build_ms"] = Elapsed(build);
            return state;
        }, deadline.Token); }
        catch (OperationCanceledException) when (!Stopping.IsCancellationRequested)
        { throw new TimeoutException("Game main-thread work was cancelled before it started"); }
    }

    private static async Task ServerLoop()
    {
        while (!Stopping.IsCancellationRequested && _listener?.IsListening == true)
        {
            try { var context = await _listener.GetContextAsync(); _ = Task.Run(() => Handle(context)); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
        }
    }

    private static bool Authenticated(HttpListenerRequest request)
    {
        string supplied = request.Headers["Authorization"] ?? "";
        byte[] expected = Encoding.UTF8.GetBytes("Bearer " + _configuration.ApiKey);
        byte[] actual = Encoding.UTF8.GetBytes(supplied);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static async Task Handle(HttpListenerContext context)
    {
        long started = Stopwatch.GetTimestamp();
        string route = context.Request.Url?.AbsolutePath ?? "/";
        var timing = new Dictionary<string, double>();
        try
        {
            if (context.Request.HttpMethod == "GET" && route == "/health")
            {
                await Respond(context.Response, 200, new { api_version = "1", bridge_version = Version, build_sha256 = _buildHash, session_id = SessionId,
                    status = "ok", state_version = Snapshots.Current?.Version, evidence_error = _journal?.LastError,
                    last_error = _lastError, control_revision = Interlocked.Read(ref _controlRevision),
                    receipt_count = Requests.Count, receipt_capacity = _configuration.ReceiptCapacity });
                return;
            }
            if (!Authenticated(context.Request)) throw new ApiFailure(401, "unauthorized", "Bearer API key required");
            object result;
            int status = 200;
            if (context.Request.HttpMethod == "GET" && route == "/v1/observe")
            {
                string detail = context.Request.QueryString["detail"] ?? "compact";
                if (detail is not ("compact" or "full")) throw new ApiFailure(400, "invalid_detail", "detail must be compact or full");
                long.TryParse(context.Request.QueryString["since_version"], out long since);
                int.TryParse(context.Request.QueryString["wait_ms"], out int wait);
                wait = Math.Clamp(wait, 0, 10000);
                long controlBefore = Interlocked.Read(ref _controlRevision);
                var snapshot = await TimedSnapshot(timing);
                while (since > 0 && snapshot.Version == since && Elapsed(started) < wait
                    && controlBefore == Interlocked.Read(ref _controlRevision))
                {
                    await Task.Delay(50, Stopping.Token);
                    snapshot = await TimedSnapshot(timing);
                }
                bool unchanged = since > 0 && snapshot.Version == since;
                if (!unchanged) _journal?.SaveState(snapshot.Version, snapshot.StateHash, snapshot.State);
                long projection = Stopwatch.GetTimestamp();
                var envelope = Envelope(snapshot, detail, null);
                if (unchanged) { envelope.Remove("state"); envelope.Remove("actions"); envelope["unchanged"] = true; }
                timing["state_projection_ms"] = Elapsed(projection);
                envelope["timing"] = FinishTiming("observe", timing, started);
                result = envelope;
            }
            else if (context.Request.HttpMethod == "POST" && route == "/v1/actions")
            {
                using var body = await ReadBody(context.Request);
                var root = body.RootElement;
                string requestId = RequiredString(root, "request_id");
                EnsureSession(root);
                if (requestId.Length > 128) throw new ApiFailure(400, "invalid_request_id", "request_id maximum 128 characters");
                _ = RequiredLong(root, "expected_version");
                _ = RequiredString(root, "action_id");
                _ = OptionalWait(root);
                ValidateDecision(root);
                string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.GetRawText())));
                RequestEntry entry;
                bool replay = false;
                lock (Requests.Sync)
                {
                    if (Requests.TryGet(requestId, out var previous))
                    {
                        if (previous.Fingerprint != fingerprint) throw new ApiFailure(409, "request_id_conflict", "request_id already used with different payload");
                        entry = previous;
                        replay = true;
                        Count("idempotent_replay");
                    }
                    else
                    {
                        var clone = root.Clone();
                        // Publish identity and settlement storage before a worker can
                        // enter the game. An observer may resolve it immediately.
                        entry = new(fingerprint, new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
                        // The oldest finished receipts are evicted when the store is full; only a store full of unfinished requests refuses.
                        if (Requests.TryAdd(requestId, entry) == ReceiptAdd.Full)
                            throw new ApiFailure(503, "receipt_capacity", "Receipt store is full of unfinished requests; no request was executed. Inspect diagnostics before continuing.");
                        entry.Work = Task.Run(() => ExecuteRequest(requestId, clone));
                        _ = entry.Work.ContinueWith(completed =>
                        {
                            if (completed.IsCompletedSuccessfully)
                                entry.Work = Task.FromResult(CompactReceipt(completed.Result));
                        }, TaskScheduler.Default);
                    }
                }
                try
                {
                    result = entry.Present(await entry.Work.WaitAsync(TimeSpan.FromSeconds(20), Stopping.Token));
                    if (replay) result = CompactReceipt(result);
                    var resultJson = JsonSerializer.SerializeToElement(result, _jsonOptions);
                    if (resultJson.TryGetProperty("status", out var resultStatus) && resultStatus.GetString() == "rejected") status = 409;
                }
                catch (TimeoutException)
                {
                    status = 202;
                    result = new { request_id = requestId, status = "pending", message = "Request is still running; query its receipt. Do not create a new request_id.",
                        receipt_url = $"/v1/requests/{Uri.EscapeDataString(requestId)}" };
                }
            }
            else if (context.Request.HttpMethod == "GET" && route.StartsWith("/v1/requests/", StringComparison.Ordinal))
            {
                string id = Uri.UnescapeDataString(route[13..]);
                if (!Requests.TryGet(id, out var entry)) throw new ApiFailure(404, "request_not_found", "Unknown request_id");
                if (!entry.Work.IsCompleted) { status = 202; result = new { request_id = id, status = "pending" }; }
                else result = entry.Present(await entry.Work);
            }
            else if (context.Request.HttpMethod == "GET" && route == "/v1/instance")
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                result = await RunOnMainThread(ReadInstanceIdentity, deadline.Token);
            }
            else if (context.Request.HttpMethod == "GET" && route == "/v1/diagnostics")
            {
                var snapshot = await TimedSnapshot(timing);
                result = BuildDiagnostics(snapshot);
            }
            else if (context.Request.HttpMethod == "POST" && route == "/v1/recovery")
            {
                using var body = await ReadBody(context.Request);
                EnsureSession(body.RootElement);
                string id = RequiredString(body.RootElement, "request_id");
                if (id.Length > 128 || RequiredString(body.RootElement, "mode") != "reconcile")
                    throw new ApiFailure(400, "invalid_recovery", "Only mode=reconcile is supported; no forced unlock or mutation retry exists");
                long expected = RequiredLong(body.RootElement, "expected_version");
                var snapshot = await TimedSnapshot(timing);
                if (snapshot.Version != expected) throw new ApiFailure(409, "stale_state", "Observe the latest state before reconciliation");
                var diagnostics = BuildDiagnostics(snapshot);
                string outcome = Volatile.Read(ref _pendingBeforeVersion) == 0 ? "reconciled"
                    : DiagnosticsStatus(snapshot).Status == "blocked" ? "blocked" : "waiting";
                _journal?.Record("recovery_reconcile", new { request_id = id, status = outcome, state_version = snapshot.Version, game_mutation = false });
                result = new { request_id = id, correlation_id = id, status = outcome, observation = Envelope(snapshot, "compact", null), diagnostics,
                    game_mutation = false, receipt_query_supported = false };
            }
            else if (context.Request.HttpMethod == "GET" && route is "/v1/knowledge/cards" or "/v1/knowledge/keywords")
            {
                string? ids = context.Request.QueryString["ids"];
                string? query = context.Request.QueryString["query"];
                if ((ids?.Length ?? 0) > 8192 || (query?.Length ?? 0) > 200 || (ids?.Split(',').Length ?? 0) > 100)
                    throw new ApiFailure(400, "query_too_large", "At most 100 IDs and a 200-character query are supported");
                int offset = QueryInteger(context.Request, "offset", 0, 0, int.MaxValue);
                int limit = QueryInteger(context.Request, "limit", 50, 1, 100);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                long queued = Stopwatch.GetTimestamp();
                result = await RunOnMainThread(() =>
                {
                    timing["queue_wait_ms"] = Elapsed(queued);
                    long build = Stopwatch.GetTimestamp();
                    var data = (Dictionary<string, object?>)(route == "/v1/knowledge/cards"
                        ? BuildCardKnowledge(ids, query, offset, limit) : BuildKeywordKnowledge(ids));
                    timing["knowledge_query_ms"] = Elapsed(build);
                    data["timing"] = FinishTiming("knowledge", timing, started);
                    return data;
                }, deadline.Token);
            }
            else if (context.Request.HttpMethod == "POST" && route == "/v1/decisions")
            {
                using var body = await ReadBody(context.Request);
                EnsureSession(body.RootElement);
                ValidateDecision(body.RootElement, isStandalone: true);
                long version = RequiredLong(body.RootElement, "state_version");
                var snapshot = await TimedSnapshot(timing);
                if (version != snapshot.Version) throw new ApiFailure(409, "stale_state", "Decision references an outdated state; no action was executed");
                _journal?.SaveState(snapshot.Version, snapshot.StateHash, snapshot.State);
                _journal?.Record("decision", new { state_version = version, payload = body.RootElement.Clone(), source = "client_reported" });
                result = new { status = "recorded", state_version = version };
            }
            else if (context.Request.HttpMethod == "POST" && route == "/v1/captures")
            {
                using var body = await ReadBody(context.Request);
                string reason = RequiredString(body.RootElement, "reason");
                if (body.RootElement.TryGetProperty("expected_version", out _)) EnsureSession(body.RootElement);
                var snapshot = await TimedSnapshot(timing);
                if (body.RootElement.TryGetProperty("expected_version", out _) && RequiredLong(body.RootElement, "expected_version") != snapshot.Version)
                    throw new ApiFailure(409, "stale_state", "Capture state is outdated");
                result = await Capture(snapshot, reason, "manual");
            }
            else if (context.Request.HttpMethod == "GET" && route == "/v1/metrics") result = MetricsReport();
            else if (context.Request.HttpMethod == "GET" && route == "/v1/events")
            {
                int.TryParse(context.Request.QueryString["limit"], out int limit);
                limit = Math.Clamp(limit == 0 ? 100 : limit, 1, 500);
                string file = Path.Combine(_journal!.SessionDirectory, "events.jsonl");
                string[] lines = [];
                if (File.Exists(file))
                {
                    using var stream = new FileStream(file, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var tail = new Queue<string>();
                    string? line;
                    while ((line = reader.ReadLine()) != null) { tail.Enqueue(line); if (tail.Count > limit) tail.Dequeue(); }
                    lines = tail.ToArray();
                }
                var events = new List<JsonElement>();
                for (int i = 0; i < lines.Length; i++)
                {
                    try { events.Add(JsonSerializer.Deserialize<JsonElement>(lines[i])); }
                    catch (JsonException) when (i == lines.Length - 1) { /* writer may still be completing its last line */ }
                }
                result = new { session_id = SessionId, events, evidence_error = _journal.LastError };
            }
            else if (context.Request.HttpMethod == "GET" && route == "/v1/capabilities")
                result = new { api_version = "1", bridge_version = Version, transport = "direct_http", singleplayer_only = true,
                    observation_details = new[] { "compact", "full" }, guarded_actions = true, idempotency = "session_scoped_identical_payload_oldest_finished_evicted_when_full",
                    receipt_capacity = _configuration.ReceiptCapacity, knowledge = new[] { "all_registered_cards", "base_and_upgraded_variants", "keywords_and_powers" },
                    diagnostics = true, recovery = "reconcile_only_no_force_unlock", ai_importance = "client_reported_optional_local_capture",
                    state_hash = "opaque_server_generated_sha256", legal_actions = "current_snapshot_candidates_rechecked_by_game",
                    captures = "local_game_viewport_png_not_sent_to_model", model_decision_time = "client_reported_only",
                    unsupported = new[] { "multiplayer", "full_card_effect_simulation", "save_editing", "automatic_jev_decisions" },
                    endpoints = new[] { "/v1/observe", "/v1/actions", "/v1/requests/{request_id}", "/v1/decisions", "/v1/captures", "/v1/metrics", "/v1/events", "/v1/knowledge/cards", "/v1/knowledge/keywords", "/v1/diagnostics", "/v1/recovery" } };
            else throw new ApiFailure(404, "not_found", "Unknown route or method");
            await Respond(context.Response, status, result, route);
        }
        catch (ApiFailure ex)
        {
            Count(ex.Code);
            await TryError(context.Response, ex.Status, ex.Code, ex.Message);
        }
        catch (JsonException ex) { await TryError(context.Response, 400, "invalid_json", ex.Message); }
        catch (TimeoutException)
        {
            await TryError(context.Response, route == "/v1/captures" ? 504 : 503,
                route == "/v1/captures" ? "capture_timeout" : "main_thread_timeout", "Timed out waiting for the game; no mutation is retried");
        }
        catch (OperationCanceledException)
        {
            await TryError(context.Response, 503, Stopping.IsCancellationRequested ? "stopping" : "request_cancelled",
                Stopping.IsCancellationRequested ? "Bridge is stopping" : "Request was cancelled");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _journal?.Record("http_error", new { route, error_type = ex.GetType().Name, message = ex.Message });
            await TryError(context.Response, 500, "internal_error", ex.Message);
        }
    }

    private static async Task<object> ExecuteRequest(string id, JsonElement request)
    {
        long started = Stopwatch.GetTimestamp();
        var timing = new Dictionary<string, double>();
        int maxWait = OptionalWait(request);
        await MutationGate.WaitAsync(Stopping.Token);
        timing["mutation_queue_wait_ms"] = Elapsed(started);
        try
        {
            long expected = RequiredLong(request, "expected_version");
            string actionId = RequiredString(request, "action_id");
            AgentSnapshot? before = null;
            object? receipt = null;
            long queued = Stopwatch.GetTimestamp();
            using var dispatchDeadline = CancellationTokenSource.CreateLinkedTokenSource(Stopping.Token);
            dispatchDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            try { await RunOnMainThread(() =>
            {
                timing["queue_wait_ms"] = Elapsed(queued);
                long build = Stopwatch.GetTimestamp();
                before = SampleState();
                timing["state_build_ms"] = Elapsed(build);
                if (_pendingBeforeVersion != 0) throw new ApiFailure(409, "action_pending", "Previous mutation has not reached a changed, ready state. Observe; do not queue another action.");
                if (before.Version != expected) throw new ApiFailure(409, "stale_state", "State changed; observe again before deciding");
                var action = before.Actions.FirstOrDefault(a => a.ActionId == actionId)
                    ?? throw new ApiFailure(409, "action_unavailable", "Action is no longer available in this state");
                if (!before.Ready) throw new ApiFailure(409, "not_ready", "Game is not ready for an action");
                _journal?.SaveState(before.Version, before.StateHash, before.State);
                _journal?.Record("action_requested", new { request_id = id, state_version = before.Version, action,
                    decision = request.TryGetProperty("decision", out var decision) ? decision.Clone() : (JsonElement?)null,
                    decision_timing_source = "client_reported_if_present" });
                // Install the guard before entering any game API. A partially successful
                // dispatch that throws must remain unresolved rather than allowing a retry.
                _pendingBeforeVersion = before.Version;
                _pendingLastVersion = before.Version;
                _pendingStableSince = Stopwatch.GetTimestamp();
                _pendingStarted = _pendingStableSince;
                _pendingRequestId = id;
                _pendingAction = action;
                _pendingSnapshot = before;
                _pendingDecision = request.TryGetProperty("decision", out var annotation) ? annotation.Clone() : null;
                Interlocked.Increment(ref _controlRevision);
                long dispatch = Stopwatch.GetTimestamp();
                Trace("exec_start " + action.Action + " " + System.Text.Json.JsonSerializer.Serialize(action.Arguments));
                receipt = ExecuteAgentAction(action);
                Trace("exec_end " + action.Action);
                timing["action_dispatch_ms"] = Elapsed(dispatch);
                var dispatchResult = JsonSerializer.SerializeToElement(receipt, _jsonOptions);
                if (dispatchResult.TryGetProperty("status", out var dispatchStatus) && dispatchStatus.GetString() == "error")
                {
                    // Handler validation errors are returned before the mutating call;
                    // exceptions after entering an API are caught outside and keep the guard.
                    ClearPending();
                }
            }, dispatchDeadline.Token); }
            catch (OperationCanceledException) when (!Stopping.IsCancellationRequested)
            {
                throw new ApiFailure(503, "dispatch_not_started", "Queued operation was cancelled before dispatch; this request performed no game mutation");
            }
            var result = JsonSerializer.SerializeToElement(receipt, _jsonOptions);
            if (result.TryGetProperty("status", out var outcome) && outcome.GetString() == "error")
            {
                _journal?.Record("action_rejected", new { request_id = id, result });
                return new { request_id = id, status = "rejected", result,
                    observation = Envelope(before!, "compact", null), timing = FinishTiming("action", timing, started) };
            }
            long settleStarted = Stopwatch.GetTimestamp();
            AgentSnapshot after = before!;
            long stableSince = Stopwatch.GetTimestamp();
            long lastVersion = before!.Version;
            bool settled = false;
            while (Elapsed(settleStarted) < maxWait)
            {
                await Task.Delay(30, Stopping.Token);
                after = await TimedSnapshot(new Dictionary<string, double>());
                if (after.Version != lastVersion) { lastVersion = after.Version; stableSince = Stopwatch.GetTimestamp(); }
                if (after.Version != before.Version && after.Ready && Volatile.Read(ref _pendingBeforeVersion) == 0
                    && Elapsed(stableSince) >= _configuration.SettleStableMs)
                { settled = true; break; }
            }
            timing["settle_wait_ms"] = Elapsed(settleStarted);
            _journal?.SaveState(after.Version, after.StateHash, after.State);
            var response = new { request_id = id, status = settled ? "completed" : "accepted", result,
                settled, settle_policy = "changed_state_interaction_ready_and_stable_window_not_animation_signal",
                observation = Envelope(after, "compact", null), timing = FinishTiming("action", timing, started) };
            _journal?.Record("action_result", response);
            Count(settled ? "action_completed" : "action_accepted_not_settled");
            return response;
        }
        catch (ApiFailure ex)
        {
            Count(ex.Code);
            _journal?.Record("action_guard_rejected", new { request_id = id, code = ex.Code, message = ex.Message });
            return new { request_id = id, status = "rejected", error = new { code = ex.Code, message = ex.Message },
                timing = FinishTiming("action", timing, started) };
        }
        catch (Exception ex)
        {
            _journal?.Record("action_outcome_unknown", new { request_id = id, error_type = ex.GetType().Name, message = ex.Message });
            return new { request_id = id, status = "unknown", error = new { code = "outcome_unknown", message = ex.Message },
                message = "Do not repeat this mutation with a new request_id. Inspect diagnostics, game state and this receipt.", timing = FinishTiming("action", timing, started) };
        }
        finally { MutationGate.Release(); }
    }

    private static Dictionary<string, object?> Envelope(AgentSnapshot snapshot, string detail, object? timing)
    {
        var value = JsonSerializer.SerializeToElement(AgentProtocol.Observation(snapshot, detail), _jsonOptions);
        var envelope = JsonSerializer.Deserialize<Dictionary<string, object?>>(value.GetRawText())!;
        envelope["session_id"] = SessionId;
        if (timing != null) envelope["timing"] = timing;
        envelope["evidence_error"] = _journal?.LastError;
        envelope["evidence_persistence"] = "queued_background_writer";
        bool pending = Volatile.Read(ref _pendingBeforeVersion) != 0;
        envelope["pending_mutation"] = pending;
        envelope["control_revision"] = Interlocked.Read(ref _controlRevision);
        var status = DiagnosticsStatus(snapshot);
        envelope["interaction_status"] = status.Status;
        envelope["reason"] = status.Reason;
        if (pending) envelope["pending_request_id"] = _pendingRequestId;
        if (pending) { envelope["ready"] = false; envelope["actions"] = Array.Empty<object>(); }
        return envelope;
    }

    private static bool ReachedPostcondition(AgentAction? action, AgentSnapshot? before, AgentSnapshot after)
    {
        if (action == null || before == null) return false;
        return ActionPostconditions.Reached(action, before, after);
    }

    private static void ClearPending()
    {
        _pendingBeforeVersion = 0;
        _pendingAction = null;
        _pendingSnapshot = null;
        _pendingRequestId = null;
        _pendingDecision = null;
        Interlocked.Increment(ref _controlRevision);
    }

    private static void ResolvePending(AgentSnapshot snapshot)
    {
        string? id = _pendingRequestId;
        long before = _pendingBeforeVersion;
        var decision = _pendingDecision;
        if (id != null && Requests.TryGet(id, out var entry))
            entry.Resolution.MarkSettled(snapshot.Version, snapshot.StateHash, DateTimeOffset.UtcNow);
        _journal?.SaveState(snapshot.Version, snapshot.StateHash, snapshot.State);
        _journal?.Record("action_settled", new { request_id = id, before_state_version = before, after_state_version = snapshot.Version,
            after_state_hash = snapshot.StateHash, completion_basis = "changed_state_ready_and_action_postcondition_observed" });
        ClearPending();
        if (decision is { } d && d.TryGetProperty("importance", out var importance)
            && importance.TryGetProperty("capture", out var capture) && capture.ValueKind == JsonValueKind.True)
        {
            string reason = importance.TryGetProperty("reason", out var why) ? why.GetString() ?? "AI marked decision" : "AI marked decision";
            _journal?.Record("decision_highlight", new { request_id = id, before_state_version = before, after_state_version = snapshot.Version,
                importance, source = "client_reported" });
            _ = AutoCapture(snapshot, reason, "ai_decision");
        }
    }

    private static (string Status, string Reason) DiagnosticsStatus(AgentSnapshot snapshot)
    {
        if (Volatile.Read(ref _pendingBeforeVersion) != 0)
            return Elapsed(_pendingStarted) >= 15000 && Elapsed(_lastInteractionChange) >= 5000
                ? ("blocked", "action_postcondition_not_observed") : ("waiting", "action_in_flight");
        if (snapshot.StateType == "game_over") return ("ended", "run_ended");
        if (snapshot.StateType is "unsupported" or "unknown" or "overlay")
            return ("unsupported", AgentProtocol.String(snapshot.State, "reason") ?? "screen_not_supported");
        if (snapshot.Ready) return ("ready", "action_available");
        if (Elapsed(_lastInteractionChange) < 15000)
            return ("waiting", AgentProtocol.String(snapshot.State, "reason") ?? "game_transition_or_animation");
        return ("blocked", snapshot.StateType == "transition" ? "transition_stalled" : "stable_screen_without_supported_actions");
    }

    private static object BuildDiagnostics(AgentSnapshot snapshot)
    {
        var status = DiagnosticsStatus(snapshot);
        bool pending = Volatile.Read(ref _pendingBeforeVersion) != 0;
        return new { session_id = SessionId, state_version = snapshot.Version, control_revision = Interlocked.Read(ref _controlRevision),
            ready = snapshot.Ready && !pending, state_type = snapshot.StateType, status = status.Status, reason = status.Reason,
            supported_action_count = pending ? 0 : snapshot.Actions.Count, unchanged_for_ms = Elapsed(_lastInteractionChange),
            pending_request = !pending ? null : new { request_id = _pendingRequestId, action = _pendingAction?.Action,
                before_state_version = _pendingBeforeVersion, age_ms = Elapsed(_pendingStarted),
                postcondition = "changed_state_ready_and_action_specific_evidence", mutation_retry_allowed = false },
            recovery = new { can_reconcile = true, can_force_unlock = false,
                suggested_action = status.Status is "blocked" or "unsupported" ? "save_evidence_and_inspect" : pending ? "wait_observe_and_receipt" : "observe" },
            receipts = new { count = Requests.Count, capacity = _configuration.ReceiptCapacity, remaining = _configuration.ReceiptCapacity - Requests.Count,
                near_capacity = Requests.Count >= _configuration.ReceiptCapacity * 0.9, eviction = "oldest_finished_when_full", evicted = Requests.Evicted },
            last_error = _lastError, evidence_error = _journal?.LastError,
            rule = "Reconciliation only observes evidence; it never clicks, retries, or unlocks an unresolved game operation." };
    }

    private static object CompactReceipt(object full)
    {
        var json = JsonSerializer.SerializeToElement(full, _jsonOptions);
        var compact = new Dictionary<string, object?>();
        foreach (var property in json.EnumerateObject())
        {
            if (property.Name == "observation")
            {
                var observation = property.Value;
                compact["observation_ref"] = new
                {
                    session_id = SessionId,
                    state_version = observation.TryGetProperty("state_version", out var v) ? v.GetInt64() : (long?)null,
                    state_hash = observation.TryGetProperty("state_hash", out var h) ? h.GetString() : null
                };
            }
            else compact[property.Name] = JsonSerializer.Deserialize<JsonElement>(property.Value.GetRawText());
        }
        compact["receipt_replay"] = true;
        return compact;
    }

    private static void ObserveMajorEvents(AgentSnapshot snapshot)
    {
        var state = snapshot.State;
        string type = snapshot.StateType;
        string run = state.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("run_id", out var rid) ? rid.ToString() : "";
        string epoch = ctx.ValueKind == JsonValueKind.Object && ctx.TryGetProperty("combat_epoch", out var ce) ? ce.ToString() : "";
        string floor = state.TryGetProperty("run", out var rs) && rs.TryGetProperty("floor", out var fl) ? fl.ToString() : "";
        string key = $"{run}:{floor}:{type}:{epoch}";
        if (!string.IsNullOrEmpty(run) && run != _journalRunId && snapshot.Ready)
        {
            _journalRunId = run;
            _journal?.SaveState(snapshot.Version, snapshot.StateHash, state);
            string runEvent = "run_" + _runObservationOrigin;
            _journal?.Record(runEvent, new { run_id = run, state_version = snapshot.Version,
                run = state.TryGetProperty("run", out var runData) ? runData : (JsonElement?)null });
            if (_configuration.AutoCapture) _ = AutoCapture(snapshot, runEvent);
        }
        ObserveEventRoom(snapshot, run, floor);
        bool combatType = type is "monster" or "elite" or "boss";
        if ((type is "monster" or "elite" or "boss" or "game_over" or "rewards") && snapshot.Ready
            && (!combatType || !string.IsNullOrEmpty(epoch)) && MajorEvents.Add(key))
        {
            string? major = type switch { "monster" => "combat_started", "elite" => "elite_started", "boss" => "boss_started", "game_over" => "run_ended",
                "rewards" => "combat_completed", _ => null };
            if (major != null)
            {
                _journal?.SaveState(snapshot.Version, snapshot.StateHash, state);
                _journal?.Record(major, new { state_version = snapshot.Version, state_type = type, run_id = run, combat_epoch = epoch, floor });
                if (_configuration.AutoCapture && type != "monster") _ = AutoCapture(snapshot, major);
            }
        }
        if (state.TryGetProperty("player", out var player) && player.TryGetProperty("hp", out var hp)
            && player.TryGetProperty("max_hp", out var max))
        {
            bool low = hp.GetInt32() > 0 && hp.GetInt32() <= max.GetInt32() * 0.2;
            if (low && !_wasLowHp && _configuration.AutoCapture) _ = AutoCapture(snapshot, "hp_critical");
            _wasLowHp = low;
        }
    }

    private static async Task AutoCapture(AgentSnapshot snapshot, string reason, string source = "automatic")
    {
        try { await Capture(snapshot, reason, source); }
        catch (Exception ex) { _journal?.Record("capture_failed", new { reason, state_version = snapshot.Version, error = ex.Message }); }
    }

    private static async Task<object> Capture(AgentSnapshot trigger, string reason, string source)
    {
        if (reason.Length > 500) throw new ApiFailure(400, "invalid_reason", "Capture reason maximum 500 characters");
        await CaptureGate.WaitAsync(Stopping.Token);
        RunCaptureBudget.Lease? lease = null;
        string? path = null;
        bool retained = false;
        PublishedCaptureFile? ownedFile = null;
        try
        {
            var current = await TimedSnapshot(new Dictionary<string, double>());
            if (source == "manual" && trigger.Version != current.Version)
                throw new ApiFailure(409, "stale_state", "State changed while waiting for capture; observe and request a new capture");
            if (!CaptureBudget.TryReserve(trigger.State, _configuration.MaxCaptures, out lease, out string error))
                throw new ApiFailure(error == "capture_limit" ? 429 : 409, error,
                    error == "capture_limit" ? "Capture limit reached for this run (or the session-wide no-run scope)"
                        : "Capture run identity changed or is unavailable; observe and request a new capture");
            string directory = Path.Combine(_journal!.SessionDirectory, "screenshots");
            Directory.CreateDirectory(directory);
            // Sequence is session-wide; changing runs never reuses a filename.
            path = Path.Combine(directory, $"{SessionId}-{lease!.Sequence:D6}-v{trigger.Version}.png");
            if (File.Exists(path)) throw new ApiFailure(409, "capture_path_exists", "Capture path already exists; existing evidence was preserved");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stopping.Token, lease.Token);
            var capture = await BridgeCapture.CaptureAsync(path, reason, current.Version, timeout.Token,
                published => ownedFile = published);
            await TimedSnapshot(new Dictionary<string, double>());
            if (!CaptureBudget.IsCurrent(lease))
                throw new ApiFailure(409, "stale_capture_run", "Run identity changed during capture; capture result rejected");
            retained = true;
            var result = new { status = "captured", trigger_state_version = trigger.Version, capture_state_version = current.Version,
                capture_scope = lease.Scope.RunId == null ? "no_run" : "run", capture_run_id = lease.Scope.RunId,
                session_id = SessionId, source, capture, frame_policy = "next_frame_post_draw_not_atomic_with_trigger_state",
                evidence_persistence = "png_written_audit_and_state_queued", sent_to_model = false };
            _journal.SaveState(current.Version, current.StateHash, current.State);
            _journal.Record("screenshot", result);
            return result;
        }
        catch (OperationCanceledException) when (!Stopping.IsCancellationRequested && lease != null && lease.Token.IsCancellationRequested)
        { throw new ApiFailure(409, "stale_capture_run", "Run identity changed during capture; capture result rejected"); }
        finally
        {
            try
            {
                if (lease != null)
                {
                    try
                    {
                        if (!retained && ownedFile != null)
                        {
                            try { ownedFile.Delete(); }
                            catch (Exception ex)
                            {
                                // An undeletable late image still consumes only its original quota.
                                retained = true;
                                _journal?.Record("capture_discard_failed", new { path, capture_run_id = lease.Scope.RunId, error = ex.Message });
                            }
                        }
                    }
                    finally { CaptureBudget.Complete(lease, retained); }
                }
            }
            finally { CaptureGate.Release(); }
        }
    }

    private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    private static void Count(string key) => Counters.AddOrUpdate(key, 1, (_, value) => value + 1);
    private static object FinishTiming(string route, Dictionary<string, double> timing, long started)
    {
        timing["server_processing_ms"] = Elapsed(started);
        var copy = new Dictionary<string, double>(timing);
        var samples = Metrics.GetOrAdd(route, _ => new());
        samples.Enqueue(copy);
        while (samples.Count > 1000) samples.TryDequeue(out _);
        return copy;
    }
    private static object MetricsReport()
    {
        var report = new Dictionary<string, object?>();
        foreach (var (route, queue) in Metrics)
        {
            var samples = queue.ToArray();
            var fields = new Dictionary<string, object>();
            foreach (string field in samples.SelectMany(x => x.Keys).Distinct())
            {
                var values = samples.Where(x => x.ContainsKey(field)).Select(x => x[field]).Order().ToArray();
                fields[field] = new { count = values.Length, mean_ms = values.Average(), p50_ms = values[(values.Length - 1) / 2],
                    p95_ms = values[(int)Math.Ceiling(values.Length * 0.95) - 1], max_ms = values[^1] };
            }
            report[route] = fields;
        }
        return new { session_id = SessionId, routes = report, counters = Counters.ToArray().ToDictionary(x => x.Key, x => x.Value),
            evidence_error = _journal?.LastError, notes = new[] { "Server processing excludes network transfer and external model/tool delay.",
                "Settle is a measured readiness/stability heuristic, not a renderer animation-complete signal.",
                "decision_duration_ms, if supplied, is client-reported; the Mod cannot measure model reasoning internally." } };
    }
    private static async Task<JsonDocument> ReadBody(HttpListenerRequest request)
    {
        const int max = 65536;
        if (request.ContentLength64 > max) throw new ApiFailure(413, "body_too_large", "Maximum body is 64 KiB");
        using var stream = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer, Stopping.Token)) > 0)
        {
            if (stream.Length + read > max) throw new ApiFailure(413, "body_too_large", "Maximum body is 64 KiB");
            stream.Write(buffer, 0, read);
        }
        var document = JsonDocument.Parse(stream.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new ApiFailure(400, "invalid_body", "JSON object required"); }
        return document;
    }
    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ApiFailure(400, "missing_field", $"Nonempty string '{name}' required");
        return value.GetString()!;
    }
    private static long RequiredLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number) || number < 1)
            throw new ApiFailure(400, "missing_field", $"Positive integer '{name}' required");
        return number;
    }
    private static void EnsureSession(JsonElement root)
    {
        if (RequiredString(root, "expected_session_id") != SessionId)
            throw new ApiFailure(409, "stale_session", "Bridge session changed; discard old references and observe again");
    }
    private static int OptionalWait(JsonElement root)
    {
        if (!root.TryGetProperty("wait_timeout_ms", out var value)) return 5000;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int wait) || wait < 0 || wait > 15000)
            throw new ApiFailure(400, "invalid_field", "wait_timeout_ms must be an integer between 0 and 15000");
        return wait;
    }
    private static void ValidateDecision(JsonElement root, bool isStandalone = false)
    {
        JsonElement decision;
        if (isStandalone) decision = root;
        else if (!root.TryGetProperty("decision", out decision)) return;
        string? error = DecisionMetadata.Validate(decision);
        if (error != null) throw new ApiFailure(400, "invalid_decision", error);
    }
    private static int QueryInteger(HttpListenerRequest request, string name, int defaultValue, int minimum, int maximum)
    {
        string? value = request.QueryString[name];
        if (value == null) return defaultValue;
        if (!int.TryParse(value, out int number) || number < minimum || number > maximum)
            throw new ApiFailure(400, "invalid_query", $"{name} must be an integer between {minimum} and {maximum}");
        return number;
    }
    private static async Task Respond(HttpListenerResponse response, int status, object result, string route = "other")
    {
        long serializeStart = Stopwatch.GetTimestamp();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, _jsonOptions);
        double serializeMs = Elapsed(serializeStart);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Bridge-Serialize-Ms"] = serializeMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        response.ContentLength64 = bytes.Length;
        long writeStart = Stopwatch.GetTimestamp();
        try { await response.OutputStream.WriteAsync(bytes); } finally { response.Close(); }
        var samples = Metrics.GetOrAdd("transport:" + route, _ => new());
        samples.Enqueue(new() { ["response_serialization_ms"] = serializeMs, ["response_write_ms"] = Elapsed(writeStart) });
        while (samples.Count > 1000) samples.TryDequeue(out _);
    }
    private static async Task TryError(HttpListenerResponse response, int status, string code, string message)
    {
        try { await Respond(response, status, new { status = "error", error = new { code, message }, session_id = SessionId }); }
        catch { }
    }
}
