using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Scheduler;

public sealed class InputCoordinator : IAsyncDisposable
{
    private sealed class Session
    {
        public object Sync { get; } = new();
        public string Id { get; init; } = string.Empty;
        public byte[] TokenHash { get; init; } = Array.Empty<byte>();
        public TargetBinding Target { get; init; } = new();
        public int LeaseTimeoutMs { get; init; }
        public IReadOnlyList<string> AllowedProcesses { get; init; } = Array.Empty<string>();
        public int? ClientProcessId { get; init; }
        public long? ClientStartTimeUtcTicks { get; init; }
        public bool Armed { get; set; }
        public bool Paused { get; set; }
        public string? PauseReason { get; set; }
        public long? ArmedUntilUs { get; set; }
        public long LastCommandSequence { get; set; } = -1;
        public long LastRealtimeSequence { get; set; } = -1;
        public long LastRealtimeReceivedUs { get; set; }
        public bool RealtimeActive { get; set; }
        public string? RealtimeConnectionId { get; set; }
        public CancellationTokenSource? ArmLifetime { get; set; }
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _ownershipSync = new();
    private readonly InputScheduler _scheduler;
    private readonly ITargetGuard _targetGuard;
    private readonly IMonotonicClock _clock;
    private readonly ITrajectorySink _trajectory;
    private string? _armedSessionId;
    private bool _disposed;

    public InputCoordinator(InputScheduler scheduler, ITargetGuard targetGuard, IMonotonicClock clock, ITrajectorySink trajectory)
    {
        _scheduler = scheduler;
        _targetGuard = targetGuard;
        _clock = clock;
        _trajectory = trajectory;
    }

    public string BackendName => _scheduler.BackendName;
    public long MonotonicMicroseconds => _clock.NowMicroseconds;
    public InputSchedulerSnapshot SchedulerSnapshot => _scheduler.Snapshot;

    public Task<CreateSessionResponse> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.LeaseTimeoutMs is < 50 or > 10_000)
            throw new InputExecutorException("INVALID_LEASE_TIMEOUT", "lease_timeout_ms must be between 50 and 10000.");
        if (request.Target is null && request.TargetProcessId is null && !request.BindToForeground)
            throw new InputExecutorException("TARGET_REQUIRED", "Provide target, target_process_id, or set bind_to_foreground to true.");
        if (request.AllowedProcesses.Count > 32)
            throw new InputExecutorException("PROCESS_ALLOWLIST_TOO_LARGE", "At most 32 process names may be allowlisted.");

        if (request.Target is not null && request.TargetProcessId is not null)
            throw new InputExecutorException("AMBIGUOUS_TARGET", "Provide either target or target_process_id, not both.");
        var target = request.Target ?? (request.TargetProcessId is not null
            ? _targetGuard.CaptureProcess(request.TargetProcessId.Value)
            : _targetGuard.CaptureForeground());
        EnsureTargetValid(target, requireForeground: false);
        var targetName = NormalizeProcessName(target.ProcessName);
        var allowed = request.AllowedProcesses.Count == 0
            ? new[] { targetName }
            : request.AllowedProcesses.Select(NormalizeProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!allowed.Contains(targetName, StringComparer.OrdinalIgnoreCase))
            throw new InputExecutorException("TARGET_NOT_ALLOWLISTED", $"Target process '{targetName}' is not in allowed_processes.", 403);

        long? clientStart = null;
        if (request.ClientProcessId is not null)
        {
            if (request.ClientProcessId <= 0) throw new InputExecutorException("INVALID_CLIENT_PROCESS", "client_process_id must be positive.");
            try
            {
                using var client = Process.GetProcessById(request.ClientProcessId.Value);
                clientStart = client.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new InputExecutorException("CLIENT_PROCESS_NOT_FOUND", "client_process_id does not identify a running process.", 409);
            }
        }

