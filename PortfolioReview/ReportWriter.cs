using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortfolioReview;

public static class ReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Generic so ReportExplainer can serialise a single portfolio with the same settings.
    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static void Write(Report report, string path) => File.WriteAllText(path, ToJson(report));
}
