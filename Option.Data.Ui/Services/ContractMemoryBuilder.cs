using Option.Data.Shared.Dto;
using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>
/// Чистый построитель «памяти контракта» из истории срезов. Использует те же функции, что и
/// рекомендация (<see cref="SessionAnalysisMath"/>) — без дублирования формул. Сырые фичи
/// (миграция flip, поток OI на стенах, тренд Net GEX, положение в диапазоне) нормируются в
/// <c>BuildSignals</c>, где доступны σ сессии, пик профиля и ΣOI.
/// </summary>
public class ContractMemoryBuilder : IContractMemoryBuilder
{
    private const double LookbackHours = 24.0;     // окно динамических фич (миграция flip и т.д.)
    private const double LookbackTolHours = 6.0;
    private const int RangeWindow = 160;           // ~20 дней (8 срезов/день) для реализованного диапазона
    private const double ThinOiShare = 0.10;       // доля OI < этого порога → тонкая серия

    public ContractMemory Build(IReadOnlyList<MemorySnapshot> history, double currentExpirationOi, double maxExpirationOi)
    {
        var m = new ContractMemory();
        if (history is null || history.Count == 0)
            return m;

        int n = history.Count;
        MemorySnapshot cur = history[n - 1];
        m.HasHistory = n >= 2;
        m.SnapshotCount = n;
        m.FirstSeen = history[0].Time;
        m.LastSeen = cur.Time;

        // Реализованный диапазон по скользящему окну ~20 дней.
        int from = Math.Max(0, n - RangeWindow);
        double lo = double.MaxValue, hi = double.MinValue;
        int loIdx = from, hiIdx = from;
        for (int i = from; i < n; i++)
        {
            double s = history[i].Spot;
            if (s <= 0 || !double.IsFinite(s))
                continue;
            if (s < lo) { lo = s; loIdx = i; }
            if (s > hi) { hi = s; hiIdx = i; }
        }
        if (hi > lo)
        {
            m.RealizedLow = lo;
            m.RealizedHigh = hi;
            m.RealizedLowAt = history[loIdx].Time;
            m.RealizedHighAt = history[hiIdx].Time;
            m.RangePosition = SessionAnalysisMath.Clamp((cur.Spot - lo) / (hi - lo), 0, 1);
            m.NetGexAtLow = SessionAnalysisMath.NetGexAtPrice(history[loIdx].Strikes, history[loIdx].Spot);
            m.NetGexAtHigh = SessionAnalysisMath.NetGexAtPrice(history[hiIdx].Strikes, history[hiIdx].Spot);
            m.OiAtLow = TotalOi(history[loIdx].Chain);
            m.OiAtHigh = TotalOi(history[hiIdx].Chain);
        }
        else
        {
            m.RangePosition = 0.5;
        }

        // Текущие профиль/flip/netgex/стены.
        double netGexNow = SessionAnalysisMath.NetGexAtPrice(cur.Strikes, cur.Spot);
        List<GammaProfilePoint> profNow = SessionAnalysisMath.GammaProfile(cur.Strikes, cur.Spot, 0.70, 1.30);
        double? flipNow = SessionAnalysisMath.GammaFlip(profNow, cur.Spot);
        List<SessionAnalysisMath.StrikeGexBreakdown> bdNow = SessionAnalysisMath.StrikeGexAtPrice(cur.Strikes, cur.Spot);
        double? cwNow = SessionAnalysisMath.GexCallWall(bdNow, cur.Spot);
        double? pwNow = SessionAnalysisMath.GexPutWall(bdNow, cur.Spot);
        m.FlipNow = flipNow;

        // Срез ~24ч назад (ближайший в пределах допуска).
        int past = PastIndex(history, n - 1, LookbackHours, LookbackTolHours);
        if (past >= 0)
        {
            MemorySnapshot p = history[past];
            double netGexPast = SessionAnalysisMath.NetGexAtPrice(p.Strikes, p.Spot);
            List<GammaProfilePoint> profPast = SessionAnalysisMath.GammaProfile(p.Strikes, p.Spot, 0.70, 1.30);
            double? flipPast = SessionAnalysisMath.GammaFlip(profPast, p.Spot);
            m.Flip24hAgo = flipPast;

            if (flipNow is { } fn && flipPast is { } fp && double.IsFinite(fn) && double.IsFinite(fp))
                m.FlipMigrationUsd = fn - fp;
            if (double.IsFinite(netGexNow) && double.IsFinite(netGexPast))
                m.NetGexChange = netGexNow - netGexPast;

            // Набор/сброс OI на ТЕКУЩИХ стенах (тот же страйк, текущий срез vs прошлый).
            if (cwNow is { } cw)
                m.CallWallOiFlow = CallOiAt(cur.Chain, cw) - CallOiAt(p.Chain, cw);
            if (pwNow is { } pw)
                m.PutWallOiFlow = PutOiAt(cur.Chain, pw) - PutOiAt(p.Chain, pw);
            if (m.CallWallOiFlow is { } dc && m.PutWallOiFlow is { } dp)
                m.WallFlowOi = dc - dp;

            // Свежий контр-ход за ~24ч.
            if (p.Spot > 0)
            {
                m.RecentRunupPct = (cur.Spot - p.Spot) / p.Spot * 100.0;
                m.RecentDrawdownPct = (p.Spot - cur.Spot) / p.Spot * 100.0;
            }
        }

        // Репрезентативность OI (кросс-секционно на текущем срезе): доля серии от самой ликвидной.
        m.OiRepresentativeness = maxExpirationOi > 0
            ? SessionAnalysisMath.Clamp(currentExpirationOi / maxExpirationOi, 0, 1)
            : 1.0;
        m.IsThin = m.OiRepresentativeness < ThinOiShare;

        return m;
    }

    private static double TotalOi(IReadOnlyList<OptionData> chain)
    {
        double s = 0;
        for (int i = 0; i < chain.Count; i++)
            s += chain[i].CallOi + chain[i].PutOi;
        return s;
    }

    private static double CallOiAt(IReadOnlyList<OptionData> chain, double strike)
    {
        for (int i = 0; i < chain.Count; i++)
            if (chain[i].Strike == strike)
                return chain[i].CallOi;
        return 0;
    }

    private static double PutOiAt(IReadOnlyList<OptionData> chain, double strike)
    {
        for (int i = 0; i < chain.Count; i++)
            if (chain[i].Strike == strike)
                return chain[i].PutOi;
        return 0;
    }

    /// <summary>Индекс среза ~targetH часов назад от jNow (ближайший в [target±tol]), иначе −1.</summary>
    private static int PastIndex(IReadOnlyList<MemorySnapshot> h, int jNow, double targetH, double tol)
    {
        int best = -1;
        double bestErr = double.MaxValue;
        for (int k = jNow - 1; k >= 0; k--)
        {
            double dh = (h[jNow].Time - h[k].Time).TotalHours;
            if (dh > targetH + tol)
                break;
            double err = Math.Abs(dh - targetH);
            if (dh >= targetH - tol && err < bestErr) { bestErr = err; best = k; }
        }
        return best;
    }
}
