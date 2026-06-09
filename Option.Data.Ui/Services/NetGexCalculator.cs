using Option.Data.Ui.Models;
using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Services;

/// <summary>
/// Чистый расчёт Net GEX по цепочке ОДНОЙ экспирации на гамме Black-76.
/// Опирается на <see cref="SessionAnalysisMath"/>; единый для Snapshot и Delta.
///
/// Конвенции (согласованы с <see cref="OptionExposureMath"/> и <see cref="SessionAnalysisMath"/>):
/// <list type="bullet">
/// <item><see cref="OptionData.Iv"/> — mark_iv в ПРОЦЕНТАХ; σ = Iv/100;</item>
/// <item>гамма Black-76 (на форвард), γ ≥ 0; конвенция знака Net GEX — дилер +γ по коллам, −по путам;</item>
/// <item>результаты — USD на 1% движения спота.</item>
/// </list>
/// </summary>
public static class NetGexCalculator
{
    /// <summary>
    /// Строит <see cref="GammaView"/> (профиль Net GEX, gamma-flip, вклад страйков) для
    /// цепочки <paramref name="chain"/> в споте <paramref name="spot"/> при сроке
    /// <paramref name="tYears"/> (доля года до экспирации).
    /// </summary>
    public static GammaView Build(IReadOnlyList<OptionData> chain, double spot, double tYears)
    {
        var view = new GammaView { Spot = spot };

        if (chain.Count == 0 || spot <= 0 || !double.IsFinite(spot))
            return view;

        List<SessionAnalysisMath.GammaStrike> strikes = chain
            .Select(o => new SessionAnalysisMath.GammaStrike(
                Strike: o.Strike,
                CallOi: o.CallOi,
                PutOi: o.PutOi,
                SigmaFraction: o.Iv / 100.0,
                TYears: tYears))
            .Where(s => s.CallOi > 0 || s.PutOi > 0)
            .OrderBy(s => s.Strike)
            .ToList();

        if (strikes.Count == 0)
            return view;

        view.GammaProfile = SessionAnalysisMath.GammaProfile(strikes, spot);
        view.GammaFlip = SessionAnalysisMath.GammaFlip(view.GammaProfile, spot);
        view.NetGexAtSpot = SessionAnalysisMath.NetGexAtPrice(strikes, spot);

        double scale = spot * spot * 0.01;
        view.StrikeGex = strikes
            .Select(s => new StrikeGex
            {
                Strike = s.Strike,
                NetGex = SessionAnalysisMath.Black76Gamma(spot, s.Strike, s.SigmaFraction, s.TYears)
                         * (s.CallOi - s.PutOi) * scale
            })
            .Where(x => double.IsFinite(x.NetGex) && x.NetGex != 0)
            .ToList();

        return view;
    }
}
