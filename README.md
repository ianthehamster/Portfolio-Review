# Portfolio Exposure Review Assistant

A C# console prototype built as a timed interview-practice challenge. It uses fictional sample data and is **not production-ready**.

## 1. Purpose

Portfolio reviewers need to know whether a portfolio is too concentrated in one instrument or sector, and whether its valuation can be trusted. This tool:

- values each position (`quantity × price_gbp`) and calculates instrument and sector exposure as a % of the portfolio
- flags **instrument and sector limit breaches** against a policy
- flags **missing prices** (the portfolio valuation is marked `Incomplete` and its exposures `provisional`)
- flags **stale prices** older than the policy's maximum age
- writes a structured `report.json`
- optionally uses Claude (`claude-opus-5-5`) to explain the results in plain English

Assumptions: long-only positions, all prices in GBP, and an explicit valuation date.

## 2. Setup

- **.NET 10 SDK.** Both projects target `net10.0`.
- **NuGet packages:** `Anthropic` 12.53.0 for the AI features, and xUnit for tests. `dotnet` restores both automatically.
- **API key, for the AI features only.** Set it in PowerShell for the current session:

  ```powershell
  $env:ANTHROPIC_API_KEY = "<your-api-key>"
  ```

  The key is read from the environment by the Anthropic SDK and is never stored in code or printed.

**Without a key**, report generation and all offline tests work normally. `--explain` and `--ask` print a warning instead of an answer.

## 3. Commands

Run these from the solution folder (the one containing `PortfolioReview.slnx`).

```powershell
# Run the offline tests (no API key needed)
dotnet test PortfolioReview.slnx

# Generate report.json without AI
dotnet run --project PortfolioReview -- --valuation-date 2026-10-02 --input PortfolioReview/SampleData --output report.json

# Explain P2 (fixed, single-request workflow)
dotnet run --project PortfolioReview -- --valuation-date 2026-10-02 --input PortfolioReview/SampleData --output report.json --explain P2

# Ask about P2 (tool-calling agent)
dotnet run --project PortfolioReview -- --valuation-date 2026-10-02 --input PortfolioReview/SampleData --output report.json --ask "Why was portfolio P2 flagged?"

# Ask about a portfolio that doesn't exist (error handling)
dotnet run --project PortfolioReview -- --valuation-date 2026-10-02 --input PortfolioReview/SampleData --output report.json --ask "Why was portfolio P9 flagged?"
```

Exit codes:

- **0:** the report was written, even if the AI step failed.
- **1:** bad arguments or invalid input, including passing `--explain` and `--ask` together.

## 4. Architecture

```
InputLoader ─► ExposureCalculator ─► ReportWriter (report.json)
                       │
                       └─► Report (in memory) ─► ReportExplainer   (--explain)
                                              └─► PortfolioAgent + PortfolioTools   (--ask)
```

| Component | Role |
|---|---|
| `InputLoader` | Reads `positions.csv`, `market_data.csv` and `policy.json`. Rejects bad input with the file and line number: negative quantities or prices, duplicate instruments, price dates after the valuation date, missing or unknown policy fields, and policy limits outside 0–100 |
| `ExposureCalculator` | Pure calculation with no file access. Combines rows for the same instrument, values priced holdings, calculates exposures, and flags breaches, missing prices and stale prices |
| `ReportWriter` | Writes the report as indented camelCase JSON |
| `ReportExplainer` | **`--explain`: a fixed workflow.** The code picks the portfolio, sends its report and the policy in **one request**, and prints the answer. 30-second timeout |
| `PortfolioTools` | **Tool dispatcher.** Two read-only tools, `get_portfolio_report(portfolio_id)` and `get_policy()`. Validates arguments and serves data only from the in-memory report. Errors are returned to the model as tool errors rather than thrown |
| `PortfolioAgent` | **`--ask`: tool calling.** Sends only the question and the tool definitions. The **model decides** which tools to call. The loop runs those tools in C#, returns results matched by tool-use ID, and repeats until the model gives a final answer. Prints a trace of each tool call |

The AI components only describe the results. All calculations and breach decisions are made in C#.

## 5. Verified examples (sample data, valuation date 2026-10-02)

Policy: instrument limit 25%, sector limit 40%, maximum price age 3 days.

**P1: Complete, GBP 10,000.00**
- HSBA is GBP 3,000 = **30%**, which breaches the 25% instrument limit.
- Financials (HSBA + BARC) is GBP 5,000 = **50%**, which breaches the 40% sector limit.
- SHEL and AZN are exactly 25%: **not** a breach, because the check is strictly greater than the limit.

**P2: Incomplete, GBP 8,500.00 priced total, exposures provisional**
- **XYZ** has no price, so it is excluded from the total.
- **BP**'s price is dated 2026-09-25: 7 days old against a 3-day maximum, so it is flagged stale but still used in the valuation.
- Exposures are measured against the priced GBP 8,500 only, so BP at 47.0588% and ULVR at 52.9412% are provisional breaches.

**Against the real API:** the author manually ran
- a successful `--ask` tool-calling session for P2
- the P9 missing-portfolio case. Here `get_portfolio_report` returns a "not found" tool error listing the available portfolios, rather than crashing.

## 6. Reliability and limitations

**Reliability**
- **Exact arithmetic:** all money and percentages use C# `decimal`, so totals like 3,000 / 10,000 come out exactly 30%.
- **Breach checks use unrounded values:** percentages are rounded to 4 decimal places only when written to the report.
- **19 offline tests (xUnit), no API calls:**
  - calculations and limit edge cases
  - missing and stale prices
  - an all-unpriced portfolio (no division by zero)
  - building the `--explain` prompt
  - tool results and errors
  - the agent loop with a fake model, including several tool calls in one reply and stopping at the call limit
- **Agent limits:** at most **5 model calls** and **60 seconds** for the whole `--ask` interaction. `--explain` has a 30-second timeout.
- **Report preserved:** `report.json` is written before any AI call. API errors, timeouts or hitting the call limit print a warning, and the exit code stays 0.

**Limitations**
- **Simple CSV reader:** comma-separated values with a header row, and no quoted fields, so a value containing a comma will be misread. Prices must use `.` as the decimal separator and dates must be `yyyy-MM-dd`.
- **AI output can be wrong:** the instructions tell the model to use only supplied facts, not to recalculate, and not to recommend trades, but its answers can still be inaccurate. Always treat `report.json` as the source of truth.
- **Untested paths:** the real-API runs above were checked manually. The timeout path has no automated test.
- **Prototype only:** fictional sample data, no persistence or audit trail, and only GBP, long-only positions.
