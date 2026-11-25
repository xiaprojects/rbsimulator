// This file is part of RB.
//
// Copyright (C) 2026 XIAPROJECTS SRL
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.
//
// This source is part of the project RB: RB Simulator
// (github.com/xiaprojects/rbsimulator).
//
// Community edition will be free for all builders and personal use as defined by the licensing model
// Dual licensing for commercial agreement is available
// Please join Discord community
//
// Program.cs: rbsim-msfs entry point; the HTTP and WebSocket server over the latest SimState.

using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using RbSim.Core;
using RbSim.Msfs;

WrapperOptions options;
Rb01Checklist checklist;
try
{
    options = WrapperOptions.Parse(args, out bool help);
    if (help)
    {
        Console.WriteLine(WrapperOptions.Usage);
        return 0;
    }
    if (options.ListPorts)
    {
        foreach (var name in System.IO.Ports.SerialPort.GetPortNames().Order()) Console.WriteLine(name);
        return 0;
    }
    // Refused, not left retrying: SimConnect.dll exists only on Windows, so this source could never connect.
    if (options.Source == "msfs" && !OperatingSystem.IsWindows())
        throw new ArgumentException("the msfs source needs Windows (SimConnect.dll); on this system use --source synthetic");
    try
    {
        checklist = new Rb01Checklist(options.Checklist is string path ? File.ReadAllText(path) : null);
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
    {
        throw new ArgumentException($"checklist {options.Checklist}: {ex.Message}");
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run rbsim-msfs --help for the options.");
    return 2;
}

string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
string wrapperId = $"rbsim-msfs {version} {options.Source} {os}/{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://{options.Listen}:{options.Port}");
builder.Logging.AddSimpleConsole(c => { c.SingleLine = true; c.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();
var log = app.Logger;

var store = new StateStore(options.Source);
var actuators = new ActuatorManager(options.Control.Enabled ? options.Control.Groups : []);
ISource source = options.Source == "synthetic"
    ? new SyntheticSource(store, actuators)
    : new MsfsSource(store, options, actuators, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("msfs"));
var serialLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("serial");
var serialOutputs = options.Serial.Where(s => s.Enabled).Select(s => new SerialOutput(store, s, options.Ems, serialLog)).ToList();
var udpLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("udp");
var udpOutputs = options.Udp.Where(u => u.Enabled).Select(u => new UdpOutput(store, u, options.Ems, udpLog)).ToList();

var canonicalJson = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = false,
};

JsonObject StateMessage(bool includeSim) =>
    RbSimIf.State(store.Snapshot, store.Seq, StateStore.Now, options.Ems, wrapperId, includeSim);

JsonObject StatusMessage()
{
    double now = StateStore.Now;
    var s = store.Snapshot;
    var st = store.Status;
    var state = StateMessage(false);
    return new JsonObject
    {
        ["v"] = RbSimIf.Version,
        ["wrapper"] = wrapperId,
        ["src"] = s.Sim.Source,
        ["connected"] = s.Sim.Connected,
        ["paused"] = s.Sim.Connected && s.Sim.Paused,
        ["sim"] = new JsonObject { ["name"] = s.Sim.SimName, ["version"] = s.Sim.SimVersion, ["aircraft"] = s.Sim.Aircraft, ["tail"] = s.Sim.Tail },
        ["simconnect"] = new JsonObject
        {
            ["dll"] = st.DllPath,
            ["open"] = st.LinkOpen,
            ["sim_running"] = st.SimRunning,
            ["pause_flags"] = st.PauseFlags,
            ["rejected_datums"] = new JsonArray([.. st.RejectedDatums.Select(n => (JsonNode)n)]),
            ["last_exception"] = st.LastException,
        },
        ["rate_hz"] = new JsonObject
        {
            ["frames"] = Math.Round(st.Frames.Hz(now), 1),
            ["state"] = Math.Round(st.Published.Hz(now), 1),
        },
        ["fresh"] = new JsonObject
        {
            ["gps"] = state["gps"] is not null,
            ["ahrs"] = state["ahrs"] is not null,
            ["baro"] = state["baro"] is not null,
            ["airspeed"] = state["airspeed"] is not null,
            ["ems"] = state["ems"] is not null,
        },
        ["traffic_count"] = s.Traffic.Count,
        ["teleport_seq"] = s.Sim.TeleportSeq,
        ["last_error"] = st.LastError,
        ["config"] = options.ConfigPath,
        ["rb01_web_dir"] = options.Rb01WebDir,
        ["control"] = ControlSummary(),
        ["serial"] = new JsonArray([.. serialOutputs.Select(o => (JsonNode)new JsonObject
        {
            ["name"] = o.Status.Name,
            ["port"] = o.Status.Port,
            ["profile"] = o.Status.Profile,
            ["open"] = o.Status.Open,
            ["lines_per_s"] = Math.Round(o.Status.Lines.Hz(now), 1),
            ["lines_sent"] = o.Status.LinesSent,
            ["bytes_sent"] = o.Status.BytesSent,
            ["write_timeouts"] = o.Status.WriteTimeouts,
            ["last_error"] = o.Status.LastError,
            ["last_device_line"] = o.Status.LastDeviceLine,
        })]),
        ["udp"] = new JsonArray([.. udpOutputs.Select(o => (JsonNode)new JsonObject
        {
            ["name"] = o.Status.Name,
            ["target"] = o.Status.Target,
            ["profile"] = o.Status.Profile,
            ["lines_per_s"] = Math.Round(o.Status.Lines.Hz(now), 1),
            ["lines_sent"] = o.Status.LinesSent,
            ["datagrams"] = o.Status.Datagrams,
            ["last_error"] = o.Status.LastError,
        })]),
        ["nav"] = Guidance.Compute(s, now) is { } g
            ? new JsonObject
            {
                ["from"] = g.FromId,
                ["to"] = g.ToId,
                ["dist_nm"] = Math.Round(Units.MToNm(g.DistanceM), 1),
                ["brg_true_deg"] = Math.Round(g.BearingTrueDeg, 0),
                ["xtk_nm"] = Math.Round(Units.MToNm(g.XtkM), 2), // + right of course
            }
            : null,
    };
}

JsonObject ControlSummary() => new()
{
    ["enabled"] = options.Control.Enabled,
    ["allow_remote"] = options.Control.AllowRemote,
    ["groups"] = new JsonArray([.. actuators.Allowed.Select(g => (JsonNode)g.ToString().ToLowerInvariant())]),
    ["unsupported"] = new JsonArray([.. Actuators.All.Where(a => !actuators.IsSupported(a)).Select(a => (JsonNode)a.Name)]),
};

JsonObject ControlMessage()
{
    var o = ControlSummary();
    o["connected"] = store.Snapshot.Sim.Connected;
    o["actuators"] = actuators.View(store.Snapshot, StateStore.Now);
    return o;
}

// Writes move the simulator, so they are narrower than reads: off with --no-control, this PC
// only unless allow_remote, and never from a web page of another origin (a page the pilot
// happens to have open could otherwise post to localhost).
string? ControlRefusal(HttpContext ctx)
{
    if (!options.Control.Enabled) return "control is disabled (--no-control or control.enabled = false)";
    var ip = ctx.Connection.RemoteIpAddress;
    if (!options.Control.AllowRemote && (ip is null || !System.Net.IPAddress.IsLoopback(ip)))
        return "control from other computers is disabled (control.allow_remote = false)";
    if (ForeignOrigin(ctx) is string origin) return $"control from origin {origin} is refused";
    return null;
}

// The Origin of a browser request from a page on another host; null for the wrapper's own pages
// and for clients that send none (curl, scripts).
static string? ForeignOrigin(HttpContext ctx)
{
    string origin = ctx.Request.Headers.Origin.ToString();
    return origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var u)
            && string.Equals(u.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)) ? origin : null;
}

