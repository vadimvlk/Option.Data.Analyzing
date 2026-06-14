using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Option.Data.Database;

// =====================================================================
//  Бэктест v2 — ПРЕДСКАЗАТЕЛЬНАЯ СИЛА СИГНАЛОВ НАПРАВЛЕНИЯ страницы Trade.
//
//  Для каждого снимка реплицируется продакшн-расчёт (агрегатное окно
//  «до квартальной», как режим «Сводка»):
//    structure — положение в диапазоне GEX-стен (+гамма) / сторона от flip (−гамма)
//    staticDEX — задемпфированный уровень DEX (фолбэк билдера)
//    flowResid — тренд DEX, резидуализованный по цене (текущий код)
//    flowRaw   — тренд сырого DEX (вариант Fable до резидуализации)
//    skew      — 25Δ Risk Reversal (ближайшая экспирация окна с валидным RR)
//    BIAS      — составной BiasScore страницы (веса 0.45/0.35/0.20, как BuildSignals)
//  Кандидаты на добавление: momentum 24ч, pin-притяжение Max Pain.
//
//  Метрики: IC = Pearson corr(сигнал, форвард-лог-доходность 6/12/24ч);
//  в скобках — IC на прореженной выборке (шаг ≈ горизонт, перекрытие убрано);
//  hit = доля совпадения знака. Отдельно — распределение |flow|-голоса:
//  диагноз, почему ветка «Поток ΔDEX» не срабатывает на странице.
// =====================================================================

int[] horizonsH = { 6, 12, 24 };
const double FlowEpsilon = 0.05;              // как в SessionRecommendationBuilder
const double StaticDexDamp = 0.5;
const double StaticDexScale = 0.10;
const double SkewScaleVolPts = 8.0;
const double NegGammaFlipDistSigmas = 1.5;
const double NeutralGexProfileFraction = 0.05;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string appsettingsPath = Path.Combine(root, "Option.Data.Ui", "appsettings.json");
if (!File.Exists(appsettingsPath))
{
    Console.Error.WriteLine($"Не найден appsettings.json: {appsettingsPath}");
    return 1;
}
string conn;
using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath)))
    conn = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString()!;

var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(conn).Options;
var expDateCache = new Dictionary<string, DateTime>();

