using System.Net;
using Polly;
using Polly.Extensions.Http;

namespace Option.Data.Shared;

internal static class Helpers
{
    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            // HandleTransientHttpError покрывает сеть/5xx/408, но НЕ 429 — добавляем явно,
            // чтобы Too Many Requests от Deribit ретраился, а не ронял сбор всей валюты.
            .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(5, retryAttempt
                => TimeSpan.FromSeconds(Math.Min(Math.Pow(3, retryAttempt), 90)));
}