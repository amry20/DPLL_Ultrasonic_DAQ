using DPLL_Ultrasonic_DAQ.Hubs;
using DPLL_Ultrasonic_DAQ.Models;
using DPLL_Ultrasonic_DAQ.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
// Serial ports come from serial.json (reloadable at runtime).
builder.Configuration.AddJsonFile("serial.json", optional: true, reloadOnChange: true);
builder.Services.Configure<SerialOptions>(builder.Configuration.GetSection(SerialOptions.SectionName));

// --- Services ---
builder.Services.AddSingleton<SerialDeviceService>();
builder.Services.AddSignalR();

var app = builder.Build();

// --- Static web UI ---
app.UseDefaultFiles();
app.UseStaticFiles();

// --- Live telemetry hub ---
app.MapHub<DpllHub>("/hubs/dpll");

// --- Auto-connect to the ports configured in serial.json ---
var device = app.Services.GetRequiredService<SerialDeviceService>();
app.Lifetime.ApplicationStarted.Register(() =>
{
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
