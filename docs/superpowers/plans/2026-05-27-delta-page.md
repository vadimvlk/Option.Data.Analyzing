# Delta Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a new Razor page `Delta` to `Option.Data.Ui` that plots Underlying Price and Delta Exposure over time (per Job run) for a selected currency + expiration, as two stacked time-series charts.

**Architecture:** Follows the existing `BaseOptionPageModel` + `IMemoryCache` pattern (like `ExposureModel`). The page reads historical rows from PostgreSQL via `ApplicationDbContext`, groups them by `CreatedAt`, computes price and Delta Exposure per timestamp, and renders two Chart.js line charts sharing the same time axis. No live Deribit API calls.

**Tech Stack:** ASP.NET Core 8 Razor Pages, EF Core 8 (Npgsql), `IMemoryCache`, Chart.js (CDN), Bootstrap + Pico CSS (already loaded in `_Layout.cshtml`).

**Testing note:** This solution has **no test project**, and the spec chose manual verification. There is no xUnit/runtime test harness to add (doing so is out of scope per the spec). The automated check for each task is `dotnet build` — .NET 8 compiles `.cshtml` views at build time, so a successful build verifies both C# and Razor syntax/types. Task 6 is end-to-end manual verification by running the app.

**Spec:** `docs/superpowers/specs/2026-05-27-delta-page-design.md`

**Reference files to mirror (read before starting):**
- `Option.Data.Ui/Pages/Exposure.cshtml.cs` — page-model pattern (primary ctor, `_context`/`_cache` capture, cache key, `LoadCommonDataAsync`, try/catch + `ModelState`).
- `Option.Data.Ui/Pages/BaseOptionPageModel.cshtml.cs` — base class; `LoadCommonDataAsync(useCurrencies, useExpirations, useAvailableDates)`, `ViewModel` (`OptionViewModel`).
- `Option.Data.Ui/Pages/Shared/_OptionsForm.cshtml` — form markup to mirror (minus the datetime block).
- `Option.Data.Ui/Models/ExposureViewModel.cs` — view-model style.
- `Option.Data.Shared/Poco/DeribitData.cs` — entity fields used: `CurrencyTypeId`, `Expiration`, `CreatedAt`, `UnderlyingPrice`, `Delta`, `OpenInterest`.

**Currency id mapping (from DB seed):** `1 = BTC`, `2 = ETH` (matches `Exposure.cshtml`).

---

### Task 1: View model (`DeltaViewModel` + `DeltaPoint`)

**Files:**
- Create: `Option.Data.Ui/Models/DeltaViewModel.cs`

- [ ] **Step 1: Create the view model file**

```csharp
namespace Option.Data.Ui.Models;

public class DeltaViewModel
{
    public List<DeltaPoint> Series { get; set; } = new();
}

public class DeltaPoint
{
    /// <summary>
    /// Момент записи данных (запуск Job), DeribitData.CreatedAt.
    /// </summary>
    public DateTimeOffset Time { get; set; }

    /// <summary>
    /// Цена базового актива на этот момент (Max UnderlyingPrice среди строк экспирации).
    /// </summary>
    public double UnderlyingPrice { get; set; }

    /// <summary>
    /// Суммарная Delta-экспозиция: -Σ(Delta · OpenInterest) по всем строкам момента.
    /// </summary>
    public double DeltaExposure { get; set; }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build Option.Data.Ui/Option.Data.Ui.csproj`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Option.Data.Ui/Models/DeltaViewModel.cs
