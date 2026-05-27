# Спецификация: страница Delta (цена и Delta Exposure во времени)

Дата: 2026-05-27

## Цель

Новая Razor-страница `Delta` в `Option.Data.Ui`, показывающая для выбранной валюты (BTC/ETH) и экспирации, как во времени менялись:

1. **Underlying Price** — цена базового актива, записанная в БД в момент работы Job (`DeribitData.UnderlyingPrice`).
2. **Delta Exposure** — суммарная дельта-экспозиция по всем страйкам экспирации.

Ось X — время (моменты запуска Job, `DeribitData.CreatedAt`). Это историческая аналитика поверх данных в PostgreSQL (как страница Exposure), а не live-запрос в Deribit API.

## Компоновка (выбор пользователя: вариант B)

Два линейных графика друг под другом с общей осью времени:

- Верхний график — Underlying Price (линия).
- Нижний график — Delta Exposure (линия).

Оба `<canvas>` одинаковой ширины с одинаковыми подписями оси X → визуально выровнены по времени. Корреляция «цена ↔ экспозиция» читается по вертикали.

Объём (выбор пользователя):
- Только Delta Exposure (Gamma не показываем).
- Период — все доступные данные (все `CreatedAt` для пары валюта+экспирация, без фильтра диапазона).

## Архитектура

Следует существующим паттернам `Option.Data.Ui` (Razor Pages + `BaseOptionPageModel` + `IMemoryCache`).

### Новые/изменяемые файлы

| Файл | Назначение |
|------|-----------|
| `Pages/Delta.cshtml.cs` | `DeltaModel : BaseOptionPageModel` — загрузка и агрегация ряда. |
| `Pages/Delta.cshtml` | Форма + два `<canvas>` + инициализация Chart.js. |
| `Pages/Shared/_CurrencyExpirationForm.cshtml` | Новый партиал формы: валюта + экспирация (без datetime). |
| `Models/DeltaViewModel.cs` | `DeltaViewModel` + `DeltaPoint`. |
| `Pages/Shared/_Layout.cshtml` | Добавить пункт навигации «Option Delta». |

### ViewModel

```csharp
public class DeltaViewModel
{
    public List<DeltaPoint> Series { get; set; } = new();
}

public class DeltaPoint
{
    public DateTimeOffset Time { get; set; }
    public double UnderlyingPrice { get; set; }
    public double DeltaExposure { get; set; }
}
```

`DeltaModel` объявляет `[BindProperty] public DeltaViewModel DeltaViewModel { get; set; } = new();` (по образцу `ExposureModel`).

### Поток данных

`OnGetAsync`:
- `await LoadCommonDataAsync(useAvailableDates: false)` — заполняет `ViewModel.Currencies` и `ViewModel.Expirations` (экспирации берутся из БД базовым классом `BaseOptionPageModel`). Datetime-список не нужен.

`OnPostAsync`:
1. `await LoadCommonDataAsync(useAvailableDates: false)`.
2. Если `!ModelState.IsValid` → `return Page()`.
3. Получить серию из кеша/БД (ключ `DeltaSeries_{SelectedCurrencyId}_{SelectedExpiration}`, TTL 15 мин):
   - запрос: `DeribitData.Where(d => d.CurrencyTypeId == SelectedCurrencyId && d.Expiration == SelectedExpiration)`;
   - группировка по `CreatedAt`;
   - для каждой группы:
     - `UnderlyingPrice = group.Max(d => d.UnderlyingPrice)`;
     - `DeltaExposure = -group.Sum(d => d.Delta * d.OpenInterest)`;
   - сортировка по `Time` (возрастание).
4. Записать в `DeltaViewModel.Series`, `return Page()`.
5. Исключения логируются (`logger.LogError`), добавляется ошибка в `ModelState`, `return Page()`.

### Эквивалентность формулы Delta Exposure

В `Exposure.cshtml`: `deltaExposure = -Σ(CallDelta·CallOi + PutDelta·PutOi)` по объединённым по страйку записям `OptionData`. Поскольку в `DeribitData` каждая строка — отдельный опцион (call **или** put) со своими `Delta` и `OpenInterest`, сумма по объединённым страйкам тождественна сумме по всем строкам:

```
Σ_strike (CallDelta·CallOi + PutDelta·PutOi) = Σ_row (Delta · OpenInterest)
```

Поэтому считаем напрямую по строкам `DeribitData` без промежуточного преобразования в `OptionData`.

### Рендеринг (Delta.cshtml)

- `<partial name="_CurrencyExpirationForm" model="Model" />`.
- Если `Model.DeltaViewModel.Series.Any()`:
  - Заголовок «Delta Analysis for {BTC|ETH} — {expiration}».
  - Два `<canvas id="priceChart">` и `<canvas id="deltaChart">` друг под другом.
  - Серия сериализуется в JSON (`System.Text.Json.JsonSerializer.Serialize`) и встраивается в `<script>`; метки оси X — `Time.ToString("g")`.
- Иначе — блок «No data available. Please select a currency and expiration to load data.» (как на Exposure).
- В `@section Scripts`:
  - `<script src="https://cdn.jsdelivr.net/npm/chart.js"></script>` (CDN, консистентно с подключением Pico CSS с CDN в layout);
  - инициализация двух графиков Chart.js типа `line` с общими `labels`. Tooltips — поведение Chart.js по умолчанию.

### Форма `_CurrencyExpirationForm.cshtml`

`@model BaseOptionPageModel`, `method="post"`: два `<select>` — `ViewModel.SelectedCurrencyId` (из `Model.ViewModel.Currencies`) и `ViewModel.SelectedExpiration` (из `Model.ViewModel.Expirations`), оба `required`, и кнопка «Load Data». По образцу `_OptionsForm.cshtml`, но без блока datetime.

## Обработка ошибок и крайние случаи

- Нет данных по выбору → дружелюбное сообщение, графики не рендерятся.
- Невалидная форма (валюта/экспирация не выбраны) → `Page()` с ошибкой ModelState.
- Ошибка запроса/БД → лог + `ModelState` ошибка + `Page()`.
- Деление при расчёте отсутствует (Delta Exposure — сумма произведений), поэтому защита от деления на ноль не нужна.

## Тестирование

В решении нет тестовых проектов — проверка ручная:
- `dotnet run --project Option.Data.Ui`, открыть `/Delta`;
- выбрать ETH и экспирацию с историей в БД → два выровненных графика, цена и Delta Exposure меняются во времени;
- выбор без данных → сообщение «No data available»;
- сабмит без выбора → ошибка валидации.

## Вне области (YAGNI)

- Gamma Exposure.
- Фильтр диапазона дат / datetime-пикер.
- Рефакторинг inline-расчёта Delta Exposure в `Exposure.cshtml`.
- Live-запрос в Deribit API (страница работает только с БД).
