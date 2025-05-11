using Microsoft.Extensions.Hosting;
using Option.Data.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
}