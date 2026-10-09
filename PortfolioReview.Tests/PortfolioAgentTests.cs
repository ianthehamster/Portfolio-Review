using System.Text.Json;
using Anthropic.Models.Messages;
using PortfolioReview;

namespace PortfolioReview.Tests;

// Offline tests of the agent loop. A fake model returns canned SDK Message objects; the API is never called.
public class PortfolioAgentTests
{
    private sealed class FakeModel(Func<int, string> responseJsonForCall) : IModelClient
    {
        public List<MessageCreateParams> Requests { get; } = [];

        public Task<Message> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken)
        {
            Requests.Add(parameters);
            var message = JsonSerializer.Deserialize<Message>(responseJsonForCall(Requests.Count))!;
            return Task.FromResult(message);
        }
    }

    private static string Response(string stopReason, string contentJson) => $$"""
        {"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5-5",
         "stop_reason":"{{stopReason}}","stop_sequence":null,
         "usage":{"input_tokens":1,"output_tokens":1},
         "content":{{contentJson}}}
        """;

    private static PortfolioAgent Agent(FakeModel model, TextWriter trace) =>
        new(model, new PortfolioTools(PortfolioToolsTests.SampleReport()), trace);

    [Fact]
    public async Task Stops_at_the_model_call_limit()
    {
        // The fake model asks for a tool on every call and never gives a final answer.
        var model = new FakeModel(call => Response("tool_use",
            $$$"""[{"type":"tool_use","id":"toolu_{{{call}}}","name":"get_policy","input":{}}]"""));

        await Assert.ThrowsAsync<AgentLimitException>(() => Agent(model, TextWriter.Null).AskAsync("Q?", CancellationToken.None));
        Assert.Equal(PortfolioAgent.MaxModelCalls, model.Requests.Count);
    }

    [Fact]
    public async Task Multiple_tool_calls_get_matching_results_then_final_answer_is_returned()
    {
        var model = new FakeModel(call => call == 1
            ? Response("tool_use", """
                [{"type":"thinking","thinking":"","signature":"sig_abc"},
                 {"type":"tool_use","id":"toolu_a","name":"get_portfolio_report","input":{"portfolio_id":"P2"}},
                 {"type":"tool_use","id":"toolu_b","name":"get_policy","input":{}},
                 {"type":"tool_use","id":"toolu_c","name":"get_portfolio_report","input":{"portfolio_id":"P9"}}]
                """)
            : Response("end_turn", """[{"type":"text","text":"P2 is flagged because..."}]"""));
        var trace = new StringWriter();

        var answer = await Agent(model, trace).AskAsync("Why was portfolio P2 flagged?", CancellationToken.None);

        Assert.Equal("P2 is flagged because...", answer);
        Assert.Equal(2, model.Requests.Count);

        // First request: only the question, no report data.
        var first = JsonSerializer.Serialize(model.Requests[0].Messages);
        Assert.Contains("Why was portfolio P2 flagged?", first);
        Assert.DoesNotContain("exposuresProvisional", first);

        // Second request: question, echoed assistant blocks (thinking signature kept), then one result per tool_use id.
        var second = model.Requests[1].Messages;
        Assert.Equal(3, second.Count);
        var echoed = JsonSerializer.Serialize(second[1]);
        Assert.Contains("sig_abc", echoed);
        Assert.Contains("toolu_a", echoed);
        var results = JsonSerializer.Serialize(second[2]);
        Assert.Contains("\"tool_use_id\":\"toolu_a\"", results);
        Assert.Contains("\"tool_use_id\":\"toolu_b\"", results);
        Assert.Contains("\"tool_use_id\":\"toolu_c\"", results);
        Assert.Contains("\"is_error\":true", results); // P9 failed but did not crash the loop

        var traceText = trace.ToString();
        Assert.Contains("get_portfolio_report {\"portfolio_id\":\"P2\"}", traceText);
        Assert.Contains("failed: Portfolio 'P9' was not found", traceText);
        Assert.DoesNotContain("sig_abc", traceText); // thinking is never printed
    }
}
