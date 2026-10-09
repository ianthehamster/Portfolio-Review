using System.Text.Json;
using PortfolioReview;

namespace PortfolioReview.Tests;

// Offline tests of the two tools. No model or API involved.
public class PortfolioToolsTests
{
    internal static readonly DateOnly ValuationDate = new(2026, 10, 2);

    internal static Report SampleReport() => ExposureCalculator.Calculate(
        [
            new("P1", "HSBA", 500m),
            new("P2", "BP", 1000m),
            new("P2", "ULVR", 100m),
            new("P2", "XYZ", 50m),
        ],
        [
            new("HSBA", 6.00m, "Financials", ValuationDate),
            new("BP", 4.00m, "Energy", ValuationDate.AddDays(-7)),
            new("ULVR", 45.00m, "Consumer Staples", ValuationDate),
        ],
        new Policy(MaxInstrumentPct: 25m, MaxSectorPct: 40m, MaxPriceAgeDays: 3),
        ValuationDate);

    private static readonly PortfolioTools Tools = new(SampleReport());

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Get_portfolio_report_returns_P2_with_issues_and_provisional_flag()
    {
        var result = Tools.Execute(PortfolioTools.GetPortfolioReport, Args("""{"portfolio_id":"P2"}"""));

        Assert.False(result.IsError);
        Assert.Contains("\"valuationDate\": \"2026-10-02\"", result.Content);
        Assert.Contains("\"portfolioId\": \"P2\"", result.Content);
        Assert.Contains("\"exposuresProvisional\": true", result.Content);
        Assert.Contains("MISSING_PRICE", result.Content);
        Assert.Contains("STALE_PRICE", result.Content);
        Assert.DoesNotContain("HSBA", result.Content); // P1 is not included
    }

    [Fact]
    public void Unknown_portfolio_returns_tool_error()
    {
        var result = Tools.Execute(PortfolioTools.GetPortfolioReport, Args("""{"portfolio_id":"P9"}"""));

        Assert.True(result.IsError);
        Assert.Contains("'P9' was not found", result.Content);
        Assert.Contains("P1, P2", result.Content);
    }

    [Theory]
    [InlineData(PortfolioTools.GetPortfolioReport, "{}")]                                 // missing
    [InlineData(PortfolioTools.GetPortfolioReport, """{"portfolio_id":2}""")]              // wrong type
    [InlineData(PortfolioTools.GetPortfolioReport, """{"portfolio_id":"  "}""")]           // blank
    [InlineData(PortfolioTools.GetPortfolioReport, """{"portfolio_id":"P2","x":1}""")]    // extra argument
    [InlineData(PortfolioTools.GetPolicy, """{"portfolio_id":"P2"}""")]                    // get_policy takes none
    public void Invalid_arguments_return_tool_error(string tool, string json)
    {
        var result = Tools.Execute(tool, Args(json));

        Assert.True(result.IsError);
        Assert.StartsWith("Invalid arguments", result.Content);
    }

    [Fact]
    public void Unknown_tool_name_is_rejected()
    {
        var result = Tools.Execute("delete_portfolio", Args("{}"));

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool 'delete_portfolio'", result.Content);
    }

    [Fact]
    public void Get_policy_returns_configured_limits()
    {
        var result = Tools.Execute(PortfolioTools.GetPolicy, Args("{}"));

        Assert.False(result.IsError);
        Assert.Contains("\"maxInstrumentPct\": 25", result.Content);
        Assert.Contains("\"maxSectorPct\": 40", result.Content);
        Assert.Contains("\"maxPriceAgeDays\": 3", result.Content);
    }
}