        var id = Guid.NewGuid().ToString("N");
        var token = CreateToken();
        var session = new Session
        {
            Id = id,
            TokenHash = HashToken(token),
            Target = target,
            LeaseTimeoutMs = request.LeaseTimeoutMs,
            AllowedProcesses = allowed,
            ClientProcessId = request.ClientProcessId,
            ClientStartTimeUtcTicks = clientStart
        };
        if (!_sessions.TryAdd(id, session)) throw new InputExecutorException("SESSION_CREATE_FAILED", "Could not allocate a unique session id.", 500);
        return Task.FromResult(new CreateSessionResponse { SessionId = id, CapabilityToken = token, Status = ToStatus(session) });
    }

    public Task<SessionStatus> GetStatusAsync(string sessionId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToStatus(Authenticate(sessionId, token)));
    }

    public Task<TargetBinding> GetObservationTargetAsync(string sessionId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = Authenticate(sessionId, token);
        EnsureTargetAndClientValid(session, requireForeground: false);
        return Task.FromResult(session.Target);
    }

    public Task<SessionStatus> FocusTargetAsync(string sessionId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = Authenticate(sessionId, token);
        EnsureTargetAndClientValid(session, requireForeground: false);
        if (!_targetGuard.TryActivate(session.Target))
            throw new InputExecutorException("TARGET_ACTIVATION_FAILED", "Windows did not allow the bound target window to become foreground.", 409);
        EnsureTargetAndClientValid(session, requireForeground: true);
        return Task.FromResult(ToStatus(session));
    }

    public async Task<SessionStatus> ArmAsync(string sessionId, string token, ArmRequest request, CancellationToken cancellationToken)
    {
        if (request.DurationSeconds is < 1 or > 3_600)
            throw new InputExecutorException("INVALID_ARM_DURATION", "duration_seconds must be between 1 and 3600.");
        var session = Authenticate(sessionId, token);
        EnsureTargetAndClientValid(session, requireForeground: true);
        var resetScheduler = false;
        lock (_ownershipSync)
        {
            if (_armedSessionId is not null && _armedSessionId != session.Id)
                throw new InputExecutorException("INPUT_BUSY", "Another session owns the global input lease.", 409);
            lock (session.Sync)
            {
                if (!session.Armed)
                {
                    session.ArmLifetime?.Cancel();
                    session.ArmLifetime?.Dispose();
                    session.ArmLifetime = new CancellationTokenSource();
                    session.Armed = true;
                    session.RealtimeActive = false;
                    session.LastRealtimeReceivedUs = 0;
                    resetScheduler = true;
                }
                session.Paused = false;
                session.PauseReason = null;
                session.ArmedUntilUs = checked(_clock.NowMicroseconds + request.DurationSeconds * 1_000_000L);
                _armedSessionId = session.Id;
            }
        }
        // A newly acquired arm lease starts from a known empty state. Re-arming only extends the lease.
        if (resetScheduler)
        {
            try { await _scheduler.ReleaseAllAndClearAsync("arm_reset", cancellationToken); }
            catch
            {
                try { await SafetyStopAsync(session, "arm_reset_failed", paused: true, CancellationToken.None); } catch { }
                throw;
            }
        }
        return ToStatus(session);
    }

    public async Task<SessionStatus> DisarmAsync(string sessionId, string token, CancellationToken cancellationToken)
    {
        var session = Authenticate(sessionId, token);
        await SafetyStopAsync(session, "disarmed", paused: false, cancellationToken);
        return ToStatus(session);
    }

    public async Task<ExecutionResult> ExecuteCommandAsync(string sessionId, string token, CommandRequest request, CancellationToken cancellationToken)
    {
        if (request.Version != "1.0") throw new InputExecutorException("UNSUPPORTED_VERSION", "Only protocol version 1.0 is supported.");
        if (request.Sequence < 0) throw new InputExecutorException("INVALID_SEQUENCE", "sequence must be non-negative.");
        var receivedAt = _clock.NowMicroseconds;
        if (request.DeadlineUs is not null && receivedAt > request.DeadlineUs) return Dropped(request.Sequence, "expired");
        var session = Authenticate(sessionId, token);
        Task<ExecutionResult> scheduled;
        CancellationToken armToken;
        lock (session.Sync)
        {
            EnsureReadyLocked(session);
            if (session.RealtimeConnectionId is not null)
                throw new InputExecutorException("REALTIME_ACTIVE", "Command input is disabled while this session owns a realtime WebSocket.", 409);
            if (request.Sequence <= session.LastCommandSequence) return Dropped(request.Sequence, "stale");
            session.LastCommandSequence = request.Sequence;
            armToken = session.ArmLifetime!.Token;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, armToken);
            scheduled = _scheduler.EnqueueCommandAsync(request, () => EnsureTargetAndClientValid(session, true), linked.Token);
            _ = scheduled.ContinueWith(_ => linked.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        try
        {
            var result = await scheduled;
            await RecordAsync(session, "command", request.Sequence, receivedAt, result.MonotonicUs, request, result.Events, result.Status, CancellationToken.None);
            return result;
        }
        catch
        {
            await SafetyStopAsync(session, "command_failed_or_disconnected", paused: true, CancellationToken.None);
            throw;
        }
    }

    public string OpenRealtimeConnection(string sessionId, string token)
    {
        var session = Authenticate(sessionId, token);
        lock (session.Sync)
        {
            EnsureReadyLocked(session);
            EnsureTargetAndClientValid(session, true);
            var scheduler = _scheduler.Snapshot;
            if (scheduler.PendingCommands != 0 || scheduler.ActiveWorkKind == "command")
                throw new InputExecutorException("COMMAND_ACTIVE", "Wait for queued Command API work before opening realtime control.", 409);
            if (session.RealtimeConnectionId is not null)
                throw new InputExecutorException("REALTIME_ALREADY_CONNECTED", "This session already has a realtime connection.", 409);
            session.RealtimeConnectionId = Guid.NewGuid().ToString("N");
            session.RealtimeActive = true;
            session.LastRealtimeReceivedUs = _clock.NowMicroseconds;
            return session.RealtimeConnectionId;
        }
    }

    public Task<ExecutionResult> SubmitRealtimeFrameAsync(string sessionId, string token, string connectionId, RealtimeFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Version != "1.0") throw new InputExecutorException("UNSUPPORTED_VERSION", "Only protocol version 1.0 is supported.");
        ValidateRealtime(frame);
        var session = Authenticate(sessionId, token);
        var now = _clock.NowMicroseconds;
        if (frame.DeadlineUs is not null && now > frame.DeadlineUs) return Task.FromResult(Dropped(frame.Sequence, "expired"));
        lock (session.Sync)
        {
            EnsureReadyLocked(session);
            if (!string.Equals(session.RealtimeConnectionId, connectionId, StringComparison.Ordinal))
                throw new InputExecutorException("REALTIME_CONNECTION_REPLACED", "Realtime connection is no longer active.", 409);
            if (frame.Sequence <= session.LastRealtimeSequence) return Task.FromResult(Dropped(frame.Sequence, "stale"));
            session.LastRealtimeSequence = frame.Sequence;
            session.LastRealtimeReceivedUs = now;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.ArmLifetime!.Token);
            var scheduled = _scheduler.EnqueueRealtimeAsync(frame, () => EnsureTargetAndClientValid(session, true), linked.Token);
            _ = scheduled.ContinueWith(_ => linked.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = RecordRealtimeCompletionAsync(session, frame, now, scheduled);
            return scheduled;
        }
    }

    public async Task CloseRealtimeConnectionAsync(string sessionId, string token, string connectionId, string reason)
    {
        var session = Authenticate(sessionId, token);
        lock (session.Sync)
        {
            if (!string.Equals(session.RealtimeConnectionId, connectionId, StringComparison.Ordinal)) return;
            session.RealtimeConnectionId = null;
        }
        await SafetyStopAsync(session, reason, paused: true, CancellationToken.None);
    }

    public async Task<SessionStatus> CancelSequenceAsync(string sessionId, string token, CancellationToken cancellationToken)
    {
        var session = Authenticate(sessionId, token);
        _scheduler.CancelActiveWork();
        await SafetyStopAsync(session, "sequence_cancelled", paused: true, cancellationToken);
        return ToStatus(session);
    }

    public async Task<SessionStatus> ReleaseAllAsync(string sessionId, string token, CancellationToken cancellationToken)
    {
        var session = Authenticate(sessionId, token);
        await _scheduler.ReleaseAllAndClearAsync("release_all", cancellationToken);
        return ToStatus(session);
    }

    public async Task DeleteSessionAsync(string sessionId, string token, CancellationToken cancellationToken)
    {
        var session = Authenticate(sessionId, token);
        await SafetyStopAsync(session, "session_deleted", paused: false, cancellationToken);
        _sessions.TryRemove(sessionId, out _);
    }

    public async Task InspectWatchdogsAsync(CancellationToken cancellationToken)
    {
        Session? session = null;
        lock (_ownershipSync)
        {
            if (_armedSessionId is not null) _sessions.TryGetValue(_armedSessionId, out session);
        }
        if (session is null) return;

        string? reason = null;
        lock (session.Sync)
        {
            if (!session.Armed) return;
            var now = _clock.NowMicroseconds;
            if (session.ArmedUntilUs is not null && now >= session.ArmedUntilUs) reason = "arm_expired";
            else if (session.RealtimeActive && now - session.LastRealtimeReceivedUs > session.LeaseTimeoutMs * 1_000L) reason = "watchdog_timeout";
        }
        if (reason is null)
        {
            try { EnsureTargetAndClientValid(session, requireForeground: true); }
            catch (InputExecutorException ex) { reason = ex.Code.ToLowerInvariant(); }
        }
        if (reason is not null) await SafetyStopAsync(session, reason, paused: true, cancellationToken);
    }

    public async Task EmergencyStopAsync(string reason = "emergency_stop")
    {
        Session? active = null;
        lock (_ownershipSync)
        {
            if (_armedSessionId is not null) _sessions.TryGetValue(_armedSessionId, out active);
        }
        if (active is not null) await SafetyStopAsync(active, reason, paused: true, CancellationToken.None);
        else await _scheduler.ReleaseAllAndClearAsync(reason, CancellationToken.None);
    }

    public bool BestEffortEmergencyStop(string reason, TimeSpan timeout)
    {
        lock (_ownershipSync)
        {
            if (_armedSessionId is not null && _sessions.TryGetValue(_armedSessionId, out var session))
            {
                lock (session.Sync)
                {
                    session.ArmLifetime?.Cancel();
                    session.Armed = false;
                    session.Paused = true;
                    session.PauseReason = reason;
                }
                _armedSessionId = null;
            }
        }
        return _scheduler.BestEffortEmergencyRelease(reason, timeout);
    }

    private async Task SafetyStopAsync(Session session, string reason, bool paused, CancellationToken cancellationToken)
    {
        lock (_ownershipSync)
        {
            lock (session.Sync)
            {
                session.ArmLifetime?.Cancel();
                session.ArmLifetime?.Dispose();
                session.ArmLifetime = null;
                session.Armed = false;
                session.ArmedUntilUs = null;
                session.RealtimeActive = false;
                session.RealtimeConnectionId = null;
                session.Paused = paused;
                session.PauseReason = paused ? reason : null;
                if (_armedSessionId == session.Id) _armedSessionId = null;
            }
        }
        await _scheduler.ReleaseAllAndClearAsync(reason, cancellationToken);
    }

    private Session Authenticate(string sessionId, string token)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new InputExecutorException("SESSION_NOT_FOUND", "Session does not exist.", 404);
        if (!CryptographicOperations.FixedTimeEquals(session.TokenHash, HashToken(token ?? string.Empty)))
            throw new InputExecutorException("UNAUTHORIZED", "Capability token is invalid.", 401);
        return session;
    }

    private void EnsureReadyLocked(Session session)
    {
        if (!session.Armed) throw new InputExecutorException("SESSION_DISARMED", "Session must be armed before executing input.", 409);
        if (session.Paused) throw new InputExecutorException("SESSION_PAUSED", $"Session is paused: {session.PauseReason}.", 409);
        if (session.ArmedUntilUs is not null && _clock.NowMicroseconds >= session.ArmedUntilUs)
            throw new InputExecutorException("ARM_EXPIRED", "The session arm lease expired.", 409);
        if (Volatile.Read(ref _armedSessionId) != session.Id)
            throw new InputExecutorException("INPUT_LEASE_LOST", "Session no longer owns the input lease.", 409);
    }

    private void EnsureTargetAndClientValid(Session session, bool requireForeground)
    {
        EnsureTargetValid(session.Target, requireForeground);
        var targetName = NormalizeProcessName(session.Target.ProcessName);
        if (!session.AllowedProcesses.Contains(targetName, StringComparer.OrdinalIgnoreCase))
            throw new InputExecutorException("TARGET_NOT_ALLOWLISTED", $"Target process '{targetName}' is not allowlisted.", 403);
        if (session.ClientProcessId is null) return;
        try
        {
            using var client = Process.GetProcessById(session.ClientProcessId.Value);
            if (client.HasExited || client.StartTime.ToUniversalTime().Ticks != session.ClientStartTimeUtcTicks)
                throw new InputExecutorException("CLIENT_PROCESS_EXITED", "The bound client process exited or its PID was reused.", 409);
        }
        catch (ArgumentException) { throw new InputExecutorException("CLIENT_PROCESS_EXITED", "The bound client process exited.", 409); }
        catch (InvalidOperationException) { throw new InputExecutorException("CLIENT_PROCESS_EXITED", "The bound client process exited.", 409); }
    }

    private void EnsureTargetValid(TargetBinding target, bool requireForeground)
    {
        var check = _targetGuard.Validate(target, requireForeground);
        if (!check.IsValid) throw new InputExecutorException(check.ErrorCode ?? "INVALID_TARGET", check.Message ?? "Target validation failed.", 409);
    }

    private SessionStatus ToStatus(Session session)
    {
        lock (session.Sync)
        {
            var ownsInput = Volatile.Read(ref _armedSessionId) == session.Id;
            var snapshot = _scheduler.Snapshot;
            return new SessionStatus
            {
                SessionId = session.Id,
                Target = session.Target,
                Armed = session.Armed,
                Paused = session.Paused,
                PauseReason = session.PauseReason,
                ArmedUntilUs = session.ArmedUntilUs,
                LeaseTimeoutMs = session.LeaseTimeoutMs,
                AllowedProcesses = session.AllowedProcesses,
                ClientProcessId = session.ClientProcessId,
                RealtimeConnected = session.RealtimeConnectionId is not null,
                LastCommandSequence = session.LastCommandSequence,
                LastRealtimeSequence = session.LastRealtimeSequence,
                HeldKeys = ownsInput ? snapshot.HeldKeys : Array.Empty<string>(),
                HeldMouseButtons = ownsInput ? snapshot.HeldMouseButtons : Array.Empty<string>(),
                MonotonicUs = _clock.NowMicroseconds
            };
        }
    }

    private async Task RecordRealtimeCompletionAsync(Session session, RealtimeFrame frame, long receivedAt, Task<ExecutionResult> scheduled)
    {
        try
        {
            var result = await scheduled;
            await RecordAsync(session, "realtime", frame.Sequence, receivedAt, result.MonotonicUs, frame, result.Events, result.Status, CancellationToken.None);
        }
        catch { }
    }

    private ValueTask RecordAsync(Session session, string source, long sequence, long receivedAt, long completedAt, object requested,
        IReadOnlyList<EmittedInput> emitted, string status, CancellationToken cancellationToken) =>
        _trajectory.WriteAsync(new TrajectoryRecord
        {
            RecordedAtUtc = DateTimeOffset.UtcNow,
            SessionId = session.Id,
            Source = source,
            Sequence = sequence,
            ExecutorReceiveTimestampUs = receivedAt,
            ExecutorCompleteTimestampUs = completedAt,
            ActionRequested = requested,
            ActionEmitted = emitted,
            Target = session.Target,
            Backend = _scheduler.BackendName,
            TerminationReason = status == "executed" ? null : status
        }, cancellationToken);

    private ExecutionResult Dropped(long sequence, string status) => new()
    {
        Accepted = false,
        Sequence = sequence,
        Status = status,
        MonotonicUs = _clock.NowMicroseconds
    };

    private static void ValidateRealtime(RealtimeFrame frame)
    {
        if (frame.Sequence < 0) throw new InputExecutorException("INVALID_SEQUENCE", "sequence must be non-negative.");
        if (frame.TimestampUs <= 0) throw new InputExecutorException("INVALID_TIMESTAMP", "timestamp_us must contain the client's monotonic uptime in microseconds.");
        if (frame.DeadlineUs is null or <= 0) throw new InputExecutorException("DEADLINE_REQUIRED", "deadline_us is required for every realtime frame.");
        if (frame.Keyboard.Held.Count > 32) throw new InputExecutorException("TOO_MANY_HELD_KEYS", "A realtime frame may hold at most 32 keys.");
        if (frame.Mouse.HeldButtons.Count > 5) throw new InputExecutorException("TOO_MANY_HELD_BUTTONS", "A realtime frame may hold at most 5 mouse buttons.");
        if (frame.Keyboard.Held.Distinct(StringComparer.OrdinalIgnoreCase).Count() != frame.Keyboard.Held.Count)
            throw new InputExecutorException("DUPLICATE_HELD_KEY", "Realtime held keys must be unique.");
        if (frame.Mouse.HeldButtons.Distinct(StringComparer.OrdinalIgnoreCase).Count() != frame.Mouse.HeldButtons.Count)
            throw new InputExecutorException("DUPLICATE_HELD_BUTTON", "Realtime held mouse buttons must be unique.");
        if (Math.Abs((long)frame.Mouse.Dx) > 32_768 || Math.Abs((long)frame.Mouse.Dy) > 32_768)
            throw new InputExecutorException("INVALID_RELATIVE_DELTA", "Realtime dx and dy must be within ±32768 counts.");
        var validButtons = new HashSet<string>(new[] { "left", "right", "middle", "x1", "x2" }, StringComparer.OrdinalIgnoreCase);
        if (frame.Mouse.HeldButtons.Any(x => !validButtons.Contains(x)))
            throw new InputExecutorException("UNSUPPORTED_MOUSE_BUTTON", "Realtime frame contains an unsupported mouse button.");
    }

    private static string NormalizeProcessName(string name) => Path.GetFileNameWithoutExtension(name.Trim()).ToLowerInvariant();
    private static byte[] HashToken(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await EmergencyStopAsync("coordinator_disposed");
    }
}
