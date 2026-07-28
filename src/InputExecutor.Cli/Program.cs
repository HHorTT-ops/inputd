using System.Net.Http.Json;
using System.Text.Json;
using InputExecutor.Core.Protocol;
using InputExecutor.Core.Training;

var arguments = args.ToList();
var baseUrl = TakeOption(arguments, "--url") ?? Environment.GetEnvironmentVariable("INPUTD_URL") ?? "http://127.0.0.1:17841";
var accessToken = TakeOption(arguments, "--access-token") ?? Environment.GetEnvironmentVariable("INPUTD_ACCESS_TOKEN");
if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
{
    Help();
    return 0;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
if (arguments[0].Equals("resample", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        var values = arguments.Skip(1).ToList();
        var rateText = TakeOption(values, "--hz") ?? TakeOption(values, "--fps") ?? "60";
        if (!int.TryParse(rateText, out var rate)) throw new ArgumentException("--hz must be an integer from 1 to 1000.");
        var allowGaps = values.Remove("--allow-gaps");
        var overwrite = values.Remove("--overwrite");
        if (values.Count is < 1 or > 2)
            throw new ArgumentException("Usage: inputctl resample <source.jsonl> [output.jsonl] [--hz 20|40|60|CUSTOM] [--allow-gaps] [--overwrite]");
        var input = Path.GetFullPath(values[0]);
        var output = values.Count == 2
            ? Path.GetFullPath(values[1])
            : Path.Combine(Path.GetDirectoryName(input)!, $"{Path.GetFileNameWithoutExtension(input)}.{rate}hz.training.jsonl");
        var result = await new TrajectoryTrainingDataGenerator().GenerateAsync(new TrainingGenerationOptions
        {
            InputPath = input,
            OutputPath = output,
            FramesPerSecond = rate,
            AllowSequenceGaps = allowGaps,
            Overwrite = overwrite
        });
        Console.WriteLine(JsonSerializer.Serialize(result, json));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(35) };

try
{
    var command = arguments[0].ToLowerInvariant();
    HttpResponseMessage response;
    switch (command)
    {
        case "health":
            response = await http.GetAsync("/health");
            break;
        case "create":
            accessToken ??= ReadDefaultAccessToken();
            if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("create requires --access-token, INPUTD_ACCESS_TOKEN, or the default access.token file.");
            using (var create = new HttpRequestMessage(HttpMethod.Post, "/v1/sessions"))
            {
                create.Headers.Add("X-Inputd-Access-Token", accessToken);
                create.Content = JsonContent.Create(new CreateSessionRequest
                {
                    BindToForeground = true,
                    LeaseTimeoutMs = arguments.Count > 1 ? int.Parse(arguments[1]) : 250,
                    ClientProcessId = arguments.Count > 2 ? int.Parse(arguments[2]) : null
                }, options: json);
                response = await http.SendAsync(create);
            }
            break;
        case "status":
            Require(arguments, 3, "status <session> <token>");
            response = await SendWithToken(HttpMethod.Get, $"/v1/sessions/{arguments[1]}", arguments[2], null);
            break;
        case "arm":
            Require(arguments, 3, "arm <session> <token> [seconds]");
            response = await SendWithToken(HttpMethod.Post, $"/v1/sessions/{arguments[1]}/arm", arguments[2],
                new ArmRequest { DurationSeconds = arguments.Count > 3 ? int.Parse(arguments[3]) : 1800 });
            break;
        case "disarm":
        case "release-all":
        case "cancel":
            Require(arguments, 3, $"{command} <session> <token>");
            response = await SendWithToken(HttpMethod.Post, $"/v1/sessions/{arguments[1]}/{command}", arguments[2], new { });
            break;
        case "move":
            Require(arguments, 5, "move <session> <token> <dx> <dy> [duration-ms]");
            response = await Command(arguments[1], arguments[2], new ActionCommand
            {
                Type = "mouse_move_relative",
                Dx = int.Parse(arguments[3]),
                Dy = int.Parse(arguments[4]),
                DurationMs = arguments.Count > 5 ? int.Parse(arguments[5]) : 0
            });
            break;
        case "key":
            Require(arguments, 5, "key <session> <token> <code> <down|up|press|hold> [duration-ms]");
            response = await Command(arguments[1], arguments[2], new ActionCommand
            {
                Type = "key",
                Code = arguments[3],
                Action = arguments[4],
                DurationMs = arguments.Count > 5 ? int.Parse(arguments[5]) : null
            });
            break;
        case "click":
            Require(arguments, 4, "click <session> <token> <left|right|middle|x1|x2>");
            response = await Command(arguments[1], arguments[2], new ActionCommand { Type = "mouse_button", Button = arguments[3], Action = "click" });
            break;
        case "type":
            Require(arguments, 4, "type <session> <token> <text>");
            response = await Command(arguments[1], arguments[2], new ActionCommand { Type = "text", Text = string.Join(' ', arguments.Skip(3)) });
            break;
        default:
            throw new ArgumentException($"Unknown command '{command}'. Run inputctl help.");
    }

    var body = await response.Content.ReadAsStringAsync();
    if (!string.IsNullOrWhiteSpace(body))
    {
        try { Console.WriteLine(JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(body), json)); }
        catch { Console.WriteLine(body); }
    }
    return response.IsSuccessStatusCode ? 0 : 1;

    async Task<HttpResponseMessage> Command(string session, string token, ActionCommand action) =>
        await SendWithToken(HttpMethod.Post, $"/v1/sessions/{session}/commands", token,
            new CommandRequest { Sequence = Environment.TickCount64, Actions = new[] { action } });

    async Task<HttpResponseMessage> SendWithToken(HttpMethod method, string path, string token, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Inputd-Token", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: json);
        return await http.SendAsync(request);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static string? TakeOption(List<string> values, string name)
{
    var index = values.IndexOf(name);
    if (index < 0) return null;
    if (index + 1 >= values.Count) throw new ArgumentException($"{name} requires a value.");
    var value = values[index + 1];
    values.RemoveRange(index, 2);
    return value;
}

static void Require(IReadOnlyList<string> values, int count, string usage)
{
    if (values.Count < count) throw new ArgumentException("Usage: inputctl " + usage);
}

static void Help() => Console.WriteLine(@"
inputctl [--url http://127.0.0.1:17841] [--access-token TOKEN] <command>

  health
  create [lease-timeout-ms] [client-process-id]
  status <session> <token>
  arm <session> <token> [seconds]
  disarm <session> <token>
  release-all <session> <token>
  cancel <session> <token>
  move <session> <token> <dx> <dy> [duration-ms]
  key <session> <token> <code> <down|up|press|hold> [duration-ms]
  click <session> <token> <left|right|middle|x1|x2>
  type <session> <token> <text>
  resample <source.jsonl> [output.jsonl] [--hz 20|40|60|CUSTOM] [--allow-gaps] [--overwrite]

The service starts disarmed. Bind/create while the intended target window is foreground,
then arm explicitly. Ctrl+Alt+F12 is the global emergency stop.");

static string? ReadDefaultAccessToken()
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inputd", "access.token");
    return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
