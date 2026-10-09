using System.Text.Json;
using Anthropic.Models.Messages;

namespace PortfolioReview;

// What a tool returns to the model. IsError tells the model the call failed, so it can explain
// the problem instead of treating the message as data.
public record ToolResult(string Content, bool IsError);

// Two read-only tools over the report that has already been calculated.
// Nothing here reloads files or calculates anything; every failure becomes a ToolResult, never an exception.
public sealed class PortfolioTools(Report report)
{
    public const string GetPortfolioReport = "get_portfolio_report";
    public const string GetPolicy = "get_policy";

    public static readonly IReadOnlyList<ToolUnion> Definitions =
    [
        new Tool
        {
            Name = GetPortfolioReport,
            Description = "Returns one portfolio's calculated exposure review: valuation date, valuation status, "
                + "total value of priced holdings, whether exposures are provisional, instrument and sector exposures "
                + "with breach flags, and the list of issues (missing prices, stale prices, limit breaches).",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["portfolio_id"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Portfolio identifier, for example \"P2\"." }),
                },
                Required = ["portfolio_id"],
            },
        },
        new Tool
        {
            Name = GetPolicy,
            Description = "Returns the policy limits used in the review: maximum instrument exposure (%), "
                + "maximum sector exposure (%) and maximum price age (days). Takes no arguments.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },
    ];

    public ToolResult Execute(string name, IReadOnlyDictionary<string, JsonElement> input) => name switch
    {
        GetPortfolioReport => ExecuteGetPortfolioReport(input),
        GetPolicy => ExecuteGetPolicy(input),
        _ => Error($"Unknown tool '{name}'. Available tools: {GetPortfolioReport}, {GetPolicy}."),
    };

    private ToolResult ExecuteGetPortfolioReport(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (UnexpectedArguments(input, "portfolio_id") is { } unexpected)
            return unexpected;

        if (!input.TryGetValue("portfolio_id", out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return Error("Invalid arguments: portfolio_id is required and must be a non-empty string.");
        }

        var portfolioId = value.GetString()!;
        var portfolio = report.Portfolios.SingleOrDefault(p => p.PortfolioId == portfolioId);
        if (portfolio is null)
        {
            var available = string.Join(", ", report.Portfolios.Select(p => p.PortfolioId));
            return Error($"Portfolio '{portfolioId}' was not found. Available portfolios: {available}.");
        }

        return new ToolResult(ReportWriter.ToJson(new { report.ValuationDate, Portfolio = portfolio }), IsError: false);
    }

    private ToolResult ExecuteGetPolicy(IReadOnlyDictionary<string, JsonElement> input) =>
        UnexpectedArguments(input) ?? new ToolResult(ReportWriter.ToJson(report.Policy), IsError: false);

    // Returns an error result if the input has any argument not in the allowed list, otherwise null.
    private static ToolResult? UnexpectedArguments(IReadOnlyDictionary<string, JsonElement> input, params string[] allowed)
    {
        var extra = input.Keys.Except(allowed).ToList();
        return extra.Count == 0 ? null : Error($"Invalid arguments: unexpected argument(s) {string.Join(", ", extra)}.");
    }

    private static ToolResult Error(string message) => new(message, IsError: true);
}
