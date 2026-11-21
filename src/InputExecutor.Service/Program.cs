using System.Net;
using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;
using InputExecutor.Core.Recording;
using InputExecutor.Scheduler;
using InputExecutor.Service;
using InputExecutor.Windows;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("inputd requires Windows.");
    return 2;
}

var builder = WebApplication.CreateBuilder(args);
var listenUrl = Environment.GetEnvironmentVariable("INPUTD_URL") ?? "http://127.0.0.1:17841";
if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) ||
    !IPAddress.TryParse(listenUri.Host, out var listenAddress) || !IPAddress.IsLoopback(listenAddress))
    throw new InvalidOperationException("INPUTD_URL must use a numeric loopback address, for example http://127.0.0.1:17841. Remote binding is disabled.");
builder.WebHost.UseUrls(listenUrl);
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});

builder.Services.AddSingleton<IMonotonicClock, SystemMonotonicClock>();
builder.Services.AddSingleton<IInputBackend>(services => new WindowsSendInputBackend(services.GetRequiredService<IMonotonicClock>()));
builder.Services.AddSingleton<ITargetGuard, WindowsTargetGuard>();
builder.Services.AddSingleton<IWindowScreenshotCapture, WindowsWindowScreenshotCapture>();
builder.Services.AddSingleton<ITrajectorySink>(_ =>
{
    if (string.Equals(Environment.GetEnvironmentVariable("INPUTD_RECORD_TRAJECTORIES"), "false", StringComparison.OrdinalIgnoreCase))
        return new NullTrajectorySink();
    var path = Environment.GetEnvironmentVariable("INPUTD_TRAJECTORY_PATH") ??
               Path.Combine(AppContext.BaseDirectory, "trajectories", $"inputd-{DateTime.UtcNow:yyyyMMdd}.jsonl");
    return new JsonLinesTrajectorySink(path);
});
builder.Services.AddSingleton<InputScheduler>(services => new InputScheduler(
    services.GetRequiredService<IInputBackend>(),
    services.GetRequiredService<IMonotonicClock>(),
    new InputSchedulerOptions(CommandQueueCapacity: 64, MaxRealtimeBurst: 8)));
builder.Services.AddSingleton<InputCoordinator>();
builder.Services.AddSingleton<WindowsRawInputRecorder>();
builder.Services.AddSingleton<IHumanInputRecorder>(sp => sp.GetRequiredService<WindowsRawInputRecorder>());
builder.Services.AddSingleton<IPhysicalInputObservationSource>(sp => new WindowsPhysicalInputObservationSource(sp.GetRequiredService<WindowsRawInputRecorder>()));
builder.Services.AddSingleton<PhysicalMouseEventHub>();
builder.Services.AddSingleton<ServiceAccessToken>();
builder.Services.AddHostedService<PhysicalMouseSseHostedService>();
builder.Services.AddHostedService<WatchdogWorker>();
builder.Services.AddHostedService<EmergencyStopWorker>();

var app = builder.Build();
var coordinator = app.Services.GetRequiredService<InputCoordinator>();
var accessToken = app.Services.GetRequiredService<ServiceAccessToken>();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (InputExecutorException ex)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex.HttpStatus;
        await context.Response.WriteAsJsonAsync(new ApiError { Error = ex.Code, Message = ex.Message, Details = ex.Details });
    }
    catch (BadHttpRequestException ex)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new ApiError { Error = "INVALID_REQUEST", Message = ex.Message });
    }
});

app.MapGet("/health", (InputCoordinator input) => Results.Ok(new
{
    status = "ok",
    backend = input.BackendName,
    protocol_version = "1.0",
    monotonic_us = input.MonotonicMicroseconds,
    scheduler_thread_id = input.SchedulerSnapshot.SchedulerThreadId,
    realtime_transports = new { websocket = "/v1/realtime", named_pipe = "reserved: \\\\.\\pipe\\inputd-realtime-v1" },
    emergency_hotkey = "Ctrl+Alt+F12"
}));

app.MapPost("/v1/sessions", async (CreateSessionRequest request, HttpRequest httpRequest, InputCoordinator input, ServiceAccessToken serviceToken, CancellationToken ct) =>
{
    RequireServiceAccess(httpRequest, serviceToken);
    return Results.Created("/v1/sessions", await input.CreateSessionAsync(request, ct));
});

app.MapGet("/v1/sessions/{sessionId}", async (string sessionId, HttpRequest request, InputCoordinator input, CancellationToken ct) =>
    Results.Ok(await input.GetStatusAsync(sessionId, CapabilityToken(request), ct)));
app.MapPost("/v1/sessions/{sessionId}/arm", async (string sessionId, ArmRequest arm, HttpRequest request, InputCoordinator input, CancellationToken ct) =>
    Results.Ok(await input.ArmAsync(sessionId, CapabilityToken(request), arm, ct)));
app.MapPost("/v1/sessions/{sessionId}/focus", async (string sessionId, HttpRequest request, InputCoordinator input, CancellationToken ct) =>
    Results.Ok(await input.FocusTargetAsync(sessionId, CapabilityToken(request), ct)));
app.MapPost("/v1/sessions/{sessionId}/disarm", async (string sessionId, HttpRequest request, InputCoordinator input, CancellationToken ct) =>
    Results.Ok(await input.DisarmAsync(sessionId, CapabilityToken(request), ct)));