foreach ((int curId, string curName) in new[] { (1, "BTC"), (2, "ETH") })
{
    List<RowR> raw;
    await using (var ctx = new ApplicationDbContext(dbOptions))
    {
        raw = await ctx.DeribitData.AsNoTracking()
            .Where(d => d.CurrencyTypeId == curId && d.OpenInterest > 0)
            .Select(d => new RowR(d.CreatedAt, d.Expiration, d.Strike, d.OptionTypeId,
                d.OpenInterest, d.Iv, d.UnderlyingPrice, d.Delta))
            .ToListAsync();
    }
    if (raw.Count == 0) { Console.WriteLine($"\n=== {curName} === нет данных"); continue; }

    List<DateTimeOffset> times = raw.Select(r => r.CreatedAt).Distinct().OrderBy(x => x).ToList();
    var tIdx = new Dictionary<DateTimeOffset, int>(times.Count);
    for (int i = 0; i < times.Count; i++) tIdx[times[i]] = i;
    int S = times.Count;

    // Снимок → экспирация → агрегат (страйки, ΣΔ·OI, ΣOI, max underlying).
    var snaps = new Dictionary<string, ExpAgg>[S];
    for (int i = 0; i < S; i++) snaps[i] = new Dictionary<string, ExpAgg>();

    foreach (var grp in raw.GroupBy(r => (r.CreatedAt, r.Expiration)))
    {
        int i = tIdx[grp.Key.CreatedAt];
        var byStrike = new Dictionary<int, StrikeAgg>();
        double sumDeltaOi = 0, sumOi = 0, maxU = 0;
        foreach (RowR r in grp)
        {
            if (!byStrike.TryGetValue(r.Strike, out StrikeAgg? sa))
            {
                sa = new StrikeAgg { Strike = r.Strike };
                byStrike[r.Strike] = sa;
            }
            if (r.OptionTypeId == 1)
            {
                sa.CallOi += r.OpenInterest;
                if (r.Iv > 0) sa.CallIv = r.Iv;
                sa.CallDelta = r.Delta;
            }
            else
            {
                sa.PutOi += r.OpenInterest;
                if (r.Iv > 0) sa.PutIv = r.Iv;
                sa.PutDelta = r.Delta;
            }
            sumDeltaOi += r.Delta * r.OpenInterest;
            sumOi += r.OpenInterest;
            if (r.UnderlyingPrice > maxU) maxU = r.UnderlyingPrice;
        }
        snaps[i][grp.Key.Expiration] = new ExpAgg
        {
            Strikes = byStrike.Values.OrderBy(s => s.Strike).ToList(),
            SumDeltaOi = sumDeltaOi,
            SumOi = sumOi,
            MaxU = maxU
        };
    }

    // Глобальный спот-ряд (одинаковая формула на каждом снимке) — для доходностей/моментума.
    var spotGlobal = new double[S];
    for (int i = 0; i < S; i++)
        spotGlobal[i] = snaps[i].Count > 0 ? snaps[i].Values.Max(e => e.MaxU) : 0;

    // Хранилища ДИНАМИЧЕСКИХ фич памяти (заполняются по мере обработки, читаются как прошлое; без look-ahead).
    var netGexAt = new double[S]; Array.Fill(netGexAt, double.NaN);
    var flipAt = new double[S]; Array.Fill(flipAt, double.NaN);
    var oiByStrikeAt = new Dictionary<int, (double C, double P)>[S];

    // Индекс снимка ~targetH часов назад (ближайший в пределах ±tol), иначе −1.
    int PastIdx(int jNow, double targetH, double tol)
    {
        int best = -1; double bestErr = double.MaxValue;
        for (int k = jNow - 1; k >= 0; k--)
        {
            double dh = (times[jNow] - times[k]).TotalHours;
            if (dh > targetH + tol) break;
            double err = Math.Abs(dh - targetH);
            if (dh >= targetH - tol && err < bestErr) { bestErr = err; best = k; }
        }
        return best;
    }

    // Центрированное положение спота в реализованном диапазоне за lb снимков: −1 (дно) … +1 (вершина).
    double RangePosCentered(int jNow, int lb)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        for (int k = Math.Max(0, jNow - lb + 1); k <= jNow; k++)
        {
            double s = spotGlobal[k];
            if (s <= 0 || !double.IsFinite(s)) continue;
            if (s < lo) lo = s;
            if (s > hi) hi = s;
        }
        if (hi <= lo) return double.NaN;
        return 2.0 * (spotGlobal[jNow] - lo) / (hi - lo) - 1.0;
    }

    // Первое появление экспирации (для статистики доступности flow в single-режиме).
    var firstSeen = new Dictionary<string, int>();
    for (int i = 0; i < S; i++)
        foreach (string e in snaps[i].Keys)
            if (!firstSeen.ContainsKey(e)) firstSeen[e] = i;

    var samples = new List<Sample>(S);
    int frontShort8 = 0, frontShort12 = 0, frontTotal = 0;

    for (int j = 0; j < S; j++)
    {
        DateTimeOffset t = times[j];
        Dictionary<string, ExpAgg> dict = snaps[j];
        if (dict.Count == 0) continue;

        List<string> alive = dict
            .Where(kv => kv.Value.SumOi > 0 && YearsToExpiry(kv.Key, t) > 0)
            .Select(kv => kv.Key)
            .ToList();
        if (alive.Count == 0) continue;

        DateTime quarterly = NearestQuarterlyExpiry(t).Date;
        List<string> W = alive.Where(e => ExpDate(e) <= quarterly).OrderBy(e => ExpDate(e)).ToList();
        if (W.Count == 0) W = [alive.OrderBy(e => ExpDate(e)).First()];

        double spot = W.Max(e => dict[e].MaxU);
        if (spot <= 0 || !double.IsFinite(spot)) continue;

        // --- гамма-страйки окна ---
        var gs = new List<GammaStrike>();
        foreach (string e in W)
        {
            double T = YearsToExpiry(e, t);
            if (T <= 0) continue;
            foreach (StrikeAgg s in dict[e].Strikes)
            {
                if (s.CallOi <= 0 && s.PutOi <= 0) continue;
                double iv = s.CallIv > 0 ? s.CallIv : s.PutIv;
                gs.Add(new GammaStrike(s.Strike, s.CallOi, s.PutOi, iv / 100.0, T));
            }
        }
        if (gs.Count == 0) continue;

        // --- режим + flip (профиль 0.70…1.30, 120 точек) ---
        double netGexSpot = NetGexAtPrice(gs, spot);
        const int steps = 120;
        double lo = spot * 0.70, hi = spot * 1.30, step = (hi - lo) / (steps - 1);
        var profP = new double[steps];
        var profG = new double[steps];
        double peak = 0;
        for (int i = 0; i < steps; i++)
        {
            double p = lo + step * i;
            double g = NetGexAtPrice(gs, p);
            profP[i] = p; profG[i] = g;
            double a = Math.Abs(g);
            if (a > peak) peak = a;
        }
        double band = peak * NeutralGexProfileFraction;
        int regime = netGexSpot > band ? 1 : netGexSpot < -band ? -1 : 0;
        double? flip = FlipFromProfile(profP, profG, spot);

        // --- GEX-стены ---
        List<StrikeGexBreakdown> breakdown = StrikeGexAtPrice(gs, spot);
        double? cw = GexCallWall(breakdown, spot);
        double? pw = GexPutWall(breakdown, spot);

        // --- σ сессии по фронту окна ---
        string front = W[0];
        double ivFront = AtmIvFraction(dict[front].Strikes, spot);
        double sessSigma = ivFront > 0 ? spot * ivFront * Math.Sqrt(1.0 / 365.0) : spot * 0.02;

        // --- сигнал 1: структура ---
        double structVote = 0;
        if (regime == 1 && cw is { } c && pw is { } p2 && c > p2)
        {
            double mid = (c + p2) / 2.0, half = (c - p2) / 2.0;
            structVote = -Clamp((spot - mid) / half, -1, 1);
        }
        else if (regime == -1 && flip is { } f && double.IsFinite(f))
        {
            structVote = Math.Tanh((spot - f) / sessSigma / NegGammaFlipDistSigmas);
        }

        // --- сигнал 2: статический DEX ---
        double dexCoin = -W.Sum(e => dict[e].SumDeltaOi);   // как DeltaPoint.DeltaExposure
        double sumOiW = W.Sum(e => dict[e].SumOi);
        double dexNorm = sumOiW > 0 ? Math.Abs(dexCoin) / sumOiW : 0;
        double staticVote = dexCoin == 0 || sumOiW <= 0
            ? 0
            : -(dexCoin > 0 ? 1 : -1) * StaticDexDamp * Math.Tanh(dexNorm / StaticDexScale);

        // --- сигнал 3: поток (резид. и сырой) по последним 12 снимкам ---
        double relResid = 0, relRaw = 0;
        bool flowOk = false;
        if (j >= 11)
        {
            var ser = new List<(double Dex, double Spot)>(12);
            for (int k = j - 11; k <= j; k++)
            {
                double dsum = 0, mu = 0;
                bool any = false;
                foreach (string e in W)
                    if (snaps[k].TryGetValue(e, out ExpAgg? ea))
                    {
                        dsum += ea.SumDeltaOi;
                        if (ea.MaxU > mu) mu = ea.MaxU;
                        any = true;
                    }
                if (any) ser.Add((-dsum, mu));
            }
            if (ser.Count >= 8)
            {
                relResid = TrendRel(ser, residualize: true);
                relRaw = TrendRel(ser, residualize: false);
                flowOk = true;
            }
        }
        double flowVoteResid = flowOk ? -Clamp(Math.Tanh(relResid), -1, 1) : 0;
        double flowVoteRaw = flowOk ? -Clamp(Math.Tanh(relRaw), -1, 1) : 0;
        // Кандидат-фикс: знак перевёрнут (контрарная трактовка) + чувствительность 0.15.
        double flowVoteNew = flowOk ? Clamp(Math.Tanh(relResid / 0.15), -1, 1) : 0;

        // --- сигнал 4: скос 25Δ RR (ближайшая экспирация окна с валидным RR) ---
        double? rr = null;
        foreach (string e in W)
        {
            rr = RR25(dict[e].Strikes);
            if (rr.HasValue) break;
        }
        double skewVote = rr is { } r0 ? -Math.Tanh(r0 / SkewScaleVolPts) : 0;

        // --- составной BIAS страницы (replica BuildSignals) ---
        double bias = ComposeBias(structVote, flowVoteResid, staticVote, rr.HasValue, skewVote);
        double biasRawFlow = ComposeBias(structVote, flowVoteRaw, staticVote, rr.HasValue, skewVote);
        double biasNew = ComposeBias(structVote, flowVoteNew, staticVote, rr.HasValue, skewVote);

        // --- кандидаты ---
        double mom24 = double.NaN;
        if (j >= 8)
        {
            double dh = (t - times[j - 8]).TotalHours;
            if (dh is >= 21 and <= 30 && spotGlobal[j - 8] > 0 && spotGlobal[j] > 0)
                mom24 = Math.Log(spotGlobal[j] / spotGlobal[j - 8]);
        }

        List<(double K, double CallOi, double PutOi)> chainAgg = AggregateChain(W, dict);
        double mp = MaxPain(chainAgg);
        double pin = mp > 0 ? (mp - spot) / spot : double.NaN;

        // ---- ДИНАМИЧЕСКИЕ фичи памяти: HIGH/LOW, изменение гаммы (Net GEX/flip), набор/сброс OI на стенах ----
        var oiMapNow = new Dictionary<int, (double C, double P)>();
        foreach ((double K, double C, double P) in chainAgg) oiMapNow[(int)K] = (C, P);

        double rangePos160 = RangePosCentered(j, 160);   // ~20 дней
        double rangePos40 = RangePosCentered(j, 40);     // ~5 дней
        double flipVal = flip ?? double.NaN;

        int p8 = PastIdx(j, 24, 5);    // ~24ч назад
        int p24 = PastIdx(j, 72, 8);   // ~72ч назад

        double netGexTrend8 = p8 >= 0 && double.IsFinite(netGexAt[p8]) ? netGexSpot - netGexAt[p8] : double.NaN;
        double flipMig8 = p8 >= 0 && double.IsFinite(flipAt[p8]) && double.IsFinite(flipVal) ? flipVal - flipAt[p8] : double.NaN;
        double flipMig24 = p24 >= 0 && double.IsFinite(flipAt[p24]) && double.IsFinite(flipVal) ? flipVal - flipAt[p24] : double.NaN;

        // Набор/сброс OI на ТЕКУЩИХ стенах относительно прошлого среза (>0 — набор на CALL-стене сильнее, чем на PUT).
        double WallNetFlow(int pIdx)
        {
            if (pIdx < 0 || oiByStrikeAt[pIdx] is null) return double.NaN;
            Dictionary<int, (double C, double P)> past = oiByStrikeAt[pIdx];
            double dCall = 0, dPut = 0;
            if (cw is { } cwk)
                dCall = (oiMapNow.TryGetValue((int)cwk, out (double C, double P) an) ? an.C : 0)
                        - (past.TryGetValue((int)cwk, out (double C, double P) ap) ? ap.C : 0);
            if (pw is { } pwk)
                dPut = (oiMapNow.TryGetValue((int)pwk, out (double C, double P) bn) ? bn.P : 0)
                       - (past.TryGetValue((int)pwk, out (double C, double P) bp) ? bp.P : 0);
            return dCall - dPut;
        }
        double PutWallBuild(int pIdx)
        {
            if (pIdx < 0 || oiByStrikeAt[pIdx] is null || pw is not { } pwk) return double.NaN;
            return (oiMapNow.TryGetValue((int)pwk, out (double C, double P) n) ? n.P : 0)
                   - (oiByStrikeAt[pIdx].TryGetValue((int)pwk, out (double C, double P) p) ? p.P : 0);
        }
        double wallFlow8 = WallNetFlow(p8);
        double wallFlow24 = WallNetFlow(p24);
        double putWallBuild24 = PutWallBuild(p24);

        // Сохранить текущие значения для будущих снимков.
        netGexAt[j] = netGexSpot;
        flipAt[j] = flipVal;
        oiByStrikeAt[j] = oiMapNow;

        // Голоса фич памяти (знаки — из IC: flipMig +, wallFlow − [набор CALL→вниз], netGex +, range +).
        double vFlipMig = double.IsNaN(flipMig8) ? 0 : Clamp(Math.Tanh(flipMig8 / Math.Max(sessSigma, 1e-9)), -1, 1);
        double vWall = double.IsNaN(wallFlow8) || sumOiW <= 0 ? 0 : Clamp(-Math.Tanh(wallFlow8 / (0.03 * sumOiW)), -1, 1);
        double vNetGex = double.IsNaN(netGexTrend8) || peak <= 0 ? 0 : Clamp(Math.Tanh(netGexTrend8 / peak), -1, 1);
        double vRange = double.IsNaN(rangePos160) ? 0 : Clamp(rangePos160, -1, 1);
        double biasMem = ComposeBiasMem(structVote, flowVoteNew, staticVote, rr.HasValue, skewVote, vFlipMig, vWall, vNetGex, vRange);
        double memOnly = ComposeMemOnly(vFlipMig, vWall, vNetGex, vRange);

        // --- форвардные доходности (глобальный спот-ряд) ---
        var fwd = new double[horizonsH.Length];
        for (int hI = 0; hI < horizonsH.Length; hI++)
        {
            fwd[hI] = double.NaN;
            double N = horizonsH[hI];
            for (int k = j + 1; k < S; k++)
            {
                double dh = (times[k] - t).TotalHours;
                if (dh > N + 0.5) break;
                if (dh >= N - 1.6 && spotGlobal[k] > 0 && spotGlobal[j] > 0)
                    fwd[hI] = Math.Log(spotGlobal[k] / spotGlobal[j]);
            }
        }

        // --- статистика доступности flow в single-режиме (история фронта) ---
        frontTotal++;
        int lenFront = j - firstSeen[front] + 1;
        if (lenFront < 8) frontShort8++;
        if (lenFront < 12) frontShort12++;

        samples.Add(new Sample(regime, structVote, staticVote,
            flowVoteResid, flowVoteRaw, flowVoteNew, skewVote, bias, biasRawFlow, biasNew,
            mom24, pin,
            rangePos160, rangePos40, netGexTrend8, flipMig8, flipMig24, wallFlow8, wallFlow24, putWallBuild24,
            biasMem, memOnly,
            fwd[0], fwd[1], fwd[2], flowOk));
    }

    // ================= ОТЧЁТ =================
    Console.WriteLine();
    Console.WriteLine($"=== {curName} ===  снимков={S}, сэмплов={samples.Count}, период [{times[0]:yyyy-MM-dd}..{times[^1]:yyyy-MM-dd}]");

    int nStruct = samples.Count(s => s.Struct != 0);
    int nFlowAvail = samples.Count(s => s.FlowOk);
    int nFlowFires = samples.Count(s => Math.Abs(s.FlowResid) >= FlowEpsilon);
    int nFlowRawFires = samples.Count(s => Math.Abs(s.FlowRaw) >= FlowEpsilon);
    int nSkew = samples.Count(s => s.Skew != 0);

    Console.WriteLine($"  Доступность: structure {Pct(nStruct, samples.Count)} | skew {Pct(nSkew, samples.Count)} | " +
                      $"flow-серия есть {Pct(nFlowAvail, samples.Count)}");
    Console.WriteLine($"  ПОЧЕМУ «всегда статический»: |flow_resid|≥{FlowEpsilon}: {Pct(nFlowFires, samples.Count)} " +
                      $"(сырой до резидуализации: {Pct(nFlowRawFires, samples.Count)})");

    double[] absResid = samples.Where(s => s.FlowOk).Select(s => Math.Abs(s.FlowResid)).OrderBy(x => x).ToArray();
    double[] absRaw = samples.Where(s => s.FlowOk).Select(s => Math.Abs(s.FlowRaw)).OrderBy(x => x).ToArray();
    if (absResid.Length > 0)
    {
        Console.WriteLine($"  |flow_resid| перцентили: p50={Pc(absResid, 50):0.0000} p75={Pc(absResid, 75):0.0000} " +
                          $"p90={Pc(absResid, 90):0.0000} p95={Pc(absResid, 95):0.0000} p99={Pc(absResid, 99):0.0000}");
        Console.WriteLine($"  |flow_raw|   перцентили: p50={Pc(absRaw, 50):0.0000} p75={Pc(absRaw, 75):0.0000} " +
                          $"p90={Pc(absRaw, 90):0.0000} p95={Pc(absRaw, 95):0.0000} p99={Pc(absRaw, 99):0.0000}");
    }
    Console.WriteLine($"  Single-режим: история фронт-экспирации <8 снимков: {Pct(frontShort8, frontTotal)}; <12: {Pct(frontShort12, frontTotal)}");

    int rPos = samples.Count(s => s.Regime == 1), rNeg = samples.Count(s => s.Regime == -1);
    Console.WriteLine($"  Режимы: +гамма {Pct(rPos, samples.Count)}, −гамма {Pct(rNeg, samples.Count)}, нейтр {Pct(samples.Count - rPos - rNeg, samples.Count)}");

    Console.WriteLine();
    Console.WriteLine($"  IC (Pearson; в скобках — без перекрытия окон), hit% по знаку:");
    Console.WriteLine($"    {"сигнал",-14}{"6ч",22}{"12ч",22}{"24ч",22}");

    PrintIc("structure", samples, s => s.Struct, s => s.Struct != 0);
    PrintIc("staticDEX", samples, s => s.Static, s => s.Static != 0);
    PrintIc("flowResid", samples, s => s.FlowResid, s => s.FlowOk && s.FlowResid != 0);
    PrintIc("flowRaw", samples, s => s.FlowRaw, s => s.FlowOk && s.FlowRaw != 0);
    PrintIc("flowFlip(new)", samples, s => s.FlowNew, s => s.FlowOk && s.FlowNew != 0);
    PrintIc("skew25RR", samples, s => s.Skew, s => s.Skew != 0);
    PrintIc("BIAS(стр.)", samples, s => s.Bias, s => s.Bias != 0);
    PrintIc("BIAS(rawFl)", samples, s => s.BiasRawFlow, s => s.BiasRawFlow != 0);
    PrintIc("BIAS(new)", samples, s => s.BiasNew, s => s.BiasNew != 0);
    int nFlowNewFires = samples.Count(s => Math.Abs(s.FlowNew) >= FlowEpsilon);
    Console.WriteLine($"    flowFlip(new) активен (|vote|≥{FlowEpsilon}): {Pct(nFlowNewFires, samples.Count)}");
    PrintIc("mom24 [канд]", samples, s => s.Mom24, s => double.IsFinite(s.Mom24));
    PrintIc("pinMP [канд]", samples, s => s.Pin, s => double.IsFinite(s.Pin));

    Console.WriteLine();
    Console.WriteLine("  --- ДИНАМИЧЕСКИЕ фичи памяти (IC vs форвард-доходность; нужна согласованность знака BTC↔ETH) ---");
    PrintIc("rangePos160", samples, s => s.RangePos160, s => double.IsFinite(s.RangePos160));
    PrintIc("rangePos40", samples, s => s.RangePos40, s => double.IsFinite(s.RangePos40));
    PrintIc("netGexTrend8", samples, s => s.NetGexTrend8, s => double.IsFinite(s.NetGexTrend8));
    PrintIc("flipMig8", samples, s => s.FlipMig8, s => double.IsFinite(s.FlipMig8));
    PrintIc("flipMig24", samples, s => s.FlipMig24, s => double.IsFinite(s.FlipMig24));
    PrintIc("wallFlow8", samples, s => s.WallFlow8, s => double.IsFinite(s.WallFlow8));
    PrintIc("wallFlow24", samples, s => s.WallFlow24, s => double.IsFinite(s.WallFlow24));
    PrintIc("putWallBuild24", samples, s => s.PutWallBuild24, s => double.IsFinite(s.PutWallBuild24));

    Console.WriteLine();
    Console.WriteLine("  --- КОМПОЗИТЫ с памятью (сравни с BIAS(new) выше) ---");
    PrintIc("BIAS+память", samples, s => s.BiasMem, s => s.BiasMem != 0);
    PrintIc("ТОЛЬКО память", samples, s => s.MemOnly, s => s.MemOnly != 0);

    void PrintIc(string name, List<Sample> all, Func<Sample, double> sig, Func<Sample, bool> use)
    {
        var cells = new string[horizonsH.Length];
        for (int hI = 0; hI < horizonsH.Length; hI++)
        {
            Func<Sample, double> f = hI == 0 ? s => s.F6 : hI == 1 ? s => s.F12 : s => s.F24;
            var pts = all.Where(s => use(s) && double.IsFinite(f(s))).Select(s => (sig(s), f(s))).ToList();
            (double ic, int n) = Pearson(pts);
            // прореживание: шаг = горизонт/каденс (3ч) → 2/4/8
            int stp = horizonsH[hI] / 3;
            var sub = all.Where((s, idx) => idx % stp == 0 && use(s) && double.IsFinite(f(s))).Select(s => (sig(s), f(s))).ToList();
            (double icSub, _) = Pearson(sub);
            double hit = pts.Count > 0
                ? pts.Count(x => Math.Sign(x.Item1) == Math.Sign(x.Item2) && x.Item1 != 0 && x.Item2 != 0) /
                  (double)Math.Max(1, pts.Count(x => x.Item1 != 0 && x.Item2 != 0))
                : 0;
            cells[hI] = n == 0 ? "—" : $"{ic:+0.000;-0.000}({icSub:+0.00;-0.00}) {hit * 100:0.0}%";
        }
        Console.WriteLine($"    {name,-14}{cells[0],22}{cells[1],22}{cells[2],22}");
    }
}

