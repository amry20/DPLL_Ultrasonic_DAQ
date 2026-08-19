using DPLL_Ultrasonic_DAQ.Hubs;
using DPLL_Ultrasonic_DAQ.Models;
using DPLL_Ultrasonic_DAQ.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
// Serial ports come from serial.json (reloadable at runtime).
builder.Configuration.AddJsonFile("serial.json", optional: true, reloadOnChange: true);
builder.Services.Configure<SerialOptions>(builder.Configuration.GetSection(SerialOptions.SectionName));

// --- Services ---
builder.Services.AddSingleton<SerialDeviceService>();
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    // The JS client reads PascalCase property names (cfg.Kp, t.ReferenceFrequencyHz).
    options.PayloadSerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

// --- Static web UI ---
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Prevent browser from caching JS/CSS — always fetch fresh copy.
        var headers = ctx.Context.Response.Headers;
        headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "0";
    }
});

// --- Live telemetry hub ---
app.MapHub<DpllHub>("/hubs/dpll");

// --- Auto-connect to the ports configured in serial.json ---
var device = app.Services.GetRequiredService<SerialDeviceService>();
var hubContext = app.Services.GetRequiredService<IHubContext<DpllHub>>();

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Broadcast every telemetry frame to all connected SignalR clients.
    device.TelemetryReceived += telemetry =>
        hubContext.Clients.All.SendAsync("Telemetry", telemetry);

    // Broadcast config snapshots (e.g. after a GET refresh).
    device.ConfigurationReceived += config =>
        hubContext.Clients.All.SendAsync("Configuration", config);

    try
    {
        device.Start();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Auto-connect to configured serial ports failed.");
    }
});

app.Run();
