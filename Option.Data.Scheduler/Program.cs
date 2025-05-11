using Quartz;
using Option.Data.Scheduler.Jobs;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddQuartz(q =>
{
    // Register your job
    JobKey jobKey = new JobKey("Deribit");
    q.AddJob<DeribitJob>(opts => opts.WithIdentity(jobKey));
    
    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity(nameof(DeribitJob))
        .WithCronSchedule("0 0 0/3 * * ?")); // Run every 3 hours starting at 00:00 UTC

});

builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

IHost host = builder.Build();
host.Run();