git commit -m "$(printf 'Add DeltaViewModel and DeltaPoint for Delta page\n\nCo-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>')"
```

---

### Task 2: Page model (`DeltaModel`)

**Files:**
- Create: `Option.Data.Ui/Pages/Delta.cshtml.cs`

Mirrors `ExposureModel`: primary constructor passes args to `BaseOptionPageModel`, re-captures `_context`/`_cache` as private fields, uses `LoadCommonDataAsync(useAvailableDates: false)`. Aggregation runs in memory after `ToListAsync()` (the `Delta`/`UnderlyingPrice` columns are `decimal` in DB but `double` in the POCO; LINQ-to-objects avoids any translation surprise and matches how `ExposureModel` post-processes).

- [ ] **Step 1: Create the page model file**

```csharp
using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Shared.Poco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class DeltaModel(
    ApplicationDbContext context,
    IMemoryCache cache,
    ILogger<DeltaModel> logger) : BaseOptionPageModel(context, cache, logger)
{
    [BindProperty]
    public DeltaViewModel DeltaViewModel { get; set; } = new();

    private readonly ApplicationDbContext _context = context;
    private readonly IMemoryCache _cache = cache;

    public async Task OnGetAsync() => await LoadCommonDataAsync(useAvailableDates: false);

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonDataAsync(useAvailableDates: false);

        if (!ModelState.IsValid)
            return Page();

        try
        {
            string cacheKey = $"DeltaSeries_{ViewModel.SelectedCurrencyId}_{ViewModel.SelectedExpiration}";

            DeltaViewModel.Series = (await _cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

                List<DeribitData> rows = await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                                d.Expiration == ViewModel.SelectedExpiration)
                    .ToListAsync();

                return rows
                    .GroupBy(d => d.CreatedAt)
                    .Select(g => new DeltaPoint
                    {
                        Time = g.Key,
                        UnderlyingPrice = g.Max(d => d.UnderlyingPrice),
                        DeltaExposure = -g.Sum(d => d.Delta * d.OpenInterest)
                    })
                    .OrderBy(p => p.Time)
                    .ToList();
            }))!;

            return Page();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading delta series data");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            return Page();
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build Option.Data.Ui/Option.Data.Ui.csproj`
Expected: `Build succeeded`, 0 errors. (At this point `Delta.cshtml` does not exist yet; the page model compiles independently. The `@page` view is added in Task 4.)

- [ ] **Step 3: Commit**

```bash
git add Option.Data.Ui/Pages/Delta.cshtml.cs
git commit -m "$(printf 'Add DeltaModel page model with time-series aggregation\n\nCo-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>')"
```

---

### Task 3: Form partial (`_CurrencyExpirationForm`)

**Files:**
- Create: `Option.Data.Ui/Pages/Shared/_CurrencyExpirationForm.cshtml`

Same as `_OptionsForm.cshtml` but with no datetime `<select>` (currency + expiration only). Typed against `BaseOptionPageModel` so it works for any derived page model.

- [ ] **Step 1: Create the partial**

```cshtml
@using Option.Data.Shared.Poco
@model BaseOptionPageModel
<form method="post">
    <div class="grid">
        <div>
            <label for="currency">Currency</label>
            <select id="currency" name="ViewModel.SelectedCurrencyId" required>
                <option value="">Select currency</option>
                @foreach (CurrencyType currency in Model.ViewModel.Currencies)
                {
                    <option value="@currency.Id" selected="@(currency.Id == Model.ViewModel.SelectedCurrencyId)">@currency.Name</option>
                }
            </select>
        </div>

        <div>
            <label for="expiration">Expiration</label>
            <select id="expiration" name="ViewModel.SelectedExpiration" required>
                <option value="">Select expiration</option>
                @foreach (string exp in Model.ViewModel.Expirations)
                {
                    <option value="@exp" selected="@(exp == Model.ViewModel.SelectedExpiration)">@exp</option>
                }
            </select>
        </div>
    </div>
    <button type="submit" asp-page-handler="" class="contrast">Load Data</button>
</form>
```

- [ ] **Step 2: Build to verify the Razor partial compiles**

Run: `dotnet build Option.Data.Ui/Option.Data.Ui.csproj`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Option.Data.Ui/Pages/Shared/_CurrencyExpirationForm.cshtml
git commit -m "$(printf 'Add _CurrencyExpirationForm partial (currency + expiration)\n\nCo-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>')"
```

---

### Task 4: Page view (`Delta.cshtml`) with stacked Chart.js charts

**Files:**
- Create: `Option.Data.Ui/Pages/Delta.cshtml`

Renders the form, and — when data is present — two `<canvas>` charts (price on top, Delta Exposure below) sharing the same X-axis labels. The series is serialized to JSON into a non-executable `<script type="application/json">` block (avoids inline-escaping pitfalls); Chart.js is loaded from CDN in `@section Scripts` and reads that block.

- [ ] **Step 1: Create the view**

```cshtml
@page
@using System.Text.Json
@using Option.Data.Ui.Models
@{
    ViewData["Title"] = "Delta Exposure Analysis";
}

@model DeltaModel

<div class="container">
    <h2>Delta Exposure Over Time</h2>

    <partial name="_CurrencyExpirationForm" model="Model"/>

    @if (Model.DeltaViewModel.Series.Any())
    {
        <h4>Analysis for @(Model.ViewModel.SelectedCurrencyId == 1 ? "BTC" : "ETH") — @Model.ViewModel.SelectedExpiration</h4>

        <article>
            <h5>Underlying Price</h5>
            <canvas id="priceChart" height="100"></canvas>
        </article>
        <article>
            <h5>Delta Exposure</h5>
            <canvas id="deltaChart" height="100"></canvas>
        </article>

        @{
            List<string> labels = Model.DeltaViewModel.Series.Select(p => p.Time.ToString("g")).ToList();
            List<double> prices = Model.DeltaViewModel.Series.Select(p => p.UnderlyingPrice).ToList();
            List<double> deltas = Model.DeltaViewModel.Series.Select(p => p.DeltaExposure).ToList();
        }
        <script id="delta-data" type="application/json">
            @Html.Raw(JsonSerializer.Serialize(new { labels, prices, deltas }))
        </script>
    }
    else
    {
        <div class="option-data-container">
            <h4>Delta Exposure Analysis</h4>
            <p>No data available. Please select a currency and expiration to load data.</p>
        </div>
    }
</div>

@section Scripts {
    @if (Model.DeltaViewModel.Series.Any())
    {
        <script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
        <script>
            (function () {
                const payload = JSON.parse(document.getElementById('delta-data').textContent);

                const baseOptions = {
                    responsive: true,
                    interaction: { mode: 'index', intersect: false },
                    scales: { y: { beginAtZero: false } }
                };

                new Chart(document.getElementById('priceChart'), {
                    type: 'line',
                    data: {
                        labels: payload.labels,
                        datasets: [{
                            label: 'Underlying Price',
                            data: payload.prices,
                            borderColor: '#4361ee',
                            backgroundColor: 'rgba(67,97,238,0.1)',
                            tension: 0.2,
                            pointRadius: 2
                        }]
                    },
                    options: baseOptions
                });

                new Chart(document.getElementById('deltaChart'), {
                    type: 'line',
                    data: {
                        labels: payload.labels,
                        datasets: [{
                            label: 'Delta Exposure',
                            data: payload.deltas,
                            borderColor: '#f9844a',
                            backgroundColor: 'rgba(249,132,74,0.1)',
                            tension: 0.2,
                            pointRadius: 2
                        }]
                    },
                    options: baseOptions
                });
            })();
        </script>
    }
}
```

