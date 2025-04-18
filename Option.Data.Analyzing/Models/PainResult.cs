﻿namespace Option.Data.Analyzing.Models;

public record PainResult
{
    /// <summary>
    /// Страйк.
    /// </summary>
    public double Strike { get; set; }

    /// <summary>
    ///  Убытки по Call опционам.
    /// </summary>
    public double CallLosses { get; set; }

    /// <summary>
    /// Убытки по Put опционам.
    /// </summary>
    public double PutLosses { get; set; }

    /// <summary>
    /// Общие убытки.
    /// </summary>
    public double TotalLosses { get; set; }
}