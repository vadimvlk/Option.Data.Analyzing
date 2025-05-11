using Quartz;

namespace Option.Data.Scheduler.Jobs;

[DisallowConcurrentExecution]
public class DeribitJob: IJob
{
    private readonly ILogger<DeribitJob> _logger;

    public DeribitJob(ILogger<DeribitJob> logger)
    {
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("DeribitJob executed at: {time}", DateTimeOffset.Now);
        
        return Task.CompletedTask;
    }
}