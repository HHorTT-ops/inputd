using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using InputExecutor.Core.Protocol;
using Microsoft.AspNetCore.TestHost;

namespace InputExecutor.IntegrationTests;

public sealed class WebSocketIntegrationTests
{
    [Fact]
    public async Task SessionCreation_RequiresBootstrapAccessToken()
    {
        using var factory = new InputdWebApplicationFactory();
        using var client = factory.CreateClient();
        var denied = await client.PostAsJsonAsync("/v1/sessions", new CreateSessionRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/sessions")
        {
            Content = JsonContent.Create(new CreateSessionRequest())
        };
        request.Headers.Add("X-Inputd-Access-Token", InputdWebApplicationFactory.AccessToken);
        var accepted = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task WebSocketDisconnect_ReleasesHeldKeyAndDisarms()
    {
        using var factory = new InputdWebApplicationFactory();
        using var client = factory.CreateClient();
        var session = await CreateAndArmAsync(client);
        var websocketClient = factory.Server.CreateWebSocketClient();
        websocketClient.ConfigureRequest = request => request.Headers["Authorization"] = "Bearer " + session.CapabilityToken;
        using var socket = await websocketClient.ConnectAsync(new Uri($"ws://localhost/v1/realtime?session_id={session.SessionId}"), default);
        var frame = new RealtimeFrame
        {
            Sequence = 1,
            TimestampUs = Environment.TickCount64 * 1_000,
            DeadlineUs = Environment.TickCount64 * 1_000 + 5_000_000,
            Keyboard = new KeyboardFrame { Held = new[] { "KeyW" } },
            Mouse = new MouseFrame { Dx = 3, Dy = -2 }
        };
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, default);
        var responseBuffer = new byte[16 * 1024];
        var response = await socket.ReceiveAsync(responseBuffer, default);
        var result = JsonSerializer.Deserialize<ExecutionResult>(responseBuffer.AsSpan(0, response.Count), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        Assert.True(result.Accepted);
        Assert.Contains(factory.Backend.Calls, x => x.Detail == "KeyW:down");

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test disconnect", default);
        SessionStatus? status = null;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(20);
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/sessions/{session.SessionId}");
            statusRequest.Headers.Add("X-Inputd-Token", session.CapabilityToken);
            var statusResponse = await client.SendAsync(statusRequest);
            status = await statusResponse.Content.ReadFromJsonAsync<SessionStatus>();
            if (status is { Armed: false }) break;
        }
        Assert.NotNull(status);
        Assert.False(status.Armed);
        Assert.Contains(factory.Backend.Calls, x => x.Detail == "KeyW:up");
        Assert.Single(factory.Backend.Calls.Select(x => x.ThreadId).Distinct());
    }

    [Fact]
    public async Task RealtimeHttpPost_IsNotAnActionEndpoint()
    {
        using var factory = new InputdWebApplicationFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/realtime", new RealtimeFrame { Sequence = 1 });
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    private static async Task<CreateSessionResponse> CreateAndArmAsync(HttpClient client)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/v1/sessions") { Content = JsonContent.Create(new CreateSessionRequest()) };
        create.Headers.Add("X-Inputd-Access-Token", InputdWebApplicationFactory.AccessToken);
        var createResponse = await client.SendAsync(create);
        createResponse.EnsureSuccessStatusCode();
        var session = (await createResponse.Content.ReadFromJsonAsync<CreateSessionResponse>())!;
        using var arm = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{session.SessionId}/arm")
        {
            Content = JsonContent.Create(new ArmRequest { DurationSeconds = 60 })
        };
        arm.Headers.Add("X-Inputd-Token", session.CapabilityToken);
        (await client.SendAsync(arm)).EnsureSuccessStatusCode();
        return session;
    }
}