// Validates and queues a command object. All or nothing: one bad key rejects the whole set.
(int Code, JsonObject Body) ApplyControl(JsonNode? body)
{
    if (body is not JsonObject cmd || cmd.Count == 0)
        return (400, new JsonObject { ["ok"] = false, ["errors"] = new JsonArray("expected a JSON object of controls") });
    var snap = store.Snapshot;
    if (!snap.Sim.Connected)
        return (503, new JsonObject { ["ok"] = false, ["errors"] = new JsonArray("simulator not connected") });
    var errors = actuators.Parse(cmd, snap, out var commands);
    if (errors.Count > 0)
        return (400, new JsonObject { ["ok"] = false, ["errors"] = new JsonArray([.. errors.Select(e => (JsonNode)e)]) });
    actuators.Submit(commands, StateStore.Now);
    var accepted = new JsonObject();
    foreach (var (a, v) in commands) accepted[a.Name] = ActuatorManager.Show(a, v);
    log.LogInformation("Control: {Commands}", accepted.ToJsonString());
    return (202, new JsonObject { ["ok"] = true, ["accepted"] = accepted });
}

static IResult Json(JsonNode node, int code = 200) => Results.Text(node.ToJsonString(), "application/json", Encoding.UTF8, code);

static IResult Refuse(string why, int code) => Json(new JsonObject { ["ok"] = false, ["errors"] = new JsonArray(why) }, code);

