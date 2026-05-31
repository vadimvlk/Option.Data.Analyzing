# Trade Plan — рекомендация на сессию (дизайн/спецификация)

Дата: 2026-05-31. Проект: `Option.Data.Ui` (.NET 8, Razor Pages). Вся UI-логика — на русском.

## 1. Цель
Новая страница `/Trade` («Trade Plan») сводит **все экспирации опционов в один торговый план на текущую сессию**: направление, режим волатильности, диапазон, ключевые уровни, сценарии и конкретные зоны вход/выход. Под каждым выводом — строка «потому что …» со ссылкой на реальную цифру. Источник — **последний снимок БД** выбранной валюты (BTC/ETH); только там есть греки (Delta/Gamma), на которых держатся DEX/Net GEX/скос. Live Deribit book summary греков не отдаёт.

Терминология: в БД хранится только **открытый интерес (OI)** (торгового объёма нет). «Где большие объёмы» = «где большие OI-стены». Это указывается в методичке на странице.

## 2. Источник данных и поток
- Последний доступный снимок: `AvailableDates.Last()`. Валюта по умолчанию — BTC (`Id=1`); переключатель BTC/ETH делает POST.
- Грузим `DeribitData` за `(CurrencyTypeId, CreatedAt==latest)`. Кэш как в `ExposureModel` (15 мин).
- Экспирации берём **из самого снимка** (distinct `Expiration`, сортировка по дате `dMMMyy`) — без обращения к Deribit API.
- `IExpirationAnalysisBuilder.Build(rows, expirations, asOf)` → `List<ExpirationAnalysis>` (вынос логики из `ExposureModel`).
- `ISessionRecommendationBuilder.Build(analyses, currency, asOf)` → `SessionRecommendation`.

## 3. Инвентарь файлов
Новые:
- `Option.Data.Ui/Models/SessionRecommendation.cs` — VM и вложенные типы (раздел 4).
- `Option.Data.Ui/Services/IExpirationAnalysisBuilder.cs` + `ExpirationAnalysisBuilder.cs` — вынос per-exp расчётов.
- `Option.Data.Ui/Services/ISessionRecommendationBuilder.cs` + `SessionRecommendationBuilder.cs` — синтез плана.
- `Option.Data.Ui/Services/SessionAnalysisMath.cs` — чистые статические функции (Black-76 гамма, профиль, flip, дневное σ, скоринг, стены).
- `Option.Data.Ui/Pages/Trade.cshtml` + `Trade.cshtml.cs`.

Изменяемые:
- `Option.Data.Ui/Pages/Exposure.cshtml.cs` — переключить на `IExpirationAnalysisBuilder`, удалить вынесенные приватные методы.
- `Option.Data.Ui/Program.cs` — DI-регистрация двух билдеров (singleton).
- `Option.Data.Ui/Pages/Shared/_Layout.cshtml` — пункт навигации «Trade Plan» после «Option Exposure».

## 4. Модели (`Models/SessionRecommendation.cs`) — точные типы
```csharp
using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Models;

public enum DirectionBias { StrongDown, ModerateDown, Neutral, ModerateUp, StrongUp }
public enum VolatilityRegime { PositiveGamma, NegativeGamma, Neutral }
public enum LevelKind { Spot, CallWall, PutWall, MaxPain, GravityEquilibrium, GammaFlip, GammaPeak, Sigma1Up, Sigma1Down, Sigma2Up, Sigma2Down }
public enum ScenarioKind { Base, Bullish, Bearish }

public class SessionRecommendation
{
    public string Currency { get; set; } = "";
    public double Spot { get; set; }
    public string FrontExpiration { get; set; } = "";
    public double FrontDaysToExpiry { get; set; }
    public double HoursToFrontExpiry { get; set; }
    public DateTimeOffset AsOf { get; set; }

    public DirectionBias Bias { get; set; }
    public double BiasScore { get; set; }              // -1..+1
    public VolatilityRegime Regime { get; set; }
    public double NetGexAtSpot { get; set; }           // USD/1%
    public double? GammaFlip { get; set; }

    public SessionRange Range { get; set; } = new();
    public List<PriceLevel> Levels { get; set; } = new();      // отсортированы по Price (убыв.)
    public List<Scenario> Scenarios { get; set; } = new();     // Base, Bullish, Bearish
    public List<BiasComponent> BiasComponents { get; set; } = new();
    public List<GammaProfilePoint> GammaProfile { get; set; } = new();
    public List<string> MacroContext { get; set; } = new();
    public List<string> Notes { get; set; } = new();           // предупреждения/краевые случаи
    public List<OptionData> FrontChain { get; set; } = new();  // для сворачиваемого блока HtmlBuilder
}

public class SessionRange
{
    public double AtmIvPercent { get; set; }           // σ фронта, % годовых
    public double SessionYears { get; set; }           // доля года до конца сессии
    public double DailySigma1 { get; set; }            // абсолют USD, 1σ за сессию
    public double Lower1 { get; set; }
    public double Upper1 { get; set; }
    public double Lower2 { get; set; }
    public double Upper2 { get; set; }
    public double FrontExpiryExpectedMove { get; set; } // EM до экспирации фронта (контекст)
}

public class PriceLevel
{
    public LevelKind Kind { get; set; }
    public string Label { get; set; } = "";            // напр. "CALL-стена 2100"
    public double Price { get; set; }
    public string Role { get; set; } = "";             // "Сопротивление"/"Поддержка"/"Магнит"/"Пивот"/"Спот"/"Граница"
    public double? OpenInterest { get; set; }          // для стен
    public double DistancePercent { get; set; }        // (Price-Spot)/Spot*100
}

public class Scenario
{
    public ScenarioKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Trigger { get; set; } = "";
    public List<double> Targets { get; set; } = new();
    public double? Stop { get; set; }
    public string Action { get; set; } = "";           // "Покупать у …"/"Продавать у …"/"Вне рынка"
    public string Reason { get; set; } = "";           // "потому что …"
    public double Probability { get; set; }            // 0..1, качественный вес
}

public class BiasComponent
{
    public string Name { get; set; } = "";
    public double RawValue { get; set; }
    public double Normalized { get; set; }             // -1..+1 (+ = бычий)
    public double Weight { get; set; }
    public double Contribution { get; set; }           // Normalized*Weight (после нормировки весов)
    public string Explanation { get; set; } = "";
}

public class GammaProfilePoint
{
    public double Price { get; set; }
    public double NetGex { get; set; }                 // USD/1%
}
```

