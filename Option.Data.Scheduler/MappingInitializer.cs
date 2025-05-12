using Mapster;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;
using Extensions.Hosting.AsyncInitialization;

namespace Option.Data.Scheduler;

public class MappingInitializer : IAsyncInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        TypeAdapterConfig<BookSummaryData, OptionData>
            .NewConfig()
            .Map(dest => dest.MarkPrice, src => src.MarkPrice ?? 0)
            .Map(dest => dest.UnderlyingPrice, src => src.UnderlyingPrice ?? 0)
            .Map(dest => dest.DeliveryPrice, src => src.EstimatedDeliveryPrice ?? 0)
            .Map(dest => dest.Iv, src => src.MarkIv ?? 0)
            .Map(dest => dest.OpenInterest, src => src.OpenInterest ?? 0)
            .Map(dest => dest.InstrumentName, src => src.InstrumentName)
            .Ignore(ignore => ignore.CreatedAt);

        return Task.CompletedTask;
    }
}