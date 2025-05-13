using System.Text;
using Microsoft.Extensions.Hosting;
using Option.Data.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Option.Data.Shared;

public static class Registration
{
    public static HostApplicationBuilder RegisterDeribit(this HostApplicationBuilder builder)
    {
        IConfigurationSection section = builder.Configuration.GetSection("Deribit");

        builder.Services.Configure<DeribitConfig>(section);

        DeribitConfig config = section.Get<DeribitConfig>()!;

        builder.Services.AddHttpClient(DeribitConfig.ClientName)
            .ConfigureHttpClient(c => { c.BaseAddress = new Uri(config.BaseAddress!); })
            .AddPolicyHandler(Helpers.GetRetryPolicy());

        builder.Services.AddMemoryCache();

        return builder;
    }


    public static HostApplicationBuilder RegisterLog(this HostApplicationBuilder builder)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Debug,
                "[{Timestamp:HH:mm:ss} {SourceContext} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, true);


        return builder;
    }
}