using System.Collections.Concurrent;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace InputExecutor.IntegrationTests;

internal sealed class InputdWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AccessToken = "test-access-token-0123456789-abcdefghijk";
    public TestBackend Backend { get; } = new();
    public TestTargetGuard Guard { get; } = new();

    public InputdWebApplicationFactory() => Environment.SetEnvironmentVariable("INPUTD_ACCESS_TOKEN", AccessToken);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IInputBackend>();
            services.RemoveAll<ITargetGuard>();
            services.RemoveAll<ITrajectorySink>();
            var emergency = services.FirstOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(EmergencyStopWorker));
            if (emergency is not null) services.Remove(emergency);
            services.AddSingleton<IInputBackend>(Backend);
            services.AddSingleton<ITargetGuard>(Guard);
            services.AddSingleton<ITrajectorySink, NullTrajectorySink>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("INPUTD_ACCESS_TOKEN", null);
    }
}

internal sealed record TestCall(int ThreadId, string Kind, string Detail);

internal sealed class TestBackend : IInputBackend
{
    private readonly ConcurrentQueue<TestCall> _calls = new();
    public string Name => "test-backend";
    public IReadOnlyList<TestCall> Calls => _calls.ToArray();
    public bool SupportsKey(string code) => true;
    public ValueTask<EmittedInput> MoveMouseRelativeAsync(int dx, int dy, bool noCoalesce, CancellationToken cancellationToken) => One("move", $"{dx},{dy}");
    public ValueTask<EmittedInput> MoveMouseAbsoluteAsync(int x, int y, CancellationToken cancellationToken) => One("absolute", $"{x},{y}");
    public ValueTask<EmittedInput> SetMouseButtonAsync(string button, bool isDown, CancellationToken cancellationToken) => One("button", $"{button}:{(isDown ? "down" : "up")}");
    public ValueTask<EmittedInput> ScrollMouseAsync(int deltaX, int deltaY, CancellationToken cancellationToken) => One("wheel", $"{deltaX},{deltaY}");
    public ValueTask<EmittedInput> SetKeyAsync(string code, bool isDown, CancellationToken cancellationToken) => One("key", $"{code}:{(isDown ? "down" : "up")}");
    public ValueTask<IReadOnlyList<EmittedInput>> TypeTextAsync(string text, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<EmittedInput>>(new[] { One("text", text).Result });

    private ValueTask<EmittedInput> One(string kind, string detail)
    {
        _calls.Enqueue(new TestCall(Environment.CurrentManagedThreadId, kind, detail));
        return ValueTask.FromResult(new EmittedInput { Kind = kind, Detail = detail, EmittedAtUs = Environment.TickCount64 * 1000 });
    }
}

internal sealed class TestTargetGuard : ITargetGuard
{
    public TargetBinding Target { get; } = new()
    {
        WindowHandle = "0x1234",
        ProcessId = 42,
        ProcessName = "integration-target",
        ProcessPath = "C:\\integration-target.exe"
    };
    public TargetBinding CaptureForeground() => Target;
    public TargetBinding CaptureProcess(int processId) => Target with { ProcessId = processId };
    public bool TryActivate(TargetBinding target) => true;
    public TargetCheck Validate(TargetBinding target, bool requireForeground) => new(true);
}
