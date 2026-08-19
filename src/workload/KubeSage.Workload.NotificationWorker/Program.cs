using KubeSage.Workload.NotificationWorker;
using KubeSage.Workload.Shared;

// Notification Worker.
//
// A background processor rather than a request handler. It exists to add a
// failure mode the HTTP services cannot show: a pod that Kubernetes considers
// perfectly healthy while it has silently stopped doing any useful work.
//
// It still runs a small web host, purely so it can expose the same health
// probes and the same /metrics endpoint as everything else.

var builder = WebApplication.CreateBuilder(args);
builder.AddWorkloadDefaults("notification-worker");

var connectionString = builder.Configuration.GetConnectionString("WorkloadDatabase")
                       ?? throw new InvalidOperationException(
                           "ConnectionStrings__WorkloadDatabase must be configured.");

builder.Services.AddSingleton(new NotificationRepository(connectionString));
builder.Services.AddHostedService<NotificationProcessor>();

var app = builder.Build();
app.UseWorkloadDefaults();

app.Run();
