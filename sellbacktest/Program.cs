using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Option.Data.Database;
using Option.Data.Shared.Poco;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;

// ======================================================================
//  МОДЕЛЬНЫЙ бэктест стратегии /Sell на реальных траекториях.
//
//  Данные РЕАЛЬНЫЕ из БД (3ч-снимки за ~год): траектория цены (форвард фронтовой
//  живой серии), GEX-режим и стены каждого снимка (по доступным сериям), ATM IV
//  фронтовой серии. Продаваемый опцион СИНТЕТИЧЕСКИЙ (история фронтов уничтожена
//  CleanupJob): 7-дневный, страйк по правилам страницы, премия = Black-76 по
//  наблюдаемой IV с haircut к биду. Управление по плану Б на 3ч-сетке.
//
//  ЧЕСТНЫЕ ОГРАНИЧЕНИЯ (печатаются в отчёте): премии модельные; IV-прокси — от
//  дальних серий (term structure); 3ч-сетка не видит внутрибарные шипы; bias/стрэнгл
//  не моделируются (нет DEX-каркаса) — тестируются ОБЕ ноги по отдельности;
//  один годовой цикл (медвежий).
//
//  Запуск: dotnet run --project sellbacktest -c Release -- BTC|ETH|ALL [GRID]
//  GRID: калибровка на первой половине периода, оценка лучшей конфигурации на второй.
// ======================================================================

const double HoldDays = 7;            // синтетический срок продаваемого опциона
const double Haircut = 0.10;          // дисконт премии к биду (и наценка при откупе)
const double FeeCoinPerContract = 0.0003; // комиссия Deribit options, в монете, кап 12.5% премии
const int DecisionHourUtc = 9;        // ближайший к 08:00 UTC снимок цикла 0/3/6/9...

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

string modeArg = args.Length > 0 ? args[0].ToUpperInvariant() : "ALL";
bool grid = args.Length > 1 && args[1].ToUpperInvariant() == "GRID";
string[] symbols = modeArg == "ALL" ? ["BTC", "ETH"] : [modeArg];

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string conn;
using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Option.Data.Ui", "appsettings.json"))))
    conn = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString()!;
var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(conn).Options;

