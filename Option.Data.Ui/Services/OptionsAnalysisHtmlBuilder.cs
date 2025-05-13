using System.Text;
using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Services;

public class OptionsAnalysisHtmlBuilder : IOptionsAnalysisHtmlBuilder
{
    public string CalculateCallPutRatioHtml(List<OptionData> data)
    {
        double totalCallOi = data.Sum(d => d.CallOi);
        double totalPutOi = data.Sum(d => d.PutOi);
        double callPutRatio = totalPutOi > 0 ? totalCallOi / (totalCallOi + totalPutOi) : 0;
        double putCallRatio = totalPutOi > 0 ? totalPutOi / (totalCallOi + totalPutOi) : 0;

        double optionRatio = totalPutOi > 0 ? totalCallOi / totalPutOi : 0;

        var htmlBuilder = new StringBuilder();

        htmlBuilder.AppendLine("<article class='card mb-4'>");
        htmlBuilder.AppendLine("<header class='bg-light p-3'>");
        htmlBuilder.AppendLine("<h4>АНАЛИЗ СООТНОШЕНИЯ CALL/PUT</h4>");
        htmlBuilder.AppendLine("</header>");

        htmlBuilder.AppendLine("<div class='p-2'>");

        // Display key metrics in a table
        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-bordered'>");
        htmlBuilder.AppendLine("<thead class='table-light'>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>Метрика</th>");
        htmlBuilder.AppendLine("<th>Значение</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");

        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<td>Общий объем Call опционов</td>");
        htmlBuilder.AppendLine($"<td>{totalCallOi:F2}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<td>Общий объем Put опционов</td>");
        htmlBuilder.AppendLine($"<td>{totalPutOi:F2}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<td>Call/Put Ratio</td>");
        htmlBuilder.AppendLine($"<td>{callPutRatio:F2}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<td>Put/Call Ratio</td>");
        htmlBuilder.AppendLine($"<td>{putCallRatio:F2}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>");

        // Comparison info
        if (totalCallOi > totalPutOi && totalPutOi > 0)
        {
            htmlBuilder.AppendLine(
                $"<p class='mt-2'>Call опционов больше в <mark>{totalCallOi / totalPutOi:F2}</mark> раза.</p>");
        }
        else if (totalPutOi > totalCallOi && totalCallOi > 0)
        {
            htmlBuilder.AppendLine(
                $"<p class='mt-2'>Put опционов больше в <mark>{totalPutOi / totalCallOi:F2}</mark> раза.</p>");
        }

        // Interpretation section
        htmlBuilder.AppendLine("<div class='alert mt-2 p-2'>");
        htmlBuilder.AppendLine("<h5>Интерпретация:</h5>");

        if (optionRatio > 1.5)
        {
            htmlBuilder.AppendLine(
                "<p class='text-success'>Значительный перевес в сторону Call опционов. Рынок демонстрирует бычьи настроения.</p>");
        }
        else if (optionRatio is >= 0.75 and <= 1.5)
        {
            htmlBuilder.AppendLine(
                "<p class='text-primary'>Относительно сбалансированное соотношение Call и Put опционов.</p>");
        }
        else if (optionRatio > 0)
        {
            htmlBuilder.AppendLine(
                "<p class='text-danger'>Значительный перевес в сторону Put опционов. Рынок демонстрирует медвежьи настроения.</p>");
        }

        htmlBuilder.AppendLine("</div>"); // Close interpretation div

        htmlBuilder.AppendLine("</div>"); // Close card body
        htmlBuilder.AppendLine("</article>"); // Close card

        return htmlBuilder.ToString();
    }

    public string CalculateMaxPainHtml(List<OptionData> data, double currentPrice)
    {
        var htmlBuilder = new StringBuilder();

        htmlBuilder.AppendLine("<article class='card mb-4'>");
        htmlBuilder.AppendLine("<header class='bg-light p-3'>");
        htmlBuilder.AppendLine("<h4>РАСЧЕТ MAX PAIN</h4>");
        htmlBuilder.AppendLine("</header>");

        htmlBuilder.AppendLine("<div class='p-3'>");

        List<PainResult> painResults = new();

        foreach (OptionData targetOption in data)
        {
            double targetStrike = targetOption.Strike;
            double callLosses = 0;
            double putLosses = 0;

            foreach (OptionData option in data)
            {
                // Убытки для держателей Call опционов при данном страйке
                callLosses += option.CallOi * Math.Max(0, targetStrike - option.Strike);

                // Убытки для держателей Put опционов при данном страйке
                putLosses += option.PutOi * Math.Max(0, option.Strike - targetStrike);
            }

            painResults.Add(new PainResult
            {
                Strike = targetStrike,
                CallLosses = callLosses,
                PutLosses = putLosses,
                TotalLosses = callLosses + putLosses
            });
        }

        // Сортируем по возрастанию общих убытков
        painResults = painResults.OrderBy(p => p.TotalLosses).ToList();

        // Находим страйк с минимальными общими убытками (Max Pain)
        PainResult maxPainResult = painResults.First();
        double maxPainStrike = maxPainResult.Strike;

        // Main Max Pain info in a highlighted box
        htmlBuilder.AppendLine("<div class='alert alert-primary' role='alert'>");
        htmlBuilder.AppendLine($"<h5>Уровень Max Pain: <strong>{maxPainStrike}</strong></h5>");

        double priceDiff = currentPrice - maxPainStrike;
        double percentDiff = priceDiff / maxPainStrike * 100;
        string diffClass = priceDiff >= 0 ? "text-success" : "text-danger";

        htmlBuilder.AppendLine("<p>");
        htmlBuilder.AppendLine(
            $"Текущая цена от Max Pain: <span class='{diffClass}'>{priceDiff:F2} пунктов ({percentDiff:F2}%)</span>");
        htmlBuilder.AppendLine("</p>");
        htmlBuilder.AppendLine("</div>");

        // Топ-5 страйков таблица
        htmlBuilder.AppendLine("<h5 class='mt-4'>Топ-5 страйков с наименьшими общими убытками:</h5>");
        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-striped table-hover'>");
        htmlBuilder.AppendLine("<thead>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>№</th>");
        htmlBuilder.AppendLine("<th>Страйк</th>");
        htmlBuilder.AppendLine("<th>Общие убытки</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");

        for (int i = 0; i < Math.Min(5, painResults.Count); i++)
        {
            string rowClass = i == 0 ? "table-primary" : "";
            htmlBuilder.AppendLine($"<tr class='{rowClass}'>");
            htmlBuilder.AppendLine($"<td>{i + 1}</td>");
            htmlBuilder.AppendLine($"<td>{painResults[i].Strike}</td>");
            htmlBuilder.AppendLine($"<td>{painResults[i].TotalLosses:N0}</td>");
            htmlBuilder.AppendLine("</tr>");
        }

        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>");

        // Интерпретация
        htmlBuilder.AppendLine("<div class='alert alert-secondary mt-4'>");
        htmlBuilder.AppendLine("<h5>Интерпретация Max Pain:</h5>");

        if (Math.Abs(currentPrice - maxPainStrike) / maxPainStrike <= 0.01)
        {
            htmlBuilder.AppendLine(
                "<p>Цена находится вблизи уровня Max Pain, что может указывать на временную стабилизацию.</p>");
        }
        else if (currentPrice < maxPainStrike)
        {
            htmlBuilder.AppendLine(
                $"<p>Цена находится ниже уровня Max Pain. Теоретически это создает потеницал в сторону роста к уровню <strong>{maxPainStrike}</strong>.</p>");
        }
        else
        {
            htmlBuilder.AppendLine(
                $"<p>Цена находится выше уровня Max Pain. Теоретически это создает давление в сторону снижения к уровню <strong>{maxPainStrike}</strong>.</p>");
        }

        htmlBuilder.AppendLine("</div>");

        // Call the pain gradient analysis (note: we'll need to implement this as a separate method)
        htmlBuilder.AppendLine(AnalyzePainGradientHtml(data, maxPainStrike, currentPrice));

        htmlBuilder.AppendLine("</div>"); // Close card body
        htmlBuilder.AppendLine("</article>"); // Close card

        return htmlBuilder.ToString();
    }


