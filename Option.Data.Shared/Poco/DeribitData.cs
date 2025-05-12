using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Option.Data.Shared.Poco;

public class DeribitData
{
    public long Id { get; set; }

    /// <summary>
    /// Открытые позиции.
    /// </summary>
    public double OpenInterest { get; set; }

    /// <summary>
    ///  Страйк.
    /// </summary>
    public int Strike { get; set; }

    /// <summary>
    /// Подразумеваемая волатильность.
    /// </summary>
    public double Iv { get; set; }

    /// <summary>
    ///  Тип опциона.
    /// </summary>
    public int OptionTypeId { get; set; }

    [ForeignKey("OptionTypeId")]
    public OptionType Type { get; set; }

    /// <summary>
    ///  Тип базовой валюты опциона.
    /// </summary>
    public int CurrencyTypeId { get; set; }

    [ForeignKey("CurrencyTypeId")]
    public CurrencyType Currency { get; set; }


    /// <summary>
    ///  Имя контракта.
    /// </summary>
    [Required]
    [Column(TypeName = "citext")]
    public string InstrumentName { get; set; }

    /// <summary>
    ///  Код экспирации.
    /// </summary>
    [Required]
    [Column(TypeName = "citext")]
    public string Expiration { get; set; }

    /// <summary>
    /// Стоимость контракта.
    /// </summary>
    public double UnderlyingPrice { get; set; }

    /// <summary>
    /// Ожидаемая стоимость на дату поставки.
    /// </summary>
    public double DeliveryPrice { get; set; }

    /// <summary>
    /// Теоретическая стоимость в базовой валюте.
    /// </summary>
    public double MarkPrice { get; set; }

    /// <summary>
    /// Дельта опциона.
    /// </summary>
    public double Delta { get; set; }

    /// <summary>
    ///  Гамма опциона.
    /// </summary>
    public double Gamma { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}