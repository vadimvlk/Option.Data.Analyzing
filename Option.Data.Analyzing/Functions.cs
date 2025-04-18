﻿using System.Globalization;
using System.Text;
using Option.Data.Analyzing.Models;

namespace Option.Data.Analyzing;

public static class Functions
{
    public static List<OptionData> ParseMoexOptionData(string filePath)
    {
        Console.WriteLine($"Чтение данных из файла: {filePath}");

        List<OptionData> result = new List<OptionData>();
        string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);

        if (lines.Length <= 1)
        {
            throw new Exception("Файл не содержит данных или содержит только заголовок");
        }

        Console.WriteLine("Анализ структуры файла...");

        // Получаем индексы нужных колонок из заголовка (разделитель - точка с запятой)
        string[] headers = lines[0].Split(';');

        int callOiIndex = FindColumnIndex(headers, "CALL: Открыт.позиций");
        int strikeIndex = FindColumnIndex(headers, "СТРАЙК");
        int ivIndex = FindColumnIndex(headers, "IV");
        int putOiIndex = FindColumnIndex(headers, "PUT: Открыт. позиций");

        Console.WriteLine("Индексы колонок найдены. Обработка данных...");

        // Парсим данные из строк
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split(';');

            // Пропускаем строки без страйка
            if (parts.Length <= strikeIndex || string.IsNullOrWhiteSpace(parts[strikeIndex]))
            {
                continue;
            }

            // Парсим страйк
            if (!double.TryParse(parts[strikeIndex].Replace(',', '.'), NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out double strike))
            {
                continue;
            }

            OptionData optionData = new OptionData
            {
                Strike = strike,
                CallOi = parts.Length > callOiIndex ? ParseDoubleWithSemicolon(parts[callOiIndex]) : 0,
                Iv = parts.Length > ivIndex ? ParseDoubleWithSemicolon(parts[ivIndex]) : 0,
                PutOi = parts.Length > putOiIndex ? ParseDoubleWithSemicolon(parts[putOiIndex]) : 0
            };

