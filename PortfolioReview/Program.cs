using System.Globalization;
using Anthropic.Exceptions;
using PortfolioReview;

// Usage: PortfolioReview --valuation-date yyyy-MM-dd [--input dir] [--output file]
//                        [--explain portfolioId | --ask "question"]
// Exit codes: 0 = report written (even if the AI step fails), 1 = bad arguments or input.

try
{
    var options = ParseArgs(args);
    if (options.ContainsKey("--explain") && options.ContainsKey("--ask"))
        throw new ArgumentException("Use either --explain or --ask, not both.");

    var valuationDate = options.TryGetValue("--valuation-date", out var dateText)
        && DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ArgumentException("--valuation-date yyyy-MM-dd is required.");
    var inputDir = options.GetValueOrDefault("--input", "SampleData");
    var outputPath = options.GetValueOrDefault("--output", "report.json");

    var positions = InputLoader.LoadPositions(Path.Combine(inputDir, "positions.csv"));
    var prices = InputLoader.LoadMarketData(Path.Combine(inputDir, "market_data.csv"), valuationDate);
    var policy = InputLoader.LoadPolicy(Path.Combine(inputDir, "policy.json"));

    var report = ExposureCalculator.Calculate(positions, prices, policy, valuationDate);
    ReportWriter.Write(report, outputPath);

    foreach (var p in report.Portfolios)
    {
        Console.WriteLine($"{p.PortfolioId}: {p.ValuationStatus}, GBP {p.TotalValueGbp:N2}, {p.Issues.Count} issue(s)");
        foreach (var issue in p.Issues)
            Console.WriteLine($"  {issue.Type}: {issue.Message}");
    }
    Console.WriteLine($"Report written to {Path.GetFullPath(outputPath)}");

    // Optional AI steps. report.json is already on disk, so any failure here is only a warning.
    if (options.TryGetValue("--explain", out var explainId))
    {
        try
        {
            var explanation = await ReportExplainer.ExplainAsync(report, explainId);
            Console.WriteLine();
            Console.WriteLine($"Explanation for {explainId} (AI-generated, based on the report above):");
            Console.WriteLine(explanation);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: explanation unavailable - {DescribeAiFailure(ex, ReportExplainer.RequestTimeout).TrimEnd('.')}. report.json was still written.");
        }
    }

    if (options.TryGetValue("--ask", out var question))
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine($"Question: {question}");
            var agent = new PortfolioAgent(
                new AnthropicModelClient(PortfolioAgent.InteractionTimeout), new PortfolioTools(report), Console.Out);
            using var cancel = new CancellationTokenSource(PortfolioAgent.InteractionTimeout);
            var answer = await agent.AskAsync(question, cancel.Token);
            Console.WriteLine();
            Console.WriteLine("Answer (AI-generated, from the report's figures):");
            Console.WriteLine(answer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: answer unavailable - {DescribeAiFailure(ex, PortfolioAgent.InteractionTimeout).TrimEnd('.')}. report.json was still written.");
        }
    }

    return 0;
}
catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// Most specific first: the auth and rate-limit exceptions derive from AnthropicApiException.
static string DescribeAiFailure(Exception ex, TimeSpan timeout) => ex switch
{
    ArgumentException or AgentException => ex.Message,
    OperationCanceledException => $"no response within {timeout.TotalSeconds} seconds",
    AnthropicUnauthorizedException => "API key was rejected (check ANTHROPIC_API_KEY)",
    AnthropicRateLimitException => "rate limited by the API; try again shortly",
    AnthropicIOException => "network error reaching the API",
    AnthropicApiException => $"API error: {ex.Message}",
    _ => ex.Message,
};

static Dictionary<string, string> ParseArgs(string[] args)
{
    if (args.Length % 2 != 0)
        throw new ArgumentException("Arguments must be --name value pairs.");

    var result = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i += 2)
        result[args[i]] = args[i + 1];
    return result;
}
