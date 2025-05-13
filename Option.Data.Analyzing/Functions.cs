using System.Text;
using System.Globalization;
using Option.Data.Shared.Dto;

namespace Option.Data.Analyzing;

public static class Functions
{
    public static List<OptionData> ParseMoexOptionData(string filePath)
    {
        Console.WriteLine($"Чтение данных из файла: {filePath}");

        List<OptionData> result = new();
        string[] lines = File.ReadAllLines(filePath, Encoding.GetEncoding(1251));

        if (lines.Length <= 1)
        {
            throw new Exception("Файл не содержит данных или содержит только заголовок");
        }

        Console.WriteLine("Анализ структуры файла...");

        // Получаем индексы нужных колонок из заголовка (разделитель - точка с запятой).
        string[] headers = lines[0].Split(';');

        int callOiIndex = FindColumnIndex(headers, "CALL: Открыт.позиций");
        int strikeIndex = FindColumnIndex(headers, "СТРАЙК");
        int ivIndex = FindColumnIndex(headers, "IV");
        int putOiIndex = FindColumnIndex(headers, "PUT: Открыт. позиций");
        int priceСallIndex = FindColumnIndex(headers, "CALL: Теоретическая цена"); 
        int pricePutIndex = FindColumnIndex(headers, "PUT: Теоретическая цена");
        
        //int priceСallIndex = FindColumnIndex(headers, "CALL: Расчетная цена");
        //int pricePutIndex = FindColumnIndex(headers, "PUT: Расчетная цена");

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
                PutOi = parts.Length > putOiIndex ? ParseDoubleWithSemicolon(parts[putOiIndex]) : 0,
                CallPrice = parts.Length > priceСallIndex ? ParseDoubleWithSemicolon(parts[priceСallIndex]) : 0,
                PutPrice = parts.Length > pricePutIndex ? ParseDoubleWithSemicolon(parts[pricePutIndex]) : 0,
            };

