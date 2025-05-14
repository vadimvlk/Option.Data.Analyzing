using Polly;
using Polly.Extensions.Http;

namespace Option.Data.Shared;

internal static class Helpers
{
    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(5, retryAttempt
                => TimeSpan.FromSeconds(Math.Min(Math.Pow(3, retryAttempt), 90)));
}