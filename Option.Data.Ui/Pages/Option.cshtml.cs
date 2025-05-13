using Option.Data.Database;
using Option.Data.Ui.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Option.Data.Ui.Pages;

public class OptionModel : PageModel
{
    private readonly ApplicationDbContext _context;
    
    [BindProperty]
    public OptionViewModel ViewModel { get; set; } = new();

    public OptionModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task OnGetAsync()
    {
        // Загружаем доступные валюты
        ViewModel.Currencies = await _context.CurrencyType
            .OrderBy(c => c.Name)
            .ToListAsync();

        // Загружаем даты экспирации (уникальные значения)
        ViewModel.Expirations = await _context.DeribitData
            .Select(o => o.Expiration)
            .Distinct()
            .OrderBy(e => e)
            .ToListAsync();

        // Загружаем доступные даты/время (группируем по CreatedAt)
        ViewModel.AvailableDates = await _context.DeribitData
            .Select(o => o.CreatedAt)
            .Distinct()
            .OrderBy(d => d)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostLoadDataAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }
        
        bool isValidTime = await _context.DeribitData
            .AnyAsync(d => d.CreatedAt == ViewModel.SelectedDateTime);
    
        if (!isValidTime)
        {
            ModelState.AddModelError("ViewModel.SelectedDateTime", 
                "Please select a valid date/time from the available options");
            return Page();
        }

        // Здесь будет обработка загрузки данных
        // Можно добавить логику для фильтрации данных
        
        return Page();
    }
}