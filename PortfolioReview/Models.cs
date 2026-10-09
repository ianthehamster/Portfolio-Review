namespace PortfolioReview;

// Inputs

public record Position(string PortfolioId, string InstrumentId, decimal Quantity);

public record MarketPrice(string InstrumentId, decimal PriceGbp, string Sector, DateOnly PriceDate);

// Limits are percentages: 25 means 25%.
public record Policy(decimal MaxInstrumentPct, decimal MaxSectorPct, int MaxPriceAgeDays);

// Report

public static class IssueTypes
{
    public const string MissingPrice = "MISSING_PRICE";
    public const string StalePrice = "STALE_PRICE";
    public const string InstrumentLimitBreach = "INSTRUMENT_LIMIT_BREACH";
    public const string SectorLimitBreach = "SECTOR_LIMIT_BREACH";
}

public static class ValuationStatus
{
    public const string Complete = "Complete";
    public const string Incomplete = "Incomplete";
}

public record Report(DateOnly ValuationDate, Policy Policy, IReadOnlyList<PortfolioReport> Portfolios);

public record PortfolioReport(
    string PortfolioId,
    string ValuationStatus,
    decimal TotalValueGbp,
    bool ExposuresProvisional,
    IReadOnlyList<InstrumentExposure> InstrumentExposures,
    IReadOnlyList<SectorExposure> SectorExposures,
    IReadOnlyList<Issue> Issues);

public record InstrumentExposure(
    string InstrumentId,
    string Sector,
    decimal Quantity,
    decimal PriceGbp,
    DateOnly PriceDate,
    decimal ValueGbp,
    decimal ExposurePct,
    bool Breach);

public record SectorExposure(string Sector, decimal ValueGbp, decimal ExposurePct, bool Breach);

// Subject is the instrument id or sector name the issue is about.
// Optional fields are omitted from JSON when null.
public record Issue(
    string Type,
    string Subject,
    string Message,
    decimal? ExposurePct = null,
    decimal? LimitPct = null,
    int? AgeDays = null);
