using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Option.Data.Database;
using Option.Data.Shared.Configuration;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;

// ======================================================================
//  Смоук /Sell боевым кодом.
//  Запуск:  dotnet run --project sellpreview -- check          — эталонные проверки математики
//           dotnet run --project sellpreview -- BTC [<эксп>]   — живая рекомендация (Deribit + БД)
// ======================================================================

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

string mode = args.Length > 0 ? args[0].ToUpperInvariant() : "CHECK";

if (mode == "CHECK")
{
    int fails = 0;

    void Check(string name, double actual, double expected, double tol)
    {
        bool ok = Math.Abs(actual - expected) <= tol;
        if (!ok) fails++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}: {actual:0.000000} (ожидалось {expected:0.000000} ± {tol:0.000000})");
    }

    // Эталоны NormCdf (таблица стандартного нормального распределения).
    Check("NormCdf(0)", SellMath.NormCdf(0), 0.5, 1e-7);
    Check("NormCdf(1.96)", SellMath.NormCdf(1.96), 0.975002, 5e-5);
    Check("NormCdf(-1)", SellMath.NormCdf(-1), 0.158655, 5e-5);

    // Black-76 ATM: F=K=100, σ=0.2, T=1 → d1=0.1, d2=−0.1, call=100·(Φ(0.1)−Φ(−0.1))=7.9656.
    Check("B76 call ATM", SellMath.Black76Price(true, 100, 100, 0.2, 1), 7.9656, 0.002);
    Check("B76 put ATM (= call, паритет на форварде)", SellMath.Black76Price(false, 100, 100, 0.2, 1), 7.9656, 0.002);
    Check("B76 delta call ATM", SellMath.Black76Delta(true, 100, 100, 0.2, 1), 0.539828, 1e-4);
    Check("B76 delta put ATM", SellMath.Black76Delta(false, 100, 100, 0.2, 1), -0.460172, 1e-4);

    // Паритет: call − put = F − K (r=0). F=110, K=100, σ=0.5, T=0.25.
    double c = SellMath.Black76Price(true, 110, 100, 0.5, 0.25);
    double p = SellMath.Black76Price(false, 110, 100, 0.5, 0.25);
    Check("Пут-колл паритет c−p−(F−K)", c - p - 10, 0, 1e-6);

    // Вероятности: P(ITM) call ATM = Φ(d2) = Φ(−0.1) = 0.460172; P(touch) = 2×.
    Check("ProbItm call ATM", SellMath.ProbItm(true, 100, 100, 0.2, 1), 0.460172, 1e-4);
    Check("ProbTouch = 2·ProbItm", SellMath.ProbTouch(true, 100, 120, 0.2, 1),
        2 * SellMath.ProbItm(true, 100, 120, 0.2, 1), 1e-12);

    // Маржа Deribit: short call F=50000, K=60000, mark=0.01 → OTM%=0.2 → max(0.15−0.2,0.1)+0.01=0.11.
    Check("Маржа short call", SellMath.ShortMarginCoin(true, 50000, 60000, 0.01), 0.11, 1e-9);
    // Short put F=50000, K=40000, mark=0.01: IM=0.11; MM=max(0.075,0.00075)+0.01=0.085 → 0.11.
    Check("Маржа short put", SellMath.ShortMarginCoin(false, 50000, 40000, 0.01), 0.11, 1e-9);
    // Стрэнгл: бОльшая IM + марк второй ноги.
    Check("Маржа стрэнгла", SellMath.StrangleMarginCoin(0.11, 0.09, 0.01, 0.02), 0.13, 1e-9);

    // EWMA-RV: постоянный 3ч-шаг с |r| = 1% за шаг → σ_год = 0.01·√(8·365) = 0.5404.
    var series = new List<PricePoint>();
    DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    double price = 100;
    for (int i = 0; i < 200; i++)
    {
        price *= i % 2 == 0 ? 1.01 : 1 / 1.01;
        series.Add(new PricePoint(t0.AddHours(3 * i), price));
    }
    Check("EWMA-RV (1% за 3ч)", SellMath.EwmaRealizedVolAnnual(series, 7), 0.01 * Math.Sqrt(8 * 365), 0.01);

    // Тета/вега ATM (F=100, σ=0.2, T=1): θ_год = −φ(0.1)·100·0.2/2 = −3.9695/день·365; vega = φ(0.1)·100/100.
    double pdf01 = Math.Exp(-0.005) / Math.Sqrt(2 * Math.PI);
    Check("Тета/день ATM", SellMath.ThetaPerDayUsd(100, 100, 0.2, 1), -100 * pdf01 * 0.2 / 2 / 365, 1e-6);
    Check("Вега/1% ATM", SellMath.VegaPerVolPointUsd(100, 100, 0.2, 1), 100 * pdf01 / 100, 1e-6);

    Console.WriteLine(fails == 0 ? "\nВСЕ ПРОВЕРКИ OK" : $"\nПРОВАЛОВ: {fails}");
    Environment.Exit(fails == 0 ? 0 : 1);
}