// The body of a write to the simulator, or why it is refused: the control rules (ControlRefusal),
// then application/json only (a cross-origin browser POST of this type needs a preflight, which
// is never granted), then valid JSON.
async Task<(JsonNode? Body, IResult? Refused)> ControlBody(HttpContext ctx)
{
    if (ControlRefusal(ctx) is string why) return (null, Refuse(why, 403));
    if (!(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        return (null, Refuse("Content-Type must be application/json", 415));
    try
    {
        var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        _ = (body as JsonObject)?.Count; // materialise now, so a duplicate key fails here
        return (body, null);
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException)
    {
        return (null, Refuse($"invalid JSON: {ex.Message}", 400));
    }
}

// Any page on the LAN may poll the status (read-only data, no credentials).
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.AccessControlAllowOrigin = "*";
    await next();
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// The RB-01 HMI files, e.g. a roastbeef working tree's web/RB-01. no-cache makes the browser
// revalidate, so an edit there shows on the next reload.
if (options.Rb01WebDir is string rb01WebDir)
{
    app.UseFileServer(new FileServerOptions
    {
        RequestPath = "/RB-01",
        FileProvider = new PhysicalFileProvider(rb01WebDir),
        StaticFileOptions = { OnPrepareResponse = f => f.Context.Response.Headers.CacheControl = "no-cache" },
    });
}

// The HMI's shared files (/css, /js, /img, /plates, /settings, /synthview, as on RB-01), from
// wwwroot beside the executable. Its index.html is the embedded status page, never copied there.
string sharedWebDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(sharedWebDir))
{
    var types = new FileExtensionContentTypeProvider();
    types.Mappings.TryAdd(".glb", "model/gltf-binary"); // synthview aircraft models
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(sharedWebDir),
        ContentTypeProvider = types,
        OnPrepareResponse = f => f.Context.Response.Headers.CacheControl = "no-cache",
    });
}

app.MapGet("/", () =>
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RbSim.Msfs.wwwroot.index.html")!;
    using var reader = new StreamReader(stream);
    return Results.Content(reader.ReadToEnd(), "text/html", Encoding.UTF8);
});
app.MapGet("/api/state", () => Json(StateMessage(true)));
app.MapGet("/api/traffic", () => Json(RbSimIf.Traffic(store.Snapshot, StateStore.Now)));
app.MapGet("/api/simstate", () => Results.Text(JsonSerializer.Serialize(store.Snapshot, canonicalJson), "application/json", Encoding.UTF8));
app.MapGet("/api/status", () => Json(StatusMessage()));
app.MapGet("/api/radio", () => Json(RadioView.Build(store.Snapshot)));
app.MapGet("/api/control", () => Json(ControlMessage()));
app.MapPost("/api/control", async (HttpContext ctx) =>
{
    var (body, refused) = await ControlBody(ctx);
    if (refused is not null) return refused;
    var (code, result) = ApplyControl(body);
    return Json(result, code);
});

