using Option.Data.Shared.Poco;

namespace Option.Data.Ui.Models;

public class OptionViewModel
{
    public int SelectedCurrencyId { get; set; }
    public string SelectedExpiration { get; set; } = string.Empty;
    public DateTimeOffset SelectedDateTime { get; set; }
    public List<CurrencyType> Currencies { get; set; } = new();
    public List<string> Expirations { get; set; } = new();
    public List<DateTimeOffset> AvailableDates { get; set; } = new();
}