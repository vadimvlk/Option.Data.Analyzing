namespace Option.Data.Shared.Dto;

public record OptionData
{
    /// <summary>
    /// Открытые позиции Call.
    /// </summary>
    public double CallOi { get; set; }

    /// <summary>
    ///  Страйк.
    /// </summary>
    public double Strike { get; set; }

    /// <summary>
    /// Подразумеваемая волатильность.
    /// </summary>
    public double Iv { get; set; }

    /// <summary>
    /// Подразумеваемая волатильность (mark_iv) Put-строки страйка. Нужна для корректной
    /// put-ветви 25Δ Risk Reversal (см. <c>OptionExposureMath.RiskReversal25Delta</c>):
    /// put-нога скоса должна считаться по собственной put-IV, а не по call-IV.
    /// 0, если put-строки нет или греки/IV занулены (страница Snapshot).
    /// </summary>
    public double PutIv { get; set; }

    /// <summary>
    ///  Открытые позиции Put.
    /// </summary>
    public double PutOi { get; set; }

    /// <summary>
    /// Теоретическая стоимость Call.
    /// </summary>
    public double CallPrice { get; set; }

    /// <summary>
    /// Теоретическая стоимость Put.
    /// </summary>
    public double PutPrice { get; set; }

    /// <summary>
    /// Дельта опциона Call.
    /// </summary>
    public double CallDelta { get; set; }

    /// <summary>
    ///  Гамма опциона Call.
    /// </summary>
    public double CallGamma { get; set; }

    /// <summary>
    /// Дельта опциона Put.
    /// </summary>
    public double PutDelta { get; set; }

    /// <summary>
    ///  Гамма опциона Put.
    /// </summary>
    public double PutGamma { get; set; }

    /// <summary>
    /// Лучший бид Call в USD (bid_coin × UnderlyingPrice) — цена, которую реально получает
    /// ПРОДАВЕЦ опциона. ИНВАРИАНТ: как и CallPrice/PutPrice, все цены в OptionData — в USD.
    /// 0 — бида нет либо источник его не отдаёт (исторический путь из БД).
    /// </summary>
    public double CallBid { get; set; }

    /// <summary>Лучший аск Call в USD. 0 — нет аска/источник не отдаёт.</summary>
    public double CallAsk { get; set; }

    /// <summary>Лучший бид Put в USD. 0 — нет бида/источник не отдаёт.</summary>
    public double PutBid { get; set; }

    /// <summary>Лучший аск Put в USD. 0 — нет аска/источник не отдаёт.</summary>
    public double PutAsk { get; set; }
}