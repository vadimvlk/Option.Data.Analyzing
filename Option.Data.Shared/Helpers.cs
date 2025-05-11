using Polly;
using Polly.Extensions.Http;

namespace Option.Data.Shared;

internal static class Helpers
{
    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(3, retryAttempt
                => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
}