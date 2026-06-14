using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Option.Data.Database;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;

// ======================================================================
//  Бэктест ТОРГОВЛИ по рекомендациям страницы Trade («Сводка») БОЕВЫМ кодом.
//
//  Хронологическая симуляция от самого старого среза к свежему:
//  - вне позиции: на каждом срезе строится рекомендация тем же конвейером,
//    что TradeModel.Compute (ExpirationAnalysisBuilder + BuildAggregate);
//    дельта-ряд для потока ΔDEX обрезан по времени среза — БЕЗ подглядывания;
//  - активный вход (импульс/направленная/фейд у стены) — открытие по споту среза;
//  - отложенный фейд (вход из середины диапазона) — лимит в середине зоны входа,
//    живёт FillWindowHours; пролёт зоны до стопа за один бар = вход + стоп (консервативно);
//  - позиция ведётся по следующим срезам: цена пересекла стоп → выход по стопу (−1R),
//    цель → выход по цели; оба уровня за один бар → СТОП (консервативно);
//    тайм-стоп TimeStopHours → выход по рынку; конец данных → по последней цене.
//  - одна позиция на монету; цены — в той же конвенции, что уровни страницы
//    (max форвард окна агрегации, окно ФИКСИРУЕТСЯ на момент входа).
//
//  Ограничение метода: срезы каждые 3 ч, внутрибарные движения не видны —
//  касания стопа/цели между срезами не детектируются (двусмысленные бары
//  считаются стопом). Комиссия: тейкер perp 0.05% за сторону.
//
//  Запуск: dotnet run --project tradebacktest -c Release [BTC|ETH|ALL]
// ======================================================================

const double FillWindowHours = 24;   // жизнь отложенного входа (фейд из середины)
const double FeePerSide = 0.0005;    // Deribit perp taker 0.05%
const int RangeLookback = 240;       // ~30 дней (8 срезов/день) — скользящее окно реализованного диапазона / перцентиля OI

// --- Параметры тестируемого ФИКСА (flip-aware стоп/вход), применяются только в режимах FLIPSTOP/FLIPENTRY ---
const double FlipBufferSig = 0.3;       // буфер стопа/входа за flip, σ сессии
const double FlipEntryMinSig = 1.0;     // отложенный вход к flip включается, если flip ≥ этого числа σ в сторону отскока
const double FlipEntryFillHours = 96;   // жизнь отложенного входа к flip (ждём возврата к flip до 4 дней)

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

string modeArg = args.Length > 0 ? args[0].ToUpperInvariant() : "ALL";
string[] symbols = modeArg == "ALL" ? ["BTC", "ETH"] : [modeArg];

// Тайм-стоп позиции, ч (2-й аргумент; по умолчанию 5 дней) — параметр СИМУЛЯЦИИ, не страницы.
double TimeStopHours = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 120;
Console.WriteLine($"Тайм-стоп позиции: {TimeStopHours:0} ч");

// ALT (3-й аргумент): эмуляция ПРЕДЛОЖЕННЫХ целей импульса/направленных — вместо σ-границ
// КВАРТАЛЬНОГО горизонта кандидатами становятся spot ∓ 1.5/2.5 σ СЕССИИ (стена и пин остаются).
// Боевой код не меняется — цель пересчитывается поверх готовой рекомендации тем же фильтром PickTarget.
string mode = args.Length > 2 ? args[2].ToUpperInvariant() : "BASE";
bool altTargets = mode == "ALT";
Console.WriteLine(mode switch
{
    "ALT" => "Режим ALT: цели импульса/направленных — стена/пин/spot∓1.5σ/2.5σ сессии (вместо σ-границ горизонта)",
    "FLIPSTOP" => "Режим FLIPSTOP: стоп импульса/направленных переносится ЗА gamma-flip, если flip в зоне отскока за стопом (фикс-тест)",
    "FLIPENTRY" => $"Режим FLIPENTRY: импульс/направленная с flip ≥ {FlipEntryMinSig:0.#}σ в сторону отскока → ОТЛОЖЕННЫЙ вход к flip, стоп за flip, цель прежняя (фикс-тест)",
    _ => "Режим BASE: боевая логика без изменений"
});

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string appsettingsPath = Path.Combine(root, "Option.Data.Ui", "appsettings.json");
string conn;
using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath)))
    conn = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString()!;

var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(conn).Options;

var expirationBuilder = new ExpirationAnalysisBuilder();
var sessionBuilder = new SessionRecommendationBuilder();
var memoryBuilder = new ContractMemoryBuilder();

var combined = new List<(string Symbol, List<TradeRec> Trades)>();