foreach (string symbol in symbols)
{
    int curId = symbol == "BTC" ? 1 : 2;
    Console.WriteLine($"\n================ {symbol} ================");
    Console.WriteLine("Загрузка истории из БД...");

    List<DeribitData> all;
    await using (var ctx = new ApplicationDbContext(dbOptions))
    {
        ctx.Database.SetCommandTimeout(600);
        all = await ctx.DeribitData.AsNoTracking()
            .Where(d => d.CurrencyTypeId == curId)
            .ToListAsync();
    }
    Console.WriteLine($"Строк: {all.Count:N0}");

    // ---- Снимки: для каждого CreatedAt — фронтовая серия, её форвард/IV; GammaStrikes всех серий ----
    var byTime = all.GroupBy(d => d.CreatedAt).OrderBy(g => g.Key).ToList();
    var snaps = new List<Snap>(byTime.Count);
    foreach (IGrouping<DateTimeOffset, DeribitData> g in byTime)
    {
        DateTimeOffset t = g.Key;
        var live = g.Where(d => OptionExposureMath.YearsToExpiry(d.Expiration, t) > 0).ToList();
        if (live.Count == 0)
            continue;

        string frontExp = live
            .Select(d => d.Expiration).Distinct()
            .OrderBy(e => OptionExposureMath.YearsToExpiry(e, t))
            .First();
        List<DeribitData> frontRows = live.Where(d => d.Expiration == frontExp).ToList();
        double px = frontRows.Max(d => d.UnderlyingPrice);
        if (px <= 0)
            continue;

        // ATM IV фронтовой серии: ближайший к форварду страйк с Iv>0.
        double atmIvPct = frontRows.Where(d => d.Iv > 0)
            .OrderBy(d => Math.Abs(d.Strike - px))
            .Select(d => d.Iv).FirstOrDefault();

        // GammaStrikes всех живых серий (как делает билдер по доске + Trade по снимку).
        var gs = new List<SessionAnalysisMath.GammaStrike>();
        foreach (IGrouping<string, DeribitData> ge in live.GroupBy(d => d.Expiration))
        {
            double tt = OptionExposureMath.YearsToExpiry(ge.Key, t);
            foreach (IGrouping<int, DeribitData> gk in ge.GroupBy(d => d.Strike))
            {
                double callOi = gk.Where(d => d.OptionTypeId == 1).Sum(d => d.OpenInterest);
                double putOi = gk.Where(d => d.OptionTypeId == 2).Sum(d => d.OpenInterest);
                double ivPct = gk.Where(d => d.Iv > 0).Select(d => d.Iv).DefaultIfEmpty(atmIvPct).First();
                if ((callOi > 0 || putOi > 0) && ivPct > 0)
                    gs.Add(new SessionAnalysisMath.GammaStrike(gk.Key, callOi, putOi, ivPct / 100.0, tt));
            }
        }

        snaps.Add(new Snap(t, px, atmIvPct / 100.0, gs));
    }
    Console.WriteLine($"Снимков: {snaps.Count:N0} ({snaps[0].T:yyyy-MM-dd} … {snaps[^1].T:yyyy-MM-dd})");

    // ---- Режим/стены на каждый снимок (один раз, дорого) ----
    var regimes = new VolatilityRegime[snaps.Count];
    var callWalls = new double?[snaps.Count];
    var putWalls = new double?[snaps.Count];
    for (int i = 0; i < snaps.Count; i++)
    {
        Snap s = snaps[i];
        List<GammaProfilePoint> prof = SessionAnalysisMath.GammaProfile(s.Gs, s.Px);
        double peak = 0;
        foreach (GammaProfilePoint p in prof)
            peak = Math.Max(peak, Math.Abs(p.NetGex));
        double net = SessionAnalysisMath.NetGexAtPrice(s.Gs, s.Px);
        double band = peak * 0.05;
        regimes[i] = net > band ? VolatilityRegime.PositiveGamma
            : net < -band ? VolatilityRegime.NegativeGamma : VolatilityRegime.Neutral;
        List<SessionAnalysisMath.StrikeGexBreakdown> bd = SessionAnalysisMath.StrikeGexAtPrice(s.Gs, s.Px);
        callWalls[i] = SessionAnalysisMath.GexCallWall(bd, s.Px);
        putWalls[i] = SessionAnalysisMath.GexPutWall(bd, s.Px);
        if (i % 500 == 0)
            Console.WriteLine($"  ...режим {i}/{snaps.Count}");
    }

    int split = snaps.Count / 2; // walk-forward: H1 — калибровка, H2 — out-of-sample

    if (!grid)
    {
        Config def = Config.Default;
        RunReport(symbol, snaps, regimes, callWalls, putWalls, def, 0, snaps.Count, "ВЕСЬ ПЕРИОД");
        RunReport(symbol, snaps, regimes, callWalls, putWalls, def, 0, split, "H1 (in-sample)");
        RunReport(symbol, snaps, regimes, callWalls, putWalls, def, split, snaps.Count, "H2 (out-of-sample)");
    }
    else
    {
        Console.WriteLine("\n=== GRID: калибровка на H1 ===");
        var results = new List<(Config Cfg, Summary H1)>();
        foreach (double mPos in new[] { 0.70, 0.85, 1.00 })
        foreach (double dAdapt in new[] { 0.10, 0.12, 0.15, 0.18 })
        {
            var cfg = Config.Default with { MultPosGamma = mPos, AdaptDelta = dAdapt };
            Summary h1 = Simulate(snaps, regimes, callWalls, putWalls, cfg, 0, split, null);
            results.Add((cfg, h1));
        }

        // Критерий: максимум Σ PnL/маржа при фактической частоте жёсткого триггера
        // не выше предсказанной P(касания) + 5 п.п. (требование «точность выше вероятности хеджа»).
        var admissible = results
            .Where(r => r.H1.Trades > 10 && r.H1.HardFreq <= r.H1.AvgPredTouch + 0.05)
            .OrderByDescending(r => r.H1.PnlOnMargin)
            .ToList();
        Console.WriteLine($"Конфигураций: {results.Count}, допустимых: {admissible.Count}");
        foreach ((Config cfg, Summary h1) in admissible.Take(5))
            Console.WriteLine($"  mPos={cfg.MultPosGamma:0.00} dA={cfg.AdaptDelta:0.00} → " +
                              $"H1 PnL/маржа {h1.PnlOnMargin:P2}, hard {h1.HardFreq:P1} (предск. {h1.AvgPredTouch:P1}), сделок {h1.Trades}");

        if (admissible.Count > 0)
        {
            Config best = admissible[0].Cfg;
            Console.WriteLine($"\nЛучшая конфигурация: mPos={best.MultPosGamma:0.00} deltaAdapt={best.AdaptDelta:0.00}");
            RunReport(symbol, snaps, regimes, callWalls, putWalls, best, split, snaps.Count, "H2 (out-of-sample, лучшая H1-конфигурация)");
        }
    }
}

