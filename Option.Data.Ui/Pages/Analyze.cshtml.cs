using Option.Data.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class AnalyzeModel(ApplicationDbContext context, IMemoryCache cache, ILogger<OptionModel> logger)
    : BaseOptionPageModel(context, cache, logger)
{
    public async Task OnGetAsync() => await LoadCommonDataAsync();

    public async Task<IActionResult> OnPostAsync() => await LoadFilteredDataAsync();
}