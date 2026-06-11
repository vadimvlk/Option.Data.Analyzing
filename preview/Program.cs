using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Option.Data.Database;
using Option.Data.Shared.Poco;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;

// =====================================================================
//  Срез рекомендации Trade БОЕВЫМ кодом: последний снимок из БД →
//  тот же конвейер, что TradeModel.Compute («Сводка» или одна экспирация).
//  Запуск: dotnet run --project preview -c Release [BTC|ETH] [<эксп. dMMMyy>]
//  Пример одиночной экспирации: ... -- ETH 19JUN26
// =====================================================================

string symbol = args.Length > 0 ? args[0].ToUpperInvariant() : "ETH";
string? singleExp = args.Length > 1 ? args[1].ToUpperInvariant() : null;
int curId = symbol == "BTC" ? 1 : 2;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string appsettingsPath = Path.Combine(root, "Option.Data.Ui", "appsettings.json");
string conn;
using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath)))
    conn = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString()!;

var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(conn).Options;
await using var ctx = new ApplicationDbContext(options);

// --- последний снимок по валюте (как TradeModel: AvailableDates.Last) ---
DateTimeOffset asOf = await ctx.DeribitData
    .Where(d => d.CurrencyTypeId == curId)
    .MaxAsync(d => d.CreatedAt);

List<DeribitData> rows = await ctx.DeribitData.AsNoTracking()
    .Where(d => d.CurrencyTypeId == curId && d.CreatedAt == asOf)
    .ToListAsync();

// --- неистёкшие экспирации с OI (как в TradeModel) ---
List<string> expirations = rows
    .GroupBy(d => d.Expiration)
    .Where(g => g.Sum(d => d.OpenInterest) > 0 &&
                OptionExposureMath.YearsToExpiry(g.Key, asOf) > 0)
    .Select(g => g.Key)
    .OrderBy(e => DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture))
    .ToList();

List<string> aggWindow = QuarterlyAggregation.WindowExpirations(expirations, asOf);

// ===== Смоук-режим ALL: все экспирации снимка + «Сводка», по строке на план =====
if (singleExp == "ALL")
{
    List<DeribitData> allRows = await ctx.DeribitData.AsNoTracking()
        .Where(d => d.CurrencyTypeId == curId)
        .ToListAsync();

    // Индекс по срезам: время → экспирация → (max форвард, −Σ(Δ·OI)) — для боевых дельта-рядов.
    var perExp = new Dictionary<DateTimeOffset, Dictionary<string, (double Px, double Dex)>>();
    foreach (DeribitData d in allRows)
    {
        if (!perExp.TryGetValue(d.CreatedAt, out Dictionary<string, (double Px, double Dex)>? byExp))
            perExp[d.CreatedAt] = byExp = new Dictionary<string, (double Px, double Dex)>();
        (double px, double dex) = byExp.TryGetValue(d.Expiration, out (double Px, double Dex) cur)
            ? cur : (0.0, 0.0);
        byExp[d.Expiration] = (Math.Max(px, d.UnderlyingPrice), dex - d.Delta * d.OpenInterest);
    }
    List<DateTimeOffset> allTimes = perExp.Keys.OrderBy(t => t).ToList();

    List<DeltaPoint> SeriesFor(List<string> exps)
    {
        var series = new List<DeltaPoint>();
        foreach (DateTimeOffset t in allTimes)
        {
            double px = double.MinValue, dex = 0;
            bool any = false;
            foreach (string e in exps)
                if (perExp[t].TryGetValue(e, out (double Px, double Dex) a))
                {
                    any = true;
                    px = Math.Max(px, a.Px);
                    dex += a.Dex;
                }
            if (any)
                series.Add(new DeltaPoint { Time = t, UnderlyingPrice = px, DeltaExposure = dex });
        }
        return series;
    }

    string PlanLine(SessionRecommendation r)
    {
        string regime = r.Regime switch
        {
            VolatilityRegime.PositiveGamma => "+γ",
            VolatilityRegime.NegativeGamma => "−γ",
            _ => "≈0"
        };
        string head = $"{regime} {r.NetGexAtSpot,15:N0} | flip {(r.GammaFlip is { } gfp ? gfp.ToString("N0", CultureInfo.InvariantCulture) : "—"),8} " +
                      $"| σс ±{r.Range.SessionSigmaUsd,6:N0} | bias {r.BiasScore,5:+0.00;-0.00} | ";
        PrimaryTrade p = r.Primary;
        if (p.Action == TradeAction.StandAside)
            return head + $"ВНЕ РЫНКА: {p.Reason}";
        return head + $"{p.Action} {(p.Side == TradeSide.Short ? "SHORT" : "LONG")}{(p.IsConditional ? " (отлож.)" : "")} " +
               $"вход {p.EntryLow:N0}…{p.EntryHigh:N0} стоп {p.Stop:N0} цель {p.Target:N0} RR {p.RiskReward:0.00}";
    }

    var expB = new ExpirationAnalysisBuilder();
    var sessB = new SessionRecommendationBuilder();

    Console.WriteLine($"=== {symbol} · СМОУК ВСЕХ ЭКСПИРАЦИЙ · снимок {asOf:yyyy-MM-dd HH:mm} UTC ===");
    foreach (string e in expirations)
    {
        double dte = OptionExposureMath.YearsToExpiry(e, asOf) * 365.0;
        ExpirationAnalysis? an = expB.Build(rows, [e], asOf).FirstOrDefault();
        if (an is null || an.OptionData.Count == 0)
        {
            Console.WriteLine($"{e,-8} {dte,6:0.0}д | нет данных");
            continue;
        }
        SessionRecommendation r1 = sessB.Build(an, symbol, asOf, SessionAnalysisMath.DexTrend(SeriesFor([e])));
        Console.WriteLine($"{e,-8} {dte,6:0.0}д | {PlanLine(r1)}");
    }

    List<ExpirationAnalysis> aggAns = expB.Build(rows, aggWindow, asOf);
    SessionRecommendation rAgg = sessB.BuildAggregate(aggAns, symbol, asOf,
        SessionAnalysisMath.DexTrend(SeriesFor(aggWindow)));
    Console.WriteLine($"{"СВОДКА",-8} {"",7} | {PlanLine(rAgg)}");
    return;
}

