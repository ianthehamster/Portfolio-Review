namespace PortfolioReview;

// Pure calculation: no file access, so it can be tested with in-memory data.
public static class ExposureCalculator
{
    // Percentages are rounded only for the report; breach checks use the unrounded value.
    private const int PctDecimals = 4;

    public static Report Calculate(
        IReadOnlyList<Position> positions,
        IReadOnlyList<MarketPrice> prices,
        Policy policy,
        DateOnly valuationDate)
    {
        var priceById = prices.ToDictionary(p => p.InstrumentId);

        var portfolios = positions
            .GroupBy(p => p.PortfolioId)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => CalculatePortfolio(g.Key, g.ToList(), priceById, policy, valuationDate))
            .ToList();

        return new Report(valuationDate, policy, portfolios);
    }

    private static PortfolioReport CalculatePortfolio(
        string portfolioId,
        IReadOnlyList<Position> positions,
        IReadOnlyDictionary<string, MarketPrice> priceById,
        Policy policy,
        DateOnly valuationDate)
    {
        var issues = new List<Issue>();
        var holdings = new List<(string InstrumentId, decimal Quantity, MarketPrice Price, decimal Value)>();

        // Rows for the same instrument within a portfolio are combined into one holding.
        foreach (var group in positions.GroupBy(p => p.InstrumentId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var quantity = group.Sum(p => p.Quantity);

            if (!priceById.TryGetValue(group.Key, out var price))
            {
                issues.Add(new Issue(IssueTypes.MissingPrice, group.Key,
                    $"No price for {group.Key}; excluded from portfolio value."));
                continue;
            }

            var ageDays = valuationDate.DayNumber - price.PriceDate.DayNumber;
            if (ageDays > policy.MaxPriceAgeDays)
            {
                issues.Add(new Issue(IssueTypes.StalePrice, group.Key,
                    $"Price dated {price.PriceDate:yyyy-MM-dd} is {ageDays} days old (max {policy.MaxPriceAgeDays}); still used in valuation.",
                    AgeDays: ageDays));
            }

            holdings.Add((group.Key, quantity, price, quantity * price.PriceGbp));
        }

        var total = holdings.Sum(h => h.Value);
        var incomplete = issues.Any(i => i.Type == IssueTypes.MissingPrice);

        var instrumentExposures = new List<InstrumentExposure>();
        var sectorExposures = new List<SectorExposure>();

        // With nothing priced there is no denominator, so no exposures are produced.
        if (total > 0)
        {
            foreach (var h in holdings)
            {
                var pct = h.Value / total * 100m;
                var breach = pct > policy.MaxInstrumentPct;
                instrumentExposures.Add(new InstrumentExposure(
                    h.InstrumentId, h.Price.Sector, h.Quantity, h.Price.PriceGbp, h.Price.PriceDate,
                    h.Value, Round(pct), breach));

                if (breach)
                {
                    issues.Add(new Issue(IssueTypes.InstrumentLimitBreach, h.InstrumentId,
                        $"{h.InstrumentId} is {Round(pct)}% of portfolio, above the {policy.MaxInstrumentPct}% instrument limit.",
                        ExposurePct: Round(pct), LimitPct: policy.MaxInstrumentPct));
                }
            }

            foreach (var sector in holdings.GroupBy(h => h.Price.Sector).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var value = sector.Sum(h => h.Value);
                var pct = value / total * 100m;
                var breach = pct > policy.MaxSectorPct;
                sectorExposures.Add(new SectorExposure(sector.Key, value, Round(pct), breach));

                if (breach)
                {
                    issues.Add(new Issue(IssueTypes.SectorLimitBreach, sector.Key,
                        $"{sector.Key} is {Round(pct)}% of portfolio, above the {policy.MaxSectorPct}% sector limit.",
                        ExposurePct: Round(pct), LimitPct: policy.MaxSectorPct));
                }
            }
        }

        return new PortfolioReport(
            portfolioId,
            incomplete ? ValuationStatus.Incomplete : ValuationStatus.Complete,
            total,
            ExposuresProvisional: incomplete,
            instrumentExposures,
            sectorExposures,
            issues);
    }

    // Adding 0.0000m pads the decimal's scale so every percentage prints with 4 places (30 -> 30.0000).
    private static decimal Round(decimal pct) =>
        Math.Round(pct, PctDecimals, MidpointRounding.AwayFromZero) + 0.0000m;
}