foreach (string symbol in symbols)
{
    int curId = symbol == "BTC" ? 1 : 2;
    Console.WriteLine($"--- {symbol}: загрузка всей истории из БД...");

    List<DeribitData> all;
    await using (var ctx = new ApplicationDbContext(dbOptions))
    {
        ctx.Database.SetCommandTimeout(600);
        all = await ctx.DeribitData.AsNoTracking()
            .Where(d => d.CurrencyTypeId == curId)
            .ToListAsync();
    }
    Console.WriteLine($"    строк: {all.Count:N0}");

    // --- индексы: срезы по времени; по каждому срезу — агрегаты по экспирациям ---
    var rowsAt = all.GroupBy(d => d.CreatedAt).ToDictionary(g => g.Key, g => g.ToList());
    List<DateTimeOffset> times = rowsAt.Keys.OrderBy(t => t).ToList();

    var perExp = new Dictionary<DateTimeOffset, Dictionary<string, ExpAgg>>(times.Count);
    foreach (DateTimeOffset t in times)
        perExp[t] = new Dictionary<string, ExpAgg>();
    foreach (DeribitData d in all)
    {
        Dictionary<string, ExpAgg> dict = perExp[d.CreatedAt];
        if (!dict.TryGetValue(d.Expiration, out ExpAgg? agg))
        {
            agg = new ExpAgg();
            dict[d.Expiration] = agg;
        }
        agg.Px = Math.Max(agg.Px, d.UnderlyingPrice);
        agg.Dex -= d.Delta * d.OpenInterest;   // DeltaExposure = −Σ(Δ·OI)
        agg.Oi += d.OpenInterest;
    }

    // Глобальный прокси спота по снимку: max форвард среди ВСЕХ экспираций
    // (стабильный «цена базового актива во времени» для реализованного диапазона и моментума —
    //  НАБЛЮДАТЕЛЬНО, в торговую логику не входит).
    double[] spotAt = new double[times.Count];
    for (int k = 0; k < times.Count; k++)
    {
        double mx = double.MinValue;
        foreach (ExpAgg a in perExp[times[k]].Values)
            mx = Math.Max(mx, a.Px);
        spotAt[k] = mx;
    }

    // Фичи «памяти контракта» на момент входа (только данные ≤ i — без подглядывания).
    MemFeat ComputeFeat(int i, double entry, int dir, double? flipPx, double? targetWall, double stop, double sigma, List<string> win)
    {
        // Реализованный диапазон по СКОЛЬЗЯЩЕМУ окну ~30 дней (не since-inception:
        // на годовом тренде since-inception вырождается — цена всегда у дна/вершины).
        double lo = double.MaxValue, hi = double.MinValue;
        for (int k = Math.Max(0, i - RangeLookback + 1); k <= i; k++) { lo = Math.Min(lo, spotAt[k]); hi = Math.Max(hi, spotAt[k]); }
        double rangePos = hi > lo ? SessionAnalysisMath.Clamp((entry - lo) / (hi - lo), 0, 1) : 0.5;

        // Краткосрочный моментум: откат вверх от мин. и просадка от макс. за последние 8 срезов (~24ч).
        int j0 = Math.Max(0, i - 7);
        double lo8 = double.MaxValue, hi8 = double.MinValue;
        for (int k = j0; k <= i; k++) { lo8 = Math.Min(lo8, spotAt[k]); hi8 = Math.Max(hi8, spotAt[k]); }
        double runupLow8 = lo8 > 0 ? (entry - lo8) / lo8 * 100.0 : 0;
        double ddHigh8 = hi8 > 0 ? (hi8 - entry) / hi8 * 100.0 : 0;

        // Положение flip ОТНОСИТЕЛЬНО сделки (against = направление отскока против позиции).
        // flipHeadroom>0  — flip в стороне отскока (есть куда откатиться против нас);
        // flipBeyondStop>0 — flip ДАЛЬШЕ стопа в стороне отскока ⇒ стоп сидит ВНУТРИ зоны отскока к flip.
        double s = sigma > 0 && double.IsFinite(sigma) ? sigma : entry * 0.01;
        double flipHeadroom = flipPx is { } f && double.IsFinite(f) ? (f - entry) * -dir / s : 0;
        double flipBeyondStop = flipPx is { } f2 && double.IsFinite(f2) ? (f2 - stop) * -dir / s : 0;

        // Близость к структурной стене в СТОРОНЕ ЦЕЛИ (−γ-пол для шорта / потолок для лонга): мало ⇒ истощение.
        // dir·(стена−вход): для шорта (dir=−1, стена ниже) = (вход−стена)/вход·100 > 0; для лонга — (стена−вход)/вход·100 > 0.
        double distTargetWall = targetWall is { } w2 && double.IsFinite(w2) && w2 > 0
            ? (w2 - entry) * dir / entry * 100.0
            : double.NaN;

        // Репрезентативность OI: перцентиль текущего OI окна среди собственной истории окна.
        double curOi = 0;
        foreach (string e in win) if (perExp[times[i]].TryGetValue(e, out ExpAgg? a)) curOi += a.Oi;
        int below = 0, tot = 0;
        for (int k = Math.Max(0, i - RangeLookback + 1); k <= i; k++)
        {
            double o = 0; bool any = false;
            foreach (string e in win) if (perExp[times[k]].TryGetValue(e, out ExpAgg? a)) { o += a.Oi; any = true; }
            if (!any) continue;
            tot++;
            if (o <= curOi) below++;
        }
        double oiPctile = tot > 0 ? 100.0 * below / tot : 50.0;

        return new MemFeat(rangePos, runupLow8, ddHigh8, flipHeadroom, flipBeyondStop, distTargetWall, oiPctile);
    }

    // Цена в конвенции страницы: max форвард по окну (окно фиксируется при входе).
    double PriceAt(DateTimeOffset t, List<string> window)
    {
        Dictionary<string, ExpAgg> dict = perExp[t];
        double px = double.MinValue;
        foreach (string e in window)
            if (dict.TryGetValue(e, out ExpAgg? a))
                px = Math.Max(px, a.Px);
        if (px == double.MinValue)
            foreach (ExpAgg a in dict.Values)
                px = Math.Max(px, a.Px);
        return px;
    }

    // История срезов окна (последние ~170 ≈ 21 день) для «памяти контракта» — без look-ahead (≤ iNow).
    List<MemorySnapshot> BuildMemHistory(int iNow, List<string> win)
    {
        var snaps = new List<MemorySnapshot>();
        var winSet = new HashSet<string>(win);
        int fromK = Math.Max(0, iNow - 169);
        for (int k = fromK; k <= iNow; k++)
        {
            if (!rowsAt.TryGetValue(times[k], out List<DeribitData>? rk)) continue;
            List<DeribitData> winRows = rk.Where(r => winSet.Contains(r.Expiration)).ToList();
            if (winRows.Count == 0) continue;
            double spot = winRows.Max(r => r.UnderlyingPrice);
            if (spot <= 0 || !double.IsFinite(spot)) continue;

            var gs = new List<SessionAnalysisMath.GammaStrike>();
            foreach (IGrouping<string, DeribitData> expG in winRows.GroupBy(r => r.Expiration))
            {
                double tt = OptionExposureMath.YearsToExpiry(expG.Key, times[k]);
                if (tt <= 0) continue;
                foreach (IGrouping<int, DeribitData> kG in expG.GroupBy(r => r.Strike))
                {
                    double callOi = 0, putOi = 0, callIv = 0, putIv = 0;
                    foreach (DeribitData r in kG)
                    {
                        if (r.OptionTypeId == 1) { callOi += r.OpenInterest; if (r.Iv > 0) callIv = r.Iv; }
                        else { putOi += r.OpenInterest; if (r.Iv > 0) putIv = r.Iv; }
                    }
                    if (callOi <= 0 && putOi <= 0) continue;
                    gs.Add(new SessionAnalysisMath.GammaStrike(kG.Key, callOi, putOi, (callIv > 0 ? callIv : putIv) / 100.0, tt));
                }
            }

            List<OptionData> chain = winRows.GroupBy(r => r.Strike).Select(kG => new OptionData
            {
                Strike = kG.Key,
                CallOi = kG.Where(r => r.OptionTypeId == 1).Sum(r => r.OpenInterest),
                PutOi = kG.Where(r => r.OptionTypeId == 2).Sum(r => r.OpenInterest)
            }).OrderBy(o => o.Strike).ToList();

            snaps.Add(new MemorySnapshot(times[k], spot, gs, chain));
        }
        return snaps;
    }

    var trades = new List<TradeRec>();
    int standAside = 0, recErrors = 0, recBuilt = 0, incomplete = 0, skipped = 0;
    int pendingPlaced = 0, pendingFilled = 0, pendingCancelled = 0, ambiguousBars = 0;

    Position? pos = null;
    Pending? pend = null;

    void Record(Position ps, DateTimeOffset closeT, double exit, string outcome)
    {
        double risk = Math.Abs(ps.Entry - ps.Stop);
        if (risk <= 0) return;
        double r = ps.Dir * (exit - ps.Entry) / risk;
        double feeR = (ps.Entry + exit) * FeePerSide / risk;
        trades.Add(new TradeRec(ps.OpenTime, closeT, ps.Type, ps.Conditional, ps.Dir,
            ps.Entry, ps.Stop, ps.Target, exit, outcome,
            r, r - feeR, ps.Dir * (exit - ps.Entry) / ps.Entry * 100.0, ps.PlannedRR, ps.Bias, ps.Feat));
    }

    for (int i = 0; i < times.Count; i++)
    {
        DateTimeOffset t = times[i];
        if (i % 500 == 0)
            Console.WriteLine($"    ... срез {i}/{times.Count} ({t:yyyy-MM-dd})");

        // === открытая позиция: ведём до стопа/цели/тайм-стопа ===
        if (pos is not null)
        {
            double p = PriceAt(t, pos.Window);
            bool hitTarget = pos.Dir < 0 ? p <= pos.Target : p >= pos.Target;
            bool hitStop = pos.Dir < 0 ? p >= pos.Stop : p <= pos.Stop;

            if (hitStop && hitTarget)
            {
                ambiguousBars++;
                Record(pos, t, pos.Stop, "STOP*");   // оба уровня за бар → консервативно стоп
                pos = null;
            }
            else if (hitStop)
            {
                Record(pos, t, pos.Stop, "STOP");
                pos = null;
            }
            else if (hitTarget)
            {
                Record(pos, t, pos.Target, "TARGET");
                pos = null;
            }
            else if ((t - pos.OpenTime).TotalHours >= TimeStopHours)
            {
                Record(pos, t, p, "TIME");
                pos = null;
            }
            continue;   // на срезе выхода новую сделку не открываем
        }

        // === отложенный фейд: ждём захода цены в зону ===
        if (pend is not null)
        {
            double p = PriceAt(t, pend.Window);
            bool throughStop = pend.Dir < 0 ? p >= pend.Stop : p <= pend.Stop;
            bool reachedLimit = pend.Dir < 0 ? p >= pend.Limit : p <= pend.Limit;

            if (throughStop)
            {
                // пролёт зоны до стопа за один бар: лимит исполнился бы по пути → вход + стоп
                pendingFilled++;
                var psFill = new Position
                {
                    Dir = pend.Dir, Entry = pend.Limit, Stop = pend.Stop, Target = pend.Target,
                    OpenTime = t, Window = pend.Window, Type = pend.Type, Conditional = true,
                    PlannedRR = pend.PlannedRR, Bias = pend.Bias, Feat = pend.Feat
                };
                Record(psFill, t, pend.Stop, "FILL+STOP");
                pend = null;
                continue;
            }
            if (reachedLimit)
            {
                pendingFilled++;
                pos = new Position
                {
                    Dir = pend.Dir, Entry = pend.Limit, Stop = pend.Stop, Target = pend.Target,
                    OpenTime = t, Window = pend.Window, Type = pend.Type, Conditional = true,
                    PlannedRR = pend.PlannedRR, Bias = pend.Bias, Feat = pend.Feat
                };
                pend = null;
                continue;
            }
            if ((t - pend.Placed).TotalHours >= pend.FillHours)
            {
                pendingCancelled++;
                pend = null;   // отмена — на этом же срезе строим новую рекомендацию (ниже)
            }
            else
            {
                continue;
            }
        }

        // === вне позиции: строим рекомендацию боевым конвейером ===
        Dictionary<string, ExpAgg> dictNow = perExp[t];
        List<string> exps = dictNow
            .Where(kv => kv.Value.Oi > 0 && OptionExposureMath.YearsToExpiry(kv.Key, t) > 0)
            .Select(kv => kv.Key)
            .OrderBy(e => DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture))
            .ToList();
        if (exps.Count == 0) { skipped++; continue; }

        List<string> w = QuarterlyAggregation.WindowExpirations(exps, t);
        if (w.Count == 0) { skipped++; continue; }

        // Дельта-ряд: только срезы ≤ t (DexTrend использует последние 12 точек; даём ≤30).
        var series = new List<DeltaPoint>();
        for (int j = Math.Max(0, i - 29); j <= i; j++)
        {
            Dictionary<string, ExpAgg> dj = perExp[times[j]];
            double px = double.MinValue, dex = 0;
            bool any = false;
            foreach (string e in w)
                if (dj.TryGetValue(e, out ExpAgg? a))
                {
                    any = true;
                    px = Math.Max(px, a.Px);
                    dex += a.Dex;
                }
            if (any)
                series.Add(new DeltaPoint { Time = times[j], UnderlyingPrice = px, DeltaExposure = dex });
        }
        double flow = SessionAnalysisMath.DexTrend(series);

        SessionRecommendation rec;
        try
        {
            List<ExpirationAnalysis> analyses = expirationBuilder.Build(rowsAt[t], w, t);
            if (analyses.Count == 0) { skipped++; continue; }

            // «Память контракта» по окну w (история ≤ t). curOi — ΣOI окна на срезе; maxOi — макс. среди всех серий.
            double curOiMem = 0, maxOiMem = 0;
            foreach (KeyValuePair<string, ExpAgg> kv in dictNow)
            {
                if (w.Contains(kv.Key)) curOiMem += kv.Value.Oi;
                if (kv.Value.Oi > maxOiMem) maxOiMem = kv.Value.Oi;
            }
            ContractMemory mem = memoryBuilder.Build(BuildMemHistory(i, w), curOiMem, maxOiMem);

            rec = sessionBuilder.BuildAggregate(analyses, symbol, t, flow, mem);
            recBuilt++;
        }
        catch
        {
            recErrors++;
            continue;
        }

        PrimaryTrade pt = rec.Primary;
        if (pt.Action == TradeAction.StandAside || pt.Side == TradeSide.None)
        {
            standAside++;
            continue;
        }
        if (pt.Stop is not { } stop0 || pt.Target is not { } target0 ||
            pt.EntryLow is not { } el || pt.EntryHigh is not { } eh)
        {
            incomplete++;
            continue;
        }

        int dir = pt.Side == TradeSide.Short ? -1 : +1;

        // Наблюдательные фичи памяти на момент входа (стена в сторону цели: −γ-пол/потолок).
        double? putWallPx = rec.Levels.FirstOrDefault(l => l.Kind == LevelKind.PutWall)?.Price;
        double? callWallPx = rec.Levels.FirstOrDefault(l => l.Kind == LevelKind.CallWall)?.Price;
        double? targetWall = dir < 0 ? putWallPx : callWallPx;
        double sigForFeat = rec.Range.SessionSigmaUsd;

        // ALT: пересчёт цели импульса/направленной по сессионным кандидатам (фейд не трогаем).
        if (altTargets && pt.Action is TradeAction.Breakout or TradeAction.Directional)
        {
            double sSig = rec.Range.SessionSigmaUsd;
            double entryC = rec.Spot;
            double riskC = Math.Abs(entryC - stop0);
            var cands = new List<double>();
            PriceLevel? wallLvl = rec.Levels.FirstOrDefault(l =>
                l.Kind == (dir < 0 ? LevelKind.PutWall : LevelKind.CallWall));
            if (wallLvl is not null) cands.Add(wallLvl.Price);
            PriceLevel? pinLvl = rec.Levels.FirstOrDefault(l => l.Kind == LevelKind.GammaPeak);
            if (pinLvl is not null) cands.Add(pinLvl.Price);
            cands.Add(entryC + dir * 1.5 * sSig);
            cands.Add(entryC + dir * 2.5 * sSig);

            double minDist = (pt.Action == TradeAction.Breakout ? 1.0 : 0.8) * sSig;
            double minRR = pt.Action == TradeAction.Breakout ? 1.5 : 1.4;
            double requiredDist = Math.Max(minDist, minRR * riskC);
            double best = 0, bestDist = double.MaxValue;
            foreach (double cand in cands)
            {
                if (cand <= 0 || !double.IsFinite(cand)) continue;
                double distC = dir > 0 ? cand - entryC : entryC - cand;
                if (distC >= requiredDist && distC < bestDist) { bestDist = distC; best = cand; }
            }
            if (best <= 0) { standAside++; continue; }   // нет цели → вне рынка (как в проде)
            target0 = best;
        }
        // origStop — боевой стоп до возможного переноса (фичи памяти считаем по нему, чтобы бакеты
        // были сопоставимы между режимами BASE/FLIPSTOP/FLIPENTRY).
        double origStop = stop0;

        // FLIPSTOP (фикс-тест): перенос стопа ЗА flip, если flip — в сторонe отскока И дальше текущего стопа.
        if (mode == "FLIPSTOP" && pt.Action is TradeAction.Breakout or TradeAction.Directional &&
            rec.GammaFlip is { } gfS && double.IsFinite(gfS) &&
            (dir < 0 ? gfS > rec.Spot && gfS > stop0 : gfS < rec.Spot && gfS < stop0))
        {
            double sSig = rec.Range.SessionSigmaUsd;
            stop0 = dir < 0 ? gfS + FlipBufferSig * sSig : gfS - FlipBufferSig * sSig;
        }

        if (pt.Action == TradeAction.FadeRange && pt.IsConditional)
        {
            double limit = (el + eh) / 2.0;
            pend = new Pending
            {
                Dir = dir, Limit = limit, Stop = stop0, Target = target0,
                Placed = t, Window = w, Type = pt.Action.ToString(),
                PlannedRR = pt.RiskReward ?? 0, Bias = rec.BiasScore, FillHours = FillWindowHours,
                Feat = ComputeFeat(i, limit, dir, rec.GammaFlip, targetWall, origStop, sigForFeat, w)
            };
            pendingPlaced++;
        }
        else if (mode == "FLIPENTRY" && pt.Action is TradeAction.Breakout or TradeAction.Directional &&
                 rec.GammaFlip is { } gfE && double.IsFinite(gfE) &&
                 (dir < 0 ? gfE > rec.Spot : gfE < rec.Spot) &&
                 Math.Abs(gfE - rec.Spot) >= FlipEntryMinSig * rec.Range.SessionSigmaUsd)
        {
            // Отложенный вход к flip: вход у flip (на возврате против хода), стоп за flip, цель — исходная.
            double sSig = rec.Range.SessionSigmaUsd;
            double limit = dir < 0 ? gfE - FlipBufferSig * sSig : gfE + FlipBufferSig * sSig;
            double fstop = dir < 0 ? gfE + FlipBufferSig * sSig : gfE - FlipBufferSig * sSig;
            pend = new Pending
            {
                Dir = dir, Limit = limit, Stop = fstop, Target = target0,
                Placed = t, Window = w, Type = pt.Action.ToString(),
                PlannedRR = pt.RiskReward ?? 0, Bias = rec.BiasScore, FillHours = FlipEntryFillHours,
                Feat = ComputeFeat(i, limit, dir, rec.GammaFlip, targetWall, origStop, sigForFeat, w)
            };
            pendingPlaced++;
        }
        else
        {
            pos = new Position
            {
                Dir = dir, Entry = rec.Spot, Stop = stop0, Target = target0,
                OpenTime = t, Window = w, Type = pt.Action.ToString(), Conditional = false,
                PlannedRR = pt.RiskReward ?? 0, Bias = rec.BiasScore,
                Feat = ComputeFeat(i, rec.Spot, dir, rec.GammaFlip, targetWall, origStop, sigForFeat, w)
            };
        }
    }

    // Конец данных: открытую позицию закрываем по последней цене.
    if (pos is not null)
    {
        Record(pos, times[^1], PriceAt(times[^1], pos.Window), "EOD");
        pos = null;
    }

    // ======================  СТАТИСТИКА  ======================
    Console.WriteLine();
    Console.WriteLine($"=== {symbol}: {times.Count} срезов ({times[0]:yyyy-MM-dd} … {times[^1]:yyyy-MM-dd}) ===");
    Console.WriteLine($"Рекомендаций построено: {recBuilt}, ошибок: {recErrors}, пропущено (нет данных): {skipped}");
    Console.WriteLine($"Вне рынка (StandAside): {standAside}, неполных планов: {incomplete}");
    Console.WriteLine($"Отложенных: размещено {pendingPlaced}, исполнено {pendingFilled}, отменено по сроку {pendingCancelled}");
    Console.WriteLine();

    if (trades.Count == 0)
    {
        Console.WriteLine("Сделок нет.");
        combined.Add((symbol, trades));
        continue;
    }

    int wins = trades.Count(x => x.Outcome == "TARGET");
    int stops = trades.Count(x => x.Outcome is "STOP" or "STOP*" or "FILL+STOP");
    int timeOuts = trades.Count(x => x.Outcome == "TIME");
    int eod = trades.Count(x => x.Outcome == "EOD");
    double sumR = trades.Sum(x => x.R);
    double sumRNet = trades.Sum(x => x.RNet);
    double posR = trades.Where(x => x.RNet > 0).Sum(x => x.RNet);
    double negR = trades.Where(x => x.RNet < 0).Sum(x => x.RNet);
    double pf = negR < 0 ? posR / -negR : double.PositiveInfinity;

    double cum = 0, peak = 0, maxDd = 0, eq = 1;
    foreach (TradeRec x in trades)
    {
        cum += x.RNet;
        peak = Math.Max(peak, cum);
        maxDd = Math.Max(maxDd, peak - cum);
        eq *= 1 + 0.01 * x.RNet;
    }

    List<double> holdH = trades.Select(x => (x.CloseTime - x.OpenTime).TotalHours).OrderBy(h => h).ToList();
    double medianHold = holdH[holdH.Count / 2];

    Console.WriteLine($"СДЕЛОК: {trades.Count}  " +
        $"(fade {trades.Count(x => x.Type == "FadeRange")}, " +
        $"breakout {trades.Count(x => x.Type == "Breakout")}, " +
        $"directional {trades.Count(x => x.Type == "Directional")}; " +
        $"long {trades.Count(x => x.Dir > 0)} / short {trades.Count(x => x.Dir < 0)})");
    Console.WriteLine($"Исходы: TARGET {wins}, STOP {stops} (из них пролёт за бар {trades.Count(x => x.Outcome is "STOP*" or "FILL+STOP")}), TIME {timeOuts}, EOD {eod}");
    Console.WriteLine($"Win-rate (TARGET / (TARGET+STOP)): {(wins + stops > 0 ? 100.0 * wins / (wins + stops) : 0):0.0}%");
    Console.WriteLine($"ΣR брутто: {sumR:+0.00;-0.00}   ΣR с комиссиями: {sumRNet:+0.00;-0.00}   средний R: {sumRNet / trades.Count:+0.000;-0.000}");
    Console.WriteLine($"Profit factor: {pf:0.00}   Max просадка: {maxDd:0.0} R");
    Console.WriteLine($"Время в позиции: медиана {medianHold:0.0} ч, средн. {holdH.Average():0.0} ч");
    Console.WriteLine($"При риске 1% депозита на сделку: {(eq - 1) * 100:+0.0;-0.0}% за период");
    Console.WriteLine();

    Console.WriteLine("ПО ТИПАМ:");
    foreach (var g in trades.GroupBy(x => x.Type).OrderBy(g => g.Key))
    {
        int w1 = g.Count(x => x.Outcome == "TARGET");
        int s1 = g.Count(x => x.Outcome is "STOP" or "STOP*" or "FILL+STOP");
        Console.WriteLine($"  {g.Key,-12} n={g.Count(),3}  TARGET {w1,3} / STOP {s1,3}  " +
            $"win {(w1 + s1 > 0 ? 100.0 * w1 / (w1 + s1) : 0),5:0.0}%  ΣRnet {g.Sum(x => x.RNet):+0.00;-0.00}  средн.R {g.Average(x => x.RNet):+0.00;-0.00}");
    }
    Console.WriteLine("ПО СТОРОНАМ:");
    foreach (var g in trades.GroupBy(x => x.Dir).OrderBy(g => g.Key))
    {
        int w1 = g.Count(x => x.Outcome == "TARGET");
        int s1 = g.Count(x => x.Outcome is "STOP" or "STOP*" or "FILL+STOP");
        Console.WriteLine($"  {(g.Key < 0 ? "SHORT" : "LONG"),-12} n={g.Count(),3}  TARGET {w1,3} / STOP {s1,3}  " +
            $"win {(w1 + s1 > 0 ? 100.0 * w1 / (w1 + s1) : 0),5:0.0}%  ΣRnet {g.Sum(x => x.RNet):+0.00;-0.00}");
    }
    Console.WriteLine("ПО МЕСЯЦАМ (ΣRnet, закрытие сделки):");
    foreach (var g in trades.GroupBy(x => $"{x.CloseTime:yyyy-MM}").OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key}  {g.Sum(x => x.RNet),7:+0.00;-0.00}  (сделок {g.Count()})");
    Console.WriteLine();

    // CSV для ручной проверки
    string csvSuffix = Math.Abs(TimeStopHours - 120) < 1e-9 ? "" : $"_T{TimeStopHours:0}h";
    if (altTargets) csvSuffix += "_ALT";
    if (mode is "FLIPSTOP" or "FLIPENTRY") csvSuffix += "_" + mode;
    string csvPath = Path.Combine(root, "tradebacktest", $"trades_{symbol}{csvSuffix}.csv");
    var sb = new StringBuilder();
    sb.AppendLine("open,close,holdHours,type,conditional,side,entry,stop,target,exit,outcome,R,RNet,retPct,plannedRR,bias," +
                  "rangePos,runupLow8,ddHigh8,flipHeadroom,flipBeyondStop,distTargetWallPct,oiPctile");
    foreach (TradeRec x in trades)
        sb.AppendLine(string.Join(",",
            x.OpenTime.ToString("yyyy-MM-dd HH:mm"), x.CloseTime.ToString("yyyy-MM-dd HH:mm"),
            (x.CloseTime - x.OpenTime).TotalHours.ToString("0.0"),
            x.Type, x.Conditional ? "Y" : "N", x.Dir < 0 ? "SHORT" : "LONG",
            x.Entry.ToString("0.00"), x.Stop.ToString("0.00"), x.Target.ToString("0.00"), x.Exit.ToString("0.00"),
            x.Outcome, x.R.ToString("0.000"), x.RNet.ToString("0.000"), x.RetPct.ToString("0.000"),
            x.PlannedRR.ToString("0.00"), x.Bias.ToString("0.000"),
            x.Feat.RangePos.ToString("0.000"), x.Feat.RunupLow8.ToString("0.00"), x.Feat.DdHigh8.ToString("0.00"),
            x.Feat.FlipHeadroom.ToString("0.00"), x.Feat.FlipBeyondStop.ToString("0.00"),
            x.Feat.DistTargetWallPct.ToString("0.00"), x.Feat.OiPctile.ToString("0.0")));
    File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
    Console.WriteLine($"Журнал сделок: {csvPath}");
    Console.WriteLine($"Двусмысленных баров (стоп+цель за один бар, засчитан стоп): {ambiguousBars}");
    Console.WriteLine();

    // ======================  АНАЛИЗ «ПАМЯТИ» (наблюдательно)  ======================
    void Stat(string label, List<TradeRec> g)
    {
        if (g.Count == 0) { Console.WriteLine($"  {label,-44} n=  0"); return; }
        int w1 = g.Count(x => x.Outcome == "TARGET");
        int s1 = g.Count(x => x.Outcome is "STOP" or "STOP*" or "FILL+STOP");
        double wr = w1 + s1 > 0 ? 100.0 * w1 / (w1 + s1) : 0;
        Console.WriteLine($"  {label,-44} n={g.Count,4}  win {wr,5:0.0}%  ΣRnet {g.Sum(x => x.RNet),8:+0.00;-0.00}  ср.R {g.Average(x => x.RNet),7:+0.000;-0.000}");
    }

    var impulse = trades.Where(x => x.Type is "Breakout" or "Directional").ToList();
    var imShort = impulse.Where(x => x.Dir < 0).ToList();
    Console.WriteLine($"=== АНАЛИЗ ПАМЯТИ ({symbol}): импульсных {impulse.Count} (из них шортов {imShort.Count}) ===");

    Console.WriteLine("[A] импульсные ШОРТЫ — положение flip относительно стопа (flipBeyondStop>0 ⇒ стоп ВНУТРИ зоны отскока к flip):");
    Stat("flipBeyondStop ≤ 0 (стоп за/у flip)", imShort.Where(x => x.Feat.FlipBeyondStop <= 0).ToList());
    Stat("flipBeyondStop 0..1σ", imShort.Where(x => x.Feat.FlipBeyondStop > 0 && x.Feat.FlipBeyondStop <= 1).ToList());
    Stat("flipBeyondStop > 1σ (стоп глубоко в зоне отскока)", imShort.Where(x => x.Feat.FlipBeyondStop > 1).ToList());

    Console.WriteLine("[A2] импульсные ШОРТЫ — глубина входа под flip (flipHeadroom = σ вверх до flip; больше = глубже в −γ):");
    Stat("flipHeadroom < 1σ (вход у/над flip)", imShort.Where(x => x.Feat.FlipHeadroom < 1).ToList());
    Stat("flipHeadroom 1..2.5σ", imShort.Where(x => x.Feat.FlipHeadroom >= 1 && x.Feat.FlipHeadroom < 2.5).ToList());
    Stat("flipHeadroom ≥ 2.5σ (глубоко под flip)", imShort.Where(x => x.Feat.FlipHeadroom >= 2.5).ToList());

    Console.WriteLine("[B] импульсные ШОРТЫ — положение входа в реализованном диапазоне (низ = вход у дна):");
    Stat("rangePos < 0.33 (у дна)", imShort.Where(x => x.Feat.RangePos < 0.33).ToList());
    Stat("rangePos 0.33..0.66 (середина)", imShort.Where(x => x.Feat.RangePos >= 0.33 && x.Feat.RangePos < 0.66).ToList());
    Stat("rangePos ≥ 0.66 (у верха, после отката)", imShort.Where(x => x.Feat.RangePos >= 0.66).ToList());

    Console.WriteLine("[C] импульсные ШОРТЫ — откат вверх за ~24ч (высокий = шорт в незавершённый отскок):");
    Stat("runupLow8 < 1%", imShort.Where(x => x.Feat.RunupLow8 < 1).ToList());
    Stat("runupLow8 1..4%", imShort.Where(x => x.Feat.RunupLow8 >= 1 && x.Feat.RunupLow8 < 4).ToList());
    Stat("runupLow8 ≥ 4% (свежий отскок)", imShort.Where(x => x.Feat.RunupLow8 >= 4).ToList());

    Console.WriteLine("[D] импульсные ШОРТЫ — близость к −γ-полу в сторону цели (мало = истощение у стены):");
    Stat("distTargetWall < 3%", imShort.Where(x => !double.IsNaN(x.Feat.DistTargetWallPct) && x.Feat.DistTargetWallPct < 3).ToList());
    Stat("distTargetWall 3..7%", imShort.Where(x => !double.IsNaN(x.Feat.DistTargetWallPct) && x.Feat.DistTargetWallPct >= 3 && x.Feat.DistTargetWallPct < 7).ToList());
    Stat("distTargetWall ≥ 7%", imShort.Where(x => !double.IsNaN(x.Feat.DistTargetWallPct) && x.Feat.DistTargetWallPct >= 7).ToList());

    Console.WriteLine("[E] ВСЕ сделки — репрезентативность OI (перцентиль текущего OI окна среди истории):");
    Stat("oiPctile < 33 (низкий OI)", trades.Where(x => x.Feat.OiPctile < 33).ToList());
    Stat("oiPctile 33..66", trades.Where(x => x.Feat.OiPctile >= 33 && x.Feat.OiPctile < 66).ToList());
    Stat("oiPctile ≥ 66 (высокий OI)", trades.Where(x => x.Feat.OiPctile >= 66).ToList());
    Console.WriteLine();

    combined.Add((symbol, trades));
}

