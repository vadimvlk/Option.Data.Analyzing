using System.Text;
using Option.Data.Analyzing;
using Option.Data.Analyzing.Models;

Console.OutputEncoding = Encoding.UTF8;

string filePath = @"C:\Users\vadim\Downloads\moex-MIX.csv";
if (args.Length > 0)
{
    filePath = args[0];
}

try
{
    Console.WriteLine("Введите текущий уровень цены фьючерса:");
    double currentPrice = double.TryParse(Console.ReadLine(), out currentPrice) ? currentPrice : 0;

    // Чтение и парсинг данных из оригинального CSV-файла биржи
    List<OptionData> data = Functions.ParseMoexOptionData(filePath);

    // Расчет текущей цены (можно указать вручную или взять из других источников)
    // Можно получать из параметров или из других источников
    Console.WriteLine($"Текущая цена фьючерса: {currentPrice}");

    // Расчет Call/Put Ratio
    Functions.CalculateCallPutRatio(data);

    // Расчет Max Pain
    Functions.CalculateMaxPain(data, currentPrice);

    // Анализ распределения открытого интереса
    Functions.AnalyzeOpenInterest(data);

    // Расчет прибыли/убытков при текущей цене
    Functions.CalculateProfitLoss(data, currentPrice);

    // Анализ по центрам тяжести
    Functions.AnalyzeCentersOfGravity(data);

    // Дополнительный анализ для прогноза движения цены
    Functions.AnalyzePriceMovementPotential(data, currentPrice);
}
catch (Exception ex)
{
    Console.WriteLine($"Ошибка при обработке файла: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine("\nНажмите любую клавишу для завершения...");
Console.ReadKey();