// ===================== Живой смоук =====================
string symbol = mode;
string? expArg = args.Length > 1 ? args[1].ToUpperInvariant() : null;
int curId = symbol == "BTC" ? 1 : 2;

// Deribit-источник через минимальный DI (HttpClientFactory + кэш + логгер).
var services = new ServiceCollection();
services.AddLogging();
services.AddMemoryCache();
services.AddHttpClient(DeribitConfig.ClientName,
    c => c.BaseAddress = new Uri("https://www.deribit.com/api/v2/public/"));
ServiceProvider sp = services.BuildServiceProvider();
var source = new DeribitOptionBoardSource(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<DeribitOptionBoardSource>());

DateTimeOffset asOf = DateTimeOffset.UtcNow;
List<string> expirations = (await source.GetExpirationsAsync(symbol))
    .Where(e => OptionExposureMath.YearsToExpiry(e, asOf) > 0)
    .OrderBy(e => DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture))
    .ToList();
string expiration = expArg
    ?? expirations.FirstOrDefault(e => OptionExposureMath.YearsToExpiry(e, asOf) * 365.0 >= 0.5)
    ?? expirations[0];

OptionBoard board = await source.GetBoardAsync(symbol, expiration);
Console.WriteLine($"=== {symbol} · {expiration} · {asOf:yyyy-MM-dd HH:mm} UTC · страйков {board.Chain.Count}, форвард {board.Spot:N2} ===");

// История из БД (та же логика, что SellModel.LoadHistoryAsync).
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string conn;
using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Option.Data.Ui", "appsettings.json"))))
    conn = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString()!;
var priceHistory = new List<PricePoint>();
var dexSeries = new List<DeltaPoint>();
try
{
    var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(conn).Options;
    await using var ctx = new ApplicationDbContext(options);
    DateTimeOffset cutoff = asOf.AddDays(-30);
    var rows = await ctx.DeribitData.AsNoTracking()
        .Where(d => d.CurrencyTypeId == curId && d.CreatedAt >= cutoff)
        .GroupBy(d => new { d.CreatedAt, d.Expiration })
        .Select(g => new
        {
            g.Key.CreatedAt, g.Key.Expiration,
            Px = g.Max(x => x.UnderlyingPrice),
            Dex = -g.Sum(x => x.Delta * x.OpenInterest)
        })
        .ToListAsync();
    priceHistory = rows.GroupBy(r => r.CreatedAt)
        .Select(g =>
        {
            var front = g.Where(r => OptionExposureMath.YearsToExpiry(r.Expiration, g.Key) > 0)
                .OrderBy(r => OptionExposureMath.YearsToExpiry(r.Expiration, g.Key)).FirstOrDefault();
            return front is null ? default : new PricePoint(g.Key, front.Px);
        })
        .Where(p => p.Price > 0).OrderBy(p => p.Time).ToList();
    dexSeries = rows.Where(r => r.Expiration == expiration)
        .Select(r => new DeltaPoint { Time = r.CreatedAt, UnderlyingPrice = r.Px, DeltaExposure = r.Dex })
        .OrderBy(p => p.Time).ToList();
    Console.WriteLine($"История: цена {priceHistory.Count} точек, DEX {dexSeries.Count} точек");
}
catch (Exception e)
{
    Console.WriteLine($"БД недоступна ({e.Message}) — фолбэк σ_phys=IV×множитель.");
}