// ======================  ИТОГ ПО ОБЕИМ МОНЕТАМ  ======================
if (combined.Count > 1)
{
    var allTrades = combined.SelectMany(c => c.Trades).OrderBy(x => x.CloseTime).ToList();
    double sumRNet = allTrades.Sum(x => x.RNet);
    int wins = allTrades.Count(x => x.Outcome == "TARGET");
    int stops = allTrades.Count(x => x.Outcome is "STOP" or "STOP*" or "FILL+STOP");
    double eq = 1;
    foreach (TradeRec x in allTrades)
        eq *= 1 + 0.01 * x.RNet;
    Console.WriteLine("=== ИТОГ BTC+ETH (хронологически, риск 1% на сделку) ===");
    Console.WriteLine($"Сделок {allTrades.Count}, win-rate {(wins + stops > 0 ? 100.0 * wins / (wins + stops) : 0):0.0}%, ΣRnet {sumRNet:+0.00;-0.00}, депозит {(eq - 1) * 100:+0.0;-0.0}%");
}

internal class ExpAgg
{
    public double Px;
    public double Dex;
    public double Oi;
}

internal class Position
{
    public int Dir;
    public double Entry, Stop, Target;
    public DateTimeOffset OpenTime;
    public List<string> Window = new();
    public string Type = "";
    public bool Conditional;
    public double PlannedRR;
    public double Bias;
    public MemFeat Feat;
}