            // Добавляем только если есть хотя бы какие-то данные
            if (optionData.CallOi > 0 || optionData.PutOi > 0)
            {
                result.Add(optionData);
            }
        }

        Console.WriteLine($"Успешно обработано {result.Count} строк данных\n");

        // Выводим первые 5 строк для проверки
        if (result.Count > 0)
        {
            Console.WriteLine("Первые 5 строк данных:");
            foreach (OptionData item in result.Take(5))
            {
                Console.WriteLine(
                    $"Strike: {item.Strike}, Call OI: {item.CallOi}, Put OI: {item.PutOi}, IV: {item.Iv}");
            }

            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("Внимание: не найдено ни одной строки с данными!");
        }

        return result;
    }

    public static void CalculateCallPutRatio(List<OptionData> data)
    {
        double totalCallOi = data.Sum(d => d.CallOi);
        double totalPutOi = data.Sum(d => d.PutOi);
        double callPutRatio = totalPutOi > 0 ? totalCallOi / totalPutOi : 0;

        Console.WriteLine("АНАЛИЗ СООТНОШЕНИЯ CALL/PUT");
        Console.WriteLine($"Общий объем Call опционов: {totalCallOi}");
        Console.WriteLine($"Общий объем Put опционов: {totalPutOi}");
        Console.WriteLine($"Call/Put Ratio: {callPutRatio:F2}");

        if (callPutRatio > 1.5)
        {
            Console.WriteLine(
                "Интерпретация: Значительный перевес в сторону Call опционов. Рынок демонстрирует бычьи настроения.");
        }
        else if (callPutRatio is >= 0.75 and <= 1.5)
        {
            Console.WriteLine("Интерпретация: Относительно сбалансированное соотношение Call и Put опционов.");
        }
        else if (callPutRatio > 0)
        {
            Console.WriteLine(
                "Интерпретация: Значительный перевес в сторону Put опционов. Рынок демонстрирует медвежьи настроения.");
        }

        Console.WriteLine();
    }

    public static void CalculateMaxPain(List<OptionData> data, double currentPrice)
    {
        Console.WriteLine("РАСЧЕТ MAX PAIN");

        List<PainResult> painResults = new List<PainResult>();

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

        Console.WriteLine($"Уровень Max Pain: {maxPainStrike}");
        Console.WriteLine(
            $"Текущая цена от Max Pain: {(currentPrice - maxPainStrike):F2} пунктов ({(currentPrice - maxPainStrike) / maxPainStrike * 100:F2}%)");

        // Выводим топ-5 страйков с наименьшими убытками
        Console.WriteLine("\nТоп-5 страйков с наименьшими общими убытками (потенциальные уровни притяжения):");
        for (int i = 0; i < Math.Min(5, painResults.Count); i++)
        {
            Console.WriteLine(
                $"{i + 1}. Страйк: {painResults[i].Strike}, Общие убытки: {painResults[i].TotalLosses:N0}");
        }

        // Интерпретация Max Pain
        Console.WriteLine("\nИнтерпретация Max Pain:");
        if (Math.Abs(currentPrice - maxPainStrike) / maxPainStrike <= 0.01)
        {
            Console.WriteLine("Цена находится вблизи уровня Max Pain, что может указывать на временную стабилизацию.");
        }
        else if (currentPrice < maxPainStrike)
        {
            Console.WriteLine(
                $"Цена находится ниже уровня Max Pain. Теоретически это создает давление в сторону роста к уровню {maxPainStrike}.");
        }
        else
        {
            Console.WriteLine(
                $"Цена находится выше уровня Max Pain. Теоретически это создает давление в сторону снижения к уровню {maxPainStrike}.");
        }

        Console.WriteLine();
    }

    public static void AnalyzeOpenInterest(List<OptionData> data)
    {
        Console.WriteLine("АНАЛИЗ ОТКРЫТОГО ИНТЕРЕСА");

        // Топ-5 страйков по объему Call опционов
        Console.WriteLine("Топ-5 страйков по объему Call опционов:");
        List<OptionData> topCallStrikes = data
            .OrderByDescending(d => d.CallOi)
            .Take(5)
            .ToList();

        foreach (OptionData strike in topCallStrikes)
        {
            Console.WriteLine($"Страйк: {strike.Strike}, Объем: {strike.CallOi}");
        }

        // Топ-5 страйков по объему Put опционов
        Console.WriteLine("\nТоп-5 страйков по объему Put опционов:");
        List<OptionData> topPutStrikes = data
            .OrderByDescending(d => d.PutOi)
            .Take(5)
            .ToList();

        foreach (OptionData strike in topPutStrikes)
        {
            Console.WriteLine($"Страйк: {strike.Strike}, Объем: {strike.PutOi}");
        }

        // Ключевые уровни на основе общего объема опционов
        Console.WriteLine("\nКлючевые уровни на основе общего объема опционов:");
        List<OptionData> keyLevels = data
            .OrderByDescending(d => d.CallOi + d.PutOi)
            .Take(5)
            .ToList();

        foreach (OptionData level in keyLevels)
        {
            string type = level.CallOi > level.PutOi ? "Сопротивление" : "Поддержка";
            Console.WriteLine($"Страйк: {level.Strike}, Общий объем: {level.CallOi + level.PutOi}, Тип: {type}");
        }

        Console.WriteLine();
    }

    public static void CalculateProfitLoss(List<OptionData> data, double currentPrice)
    {
        Console.WriteLine($"АНАЛИЗ ПРИБЫЛИ/УБЫТКА ПРИ ТЕКУЩЕЙ ЦЕНЕ {currentPrice}");

        double callProfit = 0;
        double putProfit = 0;

        foreach (OptionData option in data)
        {
            // Прибыль для держателей Call опционов
            callProfit += option.CallOi * Math.Max(0, currentPrice - option.Strike);

            // Прибыль для держателей Put опционов
            putProfit += option.PutOi * Math.Max(0, option.Strike - currentPrice);
        }

        Console.WriteLine($"Прибыль держателей Call: {callProfit:N0}");
        Console.WriteLine($"Прибыль держателей Put: {putProfit:N0}");
        Console.WriteLine($"Соотношение прибыли Call/Put: {(putProfit > 0 ? callProfit / putProfit : 0):F2}");

        // Интерпретация
        if (callProfit > putProfit * 1.5)
        {
            Console.WriteLine(
                "Интерпретация: Значительный перевес прибыли в пользу держателей Call опционов, что может создавать стимул для дальнейшего роста цены.");
        }
        else if (putProfit > callProfit * 1.5)
        {
            Console.WriteLine(
                "Интерпретация: Значительный перевес прибыли в пользу держателей Put опционов, что может создавать стимул для снижения цены.");
        }
        else
        {
            Console.WriteLine(
                "Интерпретация: Относительно сбалансированное соотношение прибыли между держателями Call и Put опционов.");
        }

        Console.WriteLine();
    }

    public static void AnalyzeCentersOfGravity(List<OptionData> data)
    {
        Console.WriteLine("АНАЛИЗ ЦЕНТРОВ ТЯЖЕСТИ");

        double totalCallOi = data.Sum(d => d.CallOi);
        double totalPutOi = data.Sum(d => d.PutOi);

        // Расчет центра тяжести для Call и Put
        double callCenter = data.Sum(d => d.Strike * d.CallOi) / totalCallOi;
        double putCenter = data.Sum(d => d.Strike * d.PutOi) / totalPutOi;

        Console.WriteLine($"Центр тяжести Call: {callCenter:F2}");
        Console.WriteLine($"Центр тяжести Put: {putCenter:F2}");
        Console.WriteLine($"Разница между центрами: {Math.Abs(callCenter - putCenter):F2}");

        // Интерпретация
        if (callCenter > putCenter)
        {
            Console.WriteLine(
                $"Интерпретация: Центр тяжести Call опционов ({callCenter:F2}) выше центра тяжести Put опционов ({putCenter:F2}), что может указывать на бычьи настроения рынка.");
        }
        else if (callCenter < putCenter)
        {
            Console.WriteLine(
                $"Интерпретация: Центр тяжести Call опционов ({callCenter:F2}) ниже центра тяжести Put опционов ({putCenter:F2}), что может указывать на медвежьи настроения рынка.");
        }
        else
        {
            Console.WriteLine(
                "Интерпретация: Центры тяжести Call и Put опционов примерно совпадают, что может указывать на нейтральные настроения рынка.");
        }

        Console.WriteLine();
    }

    public static void AnalyzePriceMovementPotential(List<OptionData> data, double currentPrice)
    {
        Console.WriteLine("ПРОГНОЗ ПОТЕНЦИАЛЬНОГО ДВИЖЕНИЯ ЦЕНЫ");

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

        Console.WriteLine("Ключевые уровни поддержки:");
        foreach (OptionData level in supportLevels.OrderBy(d => d.Strike))
        {
            Console.WriteLine($"Страйк: {level.Strike}, Put OI: {level.PutOi}, Call OI: {level.CallOi}");
        }

        Console.WriteLine("\nКлючевые уровни сопротивления:");
        foreach (OptionData level in resistanceLevels)
        {
            Console.WriteLine($"Страйк: {level.Strike}, Call OI: {level.CallOi}, Put OI: {level.PutOi}");
        }

        // Расчет "силы" уровней
        double totalOi = data.Sum(d => d.CallOi + d.PutOi);

        Console.WriteLine($"\nОбщий открытый интерес: {totalOi} контрактов.");

        // Интерпретация на основе всего проведенного анализа
        Console.WriteLine("\nКОМПЛЕКСНЫЙ АНАЛИЗ И ПРОГНОЗ:");

        // Центры тяжести
        double callCenter = data.Sum(d => d.Strike * d.CallOi) / data.Sum(d => d.CallOi);
        double putCenter = data.Sum(d => d.Strike * d.PutOi) / data.Sum(d => d.PutOi);

        // Max Pain
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

        Console.WriteLine($"На основе проведенного анализа можно сделать следующие выводы:");
        Console.WriteLine(
            $"1. Уровень Max Pain: {maxPainStrike} (текущая цена {(currentPrice > maxPainStrike ? "выше" : "ниже")})");
        Console.WriteLine(
            $"2. Соотношение Call/Put: {callPutRatio:F2} ({(callPutRatio > 1.2 ? "бычий" : callPutRatio < 0.8 ? "медвежий" : "нейтральный")} настрой)");
        Console.WriteLine($"3. Центры тяжести: Call = {callCenter:F2}, Put = {putCenter:F2}");

        // Финальный прогноз
        Console.WriteLine("\nПРОГНОЗ ДВИЖЕНИЯ ЦЕНЫ:");

        if (currentPrice < maxPainStrike && callPutRatio > 1.2 && callCenter > putCenter)
        {
            Console.WriteLine(
                $"Бычий сценарий: Высокая вероятность движения к уровню {maxPainStrike} с потенциалом дальнейшего роста к {callCenter:F2}.");
            Console.WriteLine(
                $"Ключевые уровни сопротивления: {string.Join(", ", resistanceLevels.Select(l => l.Strike))}");
            Console.WriteLine(
                $"Ближайшая поддержка: {(supportLevels.Any() ? supportLevels.OrderByDescending(l => l.Strike).First().Strike.ToString(CultureInfo.InvariantCulture) : "не выявлена")}");
        }
        else if (currentPrice > maxPainStrike && callPutRatio < 0.8 && callCenter < putCenter)
        {
            Console.WriteLine(
                $"Медвежий сценарий: Высокая вероятность движения к уровню {maxPainStrike} с потенциалом дальнейшего снижения к {putCenter:F2}.");
            Console.WriteLine($"Ключевые уровни поддержки: {string.Join(", ", supportLevels.Select(l => l.Strike))}");
            Console.WriteLine(
                $"Ближайшее сопротивление: {(resistanceLevels.Any() ? resistanceLevels.OrderBy(l => l.Strike).First().Strike.ToString(CultureInfo.InvariantCulture) : "не выявлено")}");
        }
        else if (Math.Abs(currentPrice - maxPainStrike) / maxPainStrike <= 0.01)
        {
            Console.WriteLine(
                $"Нейтральный сценарий: Цена близка к уровню Max Pain, возможно боковое движение в диапазоне " +
                $"{Math.Min(putCenter, callCenter):F2} - {Math.Max(putCenter, callCenter):F2}.");
        }
        else
        {
            Console.WriteLine(
                $"Смешанный сценарий: Возможно движение к уровню Max Pain {maxPainStrike} с последующим определением направления.");
            if (callPutRatio > 1.2)
            {
                Console.WriteLine(
                    "Преобладание Call опционов указывает на потенциал роста после достижения уровня Max Pain.");
            }
            else if (callPutRatio < 0.8)
            {
                Console.WriteLine(
                    "Преобладание Put опционов указывает на потенциал снижения после достижения уровня Max Pain.");
            }
        }
    }


    #region Private

    private static int FindColumnIndex(string[] headers, string columnName)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            if (headers[i].Trim() == columnName)
            {
                return i;
            }
        }

        throw new Exception($"Колонка '{columnName}' не найдена в заголовке файла");
    }

    private static double ParseDoubleWithSemicolon(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        // Удаляем все пробелы и заменяем запятую  на точку.
        string cleanValue = value.Replace(" ", "").Replace(",", ".");

        if (double.TryParse(cleanValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
        {
            return result;
        }

        return 0;
    }

    #endregion Private
}