    public string AnalyzeOpenInterestHtml(List<OptionData> data)
    {
        var htmlBuilder = new StringBuilder();

        htmlBuilder.AppendLine("<article class='card mb-4'>");
        htmlBuilder.AppendLine("<header class='bg-light p-3'>");
        htmlBuilder.AppendLine("<h4>АНАЛИЗ ОТКРЫТОГО ИНТЕРЕСА</h4>");
        htmlBuilder.AppendLine("</header>");

        htmlBuilder.AppendLine("<div class='p-3'>");

        // Create a responsive grid layout for the three tables
        htmlBuilder.AppendLine("<div class='row g-4'>");

        // Топ-5 страйков по объему Call опционов
        htmlBuilder.AppendLine("<div class='col-md-4'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-primary'>");
        htmlBuilder.AppendLine(
            "<div class='card-header text-white bg-primary'>Топ-5 страйков по объему Call опционов</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        List<OptionData> topCallStrikes = data
            .OrderByDescending(d => d.CallOi)
            .Take(5)
            .ToList();

        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-hover'>");
        htmlBuilder.AppendLine("<thead>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>Страйк</th>");
        htmlBuilder.AppendLine("<th>Объем</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");

        foreach (OptionData strike in topCallStrikes)
        {
            htmlBuilder.AppendLine("<tr>");
            htmlBuilder.AppendLine($"<td>{strike.Strike}</td>");
            htmlBuilder.AppendLine($"<td>{strike.CallOi:N0}</td>");
            htmlBuilder.AppendLine("</tr>");
        }

        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>"); // Close table-responsive

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card
        htmlBuilder.AppendLine("</div>"); // Close col

        // Топ-5 страйков по объему Put опционов
        htmlBuilder.AppendLine("<div class='col-md-4'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-danger'>");
        htmlBuilder.AppendLine(
            "<div class='card-header text-white bg-danger'>Топ-5 страйков по объему Put опционов</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        List<OptionData> topPutStrikes = data
            .OrderByDescending(d => d.PutOi)
            .Take(5)
            .ToList();

        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-hover'>");
        htmlBuilder.AppendLine("<thead>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>Страйк</th>");
        htmlBuilder.AppendLine("<th>Объем</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");

        foreach (OptionData strike in topPutStrikes)
        {
            htmlBuilder.AppendLine("<tr>");
            htmlBuilder.AppendLine($"<td>{strike.Strike}</td>");
            htmlBuilder.AppendLine($"<td>{strike.PutOi:N0}</td>");
            htmlBuilder.AppendLine("</tr>");
        }

        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>"); // Close table-responsive

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card
        htmlBuilder.AppendLine("</div>"); // Close col

        // Ключевые уровни на основе общего объема опционов
        htmlBuilder.AppendLine("<div class='col-md-4'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-success'>");
        htmlBuilder.AppendLine(
            "<div class='card-header text-white bg-success'>Ключевые уровни на основе общего объема</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        List<OptionData> keyLevels = data
            .OrderByDescending(d => d.CallOi + d.PutOi)
            .Take(5)
            .ToList();

        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-hover'>");
        htmlBuilder.AppendLine("<thead>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>Страйк</th>");
        htmlBuilder.AppendLine("<th>Общий объем</th>");
        htmlBuilder.AppendLine("<th>Тип</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");

        foreach (OptionData level in keyLevels)
        {
            string type = level.CallOi > level.PutOi ? "Сопротивление" : "Поддержка";
            string typeClass = type == "Сопротивление" ? "text-danger" : "text-success";

            htmlBuilder.AppendLine("<tr>");
            htmlBuilder.AppendLine($"<td>{level.Strike}</td>");
            htmlBuilder.AppendLine($"<td>{level.CallOi + level.PutOi:N0}</td>");
            htmlBuilder.AppendLine($"<td class='{typeClass}'><strong>{type}</strong></td>");
            htmlBuilder.AppendLine("</tr>");
        }

        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>"); // Close table-responsive

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card
        htmlBuilder.AppendLine("</div>"); // Close col

        htmlBuilder.AppendLine("</div>"); // Close row

        htmlBuilder.AppendLine("</div>"); // Close card body
        htmlBuilder.AppendLine("</article>"); // Close card

        return htmlBuilder.ToString();
    }

    public string CalculateProfitLossHtml(List<OptionData> data, double currentPrice)
    {
        var htmlBuilder = new StringBuilder();

        htmlBuilder.AppendLine("<article class='card mb-4'>");
        htmlBuilder.AppendLine("<header class='bg-light p-3'>");
        htmlBuilder.AppendLine($"<h4>АНАЛИЗ ПРИБЫЛИ/УБЫТКА ПРИ ТЕКУЩЕЙ ЦЕНЕ {currentPrice}</h4>");
        htmlBuilder.AppendLine("</header>");

        htmlBuilder.AppendLine("<div class='p-3'>");

        double callProfit = 0;
        double putProfit = 0;

        foreach (OptionData option in data)
        {
            // Прибыль для держателей Call опционов
            callProfit += option.CallOi * Math.Max(0, currentPrice - option.Strike);

            // Прибыль для держателей Put опционов
            putProfit += option.PutOi * Math.Max(0, option.Strike - currentPrice);
        }

        double totalProfit = callProfit + putProfit;
        double callProfitPercent = totalProfit > 0 ? callProfit / totalProfit * 100 : 0;
        double putProfitPercent = totalProfit > 0 ? putProfit / totalProfit * 100 : 0;
        double callPutRatio = putProfit > 0 ? callProfit / putProfit : 0;

        // Container for-profit stats and chart
        htmlBuilder.AppendLine("<div class='row'>");

        // Left column - profit stats
        htmlBuilder.AppendLine("<div class='col-md-6'>");

        // Create a table for the profit data
        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-bordered'>");
        htmlBuilder.AppendLine("<thead class='table-light'>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>Показатель</th>");
        htmlBuilder.AppendLine("<th>Значение</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");

        htmlBuilder.AppendLine("<tr class='table-primary'>");
        htmlBuilder.AppendLine("<td>Прибыль держателей Call</td>");
        htmlBuilder.AppendLine($"<td>{callProfit:N0}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("<tr class='table-danger'>");
        htmlBuilder.AppendLine("<td>Прибыль держателей Put</td>");
        htmlBuilder.AppendLine($"<td>{putProfit:N0}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<td>Соотношение прибыли Call/Put</td>");
        htmlBuilder.AppendLine($"<td>{callPutRatio:F2}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>"); // Close table-responsive

        htmlBuilder.AppendLine("</div>"); // Close col-md-6

        // Right column - visual representation (progress bars)
        htmlBuilder.AppendLine("<div class='col-md-6'>");
        htmlBuilder.AppendLine("<h5 class='mb-3'>Распределение прибыли</h5>");

        // Call progress bar
        htmlBuilder.AppendLine("<div class='mb-3'>");
        htmlBuilder.AppendLine("<div class='d-flex justify-content-between align-items-center mb-1'>");
        htmlBuilder.AppendLine("<span>Call опционы</span>");
        htmlBuilder.AppendLine($"<span>{callProfitPercent:F1}%</span>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='progress'>");
        htmlBuilder.AppendLine(
            $"<div class='progress-bar bg-primary' role='progressbar' style='width: {callProfitPercent}%' " +
            $"aria-valuenow='{callProfitPercent}' aria-valuemin='0' aria-valuemax='100'></div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Put progress bar
        htmlBuilder.AppendLine("<div class='mb-3'>");
        htmlBuilder.AppendLine("<div class='d-flex justify-content-between align-items-center mb-1'>");
        htmlBuilder.AppendLine("<span>Put опционы</span>");
        htmlBuilder.AppendLine($"<span>{putProfitPercent:F1}%</span>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='progress'>");
        htmlBuilder.AppendLine(
            $"<div class='progress-bar bg-danger' role='progressbar' style='width: {putProfitPercent}%' " +
            $"aria-valuenow='{putProfitPercent}' aria-valuemin='0' aria-valuemax='100'></div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close col-md-6

        htmlBuilder.AppendLine("</div>"); // Close row

        // Interpretation section
        htmlBuilder.AppendLine("<div class='alert mt-4 p-3'>");
        htmlBuilder.AppendLine("<h5>Интерпретация:</h5>");

        if (callProfit > putProfit * 1.5)
        {
            htmlBuilder.AppendLine(
                "<p class='text-success'><strong>Значительный перевес прибыли в пользу держателей Call опционов</strong>, " +
                "что может создавать стимул для дальнейшего роста цены.</p>");
        }
        else if (putProfit > callProfit * 1.5)
        {
            htmlBuilder.AppendLine(
                "<p class='text-danger'><strong>Значительный перевес прибыли в пользу держателей Put опционов</strong>, " +
                "что может создавать стимул для снижения цены.</p>");
        }
        else
        {
            htmlBuilder.AppendLine(
                "<p class='text-primary'><strong>Относительно сбалансированное соотношение прибыли</strong> " +
                "между держателями Call и Put опционов.</p>");
        }

        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close card body
        htmlBuilder.AppendLine("</article>"); // Close card

        return htmlBuilder.ToString();
    }

    public string AnalyzeCentersOfGravityHtml(List<OptionData> data, double currentPrice)
    {
        var htmlBuilder = new StringBuilder();

        htmlBuilder.AppendLine("<article class='card mb-4'>");
        htmlBuilder.AppendLine("<header class='bg-light p-3'>");
        htmlBuilder.AppendLine("<h4>АНАЛИЗ ЦЕНТРОВ ТЯЖЕСТИ</h4>");
        htmlBuilder.AppendLine("</header>");

        htmlBuilder.AppendLine("<div class='p-3'>");

        double totalCallOi = data.Sum(d => d.CallOi);
        double totalPutOi = data.Sum(d => d.PutOi);

        // Расчет центра тяжести для Call и Put
        double callCenter = data.Sum(d => d.Strike * d.CallOi) / totalCallOi;
        double putCenter = data.Sum(d => d.Strike * d.PutOi) / totalPutOi;
        // Расчет равновестной цены центра тяжести.
        double gravityPrice = (callCenter + putCenter) / 2;

        // Расчет убытков на уровнях центров тяжести
        double callCenterLosses = CalculateTotalLossesAtPrice(data, callCenter);
        double putCenterLosses = CalculateTotalLossesAtPrice(data, putCenter);
        double gravityPriceLosses = CalculateTotalLossesAtPrice(data, gravityPrice);

        // Create table with a centered layout
        htmlBuilder.AppendLine("<div class='row justify-content-start'>");
        htmlBuilder.AppendLine("<div class='col-md-8'>");

        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-bordered'>");
        htmlBuilder.AppendLine("<thead class='table-light'>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>Показатель</th>");
        htmlBuilder.AppendLine("<th>Значение</th>");
        htmlBuilder.AppendLine("<th>Убытки</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");

        htmlBuilder.AppendLine("<tr class='table-primary'>");
        htmlBuilder.AppendLine("<td>Центр тяжести Call</td>");
        htmlBuilder.AppendLine($"<td>{callCenter:F2}</td>");
        htmlBuilder.AppendLine($"<td>{callCenterLosses:N0}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("<tr class='table-danger'>");
        htmlBuilder.AppendLine("<td>Центр тяжести Put</td>");
        htmlBuilder.AppendLine($"<td>{putCenter:F2}</td>");
        htmlBuilder.AppendLine($"<td>{putCenterLosses:N0}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("<tr class='table-success'>");
        htmlBuilder.AppendLine("<td>Равновестная цена</td>");
        htmlBuilder.AppendLine($"<td>{gravityPrice:F2}</td>");
        htmlBuilder.AppendLine($"<td>{gravityPriceLosses:N0}</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("<tr class='table-warning'>");
        htmlBuilder.AppendLine("<td>Текущая цена</td>");
        htmlBuilder.AppendLine($"<td>{currentPrice:F2}</td>");
        htmlBuilder.AppendLine("<td>-</td>");
        htmlBuilder.AppendLine("</tr>");

        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>"); // Close table-responsive

        // Price potential section
        htmlBuilder.AppendLine("<div class='alert p-0'>");

        if (gravityPrice > currentPrice)
        {
            if (putCenter > currentPrice)
            {
                htmlBuilder.AppendLine("<div class='alert-success p-3 rounded'>");
                htmlBuilder.AppendLine("<h5>Анализ потенциала движения цены:</h5>");
                htmlBuilder.AppendLine("<p><i class='bi bi-arrow-up-circle-fill me-2'></i>Потенциал роста к <strong>" +
                                       putCenter.ToString("F2") + "</strong>, признак перепроданности.</p>");
                htmlBuilder.AppendLine("</div>");
            }
            else
            {
                htmlBuilder.AppendLine("<div class='alert-success p-3 rounded'>");
                htmlBuilder.AppendLine("<h5>Анализ потенциала движения цены:</h5>");
                htmlBuilder.AppendLine("<p><i class='bi bi-arrow-up-circle-fill me-2'></i>Потенциал роста к <strong>" +
                                       gravityPrice.ToString("F2") + "</strong>, поддержка на уровне <strong>" +
                                       putCenter.ToString("F2") + "</strong>.</p>");
                htmlBuilder.AppendLine("</div>");
            }
        }
        else if (currentPrice > gravityPrice)
        {
            if (currentPrice > callCenter)
            {
                htmlBuilder.AppendLine("<div class='alert-danger p-3 rounded'>");
                htmlBuilder.AppendLine("<h5>Анализ потенциала движения цены:</h5>");
                htmlBuilder.AppendLine(
                    "<p><i class='bi bi-arrow-down-circle-fill me-2'></i>Потенциал снижения к <strong>" +
                    callCenter.ToString("F2") + "</strong>, признак перекупленности.</p>");
                htmlBuilder.AppendLine("</div>");
            }
            else
            {
                htmlBuilder.AppendLine("<div class='alert-danger p-3 rounded'>");
                htmlBuilder.AppendLine("<h5>Анализ потенциала движения цены:</h5>");
                htmlBuilder.AppendLine(
                    "<p><i class='bi bi-arrow-down-circle-fill me-2'></i>Потенциал снижения к <strong>" +
                    gravityPrice.ToString("F2") + "</strong>, сопротивление на уровне <strong>" +
                    callCenter.ToString("F2") + "</strong>.</p>");
                htmlBuilder.AppendLine("</div>");
            }
        }
        else
        {
            htmlBuilder.AppendLine("<div class='alert-info p-3 rounded'>");
            htmlBuilder.AppendLine("<h5>Анализ потенциала движения цены:</h5>");
            htmlBuilder.AppendLine(
                "<p><i class='bi bi-dash-circle-fill me-2'></i>Цена находится вблизи равновесного уровня.</p>");
            htmlBuilder.AppendLine("</div>");
        }

        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close col-md-8
        htmlBuilder.AppendLine("</div>"); // Close row

        htmlBuilder.AppendLine("</div>"); // Close card body
        htmlBuilder.AppendLine("</article>"); // Close card

        return htmlBuilder.ToString();
    }

    public string AnalyzeGlobalSellerPositionHtml(List<OptionData> data, double currentPrice)
    {
        var htmlBuilder = new StringBuilder();

        htmlBuilder.AppendLine("<article class='card mb-4'>");
        htmlBuilder.AppendLine("<header class='bg-dark text-white p-3'>");
        htmlBuilder.AppendLine("<h4>АНАЛИЗ ПОЗИЦИИ ГЛОБАЛЬНОГО ПРОДАВЦА ВСЕХ ОПЦИОНОВ</h4>");
        htmlBuilder.AppendLine("</header>");

        htmlBuilder.AppendLine("<div class='p-3'>");

        // 1. Подсчитываем общую полученную премию продавцом
        double totalCallPremium = 0;
        double totalPutPremium = 0;

        foreach (OptionData option in data)
        {
            totalCallPremium += option.CallPrice * option.CallOi;
            totalPutPremium += option.PutPrice * option.PutOi;
        }

        double totalPremium = totalCallPremium + totalPutPremium;

        // Overview section at the top
        htmlBuilder.AppendLine("<div class='card mb-4'>");
        htmlBuilder.AppendLine("<div class='card-header'>");
        htmlBuilder.AppendLine("<h5 class='mb-0'>Обзор позиции продавца</h5>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        htmlBuilder.AppendLine("<div class='row'>");

        // Current price
        htmlBuilder.AppendLine("<div class='col-md-3 col-sm-6 mb-3'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-primary'>");
        htmlBuilder.AppendLine("<div class='card-body text-center'>");
        htmlBuilder.AppendLine("<h6 class='text-muted'>Текущая цена</h6>");
        htmlBuilder.AppendLine($"<h4 class='mb-0 fw-bold'>{currentPrice:F2}</h4>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Total premium
        htmlBuilder.AppendLine("<div class='col-md-3 col-sm-6 mb-3'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-success'>");
        htmlBuilder.AppendLine("<div class='card-body text-center'>");
        htmlBuilder.AppendLine("<h6 class='text-muted'>Общая премия</h6>");
        htmlBuilder.AppendLine($"<h4 class='mb-0 fw-bold'>{totalPremium:N0}</h4>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Call premium
        htmlBuilder.AppendLine("<div class='col-md-3 col-sm-6 mb-3'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-info'>");
        htmlBuilder.AppendLine("<div class='card-body text-center'>");
        htmlBuilder.AppendLine("<h6 class='text-muted'>Call премия</h6>");
        htmlBuilder.AppendLine($"<h4 class='mb-0 fw-bold'>{totalCallPremium:N0}</h4>");
        htmlBuilder.AppendLine($"<small class='text-muted'>({totalCallPremium / totalPremium * 100:F1}%)</small>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Put premium
        htmlBuilder.AppendLine("<div class='col-md-3 col-sm-6 mb-3'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-danger'>");
        htmlBuilder.AppendLine("<div class='card-body text-center'>");
        htmlBuilder.AppendLine("<h6 class='text-muted'>Put премия</h6>");
        htmlBuilder.AppendLine($"<h4 class='mb-0 fw-bold'>{totalPutPremium:N0}</h4>");
        htmlBuilder.AppendLine($"<small class='text-muted'>({totalPutPremium / totalPremium * 100:F1}%)</small>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close row

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card

        // 2. Определяем диапазон цен для анализа
        double minStrike = data.Min(d => d.Strike);
        double maxStrike = data.Max(d => d.Strike);

        // Чтобы не упустить безубыточные точки вне диапазона страйков, расширим диапазон на 30%
        double rangeExtensionPercent = 0.3;
        double minPrice = minStrike * (1 - rangeExtensionPercent);
        double maxPrice = maxStrike * (1 + rangeExtensionPercent);

        // Шаг для анализа (примерно 0.1% от текущей цены)
        double step = currentPrice * 0.001;

        // 3. Анализируем профит/убыток продавца на разных уровнях цены
        List<(double Price, double TotalPnL, double CallPnL, double PutPnL)> pnlData = new();

        for (double price = minPrice; price <= maxPrice; price += step)
        {
            double callPnL = CalculateSellerCallPnL(data, price);
            double putPnL = CalculateSellerPutPnL(data, price);
            double totalPnL = callPnL + putPnL;

            pnlData.Add((price, totalPnL, callPnL, putPnL));
        }

        // 4. Анализируем убытки на текущей цене
        (double Price, double TotalPnL, double CallPnL, double PutPnL) currentPnL =
            pnlData.FirstOrDefault(p => Math.Abs(p.Price - currentPrice) < step / 2);
        if (currentPnL == default)
        {
            // Если точно не нашли, найдем ближайшую точку
            currentPnL = pnlData.OrderBy(p => Math.Abs(p.Price - currentPrice)).First();
        }

        // Current position card
        htmlBuilder.AppendLine("<div class='card mb-4'>");
        htmlBuilder.AppendLine("<div class='card-header'>");
        htmlBuilder.AppendLine("<h5 class='mb-0'>Позиция продавца при текущей цене</h5>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        htmlBuilder.AppendLine("<div class='row'>");

        // Total PnL at current price
        string pnlClass = currentPnL.TotalPnL >= 0 ? "text-success" : "text-danger";
        string pnlBorderClass = currentPnL.TotalPnL >= 0 ? "border-success" : "border-danger";
        string pnlIcon = currentPnL.TotalPnL >= 0 ? "bi-graph-up-arrow" : "bi-graph-down-arrow";

        htmlBuilder.AppendLine("<div class='col-md-4 mb-3'>");
        htmlBuilder.AppendLine($"<div class='card h-100 {pnlBorderClass}'>");
        htmlBuilder.AppendLine("<div class='card-body text-center'>");
        htmlBuilder.AppendLine("<h6 class='text-muted'>Общий PnL</h6>");
        htmlBuilder.AppendLine($"<h4 class='mb-0 fw-bold {pnlClass}'>");
        htmlBuilder.AppendLine($"<i class='bi {pnlIcon} me-2'></i>{currentPnL.TotalPnL:N0}</h4>");

        if (currentPnL.TotalPnL < 0)
        {
            htmlBuilder.AppendLine(
                $"<small class='text-danger'>({Math.Abs(currentPnL.TotalPnL) / totalPremium * 100:F1}% от премии)</small>");
        }

        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Call PnL at current price
        string callPnlClass = currentPnL.CallPnL >= 0 ? "text-success" : "text-danger";

        htmlBuilder.AppendLine("<div class='col-md-4 mb-3'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-info'>");
        htmlBuilder.AppendLine("<div class='card-body text-center'>");
        htmlBuilder.AppendLine("<h6 class='text-muted'>PnL от Call</h6>");
        htmlBuilder.AppendLine($"<h4 class='mb-0 fw-bold {callPnlClass}'>{currentPnL.CallPnL:N0}</h4>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Put PnL at current price
        string putPnlClass = currentPnL.PutPnL >= 0 ? "text-success" : "text-danger";

        htmlBuilder.AppendLine("<div class='col-md-4 mb-3'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-danger'>");
        htmlBuilder.AppendLine("<div class='card-body text-center'>");
        htmlBuilder.AppendLine("<h6 class='text-muted'>PnL от Put</h6>");
        htmlBuilder.AppendLine($"<h4 class='mb-0 fw-bold {putPnlClass}'>{currentPnL.PutPnL:N0}</h4>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close row

        // Status alert
        if (currentPnL.TotalPnL >= 0)
        {
            htmlBuilder.AppendLine("<div class='alert alert-success mt-3'>");
            htmlBuilder.AppendLine(
                "<i class='bi bi-check-circle-fill me-2'></i><strong>ПРОДАВЕЦ В ПРИБЫЛИ</strong> на текущем уровне цены");
            htmlBuilder.AppendLine("</div>");
        }
        else
        {
            htmlBuilder.AppendLine("<div class='alert alert-danger mt-3'>");
            htmlBuilder.AppendLine(
                "<i class='bi bi-exclamation-triangle-fill me-2'></i><strong>ПРОДАВЕЦ В УБЫТКЕ</strong> на текущем уровне цены");
            htmlBuilder.AppendLine(
                $"<p class='mb-0 mt-2'>Размер убытка: <strong>{Math.Abs(currentPnL.TotalPnL):N0}</strong> " +
                $"({Math.Abs(currentPnL.TotalPnL) / totalPremium * 100:F1}% от полученной премии)</p>");
            htmlBuilder.AppendLine("</div>");
        }

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card

        // 5. Находим безубыточные зоны (где PnL >= 0)
        List<(double LowerBound, double UpperBound)> profitZones = new();

        for (int i = 0; i < pnlData.Count - 1; i++)
        {
            if (pnlData[i].TotalPnL >= 0 && (i == 0 || pnlData[i - 1].TotalPnL < 0))
            {
                // Начало зоны прибыли
                double lowerBound = pnlData[i].Price;

                // Ищем конец зоны прибыли
                double upperBound = maxPrice;
                for (int j = i + 1; j < pnlData.Count; j++)
                {
                    if (pnlData[j].TotalPnL < 0)
                    {
                        upperBound = pnlData[j - 1].Price;
                        break;
                    }
                }

                profitZones.Add((lowerBound, upperBound));

                // Переходим к поиску следующей зоны прибыли
                while (i < pnlData.Count && pnlData[i].TotalPnL >= 0)
                {
                    i++;
                }
            }
        }

        // 7. Находим нижнюю и верхнюю точки безубытка (ближайшие к текущей цене)
        // Сначала находим все точки безубытка (переходы через 0)
        List<double> breakEvenPoints = new();

        for (int i = 0; i < pnlData.Count - 1; i++)
        {
            // Если PnL меняет знак между соседними точками - это точка безубытка
            if ((pnlData[i].TotalPnL >= 0 && pnlData[i + 1].TotalPnL < 0) ||
                (pnlData[i].TotalPnL < 0 && pnlData[i + 1].TotalPnL >= 0))
            {
                // Линейная интерполяция для нахождения точного значения
                double pnl1 = pnlData[i].TotalPnL;
                double pnl2 = pnlData[i + 1].TotalPnL;
                double price1 = pnlData[i].Price;
                double price2 = pnlData[i + 1].Price;

                // Формула линейной интерполяции: price = price1 + (0 - pnl1) * (price2 - price1) / (pnl2 - pnl1)
                double breakEvenPrice = price1 + (0 - pnl1) * (price2 - price1) / (pnl2 - pnl1);
                breakEvenPoints.Add(breakEvenPrice);
            }
        }

        // Сортируем точки безубытка
        breakEvenPoints.Sort();

        // 6. Находим точки максимальной прибыли и максимального убытка
        (double Price, double TotalPnL, double CallPnL, double PutPnL) maxProfitPoint =
            pnlData.OrderByDescending(p => p.TotalPnL).First();
        (double Price, double TotalPnL, double CallPnL, double PutPnL) maxLossPoint =
            pnlData.OrderBy(p => p.TotalPnL).First();

        // Key Price Levels card
        htmlBuilder.AppendLine("<div class='card mb-4'>");
        htmlBuilder.AppendLine("<div class='card-header'>");
        htmlBuilder.AppendLine("<h5 class='mb-0'>Ключевые ценовые уровни</h5>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        htmlBuilder.AppendLine("<div class='row'>");

        // Maximum Profit Point
        htmlBuilder.AppendLine("<div class='col-md-6 mb-3'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-success'>");
        htmlBuilder.AppendLine("<div class='card-header bg-success text-white'>");
        htmlBuilder.AppendLine(
            "<h6 class='mb-0'><i class='bi bi-graph-up-arrow me-2'></i>Точка максимальной прибыли</h6>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");
        htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
        htmlBuilder.AppendLine("<span>Уровень цены:</span>");
        htmlBuilder.AppendLine($"<strong>{maxProfitPoint.Price:F2}</strong>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
        htmlBuilder.AppendLine("<span>Максимальная прибыль:</span>");
        htmlBuilder.AppendLine($"<strong class='text-success'>{maxProfitPoint.TotalPnL:N0}</strong>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Maximum Loss Point
        htmlBuilder.AppendLine("<div class='col-md-6 mb-3'>");
        htmlBuilder.AppendLine("<div class='card h-100 border-danger'>");
        htmlBuilder.AppendLine("<div class='card-header bg-danger text-white'>");
        htmlBuilder.AppendLine(
            "<h6 class='mb-0'><i class='bi bi-graph-down-arrow me-2'></i>Точка максимального убытка</h6>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");
        htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
        htmlBuilder.AppendLine("<span>Уровень цены:</span>");
        htmlBuilder.AppendLine($"<strong>{maxLossPoint.Price:F2}</strong>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
        htmlBuilder.AppendLine("<span>Максимальный убыток:</span>");
        htmlBuilder.AppendLine($"<strong class='text-danger'>{maxLossPoint.TotalPnL:N0}</strong>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close row

        // Break-even points section
        htmlBuilder.AppendLine("<h5 class='mt-4 mb-3'>Точки безубытка (Zero-Cross Levels)</h5>");

        if (breakEvenPoints.Count == 0)
        {
            htmlBuilder.AppendLine("<div class='alert alert-warning'>");
            htmlBuilder.AppendLine(
                "<i class='bi bi-exclamation-circle me-2'></i>Точек безубытка не найдено в анализируемом диапазоне цен.");
            htmlBuilder.AppendLine("</div>");
        }
        else
        {
            // Table of break-even points
            htmlBuilder.AppendLine("<div class='table-responsive'>");
            htmlBuilder.AppendLine("<table class='table table-bordered table-striped'>");
            htmlBuilder.AppendLine("<thead class='table-light'>");
            htmlBuilder.AppendLine("<tr>");
            htmlBuilder.AppendLine("<th>#</th>");
            htmlBuilder.AppendLine("<th>Уровень цены</th>");
            htmlBuilder.AppendLine("<th>Отклонение от текущей цены</th>");
            htmlBuilder.AppendLine("<th>Процентное отклонение</th>");
            htmlBuilder.AppendLine("</tr>");
            htmlBuilder.AppendLine("</thead>");
            htmlBuilder.AppendLine("<tbody>");

            for (int i = 0; i < breakEvenPoints.Count; i++)
            {
                double point = breakEvenPoints[i];
                double deviation = point - currentPrice;
                double percentDeviation = deviation / currentPrice * 100;
                string deviationClass = deviation >= 0 ? "text-success" : "text-danger";

                htmlBuilder.AppendLine("<tr>");
                htmlBuilder.AppendLine($"<td>{i + 1}</td>");
                htmlBuilder.AppendLine($"<td><strong>{point:F2}</strong></td>");
                htmlBuilder.AppendLine($"<td class='{deviationClass}'>{deviation:F2}</td>");
                htmlBuilder.AppendLine($"<td class='{deviationClass}'>{percentDeviation:F2}%</td>");
                htmlBuilder.AppendLine("</tr>");
            }

            htmlBuilder.AppendLine("</tbody>");
            htmlBuilder.AppendLine("</table>");
            htmlBuilder.AppendLine("</div>");

            // Находим ближайшие точки безубытка снизу и сверху от текущей цены
            double? lowerBreakEven = breakEvenPoints.Where(p => p < currentPrice).DefaultIfEmpty(double.NaN).Max();
            double? upperBreakEven = breakEvenPoints.Where(p => p > currentPrice).DefaultIfEmpty(double.NaN).Min();

            htmlBuilder.AppendLine("<div class='row mt-4'>");

            // Ближайшая нижняя точка безубытка
            htmlBuilder.AppendLine("<div class='col-md-6 mb-3'>");
            if (!double.IsNaN((double)lowerBreakEven))
            {
                double lowerDeviation = (double)lowerBreakEven - currentPrice;
                double lowerPercentDeviation = lowerDeviation / currentPrice * 100;

                htmlBuilder.AppendLine("<div class='card border-primary h-100'>");
                htmlBuilder.AppendLine("<div class='card-header bg-primary text-white'>");
                htmlBuilder.AppendLine(
                    "<h6 class='mb-0'><i class='bi bi-arrow-down me-2'></i>Нижняя точка безубытка</h6>");
                htmlBuilder.AppendLine("</div>");
                htmlBuilder.AppendLine("<div class='card-body'>");
                htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
                htmlBuilder.AppendLine("<span>Уровень цены:</span>");
                htmlBuilder.AppendLine($"<strong>{lowerBreakEven:F2}</strong>");
                htmlBuilder.AppendLine("</div>");
                htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
                htmlBuilder.AppendLine("<span>Отклонение:</span>");
                htmlBuilder.AppendLine(
                    $"<strong class='text-danger'>{lowerDeviation:F2} ({lowerPercentDeviation:F2}%)</strong>");
                htmlBuilder.AppendLine("</div>");
                htmlBuilder.AppendLine("</div>");
                htmlBuilder.AppendLine("</div>");
            }
            else
            {
                htmlBuilder.AppendLine("<div class='alert alert-secondary h-100'>");
                htmlBuilder.AppendLine(
                    "<i class='bi bi-info-circle me-2'></i>Нижняя точка безубытка не найдена в анализируемом диапазоне.");
                htmlBuilder.AppendLine("</div>");
            }

            htmlBuilder.AppendLine("</div>");

            // Ближайшая верхняя точка безубытка
            htmlBuilder.AppendLine("<div class='col-md-6 mb-3'>");
            if (!double.IsNaN((double)upperBreakEven))
            {
                double upperDeviation = (double)upperBreakEven - currentPrice;
                double upperPercentDeviation = upperDeviation / currentPrice * 100;

                htmlBuilder.AppendLine("<div class='card border-primary h-100'>");
                htmlBuilder.AppendLine("<div class='card-header bg-primary text-white'>");
                htmlBuilder.AppendLine(
                    "<h6 class='mb-0'><i class='bi bi-arrow-up me-2'></i>Верхняя точка безубытка</h6>");
                htmlBuilder.AppendLine("</div>");
                htmlBuilder.AppendLine("<div class='card-body'>");
                htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
                htmlBuilder.AppendLine("<span>Уровень цены:</span>");
                htmlBuilder.AppendLine($"<strong>{upperBreakEven:F2}</strong>");
                htmlBuilder.AppendLine("</div>");
                htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
                htmlBuilder.AppendLine("<span>Отклонение:</span>");
                htmlBuilder.AppendLine(
                    $"<strong class='text-success'>{upperDeviation:F2} ({upperPercentDeviation:F2}%)</strong>");
                htmlBuilder.AppendLine("</div>");
                htmlBuilder.AppendLine("</div>");
                htmlBuilder.AppendLine("</div>");
            }
            else
            {
                htmlBuilder.AppendLine("<div class='alert alert-secondary h-100'>");
                htmlBuilder.AppendLine(
                    "<i class='bi bi-info-circle me-2'></i>Верхняя точка безубытка не найдена в анализируемом диапазоне.");
                htmlBuilder.AppendLine("</div>");
            }

            htmlBuilder.AppendLine("</div>");

            htmlBuilder.AppendLine("</div>"); // Close row

            // Potential price range
            if (!double.IsNaN((double)lowerBreakEven) && !double.IsNaN((double)upperBreakEven))
            {
                double rangeWidth = (double)upperBreakEven - (double)lowerBreakEven;
                double rangePercent = rangeWidth / currentPrice * 100;

                htmlBuilder.AppendLine("<div class='alert alert-primary mt-3 mb-3'>");
                htmlBuilder.AppendLine(
                    "<h5><i class='bi bi-arrows-expand me-2'></i>Вероятный диапазон движения цены</h5>");
                htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
                htmlBuilder.AppendLine(
                    $"<span>Диапазон: <strong>{lowerBreakEven:F2} - {upperBreakEven:F2}</strong></span>");
                htmlBuilder.AppendLine($"<span>Ширина: <strong>{rangeWidth:F2} ({rangePercent:F2}%)</strong></span>");
                htmlBuilder.AppendLine("</div>");

                // Находим преобладающую силу: растущую или падающую
                double distanceToLower = currentPrice - (double)lowerBreakEven;
                double distanceToUpper = (double)upperBreakEven - currentPrice;

                if (distanceToLower * 1.2 < distanceToUpper)
                {
                    htmlBuilder.AppendLine("<div class='alert alert-warning mt-2 mb-0'>");
                    htmlBuilder.AppendLine(
                        "<i class='bi bi-exclamation-triangle-fill me-2'></i><strong>ВНИМАНИЕ:</strong> Текущая цена значительно ближе к нижней точке безубытка.");
                    htmlBuilder.AppendLine(
                        "<p class='mb-0'>Это указывает на повышенный риск пробоя вниз и возможное значительное падение цены.</p>");
                    htmlBuilder.AppendLine("</div>");
                }
                else if (distanceToUpper * 1.2 < distanceToLower)
                {
                    htmlBuilder.AppendLine("<div class='alert alert-warning mt-2 mb-0'>");
                    htmlBuilder.AppendLine(
                        "<i class='bi bi-exclamation-triangle-fill me-2'></i><strong>ВНИМАНИЕ:</strong> Текущая цена значительно ближе к верхней точке безубытка.");
                    htmlBuilder.AppendLine(
                        "<p class='mb-0'>Это указывает на повышенный риск пробоя вверх и возможное значительное повышение цены.</p>");
                    htmlBuilder.AppendLine("</div>");
                }
                else
                {
                    htmlBuilder.AppendLine("<div class='alert alert-info mt-2 mb-0'>");
                    htmlBuilder.AppendLine(
                        "<i class='bi bi-info-circle-fill me-2'></i>Текущая цена находится в относительно сбалансированном положении между точками безубытка.");
                    htmlBuilder.AppendLine("</div>");
                }

                htmlBuilder.AppendLine("</div>");
            }
        }

        // Profit Zones section
        htmlBuilder.AppendLine("<h5 class='mt-4 mb-3'>Безубыточные зоны для продавца</h5>");

        if (profitZones.Count == 0)
        {
            htmlBuilder.AppendLine("<div class='alert alert-danger'>");
            htmlBuilder.AppendLine(
                "<i class='bi bi-exclamation-triangle-fill me-2'></i>Продавец в убытке на всём рассматриваемом диапазоне цен.");
            htmlBuilder.AppendLine("</div>");
        }
        else
        {
            // Table of profit zones
            htmlBuilder.AppendLine("<div class='table-responsive'>");
            htmlBuilder.AppendLine("<table class='table table-bordered table-success'>");
            htmlBuilder.AppendLine("<thead class='table-success'>");
            htmlBuilder.AppendLine("<tr>");
            htmlBuilder.AppendLine("<th>#</th>");
            htmlBuilder.AppendLine("<th>Нижняя граница</th>");
            htmlBuilder.AppendLine("<th>Верхняя граница</th>");
            htmlBuilder.AppendLine("<th>Ширина диапазона</th>");
            htmlBuilder.AppendLine("<th>% от текущей цены</th>");
            htmlBuilder.AppendLine("</tr>");
            htmlBuilder.AppendLine("</thead>");
            htmlBuilder.AppendLine("<tbody>");

            for (int i = 0; i < profitZones.Count; i++)
            {
                var zone = profitZones[i];
                double zoneWidth = zone.UpperBound - zone.LowerBound;
                double zonePercentWidth = zoneWidth / currentPrice * 100;

                string rowClass = "";
                if (currentPrice >= zone.LowerBound && currentPrice <= zone.UpperBound)
                {
                    rowClass = "table-active";
                }

                htmlBuilder.AppendLine($"<tr class='{rowClass}'>");
                htmlBuilder.AppendLine($"<td>{i + 1}</td>");
                htmlBuilder.AppendLine($"<td>{zone.LowerBound:F2}</td>");
                htmlBuilder.AppendLine($"<td>{zone.UpperBound:F2}</td>");
                htmlBuilder.AppendLine($"<td>{zoneWidth:F2}</td>");
                htmlBuilder.AppendLine($"<td>{zonePercentWidth:F2}%</td>");
                htmlBuilder.AppendLine("</tr>");
            }

            htmlBuilder.AppendLine("</tbody>");
            htmlBuilder.AppendLine("</table>");
            htmlBuilder.AppendLine("</div>");

            // If seller is currently at a loss, show closest profit zone
            if (currentPnL.TotalPnL < 0)
            {
                // Находим ближайшую зону безубытка
                var closest = profitZones
                    .Select(zone => new
                    {
                        Zone = zone,
                        Distance = Math.Min(
                            Math.Abs(zone.LowerBound - currentPrice),
                            Math.Abs(zone.UpperBound - currentPrice)
                        )
                    })
                    .OrderBy(x => x.Distance)
                    .First();

                htmlBuilder.AppendLine("<div class='alert alert-warning mt-3'>");
                htmlBuilder.AppendLine(
                    "<i class='bi bi-info-circle-fill me-2'></i>Продавец опционов сейчас в убытке, что создает давление на движение цены к ближайшей зоне безубытка.");

                if (closest.Zone.LowerBound <= currentPrice && closest.Zone.UpperBound >= currentPrice)
                {
                    htmlBuilder.AppendLine("<p class='mb-0'>Текущая цена находится внутри зоны безубытка.</p>");
                }
                else if (Math.Abs(closest.Zone.LowerBound - currentPrice) <
                         Math.Abs(closest.Zone.UpperBound - currentPrice))
                {
                    htmlBuilder.AppendLine(
                        $"<p class='mb-0'>Ближайший уровень безубытка находится снизу на уровне <strong>{closest.Zone.LowerBound:F2}</strong>. Возможно давление на цену в сторону снижения.</p>");
                }
                else
                {
                    htmlBuilder.AppendLine(
                        $"<p class='mb-0'>Ближайший уровень безубытка находится сверху на уровне <strong>{closest.Zone.UpperBound:F2}</strong>. Возможно давление на цену в сторону повышения.</p>");
                }

                htmlBuilder.AppendLine("</div>");
            }
        }

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card

        // Delta/Gamma Exposure Analysis
        if (!data.All(x => x.CallDelta == 0 || x.PutDelta == 0))
        {
            double totalCallDelta = 0.0, totalPutDelta = 0.0;
            double totalCallGamma = 0.0, totalPutGamma = 0.0;

            foreach (OptionData option in data)
            {
                totalCallDelta += option.CallDelta * option.CallOi;
                totalPutDelta += option.PutDelta * option.PutOi;
                totalCallGamma += option.CallGamma * option.CallOi;
                totalPutGamma += option.PutGamma * option.PutOi;
            }

            double totalDeltaSellers = -(totalCallDelta + totalPutDelta);
            double totalGammaSellers = -(totalCallGamma + totalPutGamma);

            htmlBuilder.AppendLine("<div class='card mb-4'>");
            htmlBuilder.AppendLine("<div class='card-header bg-info text-white'>");
            htmlBuilder.AppendLine("<h5 class='mb-0'>Delta/Gamma Exposure Анализ</h5>");
            htmlBuilder.AppendLine("</div>");
            htmlBuilder.AppendLine("<div class='card-body'>");

            htmlBuilder.AppendLine("<div class='row'>");

            // Delta Exposure
            htmlBuilder.AppendLine("<div class='col-md-6'>");
            htmlBuilder.AppendLine("<div class='card h-100'>");
            htmlBuilder.AppendLine("<div class='card-header'>Delta Exposure</div>");
            htmlBuilder.AppendLine("<div class='card-body'>");

            string deltaClass = totalDeltaSellers >= 0 ? "text-success" : "text-danger";
            string deltaIcon = totalDeltaSellers >= 0 ? "bi-arrow-up-circle-fill" : "bi-arrow-down-circle-fill";

            htmlBuilder.AppendLine("<h4 class='text-center mb-4'>");
            htmlBuilder.AppendLine($"<i class='bi {deltaIcon} me-2'></i>");
            htmlBuilder.AppendLine($"<span class='{deltaClass}'>{totalDeltaSellers:N2}</span>");
            htmlBuilder.AppendLine("</h4>");

            htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
            htmlBuilder.AppendLine("<span>Call Delta:</span>");
            htmlBuilder.AppendLine($"<strong>{totalCallDelta:N2}</strong>");
            htmlBuilder.AppendLine("</div>");

            htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
            htmlBuilder.AppendLine("<span>Put Delta:</span>");
            htmlBuilder.AppendLine($"<strong>{totalPutDelta:N2}</strong>");
            htmlBuilder.AppendLine("</div>");

            // Interpretation for Delta
            htmlBuilder.AppendLine("<div class='alert alert-light mt-3 mb-0'>");
            if (totalDeltaSellers > 0)
            {
                htmlBuilder.AppendLine(
                    "<small>Положительная дельта означает, что продавцы опционов в целом выигрывают от роста цены актива.</small>");
            }
            else if (totalDeltaSellers < 0)
            {
                htmlBuilder.AppendLine(
                    "<small>Отрицательная дельта означает, что продавцы опционов в целом выигрывают от падения цены актива.</small>");
            }
            else
            {
                htmlBuilder.AppendLine(
                    "<small>Нейтральная дельта означает, что продавцы опционов в целом индифферентны к направлению движения цены.</small>");
            }

            htmlBuilder.AppendLine("</div>");

            htmlBuilder.AppendLine("</div>"); // Close card-body
            htmlBuilder.AppendLine("</div>"); // Close card
            htmlBuilder.AppendLine("</div>"); // Close col

            // Gamma Exposure
            htmlBuilder.AppendLine("<div class='col-md-6'>");
            htmlBuilder.AppendLine("<div class='card h-100'>");
            htmlBuilder.AppendLine("<div class='card-header'>Gamma Exposure</div>");
            htmlBuilder.AppendLine("<div class='card-body'>");

            string gammaClass = totalGammaSellers >= 0 ? "text-success" : "text-danger";

            htmlBuilder.AppendLine("<h4 class='text-center mb-4'>");
            htmlBuilder.AppendLine($"<span class='{gammaClass}'>{totalGammaSellers:N2}</span>");
            htmlBuilder.AppendLine("</h4>");

            htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
            htmlBuilder.AppendLine("<span>Call Gamma:</span>");
            htmlBuilder.AppendLine($"<strong>{totalCallGamma:N2}</strong>");
            htmlBuilder.AppendLine("</div>");

            htmlBuilder.AppendLine("<div class='d-flex justify-content-between'>");
            htmlBuilder.AppendLine("<span>Put Gamma:</span>");
            htmlBuilder.AppendLine($"<strong>{totalPutGamma:N2}</strong>");
            htmlBuilder.AppendLine("</div>");

            // Interpretation for Gamma
            htmlBuilder.AppendLine("<div class='alert alert-light mt-3 mb-0'>");
            if (totalGammaSellers > 0)
            {
                htmlBuilder.AppendLine(
                    "<small>Положительная гамма означает, что продавцы опционов выигрывают от высокой волатильности и резких движений цены.</small>");
            }
            else if (totalGammaSellers < 0)
            {
                htmlBuilder.AppendLine(
                    "<small>Отрицательная гамма означает, что продавцы опционов проигрывают от высокой волатильности и выигрывают от стабильной цены.</small>");
            }
            else
            {
                htmlBuilder.AppendLine(
                    "<small>Нейтральная гамма означает, что продавцы опционов в целом индифферентны к волатильности актива.</small>");
            }

            htmlBuilder.AppendLine("</div>");

            htmlBuilder.AppendLine("</div>"); // Close card-body
            htmlBuilder.AppendLine("</div>"); // Close card
            htmlBuilder.AppendLine("</div>"); // Close col

            htmlBuilder.AppendLine("</div>"); // Close row

            htmlBuilder.AppendLine("</div>"); // Close card-body
            htmlBuilder.AppendLine("</div>"); // Close card
        }

        // Interpretation and Recommendation section
        htmlBuilder.AppendLine("<div class='card mb-4'>");
        htmlBuilder.AppendLine("<div class='card-header bg-primary text-white'>");
        htmlBuilder.AppendLine("<h5 class='mb-0'>Интерпретация и рекомендации</h5>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        // Main interpretation based on break-even points
        if (breakEvenPoints.Count >= 2)
        {
            // Найдем ближайшие точки безубытка
            double? lowerBreakEven = breakEvenPoints.Where(p => p < currentPrice).DefaultIfEmpty(double.NaN).Max();
            double? upperBreakEven = breakEvenPoints.Where(p => p > currentPrice).DefaultIfEmpty(double.NaN).Min();

            if (!double.IsNaN((double)lowerBreakEven) && !double.IsNaN((double)upperBreakEven))
            {
                // Определим, в какой части диапазона между точками безубытка находится текущая цена
                double rangePosition = (currentPrice - (double)lowerBreakEven) /
                                       ((double)upperBreakEven - (double)lowerBreakEven);

                htmlBuilder.AppendLine("<div class='alert alert-primary'>");

                if (rangePosition < 0.2)
                {
                    htmlBuilder.AppendLine(
                        "<p><i class='bi bi-info-circle-fill me-2'></i><strong>Позиция в диапазоне:</strong> Текущая цена находится очень близко к нижней точке безубытка.</p>");
                    htmlBuilder.AppendLine(
                        "<p><strong>Рекомендация:</strong> Существует высокая вероятность отскока вверх или пробоя вниз. Возможны среднесрочные длинные позиции с защитным стоп-лоссом ниже точки безубытка.</p>");
                }
                else if (rangePosition > 0.8)
                {
                    htmlBuilder.AppendLine(
                        "<p><i class='bi bi-info-circle-fill me-2'></i><strong>Позиция в диапазоне:</strong> Текущая цена находится очень близко к верхней точке безубытка.</p>");
                    htmlBuilder.AppendLine(
                        "<p><strong>Рекомендация:</strong> Существует высокая вероятность отскока вниз или пробоя вверх. Возможны среднесрочные короткие позиции с защитным стоп-лоссом выше точки безубытка.</p>");
                }
                else if (rangePosition >= 0.4 && rangePosition <= 0.6)
                {
                    htmlBuilder.AppendLine(
                        "<p><i class='bi bi-info-circle-fill me-2'></i><strong>Позиция в диапазоне:</strong> Текущая цена находится примерно в середине диапазона между точками безубытка.</p>");
                    htmlBuilder.AppendLine(
                        "<p><strong>Рекомендация:</strong> Боковое движение наиболее вероятно. Рассмотрите стратегии диапазонной торговли между точками безубытка.</p>");
                }
                else if (rangePosition < 0.4)
                {
                    htmlBuilder.AppendLine(
                        "<p><i class='bi bi-info-circle-fill me-2'></i><strong>Позиция в диапазоне:</strong> Текущая цена находится ближе к нижней точке безубытка.</p>");
                    htmlBuilder.AppendLine(
                        "<p><strong>Рекомендация:</strong> Повышенная вероятность движения вверх в среднесрочной перспективе. Предпочтительны длинные позиции.</p>");
                }
                else // rangePosition > 0.6
                {
                    htmlBuilder.AppendLine(
                        "<p><i class='bi bi-info-circle-fill me-2'></i><strong>Позиция в диапазоне:</strong> Текущая цена находится ближе к верхней точке безубытка.</p>");
                    htmlBuilder.AppendLine(
                        "<p><strong>Рекомендация:</strong> Повышенная вероятность движения вниз в среднесрочной перспективе. Предпочтительны короткие позиции.</p>");
                }

                htmlBuilder.AppendLine("</div>");
            }
        }

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card

        htmlBuilder.AppendLine("</div>"); // Close main div
        htmlBuilder.AppendLine("</article>"); // Close article

        return htmlBuilder.ToString();
    }

        


    private static string AnalyzePainGradientHtml(List<OptionData> data, double maxPainStrike, double currentPrice)
    {
        var htmlBuilder = new StringBuilder();

        htmlBuilder.AppendLine("<div class='pain-gradient-analysis mt-4'>");
        htmlBuilder.AppendLine("<h5>АНАЛИЗ СКОРОСТИ ПРИРОСТА УБЫТКОВ</h5>");

        // Определяем шаг для расчета скорости изменения (например, 1% от maxPainStrike)
        double step = maxPainStrike * 0.01;

        // 1. АНАЛИЗ ОТНОСИТЕЛЬНО MAX PAIN
        htmlBuilder.AppendLine("<div class='card mb-3'>");
        htmlBuilder.AppendLine("<div class='card-header bg-light'><h6>1. АНАЛИЗ ОТНОСИТЕЛЬНО MAX PAIN</h6></div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        // Рассчитываем убытки на уровне MaxPain
        double maxPainLosses = CalculateTotalLossesAtPrice(data, maxPainStrike);
        double callLossesAtMaxPain = CalculateCallLossesAtPrice(data, maxPainStrike);
        double putLossesAtMaxPain = CalculatePutLossesAtPrice(data, maxPainStrike);

        htmlBuilder.AppendLine("<div class='mb-3'>");
        htmlBuilder.AppendLine($"<p class='mb-1'>Убытки на уровне Max Pain ({maxPainStrike:F2}):</p>");
        htmlBuilder.AppendLine("<ul class='list-group'>");
        htmlBuilder.AppendLine($"<li class='list-group-item'>Общие убытки: <strong>{maxPainLosses:N0}</strong></li>");
        htmlBuilder.AppendLine(
            $"<li class='list-group-item'>Убытки Call опционов: <strong>{callLossesAtMaxPain:N0}</strong></li>");
        htmlBuilder.AppendLine(
            $"<li class='list-group-item'>Убытки Put опционов: <strong>{putLossesAtMaxPain:N0}</strong></li>");
        htmlBuilder.AppendLine("</ul>");
        htmlBuilder.AppendLine("</div>");

        // Рассчитываем убытки выше и ниже MaxPain
        double upLevel = maxPainStrike + step;
        double totalLossesUp = CalculateTotalLossesAtPrice(data, upLevel);
        double callLossesUp = CalculateCallLossesAtPrice(data, upLevel);
        double putLossesUp = CalculatePutLossesAtPrice(data, upLevel);

        double downLevel = maxPainStrike - step;
        double totalLossesDown = CalculateTotalLossesAtPrice(data, downLevel);
        double callLossesDown = CalculateCallLossesAtPrice(data, downLevel);
        double putLossesDown = CalculatePutLossesAtPrice(data, downLevel);

        // Расчет градиентов относительно MaxPain
        double totalGradientUp = (totalLossesUp - maxPainLosses) / step;
        double totalGradientDown = (totalLossesDown - maxPainLosses) / step;
        double callGradientUp = (callLossesUp - callLossesAtMaxPain) / step;
        double callGradientDown = (callLossesDown - callLossesAtMaxPain) / step;
        double putGradientUp = (putLossesUp - putLossesAtMaxPain) / step;
        double putGradientDown = (putLossesDown - putLossesAtMaxPain) / step;

        htmlBuilder.AppendLine("<div class='mb-3'>");
        htmlBuilder.AppendLine("<p class='fw-bold mb-2'>ИЗМЕНЕНИЕ УБЫТКОВ ПРИ ДВИЖЕНИИ ЦЕНЫ ОТ MAX PAIN:</p>");

        // Движение вверх
        htmlBuilder.AppendLine("<div class='card mb-2'>");
        htmlBuilder.AppendLine("<div class='card-header bg-light text-primary'>При движении ВВЕРХ на " +
                               $"{step:F2} пунктов от Max Pain:</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");
        htmlBuilder.AppendLine($"<p>Общие убытки: <strong>+{totalLossesUp - maxPainLosses:N0}</strong> " +
                               $"(скорость: <strong>{totalGradientUp:N0}</strong> на пункт)</p>");
        htmlBuilder.AppendLine($"<p>Убытки Call: <strong>+{callLossesUp - callLossesAtMaxPain:N0}</strong> " +
                               $"(скорость: <strong>{callGradientUp:N0}</strong> на пункт)</p>");

        string putSignUp = (putLossesUp - putLossesAtMaxPain >= 0) ? "+" : "";
        htmlBuilder.AppendLine($"<p>Убытки Put: <strong>{putSignUp}{putLossesUp - putLossesAtMaxPain:N0}</strong> " +
                               $"(скорость: <strong>{putGradientUp:N0}</strong> на пункт)</p>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Движение вниз
        htmlBuilder.AppendLine("<div class='card mb-2'>");
        htmlBuilder.AppendLine("<div class='card-header bg-light text-danger'>При движении ВНИЗ на " +
                               $"{step:F2} пунктов от Max Pain:</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");
        htmlBuilder.AppendLine($"<p>Общие убытки: <strong>+{totalLossesDown - maxPainLosses:N0}</strong> " +
                               $"(скорость: <strong>{totalGradientDown:N0}</strong> на пункт)</p>");

        string callSignDown = (callLossesDown - callLossesAtMaxPain >= 0) ? "+" : "";
        htmlBuilder.AppendLine(
            $"<p>Убытки Call: <strong>{callSignDown}{callLossesDown - callLossesAtMaxPain:N0}</strong> " +
            $"(скорость: <strong>{callGradientDown:N0}</strong> на пункт)</p>");
        htmlBuilder.AppendLine($"<p>Убытки Put: <strong>+{putLossesDown - putLossesAtMaxPain:N0}</strong> " +
                               $"(скорость: <strong>{putGradientDown:N0}</strong> на пункт)</p>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close изменение убытков div

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card

        // 2. АНАЛИЗ ОТНОСИТЕЛЬНО ТЕКУЩЕЙ ЦЕНЫ
        htmlBuilder.AppendLine("<div class='card mb-3'>");
        htmlBuilder.AppendLine("<div class='card-header bg-light'><h6>2. АНАЛИЗ ОТНОСИТЕЛЬНО ТЕКУЩЕЙ ЦЕНЫ</h6></div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        // Рассчитываем убытки на уровне текущей цены
        double currentPriceLosses = CalculateTotalLossesAtPrice(data, currentPrice);
        double callLossesAtCurrent = CalculateCallLossesAtPrice(data, currentPrice);
        double putLossesAtCurrent = CalculatePutLossesAtPrice(data, currentPrice);

        // Рассчитываем убытки выше текущей цены
        double currentUpLevel = currentPrice + step;
        double totalLossesCurrentUp = CalculateTotalLossesAtPrice(data, currentUpLevel);
        double callLossesCurrentUp = CalculateCallLossesAtPrice(data, currentUpLevel);
        double putLossesCurrentUp = CalculatePutLossesAtPrice(data, currentUpLevel);

        // Рассчитываем убытки ниже текущей цены
        double currentDownLevel = currentPrice - step;
        double totalLossesCurrentDown = CalculateTotalLossesAtPrice(data, currentDownLevel);
        double callLossesCurrentDown = CalculateCallLossesAtPrice(data, currentDownLevel);
        double putLossesCurrentDown = CalculatePutLossesAtPrice(data, currentDownLevel);

        // Расчет градиентов относительно текущей цены
        double totalGradientCurrentUp = (totalLossesCurrentUp - currentPriceLosses) / step;
        double totalGradientCurrentDown = (totalLossesCurrentDown - currentPriceLosses) / step;
        double callGradientCurrentUp = (callLossesCurrentUp - callLossesAtCurrent) / step;
        double callGradientCurrentDown = (callLossesCurrentDown - callLossesAtCurrent) / step;
        double putGradientCurrentUp = (putLossesCurrentUp - putLossesAtCurrent) / step;
        double putGradientCurrentDown = (putLossesCurrentDown - putLossesAtCurrent) / step;

        // Движение вверх от текущей цены
        htmlBuilder.AppendLine("<div class='card mb-2'>");
        htmlBuilder.AppendLine("<div class='card-header bg-light text-primary'>При движении ВВЕРХ на " +
                               $"{step:F2} пунктов от текущей цены:</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        string totalSignCurrUp = totalLossesCurrentUp - currentPriceLosses >= 0 ? "+" : "";
        htmlBuilder.AppendLine(
            $"<p>Общие убытки: <strong>{totalSignCurrUp}{totalLossesCurrentUp - currentPriceLosses:N0}</strong> " +
            $"(скорость: <strong>{totalGradientCurrentUp:N0}</strong> на пункт)</p>");

        string callSignCurrUp = (callLossesCurrentUp - callLossesAtCurrent >= 0) ? "+" : "";
        htmlBuilder.AppendLine(
            $"<p>Убытки Call: <strong>{callSignCurrUp}{callLossesCurrentUp - callLossesAtCurrent:N0}</strong> " +
            $"(скорость: <strong>{callGradientCurrentUp:N0}</strong> на пункт)</p>");

        string putSignCurrUp = (putLossesCurrentUp - putLossesAtCurrent >= 0) ? "+" : "";
        htmlBuilder.AppendLine(
            $"<p>Убытки Put: <strong>{putSignCurrUp}{putLossesCurrentUp - putLossesAtCurrent:N0}</strong> " +
            $"(скорость: <strong>{putGradientCurrentUp:N0}</strong> на пункт)</p>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        // Движение вниз от текущей цены
        htmlBuilder.AppendLine("<div class='card mb-2'>");
        htmlBuilder.AppendLine("<div class='card-header bg-light text-danger'>При движении ВНИЗ на " +
                               $"{step:F2} пунктов от текущей цены:</div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        string totalSignCurrDown = (totalLossesCurrentDown - currentPriceLosses >= 0) ? "+" : "";
        htmlBuilder.AppendLine(
            $"<p>Общие убытки: <strong>{totalSignCurrDown}{totalLossesCurrentDown - currentPriceLosses:N0}</strong> " +
            $"(скорость: <strong>{totalGradientCurrentDown:N0}</strong> на пункт)</p>");

        string callSignCurrDown = (callLossesCurrentDown - callLossesAtCurrent >= 0) ? "+" : "";
        htmlBuilder.AppendLine(
            $"<p>Убытки Call: <strong>{callSignCurrDown}{callLossesCurrentDown - callLossesAtCurrent:N0}</strong> " +
            $"(скорость: <strong>{callGradientCurrentDown:N0}</strong> на пункт)</p>");

        string putSignCurrDown = (putLossesCurrentDown - putLossesAtCurrent >= 0) ? "+" : "";
        htmlBuilder.AppendLine(
            $"<p>Убытки Put: <strong>{putSignCurrDown}{putLossesCurrentDown - putLossesAtCurrent:N0}</strong> " +
            $"(скорость: <strong>{putGradientCurrentDown:N0}</strong> на пункт)</p>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card

        // 3. АНАЛИЗ ВЕРОЯТНОГО ДИАПАЗОНА И РАВНОВЕСИЯ
        htmlBuilder.AppendLine("<div class='card mb-3'>");
        htmlBuilder.AppendLine("<div class='card-header bg-light'><h6>3. ДИАПАЗОНЫ И УРОВНИ РАВНОВЕСИЯ</h6></div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        double painThresholdPercent = 0.10; // 10% прирост убытков как порог
        double painThreshold = maxPainLosses * painThresholdPercent;

        // Находим примерные границы, где убытки вырастут на 10%
        double upperBound = EstimateThresholdLevel(data, maxPainStrike, maxPainLosses, painThreshold, step, true);
        double lowerBound = EstimateThresholdLevel(data, maxPainStrike, maxPainLosses, painThreshold, step, false);

        double equilibriumGradients = CalculateGradientEquilibriumLevel(data, maxPainStrike, step);

        htmlBuilder.AppendLine(
            $"<p>При увеличении убытков на <strong>{painThresholdPercent * 100}%</strong> от минимального уровня:</p>");

        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-bordered'>");
        htmlBuilder.AppendLine("<tbody>");
        htmlBuilder.AppendLine(
            $"<tr><td>Вероятный верхний уровень диапазона цены</td><td><strong>{upperBound:F2}</strong></td></tr>");
        htmlBuilder.AppendLine(
            $"<tr><td>Вероятный нижний уровень диапазона цены</td><td><strong>{lowerBound:F2}</strong></td></tr>");
        htmlBuilder.AppendLine(
            $"<tr><td>Ожидаемый диапазон движения цены</td><td><strong>{lowerBound:F2} - {upperBound:F2}</strong> " +
            $"(<strong>{(upperBound - lowerBound) / maxPainStrike * 100:F2}%</strong> от Max Pain)</td></tr>");
        htmlBuilder.AppendLine(
            $"<tr><td>Уровень равновесия по скорости прироста убытков</td><td><strong>{equilibriumGradients:F2}</strong></td></tr>");
        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>");

        // Положение текущей цены относительно равновесия
        htmlBuilder.AppendLine("<div class='mt-3'>");
        htmlBuilder.AppendLine("<h6>Положение текущей цены относительно уровней:</h6>");

        string deviationSign = (currentPrice - equilibriumGradients >= 0) ? "+" : "";
        double deviationPercent = (currentPrice - equilibriumGradients) / equilibriumGradients * 100;

        htmlBuilder.AppendLine("<div class='alert alert-info'>");
        htmlBuilder.AppendLine(
            $"<p>Отклонение от уровня равновесия: <strong>{deviationSign}{currentPrice - equilibriumGradients:F2}</strong> " +
            $"(<strong>{deviationPercent:F2}%</strong>)</p>");

        if (currentPrice >= lowerBound && currentPrice <= upperBound)
        {
            htmlBuilder.AppendLine(
                "<p class='text-success'><strong>Текущая цена находится в пределах ожидаемого диапазона движения.</strong></p>");
        }
        else if (currentPrice < lowerBound)
        {
            htmlBuilder.AppendLine(
                "<p class='text-info'><strong>Текущая цена ниже ожидаемого диапазона движения, возможен возврат в диапазон.</strong></p>");
        }
        else // currentPrice > upperBound
        {
            htmlBuilder.AppendLine(
                "<p class='text-info'><strong>Текущая цена выше ожидаемого диапазона движения, возможен возврат в диапазон.</strong></p>");
        }

        htmlBuilder.AppendLine("</div>"); // Close alert
        htmlBuilder.AppendLine("</div>"); // Close положение div

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card

        // 4. ИНТЕРПРЕТАЦИЯ
        htmlBuilder.AppendLine("<div class='card mb-3'>");
        htmlBuilder.AppendLine("<div class='card-header bg-light'><h6>4. ИНТЕРПРЕТАЦИЯ</h6></div>");
        htmlBuilder.AppendLine("<div class='card-body'>");

        // Интерпретация относительно Max Pain
        htmlBuilder.AppendLine("<div class='mb-3'>");
        htmlBuilder.AppendLine("<h6 class='text-primary'>ОТНОСИТЕЛЬНО MAX PAIN:</h6>");

        if (Math.Abs(totalGradientUp) > Math.Abs(totalGradientDown) * 1.5)
        {
            htmlBuilder.AppendLine(
                "<p>Общая скорость прироста убытков <strong>значительно выше при движении цены ВВЕРХ</strong> от Max Pain. " +
                "Это указывает на повышенное сопротивление при движении цены выше Max Pain и может " +
                "создавать давление в сторону снижения.</p>");
        }
        else if (Math.Abs(totalGradientDown) > Math.Abs(totalGradientUp) * 1.5)
        {
            htmlBuilder.AppendLine(
                "<p>Общая скорость прироста убытков <strong>значительно выше при движении цены ВНИЗ</strong> от Max Pain. " +
                "Это указывает на повышенную поддержку при снижении цены ниже Max Pain и может " +
                "создавать давление в сторону роста.</p>");
        }
        else
        {
            htmlBuilder.AppendLine(
                "<p>Общая скорость прироста убытков <strong>относительно сбалансирована в обоих направлениях</strong>. " +
                "Давление на цену со стороны опционов примерно одинаково как вверх, так и вниз.</p>");
        }

        htmlBuilder.AppendLine("</div>");

        // Интерпретация относительно текущей цены
        htmlBuilder.AppendLine("<div class='mb-3'>");
        htmlBuilder.AppendLine("<h6 class='text-primary'>ОТНОСИТЕЛЬНО ТЕКУЩЕЙ ЦЕНЫ:</h6>");

        if (Math.Abs(totalGradientCurrentUp) > Math.Abs(totalGradientCurrentDown) * 1.5)
        {
            htmlBuilder.AppendLine(
                "<p>Общая скорость прироста убытков <strong>значительно выше при движении цены ВВЕРХ</strong> от текущей цены. " +
                "Это указывает на повышенное сопротивление дальнейшему росту цены.</p>");
        }
        else if (Math.Abs(totalGradientCurrentDown) > Math.Abs(totalGradientCurrentUp) * 1.5)
        {
            htmlBuilder.AppendLine(
                "<p>Общая скорость прироста убытков <strong>значительно выше при движении цены ВНИЗ</strong> от текущей цены. " +
                "Это указывает на повышенную поддержку текущим уровням цены.</p>");
        }
        else
        {
            htmlBuilder.AppendLine(
                "<p>Общая скорость прироста убытков <strong>относительно сбалансирована в обоих направлениях</strong> от текущей цены. " +
                "Давление на цену со стороны опционов примерно одинаково как вверх, так и вниз.</p>");
        }

        htmlBuilder.AppendLine("</div>");

        // Анализ по типам опционов
        htmlBuilder.AppendLine("<div class='mb-3'>");
        htmlBuilder.AppendLine("<h6 class='text-primary'>АНАЛИЗ ПО ТИПАМ ОПЦИОНОВ:</h6>");

        if (callGradientUp > putGradientDown * 1.2)
        {
            htmlBuilder.AppendLine(
                "<p>Скорость прироста убытков Call опционов при движении вверх <strong>превышает</strong> скорость " +
                "прироста убытков Put опционов при движении вниз. Это может создавать более " +
                "сильное сопротивление росту, чем поддержку при снижении.</p>");
        }
        else if (putGradientDown > callGradientUp * 1.2)
        {
            htmlBuilder.AppendLine(
                "<p>Скорость прироста убытков Put опционов при движении вниз <strong>превышает</strong> скорость " +
                "прироста убытков Call опционов при движении вверх. Это может создавать более " +
                "сильную поддержку при снижении, чем сопротивление росту.</p>");
        }
        else
        {
            htmlBuilder.AppendLine(
                "<p>Скорости прироста убытков Call и Put опционов <strong>примерно сбалансированы</strong>, " +
                "что указывает на равномерное давление в обоих направлениях.</p>");
        }

        htmlBuilder.AppendLine("</div>");

        htmlBuilder.AppendLine("</div>"); // Close card-body
        htmlBuilder.AppendLine("</div>"); // Close card

        htmlBuilder.AppendLine("</div>"); // Close pain-gradient-analysis div

        return htmlBuilder.ToString();
    }
    
    
    public string AnalyzePriceMovementPotentialHtml(List<OptionData> data, double currentPrice)
{
    var htmlBuilder = new StringBuilder();
    
    htmlBuilder.AppendLine("<article class='card mb-4'>");
    htmlBuilder.AppendLine("<header class='bg-dark text-white p-3'>");
    htmlBuilder.AppendLine("<h4>ПРОГНОЗ ПОТЕНЦИАЛЬНОГО ДВИЖЕНИЯ ЦЕНЫ</h4>");
    htmlBuilder.AppendLine("</header>");
    
    htmlBuilder.AppendLine("<div class='p-3'>");
    
    // Идентификация ключевых уровней сопротивления (высокий объем Call)
    List<OptionData> resistanceLevels = data
        .Where(d => d.Strike > currentPrice)
        .OrderBy(d => d.Strike)
        .Take(3)
        .Where(d => d.CallOi > d.PutOi)
        .ToList();

    // Идентификация ключевых уровней поддержки (высокий объем Put)
    List<OptionData> supportLevels = data
        .Where(d => d.Strike < currentPrice)
        .OrderByDescending(d => d.Strike)
        .Take(3)
        .Where(d => d.PutOi > d.CallOi)
        .ToList();
    
    // Support and Resistance Levels
    htmlBuilder.AppendLine("<div class='row'>");
    
    // Support Levels
    htmlBuilder.AppendLine("<div class='col-md-6 mb-4'>");
    htmlBuilder.AppendLine("<div class='card h-100 border-success'>");
    htmlBuilder.AppendLine("<div class='card-header bg-success text-white'>");
    htmlBuilder.AppendLine("<h5 class='mb-0'><i class='bi bi-arrow-up-circle-fill me-2'></i>Ключевые уровни поддержки</h5>");
    htmlBuilder.AppendLine("</div>");
    
    htmlBuilder.AppendLine("<div class='card-body'>");
    
    if (supportLevels.Any())
    {
        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-bordered table-hover'>");
        htmlBuilder.AppendLine("<thead class='table-light'>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>Страйк</th>");
        htmlBuilder.AppendLine("<th>Put OI</th>");
        htmlBuilder.AppendLine("<th>Call OI</th>");
        htmlBuilder.AppendLine("<th>Соотношение</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");
        
        foreach (OptionData level in supportLevels.OrderBy(d => d.Strike))
        {
            double ratio = level.PutOi / level.CallOi;
            htmlBuilder.AppendLine("<tr>");
            htmlBuilder.AppendLine($"<td><strong>{level.Strike}</strong></td>");
            htmlBuilder.AppendLine($"<td>{level.PutOi:N0}</td>");
            htmlBuilder.AppendLine($"<td>{level.CallOi:N0}</td>");
            htmlBuilder.AppendLine($"<td><span class='badge bg-success'>{ratio:F2}x</span></td>");
            htmlBuilder.AppendLine("</tr>");
        }
        
        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>");
    }
    else
    {
        htmlBuilder.AppendLine("<div class='alert alert-warning'>");
        htmlBuilder.AppendLine("<i class='bi bi-exclamation-triangle-fill me-2'></i>Ключевые уровни поддержки не выявлены");
        htmlBuilder.AppendLine("</div>");
    }
    
    htmlBuilder.AppendLine("</div>"); // Close card-body
    htmlBuilder.AppendLine("</div>"); // Close card
    htmlBuilder.AppendLine("</div>"); // Close col
    
    // Resistance Levels
    htmlBuilder.AppendLine("<div class='col-md-6 mb-4'>");
    htmlBuilder.AppendLine("<div class='card h-100 border-danger'>");
    htmlBuilder.AppendLine("<div class='card-header bg-danger text-white'>");
    htmlBuilder.AppendLine("<h5 class='mb-0'><i class='bi bi-arrow-down-circle-fill me-2'></i>Ключевые уровни сопротивления</h5>");
    htmlBuilder.AppendLine("</div>");
    
    htmlBuilder.AppendLine("<div class='card-body'>");
    
    if (resistanceLevels.Any())
    {
        htmlBuilder.AppendLine("<div class='table-responsive'>");
        htmlBuilder.AppendLine("<table class='table table-bordered table-hover'>");
        htmlBuilder.AppendLine("<thead class='table-light'>");
        htmlBuilder.AppendLine("<tr>");
        htmlBuilder.AppendLine("<th>Страйк</th>");
        htmlBuilder.AppendLine("<th>Call OI</th>");
        htmlBuilder.AppendLine("<th>Put OI</th>");
        htmlBuilder.AppendLine("<th>Соотношение</th>");
        htmlBuilder.AppendLine("</tr>");
        htmlBuilder.AppendLine("</thead>");
        htmlBuilder.AppendLine("<tbody>");
        
        foreach (OptionData level in resistanceLevels)
        {
            double ratio = level.CallOi / level.PutOi;
            htmlBuilder.AppendLine("<tr>");
            htmlBuilder.AppendLine($"<td><strong>{level.Strike}</strong></td>");
            htmlBuilder.AppendLine($"<td>{level.CallOi:N0}</td>");
            htmlBuilder.AppendLine($"<td>{level.PutOi:N0}</td>");
            htmlBuilder.AppendLine($"<td><span class='badge bg-danger'>{ratio:F2}x</span></td>");
            htmlBuilder.AppendLine("</tr>");
        }
        
        htmlBuilder.AppendLine("</tbody>");
        htmlBuilder.AppendLine("</table>");
        htmlBuilder.AppendLine("</div>");
    }
    else
    {
        htmlBuilder.AppendLine("<div class='alert alert-warning'>");
        htmlBuilder.AppendLine("<i class='bi bi-exclamation-triangle-fill me-2'></i>Ключевые уровни сопротивления не выявлены");
        htmlBuilder.AppendLine("</div>");
    }
    
    htmlBuilder.AppendLine("</div>"); // Close card-body
    htmlBuilder.AppendLine("</div>"); // Close card
    htmlBuilder.AppendLine("</div>"); // Close col
    
    htmlBuilder.AppendLine("</div>"); // Close row
    
    // Расчет "силы" уровней
    double totalOi = data.Sum(d => d.CallOi + d.PutOi);
    
    // Key metrics for price movement analysis
    htmlBuilder.AppendLine("<div class='card mb-4'>");
    htmlBuilder.AppendLine("<div class='card-header bg-primary text-white'>");
    htmlBuilder.AppendLine("<h5 class='mb-0'>Ключевые метрики</h5>");
    htmlBuilder.AppendLine("</div>");
    
    // Calculation of metrics
    double callCenter = data.Sum(d => d.Strike * d.CallOi) / data.Sum(d => d.CallOi);
    double putCenter = data.Sum(d => d.Strike * d.PutOi) / data.Sum(d => d.PutOi);
    double equilibriumLevel = (callCenter + putCenter) / 2;
    
    // Max Pain calculation
    List<PainResult> painResults = new List<PainResult>();
    foreach (OptionData targetOption in data)
    {
        double targetStrike = targetOption.Strike;
        double callLosses = 0;
        double putLosses = 0;

        foreach (OptionData option in data)
        {
            callLosses += option.CallOi * Math.Max(0, targetStrike - option.Strike);
            putLosses += option.PutOi * Math.Max(0, option.Strike - targetStrike);
        }

        painResults.Add(new PainResult
        {
            Strike = targetStrike,
            CallLosses = callLosses,
            PutLosses = putLosses,
            TotalLosses = callLosses + putLosses
        });
    }

    double maxPainStrike = painResults.OrderBy(p => p.TotalLosses).First().Strike;
    
    // Call/Put Ratio
    double callPutRatio = data.Sum(d => d.CallOi) / data.Sum(d => d.PutOi);
    string sentimentClass;
    string sentimentText;
    
    if (callPutRatio > 1.2)
    {
        sentimentClass = "bg-success";
        sentimentText = "Бычий настрой";
    }
    else if (callPutRatio < 0.8)
    {
        sentimentClass = "bg-danger";
        sentimentText = "Медвежий настрой";
    }
    else
    {
        sentimentClass = "bg-secondary";
        sentimentText = "Нейтральный настрой";
    }
    
    // Display metrics in a card grid
    htmlBuilder.AppendLine("<div class='card-body'>");
    htmlBuilder.AppendLine("<div class='row'>");
    
    // Current Price
    htmlBuilder.AppendLine("<div class='col-md-4 col-sm-6 mb-3'>");
    htmlBuilder.AppendLine("<div class='card h-100 border-dark'>");
    htmlBuilder.AppendLine("<div class='card-header bg-dark text-white text-center'>Текущая цена</div>");
    htmlBuilder.AppendLine("<div class='card-body text-center'>");
    htmlBuilder.AppendLine($"<h3 class='mb-0'>{currentPrice:F2}</h3>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Max Pain
    string maxPainRelationClass = currentPrice > maxPainStrike ? "text-danger" : (currentPrice < maxPainStrike ? "text-success" : "text-dark");
    string maxPainRelationText = currentPrice > maxPainStrike ? "выше" : (currentPrice < maxPainStrike ? "ниже" : "равна");
    
    htmlBuilder.AppendLine("<div class='col-md-4 col-sm-6 mb-3'>");
    htmlBuilder.AppendLine("<div class='card h-100 border-primary'>");
    htmlBuilder.AppendLine("<div class='card-header bg-primary text-white text-center'>Max Pain</div>");
    htmlBuilder.AppendLine("<div class='card-body text-center'>");
    htmlBuilder.AppendLine($"<h3 class='mb-0'>{maxPainStrike:F2}</h3>");
    htmlBuilder.AppendLine($"<small class='{maxPainRelationClass}'>Текущая цена {maxPainRelationText}</small>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Call/Put Ratio
    htmlBuilder.AppendLine("<div class='col-md-4 col-sm-6 mb-3'>");
    htmlBuilder.AppendLine("<div class='card h-100 border-info'>");
    htmlBuilder.AppendLine("<div class='card-header bg-info text-white text-center'>Call/Put Ratio</div>");
    htmlBuilder.AppendLine("<div class='card-body text-center'>");
    htmlBuilder.AppendLine($"<h3 class='mb-0'>{callPutRatio:F2}</h3>");
    htmlBuilder.AppendLine($"<span class='badge {sentimentClass}'>{sentimentText}</span>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Centers of Gravity - Call
    htmlBuilder.AppendLine("<div class='col-md-4 col-sm-6 mb-3'>");
    htmlBuilder.AppendLine("<div class='card h-100 border-primary'>");
    htmlBuilder.AppendLine("<div class='card-header bg-primary text-white text-center'>Центр тяжести Call</div>");
    htmlBuilder.AppendLine("<div class='card-body text-center'>");
    htmlBuilder.AppendLine($"<h3 class='mb-0'>{callCenter:F2}</h3>");
    string callCenterRelation = callCenter > currentPrice ? "выше текущей цены" : "ниже текущей цены";
    string callCenterClass = callCenter > currentPrice ? "text-success" : "text-danger";
    htmlBuilder.AppendLine($"<small class='{callCenterClass}'>{callCenterRelation}</small>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Centers of Gravity - Put
    htmlBuilder.AppendLine("<div class='col-md-4 col-sm-6 mb-3'>");
    htmlBuilder.AppendLine("<div class='card h-100 border-danger'>");
    htmlBuilder.AppendLine("<div class='card-header bg-danger text-white text-center'>Центр тяжести Put</div>");
    htmlBuilder.AppendLine("<div class='card-body text-center'>");
    htmlBuilder.AppendLine($"<h3 class='mb-0'>{putCenter:F2}</h3>");
    string putCenterRelation = putCenter > currentPrice ? "выше текущей цены" : "ниже текущей цены";
    string putCenterClass = putCenter > currentPrice ? "text-success" : "text-danger";
    htmlBuilder.AppendLine($"<small class='{putCenterClass}'>{putCenterRelation}</small>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Equilibrium Level
    htmlBuilder.AppendLine("<div class='col-md-4 col-sm-6 mb-3'>");
    htmlBuilder.AppendLine("<div class='card h-100 border-success'>");
    htmlBuilder.AppendLine("<div class='card-header bg-success text-white text-center'>Уровень равновесия</div>");
    htmlBuilder.AppendLine("<div class='card-body text-center'>");
    htmlBuilder.AppendLine($"<h3 class='mb-0'>{equilibriumLevel:F2}</h3>");
    string equilibriumRelation = equilibriumLevel > currentPrice ? "выше текущей цены" : "ниже текущей цены";
    string equilibriumClass = equilibriumLevel > currentPrice ? "text-success" : "text-danger";
    htmlBuilder.AppendLine($"<small class='{equilibriumClass}'>{equilibriumRelation}</small>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    htmlBuilder.AppendLine("</div>"); // Close row
    htmlBuilder.AppendLine("</div>"); // Close card-body
    htmlBuilder.AppendLine("</div>"); // Close card
    
    // Финальный прогноз
    htmlBuilder.AppendLine("<div class='card mb-4'>");
    htmlBuilder.AppendLine("<div class='card-header bg-dark text-white'>");
    htmlBuilder.AppendLine("<h5 class='mb-0'>Прогноз движения цены</h5>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("<div class='card-body'>");
    
    if (currentPrice < maxPainStrike && callPutRatio > 1.2 && callCenter > putCenter)
    {
        htmlBuilder.AppendLine("<div class='alert alert-success'>");
        htmlBuilder.AppendLine("<h4 class='alert-heading'><i class='bi bi-graph-up-arrow me-2'></i>Бычий сценарий</h4>");
        htmlBuilder.AppendLine("<hr>");
        htmlBuilder.AppendLine($"<p>Высокая вероятность движения к уровню <strong>{maxPainStrike}</strong> с потенциалом дальнейшего роста к <strong>{callCenter:F2}</strong>.</p>");
        
        if (resistanceLevels.Any())
        {
            htmlBuilder.AppendLine("<p><strong>Ключевые уровни сопротивления:</strong> " + 
                                  string.Join(", ", resistanceLevels.Select(l => l.Strike)) + "</p>");
        }
        
        if (supportLevels.Any())
        {
            htmlBuilder.AppendLine("<p><strong>Ближайшая поддержка:</strong> " + 
                                  supportLevels.OrderByDescending(l => l.Strike).First().Strike + "</p>");
        }
        else
        {
            htmlBuilder.AppendLine("<p><strong>Ближайшая поддержка:</strong> не выявлена</p>");
        }
        
        htmlBuilder.AppendLine("</div>");
    }
    else if (currentPrice > maxPainStrike && callPutRatio < 0.8 && callCenter < putCenter)
    {
        htmlBuilder.AppendLine("<div class='alert alert-danger'>");
        htmlBuilder.AppendLine("<h4 class='alert-heading'><i class='bi bi-graph-down-arrow me-2'></i>Медвежий сценарий</h4>");
        htmlBuilder.AppendLine("<hr>");
        htmlBuilder.AppendLine($"<p>Высокая вероятность движения к уровню <strong>{maxPainStrike}</strong> с потенциалом дальнейшего снижения к <strong>{putCenter:F2}</strong>.</p>");
        
        if (supportLevels.Any())
        {
            htmlBuilder.AppendLine("<p><strong>Ключевые уровни поддержки:</strong> " + 
                                  string.Join(", ", supportLevels.Select(l => l.Strike)) + "</p>");
        }
        
        if (resistanceLevels.Any())
        {
            htmlBuilder.AppendLine("<p><strong>Ближайшее сопротивление:</strong> " + 
                                  resistanceLevels.OrderBy(l => l.Strike).First().Strike + "</p>");
        }
        else
        {
            htmlBuilder.AppendLine("<p><strong>Ближайшее сопротивление:</strong> не выявлено</p>");
        }
        
        htmlBuilder.AppendLine("</div>");
    }
    else if (Math.Abs(currentPrice - maxPainStrike) / maxPainStrike <= 0.01)
    {
        htmlBuilder.AppendLine("<div class='alert alert-secondary'>");
        htmlBuilder.AppendLine("<h4 class='alert-heading'><i class='bi bi-arrow-left-right me-2'></i>Нейтральный сценарий</h4>");
        htmlBuilder.AppendLine("<hr>");
        htmlBuilder.AppendLine("<p>Цена близка к уровню Max Pain, возможно боковое движение в диапазоне " +
                              $"<strong>{Math.Min(putCenter, callCenter):F2} - {Math.Max(putCenter, callCenter):F2}</strong>.</p>");
        htmlBuilder.AppendLine("</div>");
    }
    else
    {
        htmlBuilder.AppendLine("<div class='alert alert-warning'>");
        htmlBuilder.AppendLine("<h4 class='alert-heading'><i class='bi bi-question-circle me-2'></i>Смешанный сценарий</h4>");
        htmlBuilder.AppendLine("<hr>");
        htmlBuilder.AppendLine($"<p>Возможно движение к уровню Max Pain <strong>{maxPainStrike}</strong> с последующим определением направления.</p>");
        
        if (callPutRatio > 1.2)
        {
            htmlBuilder.AppendLine("<p><i class='bi bi-arrow-up-circle me-2'></i>Преобладание Call опционов указывает на потенциал роста после достижения уровня Max Pain.</p>");
        }
        else if (callPutRatio < 0.8)
        {
            htmlBuilder.AppendLine("<p><i class='bi bi-arrow-down-circle me-2'></i>Преобладание Put опционов указывает на потенциал снижения после достижения уровня Max Pain.</p>");
        }
        
        htmlBuilder.AppendLine("</div>");
    }
    
    // Price Movement Visualization
    htmlBuilder.AppendLine("<h5 class='mt-4 mb-3'>Визуализация ключевых уровней</h5>");
    htmlBuilder.AppendLine("<div class='position-relative py-5 mb-4 px-4' style='background-color: #f8f9fa; border-radius: 4px;'>");
    
    // Find min and max for visualization scale
    double minLevel = Math.Min(Math.Min(currentPrice, maxPainStrike), Math.Min(callCenter, putCenter)) * 0.95;
    double maxLevel = Math.Max(Math.Max(currentPrice, maxPainStrike), Math.Max(callCenter, putCenter)) * 1.05;
    
    // If we have support or resistance levels, include them in scale calculation
    if (supportLevels.Any())
    {
        minLevel = Math.Min(minLevel, supportLevels.Min(l => l.Strike) * 0.95);
    }
    
    if (resistanceLevels.Any())
    {
        maxLevel = Math.Max(maxLevel, resistanceLevels.Max(l => l.Strike) * 1.05);
    }
    
    double range = maxLevel - minLevel;
    
    // Scale line
    htmlBuilder.AppendLine("<div class='position-absolute w-100 start-0' style='height: 4px; background-color: #dee2e6; top: 50%; transform: translateY(-50%);'></div>");
    
    // Current Price marker
    double currentPricePos = (currentPrice - minLevel) / range * 100;
    htmlBuilder.AppendLine($"<div class='position-absolute' style='left: {currentPricePos}%; top: 50%; transform: translate(-50%, -50%);'>");
    htmlBuilder.AppendLine("<div class='bg-warning text-dark px-2 py-1 rounded' style='white-space: nowrap;'>");
    htmlBuilder.AppendLine($"<small><strong>Текущая: {currentPrice:F2}</strong></small>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Max Pain marker
    double maxPainPos = (maxPainStrike - minLevel) / range * 100;
    htmlBuilder.AppendLine($"<div class='position-absolute' style='left: {maxPainPos}%; top: 10px; transform: translateX(-50%);'>");
    htmlBuilder.AppendLine("<div class='bg-primary text-white px-2 py-1 rounded' style='white-space: nowrap;'>");
    htmlBuilder.AppendLine($"<small><strong>Max Pain: {maxPainStrike:F2}</strong></small>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Call Center marker
    double callCenterPos = (callCenter - minLevel) / range * 100;
    htmlBuilder.AppendLine($"<div class='position-absolute' style='left: {callCenterPos}%; bottom: 10px; transform: translateX(-50%);'>");
    htmlBuilder.AppendLine("<div class='bg-info text-white px-2 py-1 rounded' style='white-space: nowrap;'>");
    htmlBuilder.AppendLine($"<small><strong>Call ЦТ: {callCenter:F2}</strong></small>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Put Center marker
    double putCenterPos = (putCenter - minLevel) / range * 100;
    htmlBuilder.AppendLine($"<div class='position-absolute' style='left: {putCenterPos}%; bottom: 10px; transform: translateX(-50%);'>");
    htmlBuilder.AppendLine("<div class='bg-danger text-white px-2 py-1 rounded' style='white-space: nowrap;'>");
    htmlBuilder.AppendLine($"<small><strong>Put ЦТ: {putCenter:F2}</strong></small>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    // Support Levels markers (if any)
    foreach (var level in supportLevels.OrderBy(l => l.Strike).Take(2))
    {
        double levelPos = (level.Strike - minLevel) / range * 100;
        htmlBuilder.AppendLine($"<div class='position-absolute' style='left: {levelPos}%; top: 10px; transform: translateX(-50%);'>");
        htmlBuilder.AppendLine("<div class='bg-success text-white px-2 py-1 rounded' style='white-space: nowrap;'>");
        htmlBuilder.AppendLine($"<small><strong>Поддержка: {level.Strike:F2}</strong></small>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
    }
    
    // Resistance Levels markers (if any)
    foreach (var level in resistanceLevels.OrderBy(l => l.Strike).Take(2))
    {
        double levelPos = (level.Strike - minLevel) / range * 100;
        htmlBuilder.AppendLine($"<div class='position-absolute' style='left: {levelPos}%; top: 10px; transform: translateX(-50%);'>");
        htmlBuilder.AppendLine("<div class='bg-danger text-white px-2 py-1 rounded' style='white-space: nowrap;'>");
        htmlBuilder.AppendLine($"<small><strong>Сопротивление: {level.Strike:F2}</strong></small>");
        htmlBuilder.AppendLine("</div>");
        htmlBuilder.AppendLine("</div>");
    }
    
    htmlBuilder.AppendLine("</div>"); // Close visualization div
    
    // Explanation section
    htmlBuilder.AppendLine("<div class='card mt-3 bg-light'>");
    htmlBuilder.AppendLine("<div class='card-body'>");
    htmlBuilder.AppendLine("<h5 class='card-title'>О прогнозе движения цены</h5>");
    htmlBuilder.AppendLine("<p class='card-text'>Прогноз основан на комплексном анализе опционных данных, включая:</p>");
    htmlBuilder.AppendLine("<ul>");
    htmlBuilder.AppendLine("<li><strong>Max Pain:</strong> Цена, при которой совокупные убытки держателей опционов максимальны. Часто наблюдается притяжение цены к этому уровню ближе к экспирации.</li>");
    htmlBuilder.AppendLine("<li><strong>Центры тяжести:</strong> Средневзвешенные уровни страйков для Call и Put опционов. Показывают ожидания рынка.</li>");
    htmlBuilder.AppendLine("<li><strong>Call/Put Ratio:</strong> Соотношение объемов Call и Put опционов. Индикатор настроений участников рынка.</li>");
    htmlBuilder.AppendLine("<li><strong>Уровни поддержки и сопротивления:</strong> Определены на основе концентрации опционных позиций.</li>");
    htmlBuilder.AppendLine("</ul>");
    htmlBuilder.AppendLine("</div>");
    htmlBuilder.AppendLine("</div>");
    
    htmlBuilder.AppendLine("</div>"); // Close card-body
    htmlBuilder.AppendLine("</div>"); // Close card
    
    htmlBuilder.AppendLine("</div>"); // Close main div
    htmlBuilder.AppendLine("</article>"); // Close article
    
    return htmlBuilder.ToString();
}



    #region Private

    // Вспомогательный метод для оценки уровня, где убытки вырастут на заданное значение
    private static double EstimateThresholdLevel(List<OptionData> data, double startPrice, double startLosses,
        double targetIncrease, double stepSize, bool goUp)
    {
        double currentPrice = startPrice;

        // Ограничиваем количество итераций, чтобы избежать бесконечного цикла
        for (int i = 0; i < 100; i++)
        {
            currentPrice = goUp ? currentPrice + stepSize : currentPrice - stepSize;
            double currentLosses = CalculateTotalLossesAtPrice(data, currentPrice);

            if (currentLosses - startLosses >= targetIncrease)
            {
                return currentPrice;
            }
        }

        // Если не нашли достаточного прироста убытков, возвращаем оценку
        return currentPrice;
    }

    // Метод для расчета только убытков Call опционов
    private static double CalculateCallLossesAtPrice(List<OptionData> data, double price)
    {
        double callLosses = 0;

        foreach (OptionData option in data)
        {
            // Убытки для держателей Call опционов при данной цене
            callLosses += option.CallOi * Math.Max(0, price - option.Strike);
        }

        return callLosses;
    }

    // Метод для расчета только убытков Put опционов
    private static double CalculatePutLossesAtPrice(List<OptionData> data, double price)
    {
        double putLosses = 0;

        foreach (OptionData option in data)
        {
            // Убытки для держателей Put опционов при данной цене
            putLosses += option.PutOi * Math.Max(0, option.Strike - price);
        }

        return putLosses;
    }


    // Вспомогательный метод для расчета убытков при заданной цене
    private static double CalculateTotalLossesAtPrice(List<OptionData> data, double price)
    {
        double callLosses = 0;
        double putLosses = 0;

        foreach (OptionData option in data)
        {
            // Убытки для держателей Call опционов при данной цене
            callLosses += option.CallOi * Math.Max(0, price - option.Strike);

            // Убытки для держателей Put опционов при данной цене
            putLosses += option.PutOi * Math.Max(0, option.Strike - price);
        }

        return callLosses + putLosses;
    }


    // Метод для расчета уровня равновесия по скорости прироста убытков
    private static double CalculateGradientEquilibriumLevel(List<OptionData> data, double startPrice, double step)
    {
        // Определяем диапазон цен для поиска
        double minPrice = data.Min(d => d.Strike) * 0.9; // Расширяем диапазон на 10% в обе стороны
        double maxPrice = data.Max(d => d.Strike) * 1.1;

        // Набор цен для расчета градиентов
        List<(double Price, double TotalGradient, double CallGradient, double PutGradient)> gradientData = new();

        // Определяем более мелкий шаг для более точного поиска
        double searchStep = step / 2;

        // Рассчитываем градиенты на сетке цен
        for (double price = minPrice; price <= maxPrice; price += searchStep)
        {
            // Рассчитываем убытки при текущем уровне цены
            double totalLossesAt = CalculateTotalLossesAtPrice(data, price);
            double callLossesAt = CalculateCallLossesAtPrice(data, price);
            double putLossesAt = CalculatePutLossesAtPrice(data, price);

            // Рассчитываем убытки при повышенном уровне цены
            double totalLossesUp = CalculateTotalLossesAtPrice(data, price + searchStep);
            double callLossesUp = CalculateCallLossesAtPrice(data, price + searchStep);
            double putLossesUp = CalculatePutLossesAtPrice(data, price + searchStep);

            // Вычисляем градиенты
            double totalGradient = (totalLossesUp - totalLossesAt) / searchStep;
            double callGradient = (callLossesUp - callLossesAt) / searchStep;
            double putGradient = (putLossesUp - putLossesAt) / searchStep;

            gradientData.Add((price, totalGradient, callGradient, putGradient));
        }

        // 1. Ищем точку, где общий градиент равен нулю (или ближайшее к нулю)
        double minTotalGradient = double.MaxValue;
        double equilibriumPrice = startPrice;

        foreach ((double Price, double TotalGradient, double CallGradient, double PutGradient) tuple in gradientData)
        {
            double absGradient = Math.Abs(tuple.TotalGradient);
            if (absGradient < minTotalGradient)
            {
                minTotalGradient = absGradient;
                equilibriumPrice = tuple.Price;
            }
        }

        // 2. Дополнительно ищем точки перехода градиента через ноль для более точного определения
        for (int i = 1; i < gradientData.Count; i++)
        {
            (double Price, double TotalGradient, double CallGradient, double PutGradient) prev = gradientData[i - 1];
            (double Price, double TotalGradient, double CallGradient, double PutGradient) curr = gradientData[i];

            // Проверяем, меняет ли градиент знак между этими точками
            if ((prev.TotalGradient > 0 && curr.TotalGradient < 0) ||
                (prev.TotalGradient < 0 && curr.TotalGradient > 0))
            {
                // Линейная интерполяция для нахождения более точного значения
                double ratio = Math.Abs(prev.TotalGradient) /
                               (Math.Abs(prev.TotalGradient) + Math.Abs(curr.TotalGradient));
                double interpolatedPrice = prev.Price + ratio * (curr.Price - prev.Price);

                // Если эта точка ближе к нулевому градиенту, обновляем результат
                double interpolatedGradient = prev.TotalGradient + ratio * (curr.TotalGradient - prev.TotalGradient);
                if (Math.Abs(interpolatedGradient) < minTotalGradient)
                {
                    minTotalGradient = Math.Abs(interpolatedGradient);
                    equilibriumPrice = interpolatedPrice;
                }
            }
        }

        return equilibriumPrice;
    }
    
    /// <summary>
    /// Рассчитывает PnL продавца Call опционов при заданной цене базового актива
    /// </summary>
    private static double CalculateSellerCallPnL(List<OptionData> data, double price)
    {
        double totalPnL = 0;

        foreach (OptionData option in data)
        {
            double receivedPremium = option.CallPrice * option.CallOi;
            double payout = Math.Max(0, price - option.Strike) * option.CallOi;
            double pnl = receivedPremium - payout;

            totalPnL += pnl;
        }

        return totalPnL;
    }

    /// <summary>
    /// Рассчитывает PnL продавца Put опционов при заданной цене базового актива
    /// </summary>
    private static double CalculateSellerPutPnL(List<OptionData> data, double price)
    {
        double totalPnL = 0;

        foreach (OptionData option in data)
        {
            double receivedPremium = option.PutPrice * option.PutOi;
            double payout = Math.Max(0, option.Strike - price) * option.PutOi;
            double pnl = receivedPremium - payout;

            totalPnL += pnl;
        }

        return totalPnL;
    }

    #endregion
}