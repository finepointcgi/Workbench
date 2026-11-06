using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Workbench.Application.Assistant;
using Workbench.Contracts.Assistant;

namespace Workbench.Infrastructure.Assistant;

/// <summary>
/// Development only: answers a request by running the Claude Code CLI (<c>claude -p</c>) signed in on this computer, so a
/// developer needs no API key. It writes text and structured drafts (proposals, lead fit); it can't call Workbench's data
/// tools, so a request that carries tools is refused. Runs from an empty temp folder with Claude Code's own tools off.
/// </summary>
internal sealed class ClaudeCodeModelClient(string executable, TimeSpan timeout, ILogger<ClaudeCodeModelClient> logger) : IAssistantModelClient
{
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.Tools.Count > 0)
        {
            throw new AssistantProviderException("The local Claude Code connection writes drafts but can't answer questions from Workbench data. Use an Anthropic or Bedrock connection for Ask.");
        }

        var prompt = new StringBuilder();
        foreach (var message in request.Messages)
        {
            prompt.AppendLine(message.Role == ModelRole.User ? "[user]" : "[assistant]");
            prompt.AppendLine(string.Concat(message.Content.OfType<ModelText>().Select(t => t.Text)));
        }

        if (request.JsonSchema is { } schema)
        {
            prompt.AppendLine("\nReply with only one JSON object that matches this JSON Schema: no prose, no code fence.\n" + schema.ToJsonString());
        }

        var (text, usage) = await RunAsync(request.System, prompt.ToString(), cancellationToken);
        yield return new ModelResponse([new ModelText(text)], "end_turn", usage);
    }

    private async Task<(string Text, AssistantTokenUsage Usage)> RunAsync(string system, string prompt, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Directory.CreateTempSubdirectory("workbench-claude-code-").FullName,
        };
        foreach (var arg in new[] { "-p", "--output-format", "json", "--tools", "", "--no-session-persistence", "--system-prompt", system })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AssistantProviderException($"Couldn't start Claude Code ('{executable}'). Is it installed and on the PATH? Set Assistant:ClaudeCodePath if not.", ex);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), limit.Token);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            var json = await output;
            if (process.ExitCode != 0)
            {
                logger.LogWarning("Claude Code exited with {Code}: {Error}", process.ExitCode, (await error).Trim());
                throw new AssistantProviderException("Claude Code didn't answer. Check that you're signed in (run `claude` once in a terminal).");
            }

            return Parse(json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new AssistantProviderException("Claude Code took too long to answer.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    internal static (string Text, AssistantTokenUsage Usage) Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AssistantProviderException("Claude Code's answer wasn't readable.", ex);
        }

        if (node is not JsonObject o || o["is_error"]?.GetValue<bool>() == true)
        {
            throw new AssistantProviderException("Claude Code reported an error: " + (node?["result"]?.ToString() ?? "no details") + ".");
        }

        var text = o["result"]?.ToString() ?? "";
        long N(string name) => o["usage"]?[name]?.GetValue<long>() ?? 0;
        return (StripFence(text), new AssistantTokenUsage(N("input_tokens"), N("output_tokens"), N("cache_read_input_tokens"), N("cache_creation_input_tokens")));
    }

    /// <summary>A model that wraps JSON in a code fence anyway: take what's inside.</summary>
    internal static string StripFence(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal) && t.EndsWith("```", StringComparison.Ordinal))
        {
            var firstLine = t.IndexOf('\n');
            if (firstLine > 0)
            {
                return t[(firstLine + 1)..^3].Trim();
            }
        }

        return t;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