app.MapPost("/v1/sessions/{sessionId}/release-all", async (string sessionId, HttpRequest request, InputCoordinator input, CancellationToken ct) =>
    Results.Ok(await input.ReleaseAllAsync(sessionId, CapabilityToken(request), ct)));
app.MapPost("/v1/sessions/{sessionId}/cancel", async (string sessionId, HttpRequest request, InputCoordinator input, CancellationToken ct) =>
    Results.Ok(await input.CancelSequenceAsync(sessionId, CapabilityToken(request), ct)));
app.MapPost("/v1/sessions/{sessionId}/commands", async (string sessionId, CommandRequest command, HttpRequest request, InputCoordinator input, CancellationToken ct) =>
    Results.Ok(await input.ExecuteCommandAsync(sessionId, CapabilityToken(request), command, ct)));
app.MapGet("/v1/sessions/{sessionId}/snapshot", async (string sessionId, HttpRequest request, HttpResponse response,
    InputCoordinator input, IWindowScreenshotCapture capture, CancellationToken ct) =>
{
    var target = await input.GetObservationTargetAsync(sessionId, CapabilityToken(request), ct);
    var frame = capture.Capture(target);
    response.Headers["X-Inputd-Frame-Id"] = frame.FrameId;
    response.Headers["X-Inputd-Origin-X"] = frame.OriginX.ToString(System.Globalization.CultureInfo.InvariantCulture);
    response.Headers["X-Inputd-Origin-Y"] = frame.OriginY.ToString(System.Globalization.CultureInfo.InvariantCulture);
    response.Headers["X-Inputd-Width"] = frame.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
    response.Headers["X-Inputd-Height"] = frame.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
    response.Headers["X-Inputd-Captured-At"] = frame.CapturedAtUtc.ToString("O");
    return Results.File(frame.PngBytes, "image/png");
});
app.MapPost("/v1/sessions/{sessionId}/recording/start", async (string sessionId, RecordingStartRequest options, HttpRequest request,
    InputCoordinator input, IHumanInputRecorder recorder, CancellationToken ct) =>
{
    var target = await input.GetObservationTargetAsync(sessionId, CapabilityToken(request), ct);
    return Results.Ok(recorder.Start(sessionId, target, options.IncludeExecutorEvents));
});
app.MapGet("/v1/sessions/{sessionId}/recording", async (string sessionId, HttpRequest request,
    InputCoordinator input, IHumanInputRecorder recorder, CancellationToken ct) =>
{
    await input.GetStatusAsync(sessionId, CapabilityToken(request), ct);
    return Results.Ok(recorder.Status);
});
app.MapPost("/v1/sessions/{sessionId}/recording/stop", async (string sessionId, HttpRequest request,
    InputCoordinator input, IHumanInputRecorder recorder, CancellationToken ct) =>
{
    await input.GetStatusAsync(sessionId, CapabilityToken(request), ct);
    return Results.Ok(await recorder.StopAsync(sessionId, ct));
});
app.MapDelete("/v1/sessions/{sessionId}", async (string sessionId, HttpRequest request, InputCoordinator input, CancellationToken ct) =>
{
    await input.DeleteSessionAsync(sessionId, CapabilityToken(request), ct);
    return Results.NoContent();
});

app.MapGet("/v1/physical-input/events", PhysicalMouseSseEndpoint.HandleAsync);

// Realtime frames are accepted only over a persistent WebSocket. There is deliberately no HTTP realtime POST route.
app.Map("/v1/realtime", RealtimeWebSocketHandler.HandleAsync);

void BestEffortRelease(string reason) => coordinator.BestEffortEmergencyStop(reason, TimeSpan.FromSeconds(2));
app.Lifetime.ApplicationStopping.Register(() => BestEffortRelease("application_stopping"));
AppDomain.CurrentDomain.ProcessExit += (_, _) => BestEffortRelease("process_exit");
AppDomain.CurrentDomain.UnhandledException += (_, _) => BestEffortRelease("unhandled_exception");

Console.WriteLine($"inputd listening on {listenUrl}");
Console.WriteLine($"Service access token file: {accessToken.TokenPath}");
Console.WriteLine("Input injection starts DISARMED. Emergency stop: Ctrl+Alt+F12");
await app.RunAsync();
return 0;

static string CapabilityToken(HttpRequest request)
{
    if (request.Headers.TryGetValue("X-Inputd-Token", out var header) && !string.IsNullOrWhiteSpace(header)) return header.ToString();
    if (request.Headers.TryGetValue("Authorization", out var authorization))
    {
        var value = authorization.ToString();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return value[7..].Trim();
    }
    throw new InputExecutorException("UNAUTHORIZED", "Provide the session capability token in X-Inputd-Token or Authorization: Bearer.", 401);
}

static void RequireServiceAccess(HttpRequest request, ServiceAccessToken accessToken)
{
    if (!request.Headers.TryGetValue("X-Inputd-Access-Token", out var value) || !accessToken.Validate(value.ToString()))
        throw new InputExecutorException("UNAUTHORIZED", "A valid X-Inputd-Access-Token is required to create a session.", 401);
}

public partial class Program { }