return;

// ======================================================================

static void RunReport(string symbol, List<Snap> snaps, VolatilityRegime[] regimes,
    double?[] cw, double?[] pw, Config cfg, int from, int to, string title)
{
    string? csv = title.StartsWith("ВЕСЬ") ? Path.Combine("sellbacktest", $"selltrades_{symbol}.csv") : null;
    Summary s = Simulate(snaps, regimes, cw, pw, cfg, from, to, csv);
    Console.WriteLine($"\n--- {title}: сделок {s.Trades} ---");
    Console.WriteLine($"PnL {s.PnlUsd:+#,0;-#,0} $ на 1 контракт · Σмаржа {s.MarginUsd:N0} $ · PnL/маржа {s.PnlOnMargin:P2}");
    Console.WriteLine($"Win-rate (PnL>0): {s.WinRate:P1} · средн. PnL/маржа сделки {s.AvgTradePnlOnMargin:P2}");
    Console.WriteLine($"Исходы: EXPIRY {s.Expiry}, TP {s.Tp}, SOFT {s.Soft}, HARD {s.Hard} ({s.HardFreq:P1}; предсказано P(касания) {s.AvgPredTouch:P1}), REGIME {s.Regime}");
    Console.WriteLine($"По ногам: CALL {s.CallPnl:+#,0;-#,0} $ ({s.CallTrades}), PUT {s.PutPnl:+#,0;-#,0} $ ({s.PutTrades})");
    if (csv is not null)
        Console.WriteLine($"Журнал: {csv}");
}

