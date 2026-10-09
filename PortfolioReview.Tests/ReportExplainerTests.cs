using PortfolioReview;

namespace PortfolioReview.Tests;

// Offline tests: none of these call the API.
public class ReportExplainerTests
{
    private static readonly DateOnly ValuationDate = new(2026, 10, 2);

    private static Report SampleReport() => ExposureCalculator.Calculate(
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

    [Fact]
    public void Prompt_contains_only_the_selected_portfolio_and_its_provisional_flag()
    {
        var prompt = ReportExplainer.BuildPrompt(SampleReport(), "P2");

        Assert.Contains("Why was portfolio P2 flagged?", prompt);
        Assert.Contains("\"portfolioId\": \"P2\"", prompt);
        Assert.Contains("\"exposuresProvisional\": true", prompt);
        Assert.Contains("MISSING_PRICE", prompt);
        Assert.Contains("\"maxInstrumentPct\": 25", prompt);
        Assert.DoesNotContain("P1", prompt);
        Assert.DoesNotContain("HSBA", prompt);
    }

    [Fact]
    public async Task Unknown_portfolio_fails_before_any_api_call()
    {
        // BuildPrompt runs before the client is created, so this needs no key or network.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ReportExplainer.ExplainAsync(SampleReport(), "P9"));
        Assert.Contains("P9", ex.Message);
    }
}