            // Добавляем, только если есть хотя бы какие-то данные
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
                Console.WriteLine($"Strike: {item.Strike}, Call OI: {item.CallOi}, Put OI: {item.PutOi}, IV: {item.Iv}");
            }

            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("Внимание: не найдено ни одной строки с данными!");
        }

        return result;
    }


    public static List<OptionData> ParseDeribitOptionData(string filePath, double currentPrice)
    {
        Console.WriteLine($"Чтение данных из файла Deribit: {filePath}");

        Dictionary<double, OptionData> optionsByStrike = new();

        string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);

        if (lines.Length <= 1)
        {
            throw new Exception("Файл не содержит данных или содержит только заголовок");
        }

        Console.WriteLine("Анализ структуры файла Deribit...");

        // Получаем индексы нужных колонок из заголовка (разделитель - запятая)
        string[] headers = lines[0].Split(',');

        // Необходимые колонки для Deribit формата
        int instrumentIndex = FindColumnIndex(headers, "Instrument");
        int openInterestIndex = TryFindColumnIndex(headers, "Open"); // Открытый интерес в Deribit
        int ivBidIndex = TryFindColumnIndex(headers, "IV Bid");
        int ivAskIndex = TryFindColumnIndex(headers, "IV Ask");
        int priceIndex = TryFindColumnIndex(headers, "Mark");
        int deltaIndex = TryFindColumnIndex(headers, "Δ|Delta");
        int gammaIndex = TryFindColumnIndex(headers, "Gamma");

        Console.WriteLine("Индексы колонок найдены. Обработка данных...");

        // Парсим данные из строк
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split(',');

            // Пропускаем неполные строки
            if (parts.Length <= instrumentIndex || string.IsNullOrWhiteSpace(parts[instrumentIndex]))
            {
                continue;
            }

            // Парсим имя инструмента, чтобы извлечь страйк и тип опциона (C/P)
            string instrument = parts[instrumentIndex];
            if (!TryParseInstrument(instrument, out double strike, out bool isCall))
            {
                continue;
            }

            // Вычисляем IV как среднее между IV Bid и IV Ask, если доступно
            double iv = 0;
            if (ivBidIndex >= 0 && ivAskIndex >= 0 && parts.Length > Math.Max(ivBidIndex, ivAskIndex))
            {
                double ivBid = ParseDoubleOrDefault(parts[ivBidIndex]);
                double ivAsk = ParseDoubleOrDefault(parts[ivAskIndex]);

                if (ivBid > 0 && ivAsk > 0)
                {
                    iv = (ivBid + ivAsk) / 2;
                }
                else if (ivBid > 0)
                {
                    iv = ivBid;
                }
                else if (ivAsk > 0)
                {
                    iv = ivAsk;
                }
            }

            // Получаем открытый интерес
            double openInterest = openInterestIndex >= 0 && parts.Length > openInterestIndex
                ? ParseDoubleOrDefault(parts[openInterestIndex])
                : 0;

            // Получаем стоимость опциона
            double optionPrice = priceIndex >= 0 && parts.Length > priceIndex
                ? ParseDoubleOrDefault(parts[priceIndex]) * currentPrice
                : 0;

            // Получаем дельту опциона
            double delta = deltaIndex >= 0 && parts.Length > deltaIndex
                ? ParseDoubleOrDefault(parts[deltaIndex])
                : 0;

            // Получаем гамму опциона
            double gamma = gammaIndex >= 0 && parts.Length > gammaIndex
                ? ParseDoubleOrDefault(parts[gammaIndex])
                : 0;


            // Проверяем, есть ли уже этот страйк в словаре
            if (optionsByStrike.TryGetValue(strike, out OptionData? existingOption))
            {
                // Обновляем существующий страйк
                if (isCall)
                {
                    existingOption.CallOi = openInterest;
                    existingOption.CallPrice = optionPrice;
                    existingOption.CallDelta = delta;
                    existingOption.CallGamma = gamma;
                    // Обновляем, IV только если есть значение и оно лучше текущего
                    if (iv > 0 && existingOption.Iv == 0)
                    {
                        existingOption.Iv = iv;
                    }
                }
                else
                {
                    existingOption.PutOi = openInterest;
                    existingOption.PutPrice = optionPrice;
                    existingOption.PutDelta = delta;
                    existingOption.PutGamma = gamma;
                    // Обновляем, IV только если есть значение и оно лучше текущего
                    if (iv > 0 && existingOption.Iv == 0)
                    {
                        existingOption.Iv = iv;
                    }
                }
            }
            else
            {
                // Создаем новую запись для страйка
                OptionData optionData = new OptionData
                {
                    Strike = strike,
                    Iv = iv,
                    CallOi = isCall ? openInterest : 0,
                    PutOi = isCall ? 0 : openInterest,
                    CallPrice = isCall ? optionPrice : 0,
                    PutPrice = isCall ? 0 : optionPrice,
                    CallDelta = isCall ? delta : 0,
                    PutDelta = isCall ? 0 : delta,
                    CallGamma = isCall ? gamma : 0,
                    PutGamma = isCall ? 0 : gamma,
                };

                optionsByStrike[strike] = optionData;
            }
        }

        // Конвертируем словарь в список
        List<OptionData> result = optionsByStrike.Values.OrderBy(o => o.Strike).ToList();

        Console.WriteLine($"Успешно обработано {result.Count} страйков данных из Deribit\n");

        // Выводим первые 5 строк для проверки
        if (result.Count > 0)
        {
            Console.WriteLine("Первые 5 страйков данных:");
            foreach (OptionData item in result.Take(5))
            {
                Console.WriteLine(
                    $"Strike: {item.Strike}, Call OI: {item.CallOi}, Put OI: {item.PutOi}, IV: {item.Iv}");
            }

            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("Внимание: не найдено ни одного страйка с данными!");
        }

        return result;
    }

    public static void CalculateCallPutRatio(List<OptionData> data)
    {
        double totalCallOi = data.Sum(d => d.CallOi);
        double totalPutOi = data.Sum(d => d.PutOi);
        double callPutRatio = totalPutOi > 0 ? totalCallOi / (totalCallOi + totalPutOi) : 0;
        double putCallRatio = totalPutOi > 0 ? totalPutOi / (totalCallOi + totalPutOi) : 0;

        double optionRatio = totalPutOi > 0 ? totalCallOi / totalPutOi : 0;

        Console.WriteLine("АНАЛИЗ СООТНОШЕНИЯ CALL/PUT");
        Console.WriteLine($"Общий объем Call опционов: {totalCallOi:F2}");
        Console.WriteLine($"Общий объем Put опционов: {totalPutOi:F2}");
        Console.WriteLine($"Call/Put Ratio: {callPutRatio:F2}");
        Console.WriteLine($"Put/Call Ratio: {putCallRatio:F2}");

        if (totalCallOi > totalPutOi && totalPutOi > 0)
        {
            Console.WriteLine($"Call опционов больше в {totalCallOi / totalPutOi:F2} раза.");
        }
        else if (totalPutOi > totalCallOi && totalCallOi > 0)
        {
            Console.WriteLine($"Put опционов больше в {totalPutOi / totalCallOi:F2} раза.");
        }

        if (optionRatio > 1.5)
        {
            Console.WriteLine(
                "Интерпретация: Значительный перевес в сторону Call опционов. Рынок демонстрирует бычьи настроения.");
        }
        else if (optionRatio is >= 0.75 and <= 1.5)
        {
            Console.WriteLine("Интерпретация: Относительно сбалансированное соотношение Call и Put опционов.");
        }
        else if (optionRatio > 0)
        {
            Console.WriteLine(
                "Интерпретация: Значительный перевес в сторону Put опционов. Рынок демонстрирует медвежьи настроения.");
        }

        Console.WriteLine();
    }

    public static void CalculateMaxPain(List<OptionData> data, double currentPrice)
    {
        Console.WriteLine("РАСЧЕТ MAX PAIN");

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
                $"Цена находится ниже уровня Max Pain. Теоретически это создает потеницал в сторону роста к уровню {maxPainStrike}.");
        }
        else
        {
            Console.WriteLine(
                $"Цена находится выше уровня Max Pain. Теоретически это создает давление в сторону снижения к уровню {maxPainStrike}.");
        }

        Console.WriteLine();

        // Анализ скорости прироста убытков
        AnalyzePainGradient(data, maxPainStrike, currentPrice);
    }


    private static void AnalyzePainGradient(List<OptionData> data, double maxPainStrike, double currentPrice)
    {
        Console.WriteLine("АНАЛИЗ СКОРОСТИ ПРИРОСТА УБЫТКОВ");

        // Определяем шаг для расчета скорости изменения (например, 1% от maxPainStrike)
        double step = maxPainStrike * 0.01;

        // 1. АНАЛИЗ ОТНОСИТЕЛЬНО MAX PAIN
        Console.WriteLine("\n1. АНАЛИЗ ОТНОСИТЕЛЬНО MAX PAIN:");

        // Рассчитываем убытки на уровне MaxPain
        double maxPainLosses = CalculateTotalLossesAtPrice(data, maxPainStrike);
        double callLossesAtMaxPain = CalculateCallLossesAtPrice(data, maxPainStrike);
        double putLossesAtMaxPain = CalculatePutLossesAtPrice(data, maxPainStrike);

        Console.WriteLine($"Убытки на уровне Max Pain ({maxPainStrike:F2}):");
        Console.WriteLine($"Общие убытки: {maxPainLosses:N0}");
        Console.WriteLine($"Убытки Call опционов: {callLossesAtMaxPain:N0}");
        Console.WriteLine($"Убытки Put опционов: {putLossesAtMaxPain:N0}");

        // Рассчитываем убытки выше MaxPain
        double upLevel = maxPainStrike + step;
        double totalLossesUp = CalculateTotalLossesAtPrice(data, upLevel);
        double callLossesUp = CalculateCallLossesAtPrice(data, upLevel);
        double putLossesUp = CalculatePutLossesAtPrice(data, upLevel);

        // Рассчитываем убытки ниже MaxPain
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

        Console.WriteLine("\nИЗМЕНЕНИЕ УБЫТКОВ ПРИ ДВИЖЕНИИ ЦЕНЫ ОТ MAX PAIN:");
        Console.WriteLine($"При движении ВВЕРХ на {step:F2} пунктов от Max Pain:");
        Console.WriteLine(
            $"  Общие убытки: +{totalLossesUp - maxPainLosses:N0} (скорость: {totalGradientUp:N0} на пункт)");
        Console.WriteLine(
            $"  Убытки Call: +{callLossesUp - callLossesAtMaxPain:N0} (скорость: {callGradientUp:N0} на пункт)");
        Console.WriteLine(
            $"  Убытки Put: {(putLossesUp - putLossesAtMaxPain >= 0 ? "+" : "")}{putLossesUp - putLossesAtMaxPain:N0} (скорость: {putGradientUp:N0} на пункт)");

        Console.WriteLine($"\nПри движении ВНИЗ на {step:F2} пунктов от Max Pain:");
        Console.WriteLine(
            $"  Общие убытки: +{totalLossesDown - maxPainLosses:N0} (скорость: {totalGradientDown:N0} на пункт)");
        Console.WriteLine(
            $"  Убытки Call: {(callLossesDown - callLossesAtMaxPain >= 0 ? "+" : "")}{callLossesDown - callLossesAtMaxPain:N0} (скорость: {callGradientDown:N0} на пункт)");
        Console.WriteLine(
            $"  Убытки Put: +{putLossesDown - putLossesAtMaxPain:N0} (скорость: {putGradientDown:N0} на пункт)");

        // 2. АНАЛИЗ ОТНОСИТЕЛЬНО ТЕКУЩЕЙ ЦЕНЫ
        Console.WriteLine("\n2. АНАЛИЗ ОТНОСИТЕЛЬНО ТЕКУЩЕЙ ЦЕНЫ:");
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

        Console.WriteLine($"При движении ВВЕРХ на {step:F2} пунктов от текущей цены:");
        Console.WriteLine(
            $"  Общие убытки: {(totalLossesCurrentUp - currentPriceLosses >= 0 ? "+" : "")}{totalLossesCurrentUp - currentPriceLosses:N0} (скорость: {totalGradientCurrentUp:N0} на пункт)");
        Console.WriteLine(
            $"  Убытки Call: {(callLossesCurrentUp - callLossesAtCurrent >= 0 ? "+" : "")}{callLossesCurrentUp - callLossesAtCurrent:N0} (скорость: {callGradientCurrentUp:N0} на пункт)");
        Console.WriteLine(
            $"  Убытки Put: {(putLossesCurrentUp - putLossesAtCurrent >= 0 ? "+" : "")}{putLossesCurrentUp - putLossesAtCurrent:N0} (скорость: {putGradientCurrentUp:N0} на пункт)");

        Console.WriteLine($"\nПри движении ВНИЗ на {step:F2} пунктов от текущей цены:");
        Console.WriteLine(
            $"  Общие убытки: {(totalLossesCurrentDown - currentPriceLosses >= 0 ? "+" : "")}{totalLossesCurrentDown - currentPriceLosses:N0} (скорость: {totalGradientCurrentDown:N0} на пункт)");
        Console.WriteLine(
            $"  Убытки Call: {(callLossesCurrentDown - callLossesAtCurrent >= 0 ? "+" : "")}{callLossesCurrentDown - callLossesAtCurrent:N0} (скорость: {callGradientCurrentDown:N0} на пункт)");
        Console.WriteLine(
            $"  Убытки Put: {(putLossesCurrentDown - putLossesAtCurrent >= 0 ? "+" : "")}{putLossesCurrentDown - putLossesAtCurrent:N0} (скорость: {putGradientCurrentDown:N0} на пункт)");

        // 3. АНАЛИЗ ВЕРОЯТНОГО ДИАПАЗОНА И РАВНОВЕСИЯ
        double painThresholdPercent = 0.10; // 10% прирост убытков как порог
        double painThreshold = maxPainLosses * painThresholdPercent;

        // Находим примерные границы, где убытки вырастут на 10%
        double upperBound = EstimateThresholdLevel(data, maxPainStrike, maxPainLosses, painThreshold, step, true);
        double lowerBound = EstimateThresholdLevel(data, maxPainStrike, maxPainLosses, painThreshold, step, false);

        double equilibriumGradients = CalculateGradientEquilibriumLevel(data, maxPainStrike, step);

        Console.WriteLine($"\n3. ДИАПАЗОНЫ И УРОВНИ РАВНОВЕСИЯ:");
        Console.WriteLine($"При увеличении убытков на {painThresholdPercent * 100}% от минимального уровня:");
        Console.WriteLine($"Вероятный верхний уровень диапазона цены: {upperBound:F2}");
        Console.WriteLine($"Вероятный нижний уровень диапазона цены: {lowerBound:F2}");
        Console.WriteLine($"Ожидаемый диапазон движения цены: {lowerBound:F2} - {upperBound:F2} " +
                          $"({(upperBound - lowerBound) / maxPainStrike * 100:F2}% от Max Pain)");

        Console.WriteLine($"Уровень равновесия по скорости прироста убытков: {equilibriumGradients:F2}");

        // Положение текущей цены относительно равновесия
        Console.WriteLine("\nПоложение текущей цены относительно уровней:");
        Console.WriteLine(
            $"Отклонение от уровня равновесия: {(currentPrice - equilibriumGradients >= 0 ? "+" : "")}{currentPrice - equilibriumGradients:F2} ({(currentPrice - equilibriumGradients) / equilibriumGradients * 100:F2}%)");

        if (currentPrice >= lowerBound && currentPrice <= upperBound)
        {
            Console.WriteLine("Текущая цена находится в пределах ожидаемого диапазона движения.");
        }
        else if (currentPrice < lowerBound)
        {
            Console.WriteLine("Текущая цена ниже ожидаемого диапазона движения, возможен возврат в диапазон.");
        }
        else // currentPrice > upperBound
        {
            Console.WriteLine("Текущая цена выше ожидаемого диапазона движения, возможен возврат в диапазон.");
        }

        // 4. ИНТЕРПРЕТАЦИЯ
        Console.WriteLine("\n4. ИНТЕРПРЕТАЦИЯ:");

        // Интерпретация относительно Max Pain
        Console.WriteLine("\nОТНОСИТЕЛЬНО MAX PAIN:");
        if (Math.Abs(totalGradientUp) > Math.Abs(totalGradientDown) * 1.5)
        {
            Console.WriteLine("Общая скорость прироста убытков значительно выше при движении цены ВВЕРХ от Max Pain. " +
                              "Это указывает на повышенное сопротивление при движении цены выше Max Pain и может " +
                              "создавать давление в сторону снижения.");
        }
        else if (Math.Abs(totalGradientDown) > Math.Abs(totalGradientUp) * 1.5)
        {
            Console.WriteLine("Общая скорость прироста убытков значительно выше при движении цены ВНИЗ от Max Pain. " +
                              "Это указывает на повышенную поддержку при снижении цены ниже Max Pain и может " +
                              "создавать давление в сторону роста.");
        }
        else
        {
            Console.WriteLine("Общая скорость прироста убытков относительно сбалансирована в обоих направлениях. " +
                              "Давление на цену со стороны опционов примерно одинаково как вверх, так и вниз.");
        }

        // Интерпретация относительно текущей цены
        Console.WriteLine("\nОТНОСИТЕЛЬНО ТЕКУЩЕЙ ЦЕНЫ:");
        if (Math.Abs(totalGradientCurrentUp) > Math.Abs(totalGradientCurrentDown) * 1.5)
        {
            Console.WriteLine(
                "Общая скорость прироста убытков значительно выше при движении цены ВВЕРХ от текущей цены. " +
                "Это указывает на повышенное сопротивление дальнейшему росту цены.");
        }
        else if (Math.Abs(totalGradientCurrentDown) > Math.Abs(totalGradientCurrentUp) * 1.5)
        {
            Console.WriteLine(
                "Общая скорость прироста убытков значительно выше при движении цены ВНИЗ от текущей цены. " +
                "Это указывает на повышенную поддержку текущим уровням цены.");
        }
        else
        {
            Console.WriteLine(
                "Общая скорость прироста убытков относительно сбалансирована в обоих направлениях от текущей цены. " +
                "Давление на цену со стороны опционов примерно одинаково как вверх, так и вниз.");
        }

        Console.WriteLine("\nАНАЛИЗ ПО ТИПАМ ОПЦИОНОВ:");
        if (callGradientUp > putGradientDown * 1.2)
        {
            Console.WriteLine("Скорость прироста убытков Call опционов при движении вверх превышает скорость " +
                              "прироста убытков Put опционов при движении вниз. Это может создавать более " +
                              "сильное сопротивление росту, чем поддержку при снижении.");
        }
        else if (putGradientDown > callGradientUp * 1.2)
        {
            Console.WriteLine("Скорость прироста убытков Put опционов при движении вниз превышает скорость " +
                              "прироста убытков Call опционов при движении вверх. Это может создавать более " +
                              "сильную поддержку при снижении, чем сопротивление росту.");
        }
        else
        {
            Console.WriteLine("Скорости прироста убытков Call и Put опционов примерно сбалансированы, " +
                              "что указывает на равномерное давление в обоих направлениях.");
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
            Console.WriteLine($"Страйк: {level.Strike}, Общий объем: {level.CallOi + level.PutOi:F2}, Тип: {type}");
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

    public static void AnalyzeCentersOfGravity(List<OptionData> data, double currentPrice)
    {
        Console.WriteLine("АНАЛИЗ ЦЕНТРОВ ТЯЖЕСТИ");

        double totalCallOi = data.Sum(d => d.CallOi);
        double totalPutOi = data.Sum(d => d.PutOi);

        // Расчет центра тяжести для Call и Put
        double callCenter = data.Sum(d => d.Strike * d.CallOi) / totalCallOi;
        double putCenter = data.Sum(d => d.Strike * d.PutOi) / totalPutOi;
        // Расчет равновестной цены центра тяжести.
        double gravityPrice = (callCenter + putCenter) / 2;

        Console.WriteLine($"Центр тяжести Call: {callCenter:F2}");
        Console.WriteLine($"Центр тяжести Put: {putCenter:F2}");
        Console.WriteLine($"Равновестная цена центра тяжести: {gravityPrice:F2}");

        // Расчет убытков на уровнях центров тяжести
        double callCenterLosses = CalculateTotalLossesAtPrice(data, callCenter);
        double putCenterLosses = CalculateTotalLossesAtPrice(data, putCenter);
        double gravityPriceLosses = CalculateTotalLossesAtPrice(data, gravityPrice);

        Console.WriteLine($"Убытки на уровне центра тяжести Call: {callCenterLosses:N0}");
        Console.WriteLine($"Убытки на уровне центра тяжести Put: {putCenterLosses:N0}");
        Console.WriteLine($"Убытки на уровне равновестной цены: {gravityPriceLosses:N0}");


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

        if (gravityPrice > currentPrice)
        {
            Console.WriteLine(putCenter > currentPrice
                ? $"Потенциал роста к {putCenter:F2},  признак перепроданности."
                : $"Потенциал роста к {gravityPrice:F2}, поддержка на уровне {putCenter:F2}.");
        }
        else if (currentPrice > gravityPrice)
        {
            Console.WriteLine(currentPrice > callCenter
                ? $"Потенциал снижения к {callCenter:F2}, признак перекупленности."
                : $"Потенциал снижения к {gravityPrice:F2}, сопротивление на уровне {callCenter:F2}");
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

        Console.WriteLine($"\nОбщий открытый интерес: {totalOi:F2} контрактов.");

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

        Console.WriteLine("На основе проведенного анализа можно сделать следующие выводы:");
        Console.WriteLine(
            $"1. Уровень Max Pain: {maxPainStrike} (текущая цена {(currentPrice > maxPainStrike ? "выше" : "ниже")})");
        Console.WriteLine(
            $"2. Соотношение Call/Put: {callPutRatio:F2} ({(callPutRatio > 1.2 ? "бычий" : callPutRatio < 0.8 ? "медвежий" : "нейтральный")} настрой)");
        Console.WriteLine($"3. Центры тяжести: Call = {callCenter:F2}, Put = {putCenter:F2}");
        Console.WriteLine($"4. Уровнь равновесия: {(callCenter + putCenter) / 2:F2}");

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


    public static void AnalyzeGlobalSellerPosition(List<OptionData> data, double currentPrice)
    {
        Console.WriteLine("АНАЛИЗ ПОЗИЦИИ ГЛОБАЛЬНОГО ПРОДАВЦА ВСЕХ ОПЦИОНОВ");
        Console.WriteLine("=====================================================");

        // 1. Подсчитываем общую полученную премию продавцом
        double totalCallPremium = 0;
        double totalPutPremium = 0;

        foreach (OptionData option in data)
        {
            totalCallPremium += option.CallPrice * option.CallOi;
            totalPutPremium += option.PutPrice * option.PutOi;
        }

        double totalPremium = totalCallPremium + totalPutPremium;

        Console.WriteLine($"Текущая цена базового актива: {currentPrice:F2}");
        Console.WriteLine($"Общая полученная премия: {totalPremium:N0}");
        Console.WriteLine($"  - От Call опционов: {totalCallPremium:N0} ({totalCallPremium / totalPremium * 100:F1}%)");
        Console.WriteLine($"  - От Put опционов: {totalPutPremium:N0} ({totalPutPremium / totalPremium * 100:F1}%)");

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

        Console.WriteLine("\nПОЗИЦИЯ ПРОДАВЦА ПРИ ТЕКУЩЕЙ ЦЕНЕ:");
        Console.WriteLine($"PnL при {currentPnL.Price:F2}: {currentPnL.TotalPnL:N0}");
        Console.WriteLine($"  - PnL от Call опционов: {currentPnL.CallPnL:N0}");
        Console.WriteLine($"  - PnL от Put опционов: {currentPnL.PutPnL:N0}");

        if (currentPnL.TotalPnL >= 0)
        {
            Console.WriteLine("✓ ПРОДАВЕЦ В ПРИБЫЛИ на текущем уровне цены");
        }
        else
        {
            Console.WriteLine("✗ ПРОДАВЕЦ В УБЫТКЕ на текущем уровне цены");
            Console.WriteLine($"  Размер убытка: {Math.Abs(currentPnL.TotalPnL):N0} ({Math.Abs(currentPnL.TotalPnL) / totalPremium * 100:F1}% от полученной премии)");
        }

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

        Console.WriteLine("\nБЕЗУБЫТОЧНЫЕ ЗОНЫ ДЛЯ ПРОДАВЦА:");
        if (profitZones.Count == 0)
        {
            Console.WriteLine("Продавец в убытке на всём рассматриваемом диапазоне цен.");
        }
        else
        {
            foreach ((double lower, double upper) in profitZones)
            {
                Console.WriteLine($"Зона безубытка: {lower:F2} - {upper:F2} ({(upper - lower) / currentPrice * 100:F2}% от текущей цены)");
            }
        }

        // 6. Находим точки максимальной прибыли и максимального убытка
        (double Price, double TotalPnL, double CallPnL, double PutPnL) maxProfitPoint =
            pnlData.OrderByDescending(p => p.TotalPnL).First();
        (double Price, double TotalPnL, double CallPnL, double PutPnL) maxLossPoint =
            pnlData.OrderBy(p => p.TotalPnL).First();

        Console.WriteLine("\nКЛЮЧЕВЫЕ УРОВНИ:");
        Console.WriteLine($"Максимальная прибыль: {maxProfitPoint.TotalPnL:N0} при цене {maxProfitPoint.Price:F2}");
        Console.WriteLine($"Максимальный убыток: {maxLossPoint.TotalPnL:N0} при цене {maxLossPoint.Price:F2}");

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

        Console.WriteLine("\nТОЧКИ БЕЗУБЫТКА (ZERO-CROSS LEVELS):");
        if (breakEvenPoints.Count == 0)
        {
            Console.WriteLine("Точек безубытка не найдено в анализируемом диапазоне цен.");
        }
        else
        {
            foreach (double point in breakEvenPoints)
            {
                Console.WriteLine($"Точка безубытка: {point:F2} ({(point - currentPrice) / currentPrice * 100:F2}% от текущей цены)");
            }

            // Находим ближайшие точки безубытка снизу и сверху от текущей цены
            double? lowerBreakEven = breakEvenPoints.Where(p => p < currentPrice).DefaultIfEmpty(double.NaN).Max();
            double? upperBreakEven = breakEvenPoints.Where(p => p > currentPrice).DefaultIfEmpty(double.NaN).Min();

            Console.WriteLine("\nБЛИЖАЙШИЕ ТОЧКИ БЕЗУБЫТКА К ТЕКУЩЕЙ ЦЕНЕ:");
            if (!double.IsNaN((double)lowerBreakEven))
            {
                Console.WriteLine(
                    $"Нижняя точка безубытка: {lowerBreakEven:F2} ({(lowerBreakEven - currentPrice) / currentPrice * 100:F2}%)");
            }
            else
            {
                Console.WriteLine("Нижняя точка безубытка не найдена в анализируемом диапазоне.");
            }

            if (!double.IsNaN((double)upperBreakEven))
            {
                Console.WriteLine($"Верхняя точка безубытка: {upperBreakEven:F2} ({(upperBreakEven - currentPrice) / currentPrice * 100:F2}%)");
            }
            else
            {
                Console.WriteLine("Верхняя точка безубытка не найдена в анализируемом диапазоне.");
            }

            // Определяем вероятный диапазон движения цены
            if (!double.IsNaN((double)lowerBreakEven) && !double.IsNaN((double)upperBreakEven))
            {
                Console.WriteLine($"\nВЕРОЯТНЫЙ ДИАПАЗОН ДВИЖЕНИЯ ЦЕНЫ: {lowerBreakEven:F2} - {upperBreakEven:F2} " +
                                  $"({(upperBreakEven - lowerBreakEven) / currentPrice * 100:F2}% от текущей цены)");

                // Находим преобладающую силу: растущую или падающую
                double distanceToLower = currentPrice - (double)lowerBreakEven;
                double distanceToUpper = (double)upperBreakEven - currentPrice;

                if (distanceToLower * 1.2 < distanceToUpper)
                {
                    Console.WriteLine("⚠ ВНИМАНИЕ: Текущая цена значительно ближе к нижней точке безубытка.");
                    Console.WriteLine("   Это указывает на повышенный риск пробоя вниз и возможное значительное падение цены.");
                }
                else if (distanceToUpper * 1.2 < distanceToLower)
                {
                    Console.WriteLine("⚠ ВНИМАНИЕ: Текущая цена значительно ближе к верхней точке безубытка.");
                    Console.WriteLine("   Это указывает на повышенный риск пробоя вверх и возможное значительное повышение цены.");
                }
                else
                {
                    Console.WriteLine("Текущая цена находится в относительно сбалансированном положении между точками безубытка.");
                }
            }
        }

        // 8. Интерпретация и рекомендация
        Console.WriteLine("\nИНТЕРПРЕТАЦИЯ И РЕКОМЕНДАЦИЯ:");

        // Анализ относительного положения
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

                if (rangePosition < 0.2)
                {
                    Console.WriteLine("Текущая цена находится очень близко к нижней точке безубытка.");
                    Console.WriteLine("Рекомендация: Существует высокая вероятность отскока вверх или пробоя вниз. Возможны среднесрочные длинные позиции с защитным стоп-лоссом ниже точки безубытка.");
                }
                else if (rangePosition > 0.8)
                {
                    Console.WriteLine("Текущая цена находится очень близко к верхней точке безубытка.");
                    Console.WriteLine("Рекомендация: Существует высокая вероятность отскока вниз или пробоя вверх. Возможны среднесрочные короткие позиции с защитным стоп-лоссом выше точки безубытка.");
                }
                else if (rangePosition >= 0.4 && rangePosition <= 0.6)
                {
                    Console.WriteLine("Текущая цена находится примерно в середине диапазона между точками безубытка.");
                    Console.WriteLine("Рекомендация: Боковое движение наиболее вероятно. Рассмотрите стратегии диапазонной торговли между точками безубытка.");
                }
                else if (rangePosition < 0.4)
                {
                    Console.WriteLine("Текущая цена находится ближе к нижней точке безубытка.");
                    Console.WriteLine("Рекомендация: Повышенная вероятность движения вверх в среднесрочной перспективе. Предпочтительны длинные позиции.");
                }
                else // rangePosition > 0.6
                {
                    Console.WriteLine("Текущая цена находится ближе к верхней точке безубытка.");
                    Console.WriteLine("Рекомендация: Повышенная вероятность движения вниз в среднесрочной перспективе. Предпочтительны короткие позиции.");
                }
            }
        }

        if (currentPnL.TotalPnL < 0 && profitZones.Count > 0)
        {
            Console.WriteLine("\nПродавец опционов сейчас в убытке, что создает давление на движение цены к ближайшей зоне безубытка.");

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

            if (closest.Zone.LowerBound <= currentPrice && closest.Zone.UpperBound >= currentPrice)
            {
                Console.WriteLine("Текущая цена находится внутри зоны безубытка.");
            }
            else if (Math.Abs(closest.Zone.LowerBound - currentPrice) <
                     Math.Abs(closest.Zone.UpperBound - currentPrice))
            {
                Console.WriteLine($"Ближайший уровень безубытка находится снизу на уровне {closest.Zone.LowerBound:F2}.");
                Console.WriteLine("Возможно давление на цену в сторону снижения.");
            }
            else
            {
                Console.WriteLine($"Ближайший уровень безубытка находится сверху на уровне {closest.Zone.UpperBound:F2}.");
                Console.WriteLine("Возможно давление на цену в сторону повышения.");
            }
        }
        
        // Если нет данных по дельте, не делаем анализ.
        if (data.All(x=> x.CallDelta is 0 || x.PutDelta == 0))
        {
            return;
        }
        
        Console.WriteLine("=== Delta/Gamma Exposure анализ ===");
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

        Console.WriteLine($"Delta Exposure продавцов: {totalDeltaSellers:N2}");
        Console.WriteLine($"    Call Delta: {totalCallDelta:N2}   Put Delta: {totalPutDelta:N2}");
        Console.WriteLine($"Gamma Exposure продавцов: {totalGammaSellers:N2}");
        Console.WriteLine($"    Call Gamma: {totalCallGamma:N2}   Put Gamma: {totalPutGamma:N2}");
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


    // Метод для определения формата файла
    public static bool IsDeribitFormat(string path)
    {
        try
        {
            string? firstLine = File.ReadLines(path).FirstOrDefault();
            if (string.IsNullOrEmpty(firstLine))
                return false;

            // Проверяем наличие характерных заголовков для Deribit
            return firstLine.Contains("Instrument") &&
                   !firstLine.Contains(';'); // Deribit использует запятые, а не точки с запятой
        }
        catch
        {
            return false;
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

        // Удаляем все пробелы и заменяем запятую на точку.
        string cleanValue = value.Replace(" ", "").Replace(",", ".");

        if (double.TryParse(cleanValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
        {
            return result;
        }

        return 0;
    }


    // Вспомогательный метод для парсинга информации из имени инструмента
    private static bool TryParseInstrument(string instrument, out double strike, out bool isCall)
    {
        strike = 0;
        isCall = false;

        // Формат: BTC-25APR25-40000-C (или -P для Put)
        string[] parts = instrument.Split('-');
        if (parts.Length < 4)
        {
            return false;
        }

        // Страйк находится в третьей части (индекс 2)
        if (!double.TryParse(parts[2], out strike))
        {
            return false;
        }

        // Тип опциона (Call/Put) находится в последней части
        string optionType = parts[3];
        isCall = optionType.EndsWith("C", StringComparison.InvariantCultureIgnoreCase);

        return true;
    }

    // Безопасный поиск колонки, возвращает -1, если не найдена
    private static int TryFindColumnIndex(string[] headers, string columnName)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            if (headers[i].Trim() == columnName)
            {
                return i;
            }
        }

        return -1;
    }

    // Безопасный парсинг double, возвращает 0, если не удалось распарсить
    private static double ParseDoubleOrDefault(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return 0;
        }

        // Удаляем все пробелы и заменяем запятую на точку
        string cleanValue = value.Replace(" ", "").Replace(",", ".");

        if (double.TryParse(cleanValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
        {
            return result;
        }

        return 0;
    }

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

    #endregion Private

    #region MaxPainCoinGlass

    public static void CalculateMaxPainCoinGlass(List<OptionData> data, double currentPrice)
    {
        List<PainResult> painResults = new();

        // Для каждого возможного уровня цены (страйка)
        foreach (OptionData targetOption in data)
        {
            double targetStrike = targetOption.Strike;
            double totalPain = 0;

            // Рассчитываем стоимость всех опционов "в деньгах" при этой цене
            foreach (OptionData option in data)
            {
                // Call опционы "в деньгах", если цена базового актива выше страйка
                if (targetStrike > option.Strike)
                {
                    totalPain += option.CallOi * (targetStrike - option.Strike);
                }

                // Put опционы "в деньгах", если цена базового актива ниже страйка
                if (targetStrike < option.Strike)
                {
                    totalPain += option.PutOi * (option.Strike - targetStrike);
                }
            }

            painResults.Add(new PainResult
            {
                Strike = targetStrike,
                TotalValue = totalPain
            });
        }

        // Max Pain - это страйк с МИНИМАЛЬНОЙ общей стоимостью
        painResults = painResults.OrderBy(p => p.TotalValue).ToList();

        // Находим страйк с минимальной общей стоимостью опционов (Max Pain)
        PainResult maxPainResult = painResults.First();
        double maxPainStrike = maxPainResult.Strike;

        Console.WriteLine($"Уровень Max Pain по расчетам Coinglass: {maxPainStrike}");
        Console.WriteLine(
            $"Текущая цена от Max Pain по расчетам Coinglass: {currentPrice - maxPainStrike:F2} пунктов ({(currentPrice - maxPainStrike) / maxPainStrike * 100:F2}%)");


        // Выводим топ-5 страйков с наименьшими убытками
        Console.WriteLine("\nТоп-5 страйков с наименьшими общими убытками (потенциальные уровни притяжения):");
        for (int i = 0; i < Math.Min(5, painResults.Count); i++)
        {
            Console.WriteLine(
                $"{i + 1}. Страйк: {painResults[i].Strike}, Общие убытки: {painResults[i].TotalValue:N0}");
        }


        Console.WriteLine();
    }

    #endregion
}