static Summary Simulate(List<Snap> snaps, VolatilityRegime[] regimes,
    double?[] callWalls, double?[] putWalls, Config cfg, int from, int to, string? csvPath)
{
    var trades = new List<Trade>();

    // Обе ноги тестируются отдельными книгами: страница выбирает ногу по bias,
    // тест оценивает экономику каждой стороны (bias-каркас в тесте не моделируется).
    foreach (bool isCall in new[] { true, false })
    {
        int i = from;
        while (i < to)
        {
            Snap s = snaps[i];
            if (s.T.Hour != DecisionHourUtc || s.AtmIv <= 0) { i++; continue; }

            VolatilityRegime regime = regimes[i];

            // Профиль: адаптивный не продаёт в −гамме; в нейтрали — пониженная дельта.
            bool adaptiveActive = regime != VolatilityRegime.NegativeGamma;
            if (!adaptiveActive) { i++; continue; }

            double deltaTarget = regime == VolatilityRegime.PositiveGamma ? cfg.AdaptDelta : Math.Min(cfg.AdaptDelta, 0.12);
            double tOpt = HoldDays / 365.0;

            // σ_phys: EWMA-RV до снимка (без подглядывания) × множитель режима, клэмп к IV.
            var hist = new List<PricePoint>();
            for (int j = Math.Max(0, i - 240); j <= i; j++)
                hist.Add(new PricePoint(snaps[j].T, snaps[j].Px));
            double rv = SellMath.EwmaRealizedVolAnnual(hist, 7);
            double mult = regime == VolatilityRegime.PositiveGamma ? cfg.MultPosGamma : 1.0;
            double sigmaPhys = rv > 0
                ? SessionAnalysisMath.Clamp(rv * mult, s.AtmIv * 0.5, s.AtmIv * 1.5)
                : s.AtmIv * mult;

            // Страйк: |δ(σ_iv)| = deltaTarget (бисекция), затем не ближе стены и 1σ_phys-границы.
            double k = StrikeForDelta(isCall, s.Px, s.AtmIv, tOpt, deltaTarget);
            (double lo1, double up1) = SessionAnalysisMath.LogNormalBand(s.Px, sigmaPhys, tOpt, 1.0);
            double? wall = isCall ? callWalls[i] : putWalls[i];
            k = isCall
                ? Math.Max(k, Math.Max(up1, wall ?? 0))
                : Math.Min(k, Math.Min(lo1, wall ?? double.MaxValue));

            double sigmaLeg = s.AtmIv; // IV-прокси: ATM фронтовой серии (скос синтетике недоступен)
            double delta = SellMath.Black76Delta(isCall, s.Px, k, sigmaLeg, tOpt);
            if (Math.Abs(delta) < 0.03) { i++; continue; } // премия выродилась — день пропущен

            double premUsd = SellMath.Black76Price(isCall, s.Px, k, sigmaLeg, tOpt) * (1 - Haircut);
            if (premUsd < s.Px * 0.0003) { i++; continue; }
            double ev = premUsd - SellMath.Black76Price(isCall, s.Px, k, sigmaPhys, tOpt);
            if (ev <= 0) { i++; continue; }

            double probTouch = SellMath.ProbTouch(isCall, s.Px, k, sigmaLeg, tOpt);
            double marginUsd = SellMath.ShortMarginCoin(isCall, s.Px, k,
                SellMath.Black76Price(isCall, s.Px, k, sigmaLeg, tOpt) / s.Px) * s.Px;
            double feeUsd = Math.Min(FeeCoinPerContract * s.Px, 0.125 * premUsd);

            // ---- Ведение позиции на 3ч-сетке ----
            DateTimeOffset expiry = s.T.AddDays(HoldDays);
            double fraction = 1.0;       // остаток позиции (soft-триггер закрывает половину)
            double pnl = premUsd - feeUsd;
            string outcome = "EXPIRY";
            bool softFired = false;
            int j2 = i + 1;
            for (; j2 < to; j2++)
            {
                Snap cur = snaps[j2];
                if (cur.T >= expiry)
                    break;
                double tRem = (expiry - cur.T).TotalDays / 365.0;
                double sigmaNow = cur.AtmIv > 0 ? cur.AtmIv : sigmaLeg;
                double premNow = SellMath.Black76Price(isCall, cur.Px, k, sigmaNow, tRem);
                double sessSigma = SessionAnalysisMath.SessionSigmaUsd(cur.Px, sigmaNow, tRem);

                // Режимный выход: уход в −гамму.
                if (regimes[j2] == VolatilityRegime.NegativeGamma)
                {
                    pnl -= fraction * premNow * (1 + Haircut);
                    fraction = 0; outcome = "REGIME"; break;
                }
                // Жёсткий: касание буфера у страйка или премия 3×.
                bool hardTouch = isCall ? cur.Px >= k - 0.5 * sessSigma : cur.Px <= k + 0.5 * sessSigma;
                if (hardTouch || premNow >= 3.0 * premUsd)
                {
                    pnl -= fraction * premNow * (1 + Haircut);
                    fraction = 0; outcome = "HARD"; break;
                }
                // Мягкий: премия 2× — закрываем половину (один раз).
                if (fraction > 0.99 && premNow >= 2.0 * premUsd)
                {
                    pnl -= 0.5 * premNow * (1 + Haircut);
                    fraction = 0.5;
                    softFired = true;
                }
                // Тейк-профит: остаток ≤ 25% и до экспирации больше суток.
                if (premNow <= 0.25 * premUsd && (expiry - cur.T).TotalDays > 1)
                {
                    pnl -= fraction * premNow * (1 + Haircut);
                    fraction = 0; outcome = "TP"; break;
                }
            }
            if (fraction > 0)
            {
                // Экспирация (или конец данных): расчёт по внутренней стоимости последней цены.
                double settlePx = snaps[Math.Min(j2, to - 1)].Px;
                double intrinsic = isCall ? Math.Max(0, settlePx - k) : Math.Max(0, k - settlePx);
                pnl -= fraction * intrinsic;
            }

            trades.Add(new Trade(s.T, snaps[Math.Min(j2, to - 1)].T, isCall, k, s.Px,
                premUsd, marginUsd, probTouch, outcome, pnl, softFired));

            // Следующая продажа — после закрытия позиции (одна позиция на ногу).
            i = Math.Max(j2, i + 1);
        }
    }

    if (csvPath is not null)
    {
        var sb = new StringBuilder("OpenUtc;CloseUtc;Leg;Strike;F0;PremUsd;MarginUsd;ProbTouchPred;Outcome;PnlUsd\n");
        foreach (Trade tr in trades.OrderBy(t => t.Open))
            sb.AppendLine($"{tr.Open:yyyy-MM-dd HH:mm};{tr.Close:yyyy-MM-dd HH:mm};{(tr.IsCall ? "CALL" : "PUT")};" +
                          $"{tr.K:0};{tr.F0:0.00};{tr.PremUsd:0.00};{tr.MarginUsd:0.00};{tr.ProbTouch:0.0000};{tr.Outcome};{tr.PnlUsd:0.00}");
        File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
    }

    double pnlSum = trades.Sum(t => t.PnlUsd);
    double marginSum = trades.Sum(t => t.MarginUsd);
    return new Summary(
        trades.Count, pnlSum, marginSum,
        marginSum > 0 ? pnlSum / marginSum : 0,
        trades.Count > 0 ? trades.Count(t => t.PnlUsd > 0) / (double)trades.Count : 0,
        trades.Count > 0 ? trades.Average(t => t.MarginUsd > 0 ? t.PnlUsd / t.MarginUsd : 0) : 0,
        trades.Count(t => t.Outcome == "EXPIRY"), trades.Count(t => t.Outcome == "TP"),
        trades.Count(t => t.SoftFired), trades.Count(t => t.Outcome == "HARD"),
        trades.Count(t => t.Outcome == "REGIME"),
        trades.Count > 0 ? trades.Count(t => t.Outcome == "HARD") / (double)trades.Count : 0,
        trades.Count > 0 ? trades.Average(t => t.ProbTouch) : 0,
        trades.Where(t => t.IsCall).Sum(t => t.PnlUsd), trades.Count(t => t.IsCall),
        trades.Where(t => !t.IsCall).Sum(t => t.PnlUsd), trades.Count(t => !t.IsCall));
}