var builder = new SellRecommendationBuilder();
SellRecommendation rec = builder.Build(board, symbol, expiration, asOf, dexSeries, priceHistory);

Console.WriteLine($"\nРЕЖИМ {rec.Regime} · NetGEX {rec.NetGexAtSpot:N0} · flip {(rec.GammaFlip is { } gf ? gf.ToString("N0") : "—")} " +
                  $"· стены PUT {(rec.PutWall is { } pw ? pw.ToString("N0") : "—")} / CALL {(rec.CallWall is { } cw ? cw.ToString("N0") : "—")}");
Console.WriteLine($"ATM IV {rec.AtmIvPct:0.0}% · RV {rec.RvAnnualPct:0.0}% · σ_phys {rec.SigmaPhysPct:0.0}% · bias {rec.BiasScore:+0.00;-0.00;0.00}");
foreach (BiasComponent bc in rec.BiasComponents)
    Console.WriteLine($"  {bc.Name,-20} голос {bc.Normalized:+0.00;-0.00} вклад {bc.Contribution:+0.00;-0.00} | {bc.Explanation}");

foreach (SellCard card in new[] { rec.Adaptive, rec.Aggressive })
{
    Console.WriteLine($"\n--- {card.Profile} ---");
    if (card.IsStandAside)
    {
        Console.WriteLine($"ВНЕ РЫНКА: {card.StandAsideReason}. Возврат: {card.ReturnCondition}.");
        continue;
    }
    Console.WriteLine($"SELL {(card.Leg == SellLeg.Strangle ? card.Primary!.Instrument + " + " + card.SecondLeg!.Instrument : card.Primary!.Instrument)}");
    Console.WriteLine($"Премия {card.TotalPremiumCoin:0.0000} ({card.TotalPremiumUsd:N0} $) · маржа {card.TotalMarginUsd:N0} $ · " +
                      $"доходность {card.YieldOnMarginPct:0.00}% ({card.YieldAnnualizedPct:0.0}% год.) · EV {card.TotalEvUsd:N0} $");
    Console.WriteLine($"P(OTM) {(1 - (card.SecondLeg is null ? card.Primary!.ProbItm : Math.Min(1, card.Primary!.ProbItm + card.SecondLeg.ProbItm))) * 100:0.0}% · " +
                      $"P(касания) {card.CombinedProbTouch * 100:0.0}% · δ {card.Primary!.Delta:+0.00;-0.00}");
    foreach (PlanBTrigger tr in card.PlanB)
        Console.WriteLine($"  ПЛАН-Б [{tr.Name}] {(tr.Level is { } lv ? lv.ToString("N0") : "—")}: {tr.Condition} → {tr.Action}");
    foreach (StressPoint st in card.Stress)
        Console.WriteLine($"  СТРЕСС {st.Label,5}: цена {st.Price:N0} → PnL {st.PnlUsd:+#,0;-#,0} $");
    foreach (string w in card.Warnings)
        Console.WriteLine($"  ПРЕДУПРЕЖДЕНИЕ: {w}");
}

Console.WriteLine("\nТоп кандидатов (EV/маржа):");
foreach (SellCandidate cnd in rec.TopCandidates)
    Console.WriteLine($"  {cnd.Instrument,-26} δ {cnd.Delta:+0.00;-0.00} mark {cnd.PremiumUsd,8:N0}$ EV {cnd.EvUsd,8:N0}$ " +
                      $"EV/маржа {cnd.EvOnMargin * 100,6:0.00}% P(OTM) {(1 - cnd.ProbItm) * 100,5:0.0}% {(cnd.BehindWall ? "за стеной" : "ВНУТРИ стены")}");
