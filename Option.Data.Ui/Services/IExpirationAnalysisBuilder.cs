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
