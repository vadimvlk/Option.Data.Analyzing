using Option.Data.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class OptionModel : BaseOptionPageModel
{
    public OptionModel(ApplicationDbContext context, IMemoryCache cache, ILogger<OptionModel> logger) 
        : base(context, cache, logger) { }

    public async Task OnGetAsync() => await LoadCommonDataAsync();

    public async Task<IActionResult> OnPostLoadDataAsync() => await LoadFilteredDataAsync();
}