Console.WriteLine();
return 0;

// =====================================================================
//  Хелперы (формулы — вербатим из SessionAnalysisMath/OptionExposureMath)
// =====================================================================

DateTime ExpDate(string code)
{
    if (!expDateCache.TryGetValue(code, out DateTime d))
    {
        d = DateTime.ParseExact(code, "dMMMyy", CultureInfo.InvariantCulture).Date;
        expDateCache[code] = d;
    }
    return d;
}

static double YearsToExpiry(string expiration, DateTimeOffset asOf)
{
    if (!DateTime.TryParseExact(expiration, "dMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
        return 0;
    var expiryUtc = new DateTimeOffset(d.Year, d.Month, d.Day, 8, 0, 0, TimeSpan.Zero);
    return Math.Max((expiryUtc - asOf).TotalDays / 365.0, 0.0);
}

static DateTime NearestQuarterlyExpiry(DateTimeOffset asOf)
{
    int[] quarterMonths = [3, 6, 9, 12];
    DateTime cursor = asOf.UtcDateTime;
    for (int i = 0; i < 24; i++)
    {
        DateTime m = cursor.AddMonths(i);
        if (Array.IndexOf(quarterMonths, m.Month) < 0) continue;
        DateTime last = new(m.Year, m.Month, DateTime.DaysInMonth(m.Year, m.Month));
        int diff = ((int)last.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7;
        DateTime lastFri = last.AddDays(-diff);
        var expiryUtc = new DateTimeOffset(lastFri.Year, lastFri.Month, lastFri.Day, 8, 0, 0, TimeSpan.Zero);
        if (expiryUtc >= asOf) return lastFri;
    }
    return new DateTime(cursor.Year + 1, 3, 31);
}

static double Black76Gamma(double f, double k, double sigmaFraction, double tYears)
{
    const double invSqrt2Pi = 0.39894228040143267793994605993438;
    if (sigmaFraction <= 0 || tYears <= 0 || f <= 0 || k <= 0) return 0;
    double sqrtT = Math.Sqrt(tYears);
    double denom = f * sigmaFraction * sqrtT;
    if (denom <= 0) return 0;
    double d1 = (Math.Log(f / k) + 0.5 * sigmaFraction * sigmaFraction * tYears) / (sigmaFraction * sqrtT);
    double pdf = invSqrt2Pi * Math.Exp(-0.5 * d1 * d1);
    double gamma = pdf / denom;
    return double.IsFinite(gamma) ? gamma : 0;
}

static double NetGexAtPrice(IReadOnlyList<GammaStrike> strikes, double price)
{
    if (strikes.Count == 0 || price <= 0 || !double.IsFinite(price)) return 0;
    double scale = price * price * 0.01;
    double sum = 0;
    for (int i = 0; i < strikes.Count; i++)
    {
        GammaStrike s = strikes[i];
        double gamma = Black76Gamma(price, s.Strike, s.SigmaFraction, s.TYears);
        if (gamma == 0) continue;
        sum += gamma * (s.CallOi - s.PutOi) * scale;
    }
    return double.IsFinite(sum) ? sum : 0;
}

static List<StrikeGexBreakdown> StrikeGexAtPrice(IReadOnlyList<GammaStrike> strikes, double price)
{
    var result = new List<StrikeGexBreakdown>();
    if (strikes.Count == 0 || price <= 0 || !double.IsFinite(price)) return result;
    double scale = price * price * 0.01;
    var byStrike = new Dictionary<double, (double Call, double Put)>();
    for (int i = 0; i < strikes.Count; i++)
    {
        GammaStrike s = strikes[i];
        double gamma = Black76Gamma(price, s.Strike, s.SigmaFraction, s.TYears);
        if (gamma == 0) continue;
        byStrike.TryGetValue(s.Strike, out (double Call, double Put) acc);
        acc.Call += gamma * s.CallOi * scale;
        acc.Put += gamma * s.PutOi * scale;
        byStrike[s.Strike] = acc;
    }
    foreach (var kv in byStrike)
        if (double.IsFinite(kv.Value.Call) && double.IsFinite(kv.Value.Put))
            result.Add(new StrikeGexBreakdown(kv.Key, kv.Value.Call, kv.Value.Put));
    result.Sort((a, b) => a.Strike.CompareTo(b.Strike));
    return result;
}

static double? GexCallWall(IReadOnlyList<StrikeGexBreakdown> breakdown, double spot)
{
    double? best = null;
    double bestGex = 0;
    foreach (StrikeGexBreakdown b in breakdown)
    {
        if (b.Strike <= spot || b.CallGex <= 0) continue;
        if (best is null || b.CallGex > bestGex) { best = b.Strike; bestGex = b.CallGex; }
    }
    return best;
}

static double? GexPutWall(IReadOnlyList<StrikeGexBreakdown> breakdown, double spot)
{
    double? best = null;
    double bestGex = 0;
    foreach (StrikeGexBreakdown b in breakdown)
    {
        if (b.Strike >= spot || b.PutGex <= 0) continue;
        if (best is null || b.PutGex > bestGex) { best = b.Strike; bestGex = b.PutGex; }
    }
    return best;
}

static double? FlipFromProfile(double[] prices, double[] gex, double spot)
{
    double? bestFlip = null;
    double bestDistance = double.MaxValue;
    for (int i = 0; i < prices.Length - 1; i++)
    {
        double y0 = gex[i], y1 = gex[i + 1];
        if (!double.IsFinite(y0) || !double.IsFinite(y1)) continue;
        bool crosses = (y0 < 0 && y1 > 0) || (y0 > 0 && y1 < 0);
        bool touches = y0 == 0;
        double crossPrice;
        if (touches) crossPrice = prices[i];
        else if (crosses)
        {
            double span = y1 - y0;
            if (span == 0) continue;
            crossPrice = prices[i] + (-y0 / span) * (prices[i + 1] - prices[i]);
        }
        else continue;
        double dist = Math.Abs(crossPrice - spot);
        if (dist < bestDistance) { bestDistance = dist; bestFlip = crossPrice; }
    }
    return bestFlip;
}

static double AtmIvFraction(List<StrikeAgg> strikes, double spot)
{
    double best = 0, bestDist = double.MaxValue;
    foreach (StrikeAgg s in strikes)
    {
        double iv = s.CallIv > 0 ? s.CallIv : s.PutIv;
        if (iv <= 0) continue;
        double d = Math.Abs(s.Strike - spot);
        if (d < bestDist) { bestDist = d; best = iv; }
    }
    return best / 100.0;
}

// Реплика текущего DexTrend (возвращает rel ДО tanh, чтобы изучать масштаб).
static double TrendRel(List<(double Dex, double Spot)> ser, bool residualize)
{
    int n = ser.Count;
    if (n < 4) return 0;
    double meanAbs = 0;
    for (int i = 0; i < n; i++)
    {
        if (!double.IsFinite(ser[i].Dex)) return 0;
        meanAbs += Math.Abs(ser[i].Dex);
    }
    meanAbs /= n;
    if (meanAbs <= 0) return 0;

    var vals = new double[n];
    bool used = false;
    if (residualize)
    {
        double meanS = 0, meanY = 0;
        bool ok = true;
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(ser[i].Spot)) { ok = false; break; }
            meanS += ser[i].Spot;
            meanY += ser[i].Dex;
        }
        if (ok)
        {
            meanS /= n; meanY /= n;
            double sSS = 0, sSY = 0;
            for (int i = 0; i < n; i++)
            {
                double ds = ser[i].Spot - meanS;
                sSS += ds * ds;
                sSY += ds * (ser[i].Dex - meanY);
            }
            if (sSS > 0)
            {
                double b = sSY / sSS, a = meanY - b * meanS;
                for (int i = 0; i < n; i++) vals[i] = ser[i].Dex - (a + b * ser[i].Spot);
                used = true;
            }
        }
    }
    if (!used)
        for (int i = 0; i < n; i++) vals[i] = ser[i].Dex;

    double meanX = (n - 1) / 2.0, meanV = 0;
    for (int i = 0; i < n; i++) meanV += vals[i];
    meanV /= n;
    double sxy = 0, sxx = 0;
    for (int i = 0; i < n; i++)
    {
        double dx = i - meanX;
        sxy += dx * (vals[i] - meanV);
        sxx += dx * dx;
    }
    if (sxx <= 0) return 0;
    double rel = sxy / sxx * (n - 1) / meanAbs;
    return double.IsFinite(rel) ? rel : 0;
}

static double? RR25(List<StrikeAgg> strikes)
{
    var callPts = strikes
        .Where(s => s.CallOi > 0 && s.CallDelta > 0 && s.CallIv > 0)
        .Select(s => (Abs: s.CallDelta, Iv: s.CallIv));
    var putPts = strikes
        .Where(s => s.PutOi > 0 && s.PutDelta < 0)
        .Select(s => (Abs: Math.Abs(s.PutDelta), Iv: s.PutIv > 0 ? s.PutIv : s.CallIv))
        .Where(x => x.Iv > 0);
    double? c = InterpIv(callPts, 0.25);
    double? p = InterpIv(putPts, 0.25);
    if (c is null || p is null) return null;
    return p - c;
}

static double? InterpIv(IEnumerable<(double Abs, double Iv)> points, double target)
{
    const double edgeTol = 0.10;
    List<(double Abs, double Iv)> p = points.OrderBy(x => x.Abs).ToList();
    if (p.Count == 0) return null;
    if (p.Count == 1) return p[0].Iv;
    for (int i = 0; i < p.Count - 1; i++)
    {
        if (target >= p[i].Abs && target <= p[i + 1].Abs)
        {
            double span = p[i + 1].Abs - p[i].Abs;
            if (span <= 0) return p[i].Iv;
            double w = (target - p[i].Abs) / span;
            return p[i].Iv + w * (p[i + 1].Iv - p[i].Iv);
        }
    }
    bool below = target < p[0].Abs;
    double edgeDelta = below ? p[0].Abs : p[^1].Abs;
    double edgeIv = below ? p[0].Iv : p[^1].Iv;
    return Math.Abs(edgeDelta - target) <= edgeTol ? edgeIv : null;
}

static List<(double K, double CallOi, double PutOi)> AggregateChain(List<string> w, Dictionary<string, ExpAgg> dict)
{
    var byK = new Dictionary<int, (double C, double P)>();
    foreach (string e in w)
        foreach (StrikeAgg s in dict[e].Strikes)
        {
            byK.TryGetValue(s.Strike, out (double C, double P) acc);
            acc.C += s.CallOi;
            acc.P += s.PutOi;
            byK[s.Strike] = acc;
        }
    return byK.Select(kv => ((double)kv.Key, kv.Value.C, kv.Value.P)).OrderBy(x => x.Item1).ToList();
}

static double MaxPain(List<(double K, double CallOi, double PutOi)> chain)
{
    double bestStrike = 0, minLoss = double.MaxValue;
    foreach (var target in chain)
    {
        double loss = 0;
        foreach (var o in chain)
        {
            loss += o.CallOi * Math.Max(0, target.K - o.K);
            loss += o.PutOi * Math.Max(0, o.K - target.K);
        }
        if (loss < minLoss) { minLoss = loss; bestStrike = target.K; }
    }
    return bestStrike;
}

static double ComposeBias(double structVote, double flowVote, double staticVote, bool hasSkew, double skewVote)
{
    var comps = new List<(double V, double W)>();
    if (structVote != 0) comps.Add((structVote, 0.45));
    if (Math.Abs(flowVote) >= FlowEpsilon) comps.Add((flowVote, 0.35));
    else if (staticVote != 0) comps.Add((staticVote, 0.35));
    if (hasSkew) comps.Add((skewVote, 0.20));
    double wsum = comps.Sum(x => x.W);
    if (wsum <= 0) return 0;
    return Clamp(comps.Sum(x => x.V * x.W) / wsum, -1, 1);
}

// Композит с фичами ПАМЯТИ (пробные веса; знаки валидированы IC). Существующие веса ужаты,
// чтобы освободить место под flipMig/wallFlow (сильнейшие новые) и слабые netGex/range.
static double ComposeBiasMem(double structVote, double flowVote, double staticVote, bool hasSkew, double skewVote,
    double flipMig, double wallFlow, double netGex, double range)
{
    var comps = new List<(double V, double W)>();
    if (structVote != 0) comps.Add((structVote, 0.28));
    if (Math.Abs(flowVote) >= FlowEpsilon) comps.Add((flowVote, 0.18));
    else if (staticVote != 0) comps.Add((staticVote, 0.18));
    if (hasSkew) comps.Add((skewVote, 0.12));
    if (flipMig != 0) comps.Add((flipMig, 0.22));
    if (wallFlow != 0) comps.Add((wallFlow, 0.12));
    if (netGex != 0) comps.Add((netGex, 0.04));
    if (range != 0) comps.Add((range, 0.04));
    double wsum = comps.Sum(x => x.W);
    if (wsum <= 0) return 0;
    return Clamp(comps.Sum(x => x.V * x.W) / wsum, -1, 1);
}

// Композит ТОЛЬКО из памяти — самостоятельная предсказательная сила набора.
static double ComposeMemOnly(double flipMig, double wallFlow, double netGex, double range)
{
    var comps = new List<(double V, double W)>();
    if (flipMig != 0) comps.Add((flipMig, 0.45));
    if (wallFlow != 0) comps.Add((wallFlow, 0.27));
    if (netGex != 0) comps.Add((netGex, 0.13));
    if (range != 0) comps.Add((range, 0.15));
    double wsum = comps.Sum(x => x.W);
    if (wsum <= 0) return 0;
    return Clamp(comps.Sum(x => x.V * x.W) / wsum, -1, 1);
}

static (double Ic, int N) Pearson(List<(double S, double F)> pts)
{
    int n = pts.Count;
    if (n < 3) return (0, n);
    double ms = pts.Average(x => x.S), mf = pts.Average(x => x.F);
    double css = 0, cff = 0, csf = 0;
    foreach ((double s, double f) in pts)
    {
        double ds = s - ms, df = f - mf;
        css += ds * ds;
        cff += df * df;
        csf += ds * df;
    }
    if (css <= 0 || cff <= 0) return (0, n);
    return (csf / Math.Sqrt(css * cff), n);
}

static double Pc(double[] sortedAsc, double pct)
{
    if (sortedAsc.Length == 0) return 0;
    double idx = (sortedAsc.Length - 1) * pct / 100.0;
    int loI = (int)Math.Floor(idx);
    int hiI = (int)Math.Ceiling(idx);
    if (loI == hiI) return sortedAsc[loI];
    double w = idx - loI;
    return sortedAsc[loI] * (1 - w) + sortedAsc[hiI] * w;
}

static string Pct(int part, int total) => total > 0 ? $"{100.0 * part / total:0.0}%" : "—";

static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

internal readonly record struct RowR(
    DateTimeOffset CreatedAt, string Expiration, int Strike, int OptionTypeId,
    double OpenInterest, double Iv, double UnderlyingPrice, double Delta);

internal sealed class StrikeAgg
{
    public int Strike;
    public double CallOi, PutOi, CallIv, PutIv, CallDelta, PutDelta;
}

internal sealed class ExpAgg
{
    public List<StrikeAgg> Strikes = new();
    public double SumDeltaOi, SumOi, MaxU;
}

internal readonly record struct GammaStrike(double Strike, double CallOi, double PutOi, double SigmaFraction, double TYears);

internal readonly record struct StrikeGexBreakdown(double Strike, double CallGex, double PutGex);

internal readonly record struct Sample(
    int Regime, double Struct, double Static,
    double FlowResid, double FlowRaw, double FlowNew, double Skew,
    double Bias, double BiasRawFlow, double BiasNew,
    double Mom24, double Pin,
    double RangePos160, double RangePos40, double NetGexTrend8, double FlipMig8, double FlipMig24,
    double WallFlow8, double WallFlow24, double PutWallBuild24,
    double BiasMem, double MemOnly,
    double F6, double F12, double F24, bool FlowOk);