## 5. Интерфейсы
```csharp
// Services/IExpirationAnalysisBuilder.cs
using Option.Data.Ui.Models;
using Option.Data.Shared.Poco;
namespace Option.Data.Ui.Services;
public interface IExpirationAnalysisBuilder
{
    List<ExpirationAnalysis> Build(
        IReadOnlyList<DeribitData> snapshotRows,
        IReadOnlyList<string> expirations,
        DateTimeOffset asOf);
}

// Services/ISessionRecommendationBuilder.cs
using Option.Data.Ui.Models;
namespace Option.Data.Ui.Services;
public interface ISessionRecommendationBuilder
{
    SessionRecommendation Build(
        IReadOnlyList<ExpirationAnalysis> analyses,
        string currency,
        DateTimeOffset asOf);
}
```

## 6. `ExpirationAnalysisBuilder` (вынос из `ExposureModel`)
Переносит без изменения формул: `ConvertToOptionData`, `CalculateBreakEvenPoints`, `CalculateSellerCallPnL`, `CalculateSellerPutPnL` и блок расчёта `ExpirationAnalysis` (центры тяжести, границы, DEX/GEX/MaxPain/EM/Skew через `OptionExposureMath`).
- **Изменение:** конвертация типа по `OptionTypeId` (1=Call, 2=Put) вместо `Type.Name` — чтобы не зависеть от EF `Include`.
- Итерация по `expirations`; для отсутствующих в данных — пропуск (как сейчас).
- `Exposure.cshtml.cs`: внедрить `IExpirationAnalysisBuilder`, заменить тело цикла на `ExposureViewModel.ExpirationsData = builder.Build(allOptionData, ViewModel.Expirations, ViewModel.SelectedDateTime);` и удалить вынесенные приватные методы. Кэш-загрузку `allOptionData` оставить.

## 7. `SessionAnalysisMath` — формулы и сигнатуры
```csharp
public readonly record struct GammaStrike(double Strike, double CallOi, double PutOi, double SigmaFraction, double TYears);
```
- `double Black76Gamma(double f, double k, double sigmaFraction, double tYears)`
  d1 = (ln(f/k) + 0.5·σ²·T) / (σ·√T); γ = φ(d1) / (f·σ·√T), φ — стандартная норм. плотность. Возврат 0 при σ≤0, T≤0, f≤0, k≤0.
- `double NetGexAtPrice(IReadOnlyList<GammaStrike> strikes, double price)`
  Σ Black76Gamma(price,K,σ,T)·(CallOi−PutOi)·price²·0.01. (Конвенция как в `OptionExposureMath.NetGammaExposure`: дилер +гамма по коллам, −по путам.)
- `List<GammaProfilePoint> GammaProfile(IReadOnlyList<GammaStrike> strikes, double spot, double lowFactor=0.85, double highFactor=1.15, int steps=120)`
- `double? GammaFlip(IReadOnlyList<GammaProfilePoint> profile, double spot)`
  Ближайшая к споту точка смены знака `NetGex` (линейная интерполяция). null — если знак не меняется.