// The WebSocket carries the same v1 messages unchanged (RB-SIM-IF-090): state messages at the
// requested rate, a traffic message (it has "targets") once a second. A client may send
// {"control": {...}} and gets {"control_result": {...}} back; with ?control=1 it also gets
// {"control_status": {...}} (the GET /api/control body) twice a second.
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    double rate = double.TryParse(ctx.Request.Query["rate"], System.Globalization.CultureInfo.InvariantCulture, out var r) ? Math.Clamp(r, 1, 30) : 10;
    bool controlStatus = ctx.Request.Query["control"] == "1";
    string? refusal = ControlRefusal(ctx); // decided once, at the upgrade
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var ct = ctx.RequestAborted;
    var sendLock = new SemaphoreSlim(1, 1); // one sender at a time on a WebSocket

    async Task SendLocked(JsonNode msg)
    {
        await sendLock.WaitAsync(ct);
        try { await Send(ws, msg, ct); }
        finally { sendLock.Release(); }
    }

    _ = Task.Run(async () =>
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                int n = 0;
                bool tooLong = false;
                ValueWebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(buf.AsMemory(n), ct);
                    n += res.Count;
                    if (n == buf.Length && !res.EndOfMessage)
                    {
                        tooLong = true;
                        n = 0; // discard the rest of an oversized message
                    }
                } while (!res.EndOfMessage);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
                if (res.MessageType != WebSocketMessageType.Text) continue;
                await SendLocked(new JsonObject { ["control_result"] = WsControl(Encoding.UTF8.GetString(buf, 0, n), refusal, !tooLong) });
            }
        }
        catch (Exception) { /* connection gone */ }
    }, ct);

    double nextTraffic = 0, nextSim = 0, nextControl = 0;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / rate));
    try
    {
        while (ws.State == WebSocketState.Open && await timer.WaitForNextTickAsync(ct))
        {
            double now = StateStore.Now;
            bool includeSim = now >= nextSim;
            if (includeSim) nextSim = now + 10;
            await SendLocked(StateMessage(includeSim));
            if (now >= nextTraffic)
            {
                nextTraffic = now + 1;
                await SendLocked(RbSimIf.Traffic(store.Snapshot, now));
            }
            if (controlStatus && now >= nextControl)
            {
                nextControl = now + 0.5;
                await SendLocked(new JsonObject { ["control_status"] = ControlMessage() });
            }
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { /* client left */ }
});

JsonObject WsControl(string text, string? refusal, bool complete)
{
    JsonObject Error(string e) => new() { ["ok"] = false, ["errors"] = new JsonArray(e) };
    if (!complete) return Error("message too long");
    JsonNode? msg;
    try
    {
        msg = JsonNode.Parse(text);
        _ = (msg as JsonObject)?.Count;
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException)
    {
        return Error($"invalid JSON: {ex.Message}");
    }
    if (msg is not JsonObject o || !o.ContainsKey("control")) return Error("expected {\"control\": {...}}");
    if (refusal is not null) return Error(refusal);
    return ApplyControl(o["control"]).Body;
}

static Task Send(WebSocket ws, JsonNode msg, CancellationToken ct) =>
    ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

// The RB-01 daemon's HMI sockets, so the HMI at /RB-01/ runs on the wrapper's data. The frames
// (Rb01Hmi) come from the same state message as /api/state, at the daemon's rates within protocol
// §4.3. The daemon's event-driven sockets have nothing to say here: they are accepted and stay
// quiet, as the daemon's are while nothing happens, so the HMI stops reconnecting every second.
var hmi = new Rb01Hmi();
string hmiVersion = $"rbsim-msfs {version}";
app.Map("/situation", ctx => HmiSocket(ctx, 0.1, now => [hmi.Situation(StateMessage(false), now)]));
app.Map("/traffic", ctx => HmiSocket(ctx, 1, now => Rb01Hmi.Traffic(store.Snapshot, now, DateTime.UtcNow)));
app.Map("/radar", ctx => HmiSocket(ctx, 1, now => Rb01Hmi.Traffic(store.Snapshot, now, DateTime.UtcNow), Rb01Hmi.RadarSettings()));
app.Map("/ems", ctx => HmiSocket(ctx, 0.5, now => [Rb01Hmi.Ems(StateMessage(false))]));
app.Map("/status", ctx => HmiSocket(ctx, 1, now => [Rb01Hmi.Status(StateMessage(false), hmiVersion, now)]));
foreach (var path in new[] { "/keypad", "/alerts", "/gdl90", "/weather" })
    app.Map(path, ctx => HmiSocket(ctx, 0, null));
