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
using RbSim.Core;
using RbSim.Msfs;

WrapperOptions options;
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
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run rbsim-msfs --help for the options.");
    return 2;
}

string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
string wrapperId = $"rbsim-msfs {version} {options.Source} windows/{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

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
        ["sim"] = new JsonObject { ["name"] = s.Sim.SimName, ["version"] = s.Sim.SimVersion, ["aircraft"] = s.Sim.Aircraft },
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
    string origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var u)
            && string.Equals(u.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        return $"control from origin {origin} is refused";
    return null;
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

// Any page on the LAN may poll the status (read-only data, no credentials).
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.AccessControlAllowOrigin = "*";
    await next();
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

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
    if (ControlRefusal(ctx) is string why) return Json(new JsonObject { ["ok"] = false, ["errors"] = new JsonArray(why) }, 403);
    // application/json only: a cross-origin browser POST of this type needs a preflight, which is never granted.
    if (!(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        return Json(new JsonObject { ["ok"] = false, ["errors"] = new JsonArray("Content-Type must be application/json") }, 415);
    JsonNode? body;
    try
    {
        body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        _ = (body as JsonObject)?.Count; // materialise now, so a duplicate key fails here
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException)
    {
        return Json(new JsonObject { ["ok"] = false, ["errors"] = new JsonArray($"invalid JSON: {ex.Message}") }, 400);
    }
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

app.Lifetime.ApplicationStarted.Register(() =>
{
    log.LogInformation("{Wrapper} listening on http://{Listen}:{Port}/", wrapperId, options.Listen, options.Port);
    log.LogInformation("Configuration: {Config}", options.ConfigPath ?? "none (defaults and command line)");
    source.Start();
    foreach (var o in serialOutputs) o.Start();
});
app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var o in serialOutputs) o.Stop();
    source.Stop();
});
app.Run();
return 0;