- `double DailyExpectedMove(double spot, double atmIvFraction, double sessionYears)` = spot·iv·√(sessionYears).
- `OptionData? CallWall(IReadOnlyList<OptionData> chain, double spot)` — max `CallOi` среди `Strike>spot`.
- `OptionData? PutWall(IReadOnlyList<OptionData> chain, double spot)` — max `PutOi` среди `Strike<spot`.
- `double Clamp(double v, double lo, double hi)`; вспомогательные по необходимости.

## 8. `SessionRecommendationBuilder` — алгоритм
**DTE:** `OptionExposureMath.YearsToExpiry(exp, asOf)*365`. **Фронт** — мин. DTE среди analyses с суммарным OI>0. **Близкие** — DTE≤14. **Спот** = `UnderlyingPrice` фронта.

**Агрегаты по близким:** объединить цепочки по страйку (сумма CallOi/PutOi; для IV/T берём по каждой экспирации отдельно при построении `GammaStrike`). Список `GammaStrike` = по каждой близкой экспирации, по каждому страйку: `(Strike, CallOi, PutOi, Iv/100, YearsToExpiry(exp,asOf))`.

**Режим:** `NetGexAtSpot` = Σ `ExpirationAnalysis.NetGammaExposure` по близким (готовые значения у спота). >0 → PositiveGamma; <0 → NegativeGamma; ≈0 → Neutral. **GammaProfile/GammaFlip** — через `SessionAnalysisMath` по `GammaStrike` близких.

**Сессия:** `sessionYears` = часы до следующего 08:00 UTC от `DateTimeOffset.UtcNow` / (24·365), не меньше 1 часа. `AtmIv` фронта = `OptionExposureMath.AtmIvFraction(frontChain, spot)`. `DailySigma1 = DailyExpectedMove(spot, atmIv, sessionYears)`. `Lower1/Upper1 = spot∓σ`, `Lower2/Upper2 = spot∓2σ`. `FrontExpiryExpectedMove = ExpirationAnalysis.ExpectedMove1Sigma` фронта.

**Уровни:** Spot; CallWall (Сопротивление); PutWall (Поддержка); MaxPain фронта (Магнит); GravityEquilibrium близких (Магнит); GammaFlip (Пивот, если есть); σ-границы (Граница). `DistancePercent` от спота. Сортировка по Price убыв. Дедуп близких (<0.1% — оставить значимый).

**Скоринг направления** (нормировки в [−1,+1], + = вверх):
- `pin` = Clamp((MaxPain_front − spot)/DailySigma1, −1, 1). Прокси-фактор `pf` = Clamp(1 − DTE_front/7, 0.3, 1).
- `gravity` = Clamp((GravityEq_near − spot)/DailySigma1, −1, 1).
- `dex` = Clamp(DEX_near / (spot·totalNearOi), −1, 1), знак как на Exposure (>0 → бычий тилт). DEX_near = Σ DEX близких; totalNearOi = Σ(CallOi+PutOi) близких.
- `skew` = −Clamp(RR25_near / 5.0, −1, 1) (RR>0 — спрос на защиту вниз → медвежий риск). RR25_near — среднее доступных `RiskReversal25Delta` близких; если нет — сигнал пропускается.
- Веса: `wPin=0.30·pf`, `wGrav=0.20`, `wDex=0.20`, `wSkew=0.20` (skew только если есть). `B = Σ(norm·w)/Σ(w)`.
- Если NegativeGamma и есть flip: `B = Clamp(B + 0.15·sign(spot − flip), −1, 1)` (импульс/пробой).
- Маппинг: B≥0.45 StrongUp; ≥0.15 ModerateUp; >−0.15 Neutral; >−0.45 ModerateDown; иначе StrongDown.
- Каждый сигнал → `BiasComponent` с `Explanation` («потому что …»). Константы весов — `private const`, помечены «калибруемо».

**Сценарии (3):**
- **Base** — по режиму. PositiveGamma: «Флэт/пиннинг к {магнит}. Фейд краёв {Lower1}…{Upper1}». NegativeGamma: «Повышенная вола, склонность к пробою в сторону {bias}». Action/Reason/Probability.
- **Bullish** — Trigger «закрепление выше {CallWall либо flip}», Targets [следующий уровень сопротивления, Upper2], Action «Покупать у {PutWall/Lower1}» (в +гамме — фейд; в −гамме — по пробою вверх), Stop, Reason.
- **Bearish** — Trigger «пробой ниже {PutWall либо flip}», Targets [{следующая поддержка}, Lower2], Action «Продавать у {CallWall/Upper1}», Stop, Reason.