internal class Pending
{
    public int Dir;
    public double Limit, Stop, Target;
    public DateTimeOffset Placed;
    public List<string> Window = new();
    public string Type = "";
    public double PlannedRR;
    public double Bias;
    public MemFeat Feat;
    public double FillHours;
}

internal record TradeRec(
    DateTimeOffset OpenTime, DateTimeOffset CloseTime, string Type, bool Conditional, int Dir,
    double Entry, double Stop, double Target, double Exit, string Outcome,
    double R, double RNet, double RetPct, double PlannedRR, double Bias, MemFeat Feat);

/// <summary>Наблюдательные фичи «памяти контракта» на момент входа (в торговую логику НЕ входят).</summary>
internal readonly record struct MemFeat(
    double RangePos,         // положение входа в реализованном диапазоне 0..1 (0 — у low, 1 — у high)
    double RunupLow8,        // % отскока вверх от мин. за ~24ч (для шорта: высокий = вход в незавершённый отскок)
    double DdHigh8,          // % просадки от макс. за ~24ч
    double FlipHeadroom,     // σ до flip в сторону отскока против позиции (>0 — есть куда откатиться против нас)
    double FlipBeyondStop,   // σ, на сколько flip ДАЛЬШЕ стопа в сторону отскока (>0 — стоп внутри зоны отскока к flip)
    double DistTargetWallPct,// % от входа до структурной стены в сторону цели (мало — истощение у стены)
    double OiPctile);        // перцентиль текущего OI окна среди собственной истории (мало — нерепрезентативно)
