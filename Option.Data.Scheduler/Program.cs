using Option.Data.Database;
using Quartz;
using Option.Data.Scheduler.Jobs;
using Option.Data.Shared;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.RegisterDeribit();


//Register PostgresSql.
builder.Services.RegisterData(builder.Configuration);

builder.Services.AddQuartz(q =>
{
    // Register your job
    JobKey jobKey = new JobKey("Deribit");
    q.AddJob<DeribitJob>(opts => opts.WithIdentity(jobKey));
    
    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity(nameof(DeribitJob))
        .WithCronSchedule("* * * * * ?"));

       // .WithCronSchedule("0 0 0/3 * * ?")); // Run every 3 hours starting at 00:00 UTC

});

builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

IHost host = builder.Build();
host.Run();