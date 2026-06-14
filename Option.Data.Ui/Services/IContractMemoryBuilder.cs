using Option.Data.Shared.Dto;
using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>Один срез истории экспирации/окна для построения «памяти контракта».</summary>
public readonly record struct MemorySnapshot(
    DateTimeOffset Time,
    double Spot,
    IReadOnlyList<SessionAnalysisMath.GammaStrike> Strikes,
    IReadOnlyList<OptionData> Chain);

public interface IContractMemoryBuilder
{
    /// <summary>
    /// Строит <see cref="ContractMemory"/> по истории срезов (по возрастанию времени; последний — текущий).
    /// </summary>
    /// <param name="history">Срезы окна/серии по возрастанию времени.</param>
    /// <param name="currentExpirationOi">ΣOI выбранной серии/окна на текущем срезе.</param>
    /// <param name="maxExpirationOi">Макс. ΣOI среди всех серий снимка на текущем срезе (для репрезентативности).</param>
    ContractMemory Build(IReadOnlyList<MemorySnapshot> history, double currentExpirationOi, double maxExpirationOi);
}
