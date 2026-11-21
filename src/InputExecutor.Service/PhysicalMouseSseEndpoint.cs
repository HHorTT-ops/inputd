using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;

namespace InputExecutor.Service;

public static class PhysicalMouseSseEndpoint
{
    public static async Task HandleAsync(HttpContext context, PhysicalMouseEventHub hub, ServiceAccessToken accessToken, IMonotonicClock clock)
    {
        // Required order: authenticate → subscribe → SSE headers → flush → heartbeat → read loop.
        // Subscribing before Flush closes the connect-time window where Raw Input frames would
        // otherwise arrive after the client saw headers but before this connection was attached.
        if (!context.Request.Headers.TryGetValue("X-Inputd-Access-Token", out var value) || !accessToken.Validate(value.ToString()))
            throw new InputExecutorException("UNAUTHORIZED", "A valid X-Inputd-Access-Token is required for physical input SSE.", 401);

        await using var subscription = hub.Subscribe();

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.ContentType = "text/event-stream";
        await context.Response.Body.FlushAsync(context.RequestAborted);

        await WriteSseAsync(context, PhysicalMouseSseMessage.Heartbeat(clock.NowMicroseconds, hub.TaggedFramesSeen), context.RequestAborted);

        try
        {
            await foreach (var message in subscription.Reader.ReadAllAsync(context.RequestAborted))
                await WriteSseAsync(context, message, context.RequestAborted);

            if (subscription.Overflowed && !context.RequestAborted.IsCancellationRequested)
            {
                await WriteSseAsync(
                    context,
                    PhysicalMouseSseMessage.Fault(
                        "OVERFLOW",
                        "Physical mouse SSE queue overflowed; takeover detection is no longer reliable for this connection."),
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // client disconnected — await using disposes the subscription
        }
    }

    private static async Task WriteSseAsync(HttpContext context, PhysicalMouseSseMessage message, CancellationToken cancellationToken)
    {
        object payload = message.EventName switch
        {
            "mouse" => new
            {
                sequence = message.Mouse!.Sequence,
                timestamp_us = message.Mouse.TimestampUs,
                dx = message.Mouse.Dx,
                dy = message.Mouse.Dy,
                held_buttons = message.Mouse.HeldButtons,
                wheel_x = message.Mouse.WheelX,
                wheel_y = message.Mouse.WheelY,
                source_class = message.Mouse.SourceClass,
                raw_device_handle = message.Mouse.RawDeviceHandle
            },
            "key_activity" => new
            {
                sequence = message.KeyActivity!.Sequence,
                timestamp_us = message.KeyActivity.TimestampUs,
                source_class = message.KeyActivity.SourceClass,
                raw_device_handle = message.KeyActivity.RawDeviceHandle,
                edge = message.KeyActivity.Edge
            },
            "heartbeat" => new
            {
                timestamp_us = message.HeartbeatTimestampUs,
                tagged_frames_seen = message.TaggedFramesSeen
            },
            _ => new
            {
                error = message.ErrorCode,
                message = message.ErrorMessage
            }
        };

        var json = JsonSerializer.Serialize(payload);
        await context.Response.WriteAsync($"event: {message.EventName}\n", cancellationToken);
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
