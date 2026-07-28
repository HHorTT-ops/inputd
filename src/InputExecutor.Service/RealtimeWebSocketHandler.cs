using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;
using InputExecutor.Scheduler;

namespace InputExecutor.Service;

public static class RealtimeWebSocketHandler
{
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsJsonAsync(new ApiError { Error = "WEBSOCKET_REQUIRED", Message = "Realtime control requires a WebSocket connection." });
            return;
        }

        var sessionId = context.Request.Query["session_id"].ToString();
        var token = BearerToken(context.Request);
        if (string.IsNullOrWhiteSpace(sessionId)) throw new InputExecutorException("SESSION_REQUIRED", "session_id query parameter is required.");
        var coordinator = context.RequestServices.GetRequiredService<InputCoordinator>();
        var connectionId = coordinator.OpenRealtimeConnection(sessionId, token);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var outbound = Channel.CreateBounded<object>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        var sender = SendLoopAsync(socket, outbound.Reader, lifetime.Token);

        try
        {
            while (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
            {
                var payload = await ReceiveMessageAsync(socket, lifetime.Token);
                if (payload is null) break;
                RealtimeFrame frame;
                try
                {
                    frame = JsonSerializer.Deserialize<RealtimeFrame>(payload, Json) ??
                            throw new JsonException("Frame body was null.");
                }
                catch (JsonException ex)
                {
                    outbound.Writer.TryWrite(new { accepted = false, status = "invalid", error = "INVALID_FRAME", message = ex.Message });
                    continue;
                }

                try
                {
                    var result = coordinator.SubmitRealtimeFrameAsync(sessionId, token, connectionId, frame, lifetime.Token);
                    _ = ForwardResultAsync(frame.Sequence, result, outbound.Writer);
                }
                catch (InputExecutorException ex)
                {
                    outbound.Writer.TryWrite(new { accepted = false, sequence = frame.Sequence, status = "rejected", error = ex.Code, message = ex.Message });
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            lifetime.Cancel();
            await coordinator.CloseRealtimeConnectionAsync(sessionId, token, connectionId, "realtime_disconnected");
            outbound.Writer.TryComplete();
            try { await sender; } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "input released", CancellationToken.None); } catch { }
            }
        }
    }

    private static async Task ForwardResultAsync(long sequence, Task<ExecutionResult> task, ChannelWriter<object> writer)
    {
        try { writer.TryWrite(await task); }
        catch (InputExecutorException ex) { writer.TryWrite(new { accepted = false, sequence, status = "rejected", error = ex.Code, message = ex.Message }); }
        catch (Exception ex) { writer.TryWrite(new { accepted = false, sequence, status = "failed", error = "EXECUTION_FAILED", message = ex.Message }); }
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<object> reader, CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item, item.GetType(), Json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(MaxMessageBytes);
        try
        {
            var total = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(rented.AsMemory(total, MaxMessageBytes - total), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InputExecutorException("INVALID_WEBSOCKET_MESSAGE", "Realtime messages must be JSON text.");
                total += result.Count;
                if (total >= MaxMessageBytes && !result.EndOfMessage)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame exceeds 64 KiB", cancellationToken);
                    return null;
                }
                if (result.EndOfMessage) return rented.AsSpan(0, total).ToArray();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static string BearerToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return value[7..].Trim();
        }
        throw new InputExecutorException("UNAUTHORIZED", "Realtime WebSocket requires Authorization: Bearer <capability-token>.", 401);
    }
}
