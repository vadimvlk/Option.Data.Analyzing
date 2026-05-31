using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Option.Data.Shared.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class ExposureModel(
    ApplicationDbContext context,
    IMemoryCache cache,
    ILogger<ExposureModel> logger,
    IHttpClientFactory clientFactory,
    IExpirationAnalysisBuilder builder) : BaseOptionPageModel(context, cache: cache, logger)
{
    [BindProperty]
    public ExposureViewModel ExposureViewModel { get; set; } = new();


    private readonly HttpClient _httpClient = clientFactory.CreateClient(DeribitConfig.ClientName);
    private readonly ApplicationDbContext _context = context;
    private readonly IMemoryCache _cache = cache;

    public async Task OnGetAsync() => await LoadCommonDataAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonDataAsync();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            string dataCacheKey = $"ExposureData_{ViewModel.SelectedCurrencyId}_{ViewModel.SelectedDateTime}";

            // Получаем все данные по опционам для выбранной даты и валюты.
            List<DeribitData> allOptionData = (await _cache.GetOrCreateAsync(dataCacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                // Конвертация в OptionData идёт по OptionTypeId (см. ExpirationAnalysisBuilder),
                // поэтому навигационное свойство Type грузить не нужно.
                return await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                                d.CreatedAt == ViewModel.SelectedDateTime)
                    .ToListAsync();
            }))!;

            ExposureViewModel.ExpirationsData =
                builder.Build(allOptionData, ViewModel.Expirations, ViewModel.SelectedDateTime);

            return Page();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading exposure analysis data");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            return Page();
        }
    }

    protected override async Task<List<string>> GetOrCreateExpirationsAsync()
    {
        return (await _cache.GetOrCreateAsync("AvailableExpirations", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

            Expirations? expirations =
                await _httpClient.GetFromJsonAsync<Expirations>("get_expirations?currency=any&kind=option");

            if (expirations?.Data.Options != null && expirations.Data.Options.Count != 0)
                return expirations.Data.Options;

            logger.LogWarning("No data returned for expirations");
            return [];
        }))!;
    }
}
