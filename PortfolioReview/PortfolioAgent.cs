using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace PortfolioReview;

// The one call the agent makes to the model. An interface so tests can supply canned responses.
public interface IModelClient
{
    Task<Message> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken);
}

public sealed class AnthropicModelClient(TimeSpan timeout) : IModelClient
{
    // Credentials come from the ANTHROPIC_API_KEY environment variable, never from source.
    private readonly AnthropicClient client = new() { Timeout = timeout };

    public Task<Message> CreateAsync(MessageCreateParams parameters, CancellationToken cancellationToken) =>
        client.Messages.Create(parameters, cancellationToken);
}

public class AgentException(string message) : Exception(message);

public sealed class AgentLimitException(string message) : AgentException(message);

// Answers a question by letting the model call PortfolioTools:
// model -> tool_use -> C# executes -> tool_result -> model ... -> final text answer.
public sealed class PortfolioAgent(IModelClient model, PortfolioTools tools, TextWriter trace)
{
    public const int MaxModelCalls = 5;
    public static readonly TimeSpan InteractionTimeout = TimeSpan.FromSeconds(60);

    public const string SystemPrompt = """
        You answer questions about a portfolio exposure review that has already been calculated by
        a separate system. Use the tools to obtain the facts you need before answering.

        Rules:
        - Use only facts returned by the tools. Quote figures exactly as returned; do not invent,
          estimate or recalculate any figure, price or breach decision.
        - If a portfolio's "exposuresProvisional" is true, say so clearly: its exposures are measured
          against priced holdings only, so they and any breaches are provisional until missing prices
          are available.
        - Explain missing prices, stale prices (age and maximum allowed) and instrument or sector
          limit breaches (exposure and limit) when they are relevant to the question.
        - Do not recommend trades or any other action, and do not suggest changes to the data.
        - If a tool returns an error or the information is not available, state that limitation
          rather than guessing.

        Write short plain-text paragraphs or a simple list. No headings, no markdown tables.
        """;

    public async Task<string> AskAsync(string question, CancellationToken cancellationToken)
    {
        // Only the question goes in the first message; the model fetches facts through tools.
        List<MessageParam> messages = [new() { Role = Role.User, Content = question }];

        for (var call = 1; call <= MaxModelCalls; call++)
        {
            var response = await model.CreateAsync(new MessageCreateParams
            {
                Model = ReportExplainer.ModelId,
                MaxTokens = 4000,
                System = SystemPrompt,
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                Tools = PortfolioTools.Definitions,
                Messages = [.. messages], // snapshot, so later additions don't alter this request
            }, cancellationToken);

            if (response.StopReason == "refusal")
                throw new AgentException("The model declined to answer.");

            if (response.StopReason == "end_turn")
            {
                var answer = string.Join("\n", response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
                return answer.Length > 0 ? answer : throw new AgentException("The model returned no answer text.");
            }

            if (response.StopReason != "tool_use")
                throw new AgentException($"The model stopped unexpectedly (stop reason: {response.StopReason}).");

            trace.WriteLine($"[model call {call}] requested tools:");
            var (assistantBlocks, toolResults) = RunRequestedTools(response);
            messages.Add(new() { Role = Role.Assistant, Content = assistantBlocks });
            messages.Add(new() { Role = Role.User, Content = toolResults });
        }

        throw new AgentLimitException($"no final answer after {MaxModelCalls} model calls");
    }

    // Echoes the assistant's blocks back unchanged (the API requires the tool_use and thinking blocks)
    // and builds one tool_result per tool_use, matched by ID.
    private (List<ContentBlockParam> Assistant, List<ContentBlockParam> Results) RunRequestedTools(Message response)
    {
        List<ContentBlockParam> assistant = [];
        List<ContentBlockParam> results = [];

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
            {
                assistant.Add(new TextBlockParam { Text = text.Text });
            }
            else if (block.TryPickThinking(out var thinking))
            {
                // Signature must be preserved unchanged. Thinking is passed back but never printed.
                assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                assistant.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });

                trace.WriteLine($"  {toolUse.Name} {Truncate(JsonSerializer.Serialize(toolUse.Input))}");
                var result = tools.Execute(toolUse.Name, toolUse.Input);
                trace.WriteLine(result.IsError ? $"    failed: {result.Content}" : "    ok");

                results.Add(new ToolResultBlockParam
                {
                    ToolUseID = toolUse.ID,
                    Content = result.Content,
                    IsError = result.IsError ? true : null,
                });
            }
            else
            {
                // Only client tools are offered, so no other block types are expected.
                throw new AgentException("The model response contained an unexpected content block.");
            }
        }

        if (results.Count == 0)
            throw new AgentException("The model asked to use tools but sent no tool requests.");

        return (assistant, results);
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
