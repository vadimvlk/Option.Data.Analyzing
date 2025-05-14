using Quartz;
using Option.Data.Shared;
using Option.Data.Database;
using Option.Data.Scheduler;
using Option.Data.Scheduler.Jobs;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.RegisterLog();

builder.Services.AddAsyncInitializer<MappingInitializer>();

builder.RegisterDeribit();

//Register PostgresSql.
builder.Services.RegisterData(builder.Configuration);

builder.Services.AddQuartz(q =>
{
    // Register job's
    JobKey jobKey = new JobKey("Deribit");
    q.AddJob<DeribitJob>(opts => opts.WithIdentity(jobKey));
    
    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity(nameof(DeribitJob))
        .WithCronSchedule("0 0 0/3 * * ?")); // Run every 3 hours starting at 00:00 UTC
    
    JobKey jobCleanupKey = new JobKey("Cleanup");
    q.AddJob<CleanupJob>(opts => opts.WithIdentity(jobCleanupKey));

    q.AddTrigger(opts => opts
        .ForJob(jobCleanupKey)
        .WithIdentity(nameof(CleanupJob))
        .WithCronSchedule("0 0 5 * * ?")); // Runs daily at 05:00 UTC
});

builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

IHost host = builder.Build();

// Initialize async initializer.
await host.InitAsync();

await host.RunAsync();
