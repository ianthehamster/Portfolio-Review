using Anthropic;
using Anthropic.Models.Messages;

namespace PortfolioReview;

// Asks Claude to explain one portfolio's already-calculated results in plain English.
// The model only describes the report; all numbers and breach decisions come from ExposureCalculator.
public static class ReportExplainer
{
    public const string ModelId = "claude-opus-5-5";

    // Overall budget for the request, including any SDK retries.
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public const string SystemPrompt = """
        You explain the results of a portfolio exposure review to a non-specialist reader.
        The review has already been calculated by a separate system. You are given one portfolio's
        results as JSON, together with the policy limits and valuation date.

        Rules:
        - Use only the data provided. Quote figures exactly as given; do not calculate new figures,
          estimate missing prices, or change any breach decision.
        - If "exposuresProvisional" is true, say so at the start: the exposures are measured against
          the value of priced holdings only, so they may change once missing prices are available.
          Describe any breaches in that portfolio as provisional.
        - Explain each issue in the "issues" list: missing prices, stale prices (give the age and the
          maximum allowed), and instrument or sector limit breaches (give the exposure and the limit).
        - Do not recommend trades or any other action on the portfolio.
        - If the data does not answer part of the question, say so rather than guessing.

        Write short plain-text paragraphs or a simple list. No headings, no markdown tables.
        """;

    // Pure function: builds the user message. Throws if the portfolio is not in the report,
    // so an unknown id fails before any API call is made.
    public static string BuildPrompt(Report report, string portfolioId)
    {
        var portfolio = report.Portfolios.SingleOrDefault(p => p.PortfolioId == portfolioId)
            ?? throw new ArgumentException($"Portfolio '{portfolioId}' is not in the report.");

        var json = ReportWriter.ToJson(new { report.ValuationDate, report.Policy, Portfolio = portfolio });

        return $"""
            Why was portfolio {portfolioId} flagged?

            <portfolio_review>
            {json}
            </portfolio_review>
            """;
    }

    public static async Task<string> ExplainAsync(Report report, string portfolioId)
    {
        var prompt = BuildPrompt(report, portfolioId);

        // Credentials come from the ANTHROPIC_API_KEY environment variable, never from source.
        AnthropicClient client = new() { Timeout = RequestTimeout };
        using var cancel = new CancellationTokenSource(RequestTimeout);

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = ModelId,
            MaxTokens = 4000,
            System = SystemPrompt,
            // A short explanation of supplied data needs little reasoning; low effort keeps it well inside 30s.
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, cancel.Token);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("The model declined to produce an explanation.");

        var text = string.Join("\n", response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("The model returned no text.");

        return text;
    }
}