// Одиночный режим: дельта-ряд и цепочка только по выбранной экспирации (как на странице).
List<string> deltaExpirations = singleExp is null ? aggWindow : [singleExp];

// --- дельта-ряд (как в TradeModel) ---
List<DeribitData> deltaRows = await ctx.DeribitData.AsNoTracking()
    .Where(d => d.CurrencyTypeId == curId && deltaExpirations.Contains(d.Expiration))
    .ToListAsync();

List<DeltaPoint> deltaSeries = deltaRows
    .GroupBy(d => d.CreatedAt)
    .Select(g => new DeltaPoint
    {
        Time = g.Key,
        UnderlyingPrice = g.Max(d => d.UnderlyingPrice),
        DeltaExposure = -g.Sum(d => d.Delta * d.OpenInterest)
    })
    .OrderBy(p => p.Time)
    .ToList();

double dexFlowTrend = SessionAnalysisMath.DexTrend(deltaSeries);

// --- боевые билдеры ---
var expirationBuilder = new ExpirationAnalysisBuilder();
var sessionBuilder = new SessionRecommendationBuilder();
SessionRecommendation rec;
List<ExpirationAnalysis>? aggAnalyses = null;
if (singleExp is null)
{
    aggAnalyses = expirationBuilder.Build(rows, aggWindow, asOf);
    rec = sessionBuilder.BuildAggregate(aggAnalyses, symbol, asOf, dexFlowTrend);
}
else
{
    ExpirationAnalysis? selected = expirationBuilder.Build(rows, [singleExp], asOf).FirstOrDefault();
    if (selected is null || selected.OptionData.Count == 0)
    {
        Console.WriteLine($"Нет данных по экспирации {singleExp}.");
        return;
    }
    rec = sessionBuilder.Build(selected, symbol, asOf, dexFlowTrend);
}

// =====================  ПЕЧАТЬ  =====================
string F(double v) => v.ToString("N2", CultureInfo.InvariantCulture);
string F0(double v) => v.ToString("N0", CultureInfo.InvariantCulture);

Console.WriteLine($"=== {symbol} · {(singleExp is null ? "СВОДКА" : $"ЭКСПИРАЦИЯ {singleExp}")} · снимок {asOf:yyyy-MM-dd HH:mm} UTC ===");
if (singleExp is null)
    Console.WriteLine($"Окно агрегации ({aggWindow.Count} эксп.): {string.Join(", ", aggWindow)}");
