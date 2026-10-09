using System.Globalization;
using System.Text.Json;

namespace PortfolioReview;

// Reads and validates input files. Any problem throws InvalidDataException
// with the file name and line number, and the run stops.
//
// The CSV reader is deliberately simple: comma-separated, no quoted fields.
public static class InputLoader
{
    public static IReadOnlyList<Position> LoadPositions(string path)
    {
        return ReadCsv(path, ["portfolio_id", "instrument_id", "quantity"], (row, where) =>
        {
            var quantity = ParseDecimal(row["quantity"], "quantity", where);
            if (quantity < 0)
                throw new InvalidDataException($"{where}: negative quantity {quantity} (long-only positions expected).");

            return new Position(
                Required(row["portfolio_id"], "portfolio_id", where),
                Required(row["instrument_id"], "instrument_id", where),
                quantity);
        });
    }

    public static IReadOnlyList<MarketPrice> LoadMarketData(string path, DateOnly valuationDate)
    {
        var prices = ReadCsv(path, ["instrument_id", "price_gbp", "sector", "price_date"], (row, where) =>
        {
            var price = ParseDecimal(row["price_gbp"], "price_gbp", where);
            if (price < 0)
                throw new InvalidDataException($"{where}: negative price {price}.");

            if (!DateOnly.TryParseExact(row["price_date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new InvalidDataException($"{where}: price_date '{row["price_date"]}' is not a yyyy-MM-dd date.");
            if (date > valuationDate)
                throw new InvalidDataException($"{where}: price_date {date:yyyy-MM-dd} is after valuation date {valuationDate:yyyy-MM-dd}.");

            return new MarketPrice(
                Required(row["instrument_id"], "instrument_id", where),
                price,
                Required(row["sector"], "sector", where),
                date);
        });

        var duplicate = prices.GroupBy(p => p.InstrumentId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"{path}: instrument {duplicate.Key} appears more than once.");

        return prices;
    }

    public static Policy LoadPolicy(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            RespectRequiredConstructorParameters = true, // a missing field is an error, not a silent 0
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, // catch typos
        };

        Policy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<Policy>(File.ReadAllText(path), options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }

        if (policy is null)
            throw new InvalidDataException($"{path}: policy is empty.");
        if (policy.MaxInstrumentPct is < 0 or > 100)
            throw new InvalidDataException($"{path}: maxInstrumentPct must be between 0 and 100.");
        if (policy.MaxSectorPct is < 0 or > 100)
            throw new InvalidDataException($"{path}: maxSectorPct must be between 0 and 100.");
        if (policy.MaxPriceAgeDays < 0)
            throw new InvalidDataException($"{path}: maxPriceAgeDays must not be negative.");

        return policy;
    }

    private static List<T> ReadCsv<T>(
        string path,
        string[] requiredColumns,
        Func<IReadOnlyDictionary<string, string>, string, T> parseRow)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
            throw new InvalidDataException($"{path}: file is empty.");

        var header = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var missing = requiredColumns.Except(header).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"{path}: missing column(s) {string.Join(", ", missing)}.");

        var results = new List<T>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var where = $"{path} line {i + 1}";
            var cells = lines[i].Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length != header.Length)
                throw new InvalidDataException($"{where}: expected {header.Length} fields, found {cells.Length}.");

            var row = header.Zip(cells).ToDictionary(p => p.First, p => p.Second);
            results.Add(parseRow(row, where));
        }

        return results;
    }

    private static decimal ParseDecimal(string text, string column, string where)
    {
        // InvariantCulture so "6.00" is read the same on any machine's regional settings.
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"{where}: {column} '{text}' is not a number.");
        return value;
    }

    private static string Required(string text, string column, string where) =>
        string.IsNullOrEmpty(text) ? throw new InvalidDataException($"{where}: {column} is empty.") : text;
}