- [ ] **Step 2: Build to verify the page compiles**

Run: `dotnet build Option.Data.Ui/Option.Data.Ui.csproj`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Option.Data.Ui/Pages/Delta.cshtml
git commit -m "$(printf 'Add Delta.cshtml with stacked price and Delta Exposure charts\n\nCo-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>')"
```

---

### Task 5: Navigation link in `_Layout.cshtml`

**Files:**
- Modify: `Option.Data.Ui/Pages/Shared/_Layout.cshtml` (add a nav item after the Exposure link, around lines 29-31)

- [ ] **Step 1: Add the nav link**

Find this block:

```cshtml
                    <li class="nav-item">
                        <a class="nav-link text-dark" asp-area="" asp-page="/Exposure">Option Exposure</a>
                    </li>
```

Add immediately after it:

```cshtml
                    <li class="nav-item">
                        <a class="nav-link text-dark" asp-area="" asp-page="/Delta">Option Delta</a>
                    </li>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Option.Data.Ui/Option.Data.Ui.csproj`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Option.Data.Ui/Pages/Shared/_Layout.cshtml
git commit -m "$(printf 'Add Option Delta link to navigation\n\nCo-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>')"
```

---

### Task 6: End-to-end manual verification

**Files:** none (verification only).

The app reads from the production PostgreSQL (`ConnectionStrings:DefaultConnection` in `appsettings.json`), so historical data already exists.

- [ ] **Step 1: Run the web app**

Run: `dotnet run --project Option.Data.Ui`
Expected: app starts, listening URL printed in console (e.g. `http://localhost:5xxx`).

- [ ] **Step 2: Verify the happy path**

Open the printed URL, click **Option Delta** in the nav. Select **ETH** and an expiration that has history (e.g. one shown on the Exposure page), click **Load Data**.
Expected: two stacked line charts appear — top "Underlying Price", bottom "Delta Exposure" — aligned on the same time axis; hovering shows tooltips with the value at each timestamp.

- [ ] **Step 3: Verify the empty state**

Select a currency/expiration combination with no stored data (or load the page fresh via GET).
Expected: message "No data available. Please select a currency and expiration to load data." and no charts.

- [ ] **Step 4: Verify validation**

Submit the form without selecting currency or expiration.
Expected: the browser `required` attribute blocks submit; if bypassed, the page returns without charts (ModelState invalid).

- [ ] **Step 5: Stop the app**

Stop the running app (Ctrl+C in its console). No commit for this task.

---

## Self-Review

**Spec coverage:**
- New page `Delta` (route + nav) → Tasks 4, 5. ✅
- Form currency + expiration, no datetime (new partial) → Task 3; `LoadCommonDataAsync(useAvailableDates: false)` → Task 2. ✅
- Query all `CreatedAt` for currency+expiration, group, `Max(UnderlyingPrice)`, `-Σ(Delta·OpenInterest)`, sort by time, cache 15 min → Task 2. ✅
- ViewModel `DeltaViewModel`/`DeltaPoint` → Task 1. ✅
- Stacked charts (variant B), Chart.js via CDN, JSON serialization, shared X labels → Task 4. ✅
- Empty state / validation / error handling → Task 2 (try/catch, ModelState) + Task 4 (empty block) + Task 6 (verify). ✅
- Out of scope: Gamma, date-range filter, refactor of Exposure formula, live API — none introduced. ✅

**Placeholder scan:** No TBD/TODO; all code blocks complete. ✅

**Type consistency:** `DeltaViewModel.Series` (`List<DeltaPoint>`), `DeltaPoint.{Time, UnderlyingPrice, DeltaExposure}` are defined in Task 1 and used identically in Tasks 2 and 4. Page model `DeltaModel` referenced as `@model DeltaModel` in Task 4. Partial typed `BaseOptionPageModel`, passed `model="Model"` (DeltaModel derives from it). Cache key string identical conceptually to Exposure pattern. ✅
