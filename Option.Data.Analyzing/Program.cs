using System.Text;
using Option.Data.Analyzing;
using Option.Data.Shared.Dto;

// Регистрируем провайдер кодировок для работы с Windows-1251.
EncodingProvider provider = CodePagesEncodingProvider.Instance;
Encoding.RegisterProvider(provider);


Console.OutputEncoding = Encoding.UTF8;

string filePath = @"C:\Users\vadim\Downloads\data.csv";
if (args.Length > 0)
{
    filePath = args[0];
    if (!File.Exists(filePath))
    {
        throw new FileNotFoundException("The specified file does not exist.", filePath);
    }
}

try
{
    Console.WriteLine("Введите текущий уровень цены фьючерса:");
    double currentPrice = double.TryParse(Console.ReadLine(), out currentPrice) ? currentPrice : 0;

    // Определяем тип CSV файла и парсим данные соответственно.
    List<OptionData> data;
    
    if (Functions.IsDeribitFormat(filePath))
    {
        Console.WriteLine("Обнаружен формат Deribit");
        data = Functions.ParseDeribitOptionData(filePath, currentPrice);
    }
    else
    {
        Console.WriteLine("Обнаружен формат MOEX");
        data = Functions.ParseMoexOptionData(filePath);
    }


    // Расчет текущей цены (можно указать вручную или взять из других источников)
    // Можно получать из параметров или из других источников
    Console.WriteLine($"Текущая цена фьючерса: {currentPrice}");

    // Расчет Call/Put Ratio
    Functions.CalculateCallPutRatio(data);

    // Расчет Max Pain
    Functions.CalculateMaxPain(data, currentPrice);
    
    // Расчет Max Pain по алгоритму Coinglass
    //Functions.CalculateMaxPainCoinGlass(data, currentPrice);

    // Анализ распределения открытого интереса
    Functions.AnalyzeOpenInterest(data);

    // Расчет прибыли/убытков при текущей цене
    Functions.CalculateProfitLoss(data, currentPrice);

    // Анализ по центрам тяжести
    Functions.AnalyzeCentersOfGravity(data, currentPrice);

    // Дополнительный анализ для прогноза движения цены
    Functions.AnalyzePriceMovementPotential(data, currentPrice);
    
    Functions.AnalyzeGlobalSellerPosition(data, currentPrice);

}
catch (Exception ex)
{
    Console.WriteLine($"Ошибка при обработке файла: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine("\nНажмите любую клавишу для завершения...");
Console.ReadKey();