namespace Option.Data.Shared.Poco;

public class OptionData
{
    
    public long Id { get; set; }  
    
    /// <summary>
    /// Открытые позиции Call.
    /// </summary>
    public double CallOi { get; set; }

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
    public OptionType Type { get; set; }
    
    /// <summary>
    ///  Тип базовой валюты опциона.
    /// </summary>
    public CurrencyType Currency { get; set; }
    
    /// <summary>
    ///  Имя контракта.
    /// </summary>
    public string InstrumentName { get; set; }
    
    /// <summary>
    ///  Код экспирации.
    /// </summary>
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
    ///  Открытые позиции Put.
    /// </summary>
    public double PutOi { get; set; }

    /// <summary>
    /// Теоретическая стоимость Call. MarkPrice * UnderlyingPrice
    /// </summary>
    public double CallPrice { get; set; }

    /// <summary>
    /// Теоретическая стоимость Put. MarkPrice * UnderlyingPrice
    /// </summary>
    public double PutPrice { get; set; }

    /// <summary>
    /// Дельта опциона Call.
    /// </summary>
    public double? CallDelta { get; set; }

    /// <summary>
    ///  Гамма опциона Call.
    /// </summary>
    public double? CallGamma { get; set; }

    /// <summary>
    /// Дельта опциона Put.
    /// </summary>
    public double? PutDelta { get; set; }

    /// <summary>
    ///  Гамма опциона Put.
    /// </summary>
    public double? PutGamma { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
}