Console.WriteLine($"Спот: {F(rec.Spot)}   Горизонт: {rec.FrontExpiration} ({rec.FrontDaysToExpiry:0.0} дн)");
Console.WriteLine();
Console.WriteLine($"РЕЖИМ: {rec.Regime}   NetGEX у спота: {F0(rec.NetGexAtSpot)} USD/1%");
Console.WriteLine($"Gamma-flip: {(rec.GammaFlip is { } gf ? F(gf) : "—")}");
Console.WriteLine($"σ_ATM горизонта: {rec.Range.AtmIvPercent:0.0}%   σ сессии (1 день): ±{F0(rec.Range.SessionSigmaUsd)} $");
Console.WriteLine($"Диапазон 1σ: {F(rec.Range.Lower1)} … {F(rec.Range.Upper1)}");
Console.WriteLine($"Диапазон 2σ: {F(rec.Range.Lower2)} … {F(rec.Range.Upper2)}");
Console.WriteLine();
Console.WriteLine($"СИГНАЛЫ (BiasScore {rec.BiasScore:+0.00;-0.00;0.00} → {rec.Bias}; dexFlowTrend {dexFlowTrend:+0.000;-0.000;0.000}, точек дельта-ряда {deltaSeries.Count}):");
foreach (BiasComponent c in rec.BiasComponents)
    Console.WriteLine($"  {c.Name,-18} голос {c.Normalized:+0.00;-0.00} · вес {c.Weight:0.00} · вклад {c.Contribution:+0.00;-0.00}  | {c.Explanation}");
Console.WriteLine();
Console.WriteLine("УРОВНИ (сверху вниз):");
foreach (PriceLevel l in rec.Levels)
    Console.WriteLine($"  {l.Label,-26} {F(l.Price),12}  {l.DistancePercent:+0.00;-0.00}%  {l.Role}{(l.OpenInterest is { } oi ? $"  OI {F0(oi)}" : "")}");
Console.WriteLine();
PrimaryTrade pt = rec.Primary;
Console.WriteLine($"СДЕЛКА: {pt.Headline}");
Console.WriteLine($"  Action={pt.Action} Side={pt.Side}{(pt.IsConditional ? " · ОТЛОЖЕННЫЙ ВХОД" : "")}");
if (pt.Action != TradeAction.StandAside)
{
    Console.WriteLine($"  Зона входа: {F(pt.EntryLow!.Value)} … {F(pt.EntryHigh!.Value)}");
    if (pt.IsConditional && !string.IsNullOrEmpty(pt.Trigger))
        Console.WriteLine($"  Активация: {pt.Trigger}");
    Console.WriteLine($"  Цель: {F(pt.Target!.Value)}   Стоп: {F(pt.Stop!.Value)}   R:R: {pt.RiskReward:0.00}");
    Console.WriteLine($"  Инвалидация: {pt.Invalidation}");
}
Console.WriteLine($"  Причина: {pt.Reason}");
if (!string.IsNullOrEmpty(pt.Setup)) Console.WriteLine($"  Сетап: {pt.Setup}");
if (!string.IsNullOrEmpty(pt.PlanB)) Console.WriteLine($"  План-Б: {pt.PlanB}");
if (pt.Drivers.Count > 0) Console.WriteLine($"  Драйверы: {string.Join("; ", pt.Drivers)}");
if (rec.Notes.Count > 0) Console.WriteLine($"  Заметки: {string.Join(" | ", rec.Notes)}");

