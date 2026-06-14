namespace Option.Data.Ui.Models;

/// <summary>
/// «Память контракта»: история выбранной экспирации/окна по срезам — реализованный диапазон,
/// гамма/OI на экстремумах, миграция gamma-flip, набор/сброс OI на стенах, репрезентативность OI.
/// Сырые фичи (FlipMigrationUsd/WallFlowOi/NetGexChange/RangePosition) нормируются в BuildSignals
/// (там доступны σ сессии, пик профиля и ΣOI); σ-зависимые поля заполняются в Assemble.
/// </summary>
public class ContractMemory
{
    public bool HasHistory { get; set; }
    public int SnapshotCount { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public double RealizedLow { get; set; }
    public double RealizedHigh { get; set; }
    public DateTimeOffset RealizedLowAt { get; set; }
    public DateTimeOffset RealizedHighAt { get; set; }
    public double RangePosition { get; set; }          // 0..1 по скользящему окну ~20 дней

    public double NetGexAtLow { get; set; }             // USD/1% на срезе минимума цены
    public double NetGexAtHigh { get; set; }            // USD/1% на срезе максимума цены
    public double OiAtLow { get; set; }
    public double OiAtHigh { get; set; }

    public double? FlipNow { get; set; }
    public double? Flip24hAgo { get; set; }
    public double? FlipMigrationUsd { get; set; }       // сырое: FlipNow − Flip24hAgo
    public double? NetGexChange { get; set; }           // сырое: NetGex(spot) − 24ч назад
    public double? CallWallOiFlow { get; set; }         // ΔOI на текущей CALL-стене за 24ч
    public double? PutWallOiFlow { get; set; }          // ΔOI на текущей PUT-стене за 24ч
    public double? WallFlowOi { get; set; }             // сырое: CallWallOiFlow − PutWallOiFlow

    public double RecentRunupPct { get; set; }          // изменение цены за ~24ч, % (знак: + рост)
    public double RecentDrawdownPct { get; set; }       // просадка от цены 24ч назад, %

    public double OiRepresentativeness { get; set; }    // 0..1: доля OI серии от макс. экспирации (1 для Сводки)
    public bool IsThin { get; set; }                    // низкая репрезентативность одиночной серии

    // Заполняются в Assemble (зависят от σ сессии и стопа рекомендации):
    public double? FlipDepthSigmas { get; set; }        // (flip − spot)/σ
    public bool StopInsideBounceZone { get; set; }      // стоп рекомендации в зоне отскока к flip
}