/// <summary>Бисекция: страйк, на котором |Black76Delta| = target (call: K>F; put: K<F).</summary>
static double StrikeForDelta(bool isCall, double f, double sigma, double t, double target)
{
    double lo = isCall ? f : f * 0.2;
    double hi = isCall ? f * 3.0 : f;
    for (int it = 0; it < 60; it++)
    {
        double mid = (lo + hi) / 2;
        double d = Math.Abs(SellMath.Black76Delta(isCall, f, mid, sigma, t));
        // |δ| убывает при удалении от денег для обеих ног.
        bool tooClose = d > target;
        if (isCall) { if (tooClose) lo = mid; else hi = mid; }
        else { if (tooClose) hi = mid; else lo = mid; }
    }
    return (lo + hi) / 2;
}

internal sealed record Snap(DateTimeOffset T, double Px, double AtmIv, List<SessionAnalysisMath.GammaStrike> Gs);

internal sealed record Config(double MultPosGamma, double AdaptDelta, double AggrDelta)
{
    public static Config Default => new(0.85, 0.15, 0.25);
}

internal sealed record Trade(DateTimeOffset Open, DateTimeOffset Close, bool IsCall, double K,
    double F0, double PremUsd, double MarginUsd, double ProbTouch, string Outcome, double PnlUsd,
    bool SoftFired);

internal sealed record Summary(int Trades, double PnlUsd, double MarginUsd, double PnlOnMargin,
    double WinRate, double AvgTradePnlOnMargin, int Expiry, int Tp, int Soft, int Hard, int Regime,
    double HardFreq, double AvgPredTouch, double CallPnl, int CallTrades, double PutPnl, int PutTrades);
