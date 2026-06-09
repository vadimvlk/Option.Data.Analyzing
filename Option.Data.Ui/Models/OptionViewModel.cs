using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;

namespace Option.Data.Ui.Models;

public class OptionViewModel
{
    /// <summary>Выбранная биржа-источник (Snapshot). По умолчанию Deribit.</summary>
    public ExchangeSource SelectedExchange { get; set; } = ExchangeSource.Deribit;

    public int SelectedCurrencyId { get; set; }
    public double UnderlyingPrice { get; set; }
    public string SelectedExpiration { get; set; } = string.Empty;
    public DateTimeOffset SelectedDateTime { get; set; }
    public List<CurrencyType> Currencies { get; set; } = new();
    public List<string> Expirations { get; set; } = new();
    public List<DateTimeOffset> AvailableDates { get; set; } = new();
    
    public List<OptionData> OptionData { get; set; } = new();

}