// =====================  ПРОВЕРКИ АГРЕГАЦИИ (независимый пересчёт)  =====================
if (aggAnalyses is not null)
{
    Console.WriteLine();
    Console.WriteLine("=== ПРОВЕРКИ АГРЕГАЦИИ ===");
    List<ExpirationAnalysis> an = aggAnalyses;
    double spot = rec.Spot;

    // 0. Базис форвардов окна: спот сводки = max форвардов.
    double fwMin = an.Min(a => a.UnderlyingPrice);
    double fwMax = an.Max(a => a.UnderlyingPrice);
    Console.WriteLine($"0. Форварды окна: {string.Join("; ", an.Select(a => $"{a.Expiration}={F(a.UnderlyingPrice)}"))}");
    Console.WriteLine($"   Разброс {F(fwMax - fwMin)} $ ({(fwMax - fwMin) / spot * 100:0.000}% от спота); спот сводки = max → {(Math.Abs(fwMax - spot) < 1e-9 ? "OK" : "FAIL")}");

    // 1. Сводная цепочка: ΣCallOi/ΣPutOi по страйкам против независимой пересборки.
    var chainCheck = new Dictionary<double, (double C, double P)>();
    foreach (ExpirationAnalysis a in an)
        foreach (var o in a.OptionData)
        {
            chainCheck.TryGetValue(o.Strike, out (double C, double P) acc);
            chainCheck[o.Strike] = (acc.C + o.CallOi, acc.P + o.PutOi);
        }
    int mismatches = 0;
    double maxDiff = 0;
    foreach (var o in rec.FrontChain)
    {
        if (!chainCheck.TryGetValue(o.Strike, out (double C, double P) exp)) { mismatches++; continue; }
        double d = Math.Abs(exp.C - o.CallOi) + Math.Abs(exp.P - o.PutOi);
        if (d > 1e-9) mismatches++;
        if (d > maxDiff) maxDiff = d;
    }
    bool chainOk = mismatches == 0 && rec.FrontChain.Count == chainCheck.Count;
    Console.WriteLine($"1. Сводная цепочка: страйков {rec.FrontChain.Count} (пересборка: {chainCheck.Count}), расхождений {mismatches}, maxΔOI {maxDiff:0.######} → {(chainOk ? "OK" : "FAIL")}");

    // 2. NetGEX у спота: пересборка gamma-страйков (свои T/IV каждой экспирации) + аддитивность.
    var gsAll = new List<SessionAnalysisMath.GammaStrike>();
    double contribSum = 0;
    var contribs = new List<string>();
    foreach (ExpirationAnalysis a in an)
    {
        double T = OptionExposureMath.YearsToExpiry(a.Expiration, asOf);
        List<SessionAnalysisMath.GammaStrike> gsExp = a.OptionData
            .Where(o => o.CallOi > 0 || o.PutOi > 0)
            .Select(o => new SessionAnalysisMath.GammaStrike(o.Strike, o.CallOi, o.PutOi, o.Iv / 100.0, T))
            .ToList();
        double g = SessionAnalysisMath.NetGexAtPrice(gsExp, spot);
        contribSum += g;
        gsAll.AddRange(gsExp);
        contribs.Add($"{a.Expiration}: {F0(g)}");
    }
    double totalG = SessionAnalysisMath.NetGexAtPrice(gsAll, spot);
    Console.WriteLine($"2. Вклад экспираций в NetGEX у спота (по той же цене {F(spot)}):");
    Console.WriteLine($"   {string.Join("; ", contribs)}");
    Console.WriteLine($"   Σ вкладов {F0(contribSum)} = общий пересчёт {F0(totalG)} = страница {F0(rec.NetGexAtSpot)} → " +
                      $"{(Math.Abs(totalG - rec.NetGexAtSpot) <= Math.Abs(rec.NetGexAtSpot) * 1e-9 + 1 && Math.Abs(contribSum - totalG) <= 1 ? "OK" : "FAIL")}");

    // 3. Gamma-flip: пересчёт профиля с теми же границами + знак по сторонам.
    double lowFactor = Math.Min(0.88, Math.Max(0.25, rec.Range.Lower2 / spot * 0.97));
    double highFactor = Math.Max(1.12, rec.Range.Upper2 / spot * 1.03);
    List<GammaProfilePoint> prof = SessionAnalysisMath.GammaProfile(gsAll, spot, lowFactor, highFactor);
    double? flip = SessionAnalysisMath.GammaFlip(prof, spot);
    bool flipOk = flip.HasValue == rec.GammaFlip.HasValue &&
                  (!flip.HasValue || Math.Abs(flip.Value - rec.GammaFlip!.Value) < 0.01);
    Console.WriteLine($"3. Gamma-flip: пересчёт {(flip is { } fv ? F(fv) : "—")} vs страница {(rec.GammaFlip is { } gv ? F(gv) : "—")} → {(flipOk ? "OK" : "FAIL")}");
    if (flip is { } fl)
    {
        double below = SessionAnalysisMath.NetGexAtPrice(gsAll, fl * 0.995);
        double above = SessionAnalysisMath.NetGexAtPrice(gsAll, fl * 1.005);
        Console.WriteLine($"   NetGEX(flip−0.5%)={F0(below)}, NetGEX(flip+0.5%)={F0(above)} → {(below < 0 && above > 0 ? "ниже 0 / выше 0 — OK (одиночное пересечение)" : "нетривиальный профиль — проверить мультипересечение")}");
    }

    // 4. GEX-стены против уровней страницы.
    List<SessionAnalysisMath.StrikeGexBreakdown> bd = SessionAnalysisMath.StrikeGexAtPrice(gsAll, spot);
    double? cwChk = SessionAnalysisMath.GexCallWall(bd, spot);
    double? pwChk = SessionAnalysisMath.GexPutWall(bd, spot);
    double? cwPage = rec.Levels.FirstOrDefault(l => l.Kind == LevelKind.CallWall)?.Price;
    double? pwPage = rec.Levels.FirstOrDefault(l => l.Kind == LevelKind.PutWall)?.Price;
    Console.WriteLine($"4. GEX-стены: CALL {(cwChk is { } c1 ? F(c1) : "—")} vs {(cwPage is { } c2 ? F(c2) : "—")}; " +
                      $"PUT {(pwChk is { } p1 ? F(p1) : "—")} vs {(pwPage is { } p2 ? F(p2) : "—")} → " +
                      $"{(Nullable.Equals(cwChk, cwPage) && Nullable.Equals(pwChk, pwPage) ? "OK" : "FAIL")}");

    // 5. Max Pain и центроид по сводной цепочке.
    double mpChk = OptionExposureMath.MaxPain(rec.FrontChain);
    double? mpPage = rec.Levels.FirstOrDefault(l => l.Kind == LevelKind.MaxPain)?.Price;
    double totOi = rec.FrontChain.Sum(o => o.CallOi + o.PutOi);
    double cgChk = rec.FrontChain.Sum(o => o.Strike * (o.CallOi + o.PutOi)) / totOi;
    double? cgPage = rec.Levels.FirstOrDefault(l => l.Kind == LevelKind.GravityEquilibrium)?.Price;
    Console.WriteLine($"5. Max Pain: {F(mpChk)} vs {(mpPage is { } m2 ? F(m2) : "—")}; центроид: {F(cgChk)} vs {(cgPage is { } g2 ? F(g2) : "—")} → " +
                      $"{(mpPage == mpChk && cgPage is { } g3 && Math.Abs(g3 - cgChk) < 0.01 ? "OK" : "FAIL")}");

    // 6. σ-границы (IV горизонта) и σ сессии (IV фронта).
    ExpirationAnalysis horizonA = an.OrderByDescending(a => OptionExposureMath.YearsToExpiry(a.Expiration, asOf)).First();
    ExpirationAnalysis frontA = an.OrderBy(a => OptionExposureMath.YearsToExpiry(a.Expiration, asOf)).First();
    double tY = OptionExposureMath.YearsToExpiry(horizonA.Expiration, asOf);
    double ivH = OptionExposureMath.AtmIvFraction(horizonA.OptionData, horizonA.UnderlyingPrice);
    double ivF = OptionExposureMath.AtmIvFraction(frontA.OptionData, frontA.UnderlyingPrice);
    (double lo1, double up1) = SessionAnalysisMath.LogNormalBand(spot, ivH, tY, 1);
    double ssChk = SessionAnalysisMath.SessionSigmaUsd(spot, ivF, tY);
    Console.WriteLine($"6. IV горизонта ({horizonA.Expiration}) {ivH * 100:0.0}%, IV фронта ({frontA.Expiration}) {ivF * 100:0.0}%");
    Console.WriteLine($"   1σ: {F(lo1)}…{F(up1)} vs {F(rec.Range.Lower1)}…{F(rec.Range.Upper1)}; σ сессии {F0(ssChk)} vs {F0(rec.Range.SessionSigmaUsd)} → " +
                      $"{(Math.Abs(lo1 - rec.Range.Lower1) < 0.01 && Math.Abs(ssChk - rec.Range.SessionSigmaUsd) < 0.5 ? "OK" : "FAIL")}");

    // 7. DEX по окну (информативно: сумма долларовых DEX при чуть разных форвардах).
    Console.WriteLine($"7. DEX: {string.Join("; ", an.Select(a => $"{a.Expiration} {a.DollarDeltaExposure / 1e6:+0.0;-0.0}M"))} → Σ {an.Sum(a => a.DollarDeltaExposure) / 1e6:+0.0;-0.0}M");

    // 8. Сводная цепочка не должна нести греков/IV (потребители — только OI).
    bool noGreeks = rec.FrontChain.All(o => o.Iv == 0 && o.PutIv == 0 && o.CallDelta == 0 && o.PutDelta == 0 &&
                                            o.CallGamma == 0 && o.PutGamma == 0 && o.CallPrice == 0 && o.PutPrice == 0);
    Console.WriteLine($"8. Сводная цепочка без греков/IV/цен (используется только OI): {(noGreeks ? "да — OK" : "ЕСТЬ ненулевые поля — проверить потребителей!")}");
}
