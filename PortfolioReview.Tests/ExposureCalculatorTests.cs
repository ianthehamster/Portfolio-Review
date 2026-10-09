 using PortfolioReview;

namespace PortfolioReview.Tests;

public class ExposureCalculatorTests
{
    private static readonly DateOnly ValuationDate = new(2026, 10, 2);
    private static readonly Policy Policy = new(MaxInstrumentPct: 25m, MaxSectorPct: 40m, MaxPriceAgeDays: 3);

    private static readonly MarketPrice[] Prices =
    [
        new("HSBA", 6.00m, "Financials", ValuationDate),
        new("BARC", 2.00m, "Financials", ValuationDate),
        new("SHEL", 25.00m, "Energy", ValuationDate.AddDays(-1)),
        new("AZN", 125.00m, "Healthcare", ValuationDate),
    ];

    // P1 totals exactly GBP 10,000: HSBA 30%, BARC 20%, SHEL 25%, AZN 25%.
    private static readonly Position[] P1 =
    [
        new("P1", "HSBA", 500m),
        new("P1", "BARC", 1000m),
        new("P1", "SHEL", 100m),
        new("P1", "AZN", 20m),
    ];

    private static PortfolioReport Single(Position[] positions, MarketPrice[]? prices = null) =>
        Assert.Single(ExposureCalculator.Calculate(positions, prices ?? Prices, Policy, ValuationDate).Portfolios);

    [Fact]
    public void Values_and_percentages_are_exact()
    {
        var p = Single(P1);

        Assert.Equal(10000m, p.TotalValueGbp);
        var hsba = p.InstrumentExposures.Single(e => e.InstrumentId == "HSBA");
        Assert.Equal(3000m, hsba.ValueGbp);
        Assert.Equal(30m, hsba.ExposurePct);
        Assert.Equal(ValuationStatus.Complete, p.ValuationStatus);
        Assert.False(p.ExposuresProvisional);
    }

    [Fact]
    public void Exposure_equal_to_limit_is_not_a_breach()
    {
        var p = Single(P1);

        Assert.False(p.InstrumentExposures.Single(e => e.InstrumentId == "SHEL").Breach); // exactly 25%
        Assert.True(p.InstrumentExposures.Single(e => e.InstrumentId == "HSBA").Breach);  // 30%
        var breach = Assert.Single(p.Issues, i => i.Type == IssueTypes.InstrumentLimitBreach);
        Assert.Equal("HSBA", breach.Subject);
    }

    [Fact]
    public void Sector_exposure_aggregates_instruments()
    {
        var p = Single(P1);

        var financials = p.SectorExposures.Single(s => s.Sector == "Financials");
        Assert.Equal(5000m, financials.ValueGbp);
        Assert.Equal(50m, financials.ExposurePct);
        Assert.True(financials.Breach);
        Assert.False(p.InstrumentExposures.Single(e => e.InstrumentId == "BARC").Breach); // 20% alone is fine
        Assert.Contains(p.Issues, i => i.Type == IssueTypes.SectorLimitBreach && i.Subject == "Financials");
    }

    [Fact]
    public void Missing_price_marks_portfolio_incomplete()
    {
        var p = Single([.. P1, new("P1", "XYZ", 50m)]);

        Assert.Equal(ValuationStatus.Incomplete, p.ValuationStatus);
        Assert.True(p.ExposuresProvisional);
        Assert.Equal(10000m, p.TotalValueGbp); // XYZ excluded from the total
        Assert.DoesNotContain(p.InstrumentExposures, e => e.InstrumentId == "XYZ");
        Assert.Contains(p.Issues, i => i.Type == IssueTypes.MissingPrice && i.Subject == "XYZ");
    }

    [Fact]
    public void Stale_price_flagged_only_beyond_max_age()
    {
        MarketPrice[] prices =
        [
            new("OLD", 1m, "Energy", ValuationDate.AddDays(-4)), // older than max of 3
            new("EDGE", 1m, "Energy", ValuationDate.AddDays(-3)), // exactly max
        ];
        var p = Single([new("P1", "OLD", 100m), new("P1", "EDGE", 100m)], prices);

        var stale = Assert.Single(p.Issues, i => i.Type == IssueTypes.StalePrice);
        Assert.Equal("OLD", stale.Subject);
        Assert.Equal(4, stale.AgeDays);
        Assert.Equal(200m, p.TotalValueGbp); // stale price is still used
        Assert.Equal(ValuationStatus.Complete, p.ValuationStatus);
    }

    [Fact]
    public void Portfolio_with_no_prices_does_not_divide_by_zero()
    {
        var p = Single([new("P9", "NOPE1", 10m), new("P9", "NOPE2", 20m)]);

        Assert.Equal(0m, p.TotalValueGbp);
        Assert.Equal(ValuationStatus.Incomplete, p.ValuationStatus);
        Assert.Empty(p.InstrumentExposures);
        Assert.Empty(p.SectorExposures);
        Assert.Equal(2, p.Issues.Count(i => i.Type == IssueTypes.MissingPrice));
    }
}
