using KubeSage.Workload.Shared;
using KubeSage.Workload.TrafficGenerator;

// Traffic Generator.
//
// Continuously exercises the demo application so the cluster is never idle.
// This is what makes the whole project work: detection rules need a baseline
// of normal behaviour to compare against, and an empty cluster produces no
// baseline at all.
//
// It starts automatically with the workload and needs no prompting.

var builder = WebApplication.CreateBuilder(args);
builder.AddWorkloadDefaults("traffic-generator");

builder.Services.Configure<TrafficOptions>(builder.Configuration.GetSection("Traffic"));
builder.Services.AddHttpClient("gateway", (provider, client) =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Dependencies:Gateway"] ?? "http://gateway:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHostedService<TrafficLoop>();

var app = builder.Build();
app.UseWorkloadDefaults();

app.Run();