// The sim GPS's active leg (D25): the socket at the daemon's 1 s, and its REST twin's GET, the
// waypoint list the HSI and the path are drawn from. The sim's GPS owns the plan, so the HMI's
// POST (load a plan), PUT (start) and DELETE (stop) change nothing; they get the daemon's reply.
app.Map("/autopilot", async ctx =>
{
    if (ctx.WebSockets.IsWebSocketRequest)
    {
        await HmiSocket(ctx, 1, now => [Rb01Hmi.Autopilot(store.Snapshot, now)]);
        return;
    }
    await Json(HttpMethods.IsGet(ctx.Request.Method) ? Rb01Hmi.Waypoints(store.Snapshot, StateStore.Now) : new JsonArray()).ExecuteAsync(ctx);
});
var bridge = new Rb01Bridge();
app.Map("/bridge/float/ws", ctx =>
{
    long seen = bridge.Version; // as on the daemon, a client gets the changes, not the map on connect
    return HmiSocket(ctx, 0.2, now => bridge.Changes(ref seen));
});

// The radio plate on the sim's COM radios (D26). /radioStatus sends the radio list on connect,
// when it changes (checked at 2 Hz) and after each plate post, refused or not, so the plate always
// comes back to what the sim has. The daemon sends it after a post only: its radios change nowhere else.
long radioPosts = 0;
app.Map("/radioStatus", ctx =>
{
    string? sent = null;
    long seen = -1;
    return HmiSocket(ctx, 0.5, now =>
    {
        long posts = Interlocked.Read(ref radioPosts);
        var list = Rb01Radio.List(store.Snapshot, now);
        string json = list.ToJsonString();
        if (json == sent && posts == seen) return [];
        (sent, seen) = (json, posts);
        return [list];
    });
});
app.MapGet("/radio", () => Json(Rb01Radio.List(store.Snapshot, StateStore.Now)));
// A frequency set on the plate is a control command (Rb01Radio.Commands), under the rules of
// POST /api/control. The answer is the list once the sim's read-back has settled, so the plate's
// next GET shows what the sim took, as the daemon answers once its radio has the frequency.
app.MapPost("/radio/{index:int}", async (HttpContext ctx, int index) =>
{
    try
    {
        if (index < 0 || index >= Rb01Radio.Count) return Refuse($"no radio {index}: the radios are 0 (COM1) and 1 (COM2)", 404);
        var (body, refused) = await ControlBody(ctx);
        if (refused is not null) return refused;
        if (Rb01Radio.Commands(index, body, store.Snapshot, out var commands) is string why) return Refuse(why, 400);
        if (commands.Count > 0)
        {
            var (code, result) = ApplyControl(commands);
            if (code != StatusCodes.Status202Accepted) return Json(result, code);
            var names = commands.Select(c => c.Key).ToList();
            // Up to 0.5 s for the rate limit, then up to 2 s for the read-back (ActuatorManager).
            for (int i = 0; i < 60 && !actuators.Settled(names); i++) await Task.Delay(50, ctx.RequestAborted);
        }
        return Json(Rb01Radio.List(store.Snapshot, StateStore.Now));
    }
    finally
    {
        Interlocked.Increment(ref radioPosts);
    }
});

// The checklist plate's list (Rb01Checklist), from --checklist, in memory as on the daemon. The
// plate posts it whole after each change, with any content type, as the daemon takes it. DELETE
// restores the file's list, for the next student.
app.MapGet("/checklist/default/status", () => Json(checklist.Snapshot()));
app.MapPost("/checklist/default/status", async (HttpContext ctx) =>
{
    if (ForeignOrigin(ctx) is string origin) return Refuse($"origin {origin} is refused", 403);
    string? text = await ReadText(ctx, Rb01Checklist.MaxChars);
    if (text is null) return Refuse($"a checklist is at most {Rb01Checklist.MaxChars} characters", 413);
    string? why = checklist.Post(text);
    return why is null ? Json(new JsonObject()) : Refuse(why, 400);
});
app.MapDelete("/checklist/default/status", (HttpContext ctx) =>
{
    if (ForeignOrigin(ctx) is string origin) return Refuse($"origin {origin} is refused", 403);
    checklist.Reset();
    log.LogInformation("Checklist reset to {File}", options.Checklist ?? "the empty list");
    return Json(new JsonObject());
});