**MacroContext:** 1–2 строки по дальним (DTE>14): крупнейший Call/Put-OI страйк, общий знак скоса. **Notes:** «экспирация сегодня — усиленный пиннинг» (DTE_front<1), «нет близких экспираций — слабый сигнал» и т.п.

`FrontChain` = цепочка фронта (для HtmlBuilder-блока).

## 9. `TradeModel` (`Trade.cshtml.cs`)
`TradeModel(ApplicationDbContext, IMemoryCache, ILogger<TradeModel>, IExpirationAnalysisBuilder, ISessionRecommendationBuilder) : BaseOptionPageModel`.
- `[BindProperty] public SessionRecommendation? Recommendation { get; set; }`
- `OnGetAsync`: `await LoadCommonDataAsync()`; если `SelectedCurrencyId==0` → `=1` (BTC); `await Compute()`.
- `OnPostAsync`: `await LoadCommonDataAsync()`; если невалидно — `Page()`; `await Compute()`.
- `Compute()`: грузим rows (кэш `TradeData_{cur}_{date}`, 15 мин) за `(SelectedCurrencyId, SelectedDateTime)`; `expirations` = distinct из rows, сорт. по `dMMMyy`; `analyses = expirationBuilder.Build(rows, expirations, SelectedDateTime)`; если пусто — `Notes`/return; `Recommendation = sessionBuilder.Build(analyses, cur=="BTC"?…, SelectedDateTime)`. Try/catch + `logger.LogError` как в Exposure.

## 10. `Trade.cshtml` — вёрстка (Pico + Bootstrap, Chart.js CDN)
Заголовок `ViewData["Title"]="Trade Plan"`. Форма выбора — `<partial name="_BaseOptionForm" model="Model"/>` (валюта+дата). При `Recommendation==null` — заглушка «нет данных».
1. **Вердикт** — крупная карточка: валюта, спот, «до экспирации {Hours} ч ({FrontExpiration})»; бейджи НАПРАВЛЕНИЕ (цвет по Bias), РЕЖИМ (зелёный +гамма / красный −гамма), диапазон 1σ `{Lower1}…{Upper1}` и 2σ.
2. **Карта уровней** — server-render **SVG** (вертикальная шкала, как ASCII-превью): спот (маркер), CALL/PUT-стены (полосы), магнит, gamma-flip, σ-границы; подписи цены и роли. Высота ~520px, диапазон оси = [Lower2·0.99 … Upper2·1.01].
3. **Профиль гаммы** — `<canvas id="gammaChart">`, Chart.js line: x=`GammaProfile.Price`, y=`NetGex`, нулевая линия выделена (flip), вертикальная отметка спота (annotation или отдельный dataset-точка). Данные — `<script type="application/json">` как в `Delta.cshtml`.
4. **План** — 3 карточки (База/Бык/Медведь): Trigger, Targets, Stop, Action, Reason, Probability.
5. **Почему** — таблица `BiasComponents` (Сигнал | Значение | Нормировка | Вес | Вклад | Пояснение), итог `BiasScore`+ярлык.
6. **Макро-контекст** + **Notes** (alert'ы).
7. **Детали фронта** (`<details>`): `@Html.Raw(HtmlBuilder.CalculateCallPutRatioHtml(Recommendation.FrontChain))` + `AnalyzeOpenInterestHtml`.
8. Методичка-сноска: «объём = открытый интерес (OI)»; «не инвестиционная рекомендация, расчёт по позиционированию опционов».

## 11. DI и навигация
`Program.cs`: `builder.Services.AddSingleton<IExpirationAnalysisBuilder, ExpirationAnalysisBuilder>();` и `…<ISessionRecommendationBuilder, SessionRecommendationBuilder>();`.
`_Layout.cshtml`: `<li class="nav-item"><a class="nav-link text-dark" asp-area="" asp-page="/Trade">Trade Plan</a></li>` после Exposure.

## 12. Краевые случаи
DTE_front→0 (экспирация сегодня): `sessionYears` ограничен остатком до 08:00 UTC, Note о пиннинге. Нет близких: фронт = ближайший, Note «слабый сигнал». Скос/греки отсутствуют: пропуск сигнала/деградация. NaN/деление на ноль в центрах/стенах/flip: guard'ы (как в существующем коде). Пустой снимок: заглушка.

## 13. Верификация
Math — чистые функции. Кросс-сверка: сумма DEX/NetGEX/MaxPain по близким на снимке согласуется с таблицей `Exposure` (общий `ExpirationAnalysisBuilder`). `dotnet build Option.Data.Analyzing.sln` без ошибок. Ручной прогон `dotnet run --project Option.Data.Ui`, визуальный осмотр. Тест-проектов в репозитории нет — следуем конвенции (ручная проверка), числа на странице видны → самопроверяемо.
