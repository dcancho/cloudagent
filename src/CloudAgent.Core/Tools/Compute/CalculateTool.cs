using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Jint.Runtime;

namespace CloudAgent.Core.Tools.Compute;

/// <summary>
/// Exact arithmetic for the agent. Language models get numbers wrong while sounding sure, so any
/// figure the agent reports should come from here. Runs JavaScript in Jint with no access to .NET,
/// files, or the network, and with limits on time, statements, memory, and recursion.
/// </summary>
public sealed class CalculateTool : ITool
{
    private const int MaxOutputChars = 20_000;

    private const string Prelude =
        "var __out = [];" +
        "var console = { log: function () { __out.push(Array.prototype.map.call(arguments, function (a) {" +
        "  return typeof a === 'object' && a !== null ? JSON.stringify(a) : String(a); }).join(' ')); } };";

    public string Name => "calculate";

    public string Description =>
        "Compute exact numbers by running JavaScript: rates, loan schedules, percentages, conversions, statistics. " +
        "Use it for every figure you report instead of calculating in your head. The value of the last expression " +
        "is returned and console.log output is included. No files, network, or imports.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        ToolParameter.String("code", "JavaScript to run, e.g. `var i = Math.pow(1.7599, 1/12) - 1; i * 100`."),
    ];

    public Task<ToolResult> InvokeAsync(ToolInput input, CancellationToken cancellationToken = default)
    {
        var code = input.GetString("code");
        var engine = new Engine(options => options
            .TimeoutInterval(TimeSpan.FromSeconds(2))
            .MaxStatements(1_000_000)
            .LimitMemory(32_000_000)
            .LimitRecursion(64)
            .CancellationToken(cancellationToken));

        try
        {
            engine.Execute(Prelude);
            var result = engine.Evaluate(code);
            engine.SetValue("__result", result);
            var rendered = engine.Evaluate(
                "__result === undefined ? '' : (typeof __result === 'object' && __result !== null ? JSON.stringify(__result, null, 1) : String(__result))")
                .AsString();
            var logs = engine.Evaluate("__out.join('\\n')").AsString();

            var output = new StringBuilder();
            if (logs.Length > 0)
                output.AppendLine(logs);
            if (rendered.Length > 0)
                output.Append(rendered);
            var text = output.ToString().TrimEnd();
            if (text.Length == 0)
                text = "(no value; end with an expression or use console.log)";
            if (text.Length > MaxOutputChars)
                text = text[..MaxOutputChars] + $"... [{text.Length - MaxOutputChars} more characters]";

            return Task.FromResult(ToolResult.Ok(text));
        }
        catch (JavaScriptException ex)
        {
            return Task.FromResult(ToolResult.Fail($"JavaScript error: {ex.Message}"));
        }
        catch (Exception ex) when (ex is TimeoutException or StatementsCountOverflowException
                                       or MemoryLimitExceededException or RecursionDepthOverflowException
                                       or ExecutionCanceledException)
        {
            return Task.FromResult(ToolResult.Fail($"Stopped: {ex.Message}"));
        }
    }
}