// The HMI's aircraft profile, wwwroot's settings/aircraft.json, named after the sim aircraft's tail
// number (D26): the aircraft plate shows it. A matched endpoint comes before the static file. The
// HMI reads the profile once, when it loads.
app.MapGet("/settings/aircraft.json", (HttpContext ctx) =>
{
    string path = Path.Combine(sharedWebDir, "settings", "aircraft.json");
    if (!File.Exists(path)) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "no-cache";
    string text = File.ReadAllText(path);
    try
    {
        if (JsonNode.Parse(text) is JsonObject profile) return Json(Rb01Hmi.Aircraft(profile, store.Snapshot.Sim.Tail));
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException) { /* served as it is, below */ }
    return Results.Text(text, "application/json", Encoding.UTF8);
});

// The daemon's REST calls the HMI makes on boot and on some plates. The wrapper has no addons
// (those in RB-01/addons write settings files and magnetometer calibration it doesn't keep), no
// recordings and no updates (a SIM build never offers flight packages), so these answer the
// daemon's empty list. The addons bridge is kept, without its airspeed input (Rb01Bridge).
foreach (var path in new[] { "/addons", "/playback", "/updates/available" })
    app.MapGet(path, () => Json(new JsonArray()));
app.MapGet("/bridge/float", () => Json(bridge.Snapshot()));
app.MapPost("/bridge/float", async (HttpContext ctx) =>
{
    if (ForeignOrigin(ctx) is string origin) return Json(new JsonObject { ["ok"] = false, ["errors"] = new JsonArray($"origin {origin} is refused") }, 403);
    using var body = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    // Any content type: the addons' own curl examples post with curl's form default.
    string? why = bridge.Post(await body.ReadToEndAsync(ctx.RequestAborted));
    return why is null ? Results.Ok() : Json(new JsonObject { ["ok"] = false, ["errors"] = new JsonArray(why) }, 400);
});

// A request body as text; null when it is longer than maxChars.
static async Task<string?> ReadText(HttpContext ctx, int maxChars)
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var text = new StringBuilder();
    var buf = new char[4096];
    int n;
    while ((n = await reader.ReadAsync(buf, ctx.RequestAborted)) > 0)
    {
        text.Append(buf, 0, n);
        if (text.Length > maxChars) return null;
    }
    return text.ToString();
}

// Sends the first frame, then frames every periodS until the client leaves. Only this loop
// sends; the reader just drains what the client says and notices when it closes.
async Task HmiSocket(HttpContext ctx, double periodS, Func<double, IEnumerable<JsonNode>>? frames, JsonNode? first = null)
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound; // the daemon's REST twin of this path isn't served
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var ct = ctx.RequestAborted;
    var reader = Task.Run(async () =>
    {
        var buf = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open && (await ws.ReceiveAsync(buf.AsMemory(), ct)).MessageType != WebSocketMessageType.Close) { }
        }
        catch (Exception) { /* connection gone */ }
    }, ct);
    try
    {
        if (first is not null) await Send(ws, first, ct);
        if (frames is null)
        {
            await reader;
        }
        else
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(periodS));
            do
            {
                foreach (var f in frames(StateStore.Now)) await Send(ws, f, ct);
            } while (ws.State == WebSocketState.Open && await timer.WaitForNextTickAsync(ct));
        }
        if (ws.State == WebSocketState.CloseReceived)
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { /* client left */ }
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    log.LogInformation("{Wrapper} listening on http://{Listen}:{Port}/", wrapperId, options.Listen, options.Port);
    log.LogInformation("Configuration: {Config}", options.ConfigPath ?? "none (defaults and command line)");
    if (options.Rb01WebDir is not null) log.LogInformation("RB-01 HMI at /RB-01/ from {Dir}", options.Rb01WebDir);
    if (Directory.Exists(sharedWebDir)) log.LogInformation("HMI shared files at / from {Dir}", sharedWebDir);
    source.Start();
    foreach (var o in serialOutputs) o.Start();
    foreach (var o in udpOutputs) o.Start();
});
app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var o in udpOutputs) o.Stop();
    foreach (var o in serialOutputs) o.Stop();
    source.Stop();
});
app.Run();
return 0;
