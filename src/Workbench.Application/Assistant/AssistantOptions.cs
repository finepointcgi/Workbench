using Workbench.Contracts.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>
/// Server-wide assistant configuration (section <c>Assistant</c>). The host configures named connections; each
/// organization's administrator picks one (and a model) in its own settings. Keys never go in appsettings files:
/// set <c>Assistant__Connections__anthropic__ApiKey</c> from a secret store.
/// </summary>
public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>The built-in name of the fake provider's connection.</summary>
    public const string FakeConnection = "fake";

    /// <summary>
    /// Offers the "fake" connection: deterministic canned answers built from the tool results, no network calls.
    /// For development and tests only (refused in Production by the configuration check).
    /// </summary>
    public bool EnableFakeProvider { get; set; }

    public Dictionary<string, AssistantConnectionOptions> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tool calls one question may make before the assistant must answer with what it has.</summary>
    public int MaxToolCallsPerQuestion { get; set; } = 12;

    /// <summary>Model round trips per question (each may call several tools).</summary>
    public int MaxRoundTrips { get; set; } = 6;

    public int MaxQuestionLength { get; set; } = 2_000;

    /// <summary>Maximum output tokens per model call.</summary>
    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>Effort for questions (low, medium, high, xhigh, max).</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Earlier questions and answers of a conversation sent with a new question.</summary>
    public int ConversationTurns { get; set; } = 6;

    /// <summary>Characters of one tool result sent to the model (larger results are cut, with a note).</summary>
    public int MaxToolResultChars { get; set; } = 24_000;

    /// <summary>Model calls time out after this long.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(3);
}

/// <summary>One connection to a model provider. Secrets (<see cref="ApiKey"/>) come from configuration secrets only.</summary>
public sealed class AssistantConnectionOptions
{
    public AssistantProviderKind Provider { get; set; } = AssistantProviderKind.Anthropic;

    /// <summary>What administrators see, e.g. "Anthropic API" or "Amazon Bedrock (GovCloud US-West)".</summary>
    public string? Label { get; set; }

    /// <summary><b>Secret.</b> Anthropic API key (Anthropic connections).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional API base URL (Anthropic connections), e.g. an egress proxy.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>AWS region (Bedrock connections), e.g. <c>us-east-1</c> or <c>us-gov-west-1</c>.</summary>
    public string? Region { get; set; }

    /// <summary>
    /// Optional AWS shared-config profile (Bedrock). Without one the default AWS credential chain is used: the instance,
    /// task or pod role (recommended), or the environment.
    /// </summary>
    public string? AwsProfile { get; set; }

    /// <summary>Bedrock API: <c>mantle</c> (Messages API endpoint) or <c>runtime</c> (InvokeModel). Default: runtime in GovCloud, mantle elsewhere.</summary>
    public string? BedrockApi { get; set; }

    /// <summary>The model used when the organization picks none.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Models administrators may choose from (the default is always allowed).</summary>
    public List<string> Models { get; set; } = [];

    public static string DefaultModelFor(AssistantProviderKind provider) => provider switch
    {
        AssistantProviderKind.Anthropic => "claude-opus-5-5",
        AssistantProviderKind.Bedrock => "anthropic.claude-opus-5-5",
        _ => "workbench-fake-1",
    };

    public string EffectiveDefaultModel => string.IsNullOrWhiteSpace(DefaultModel) ? DefaultModelFor(Provider) : DefaultModel.Trim();

    public IReadOnlyList<string> AllowedModels =>
        Models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Prepend(EffectiveDefaultModel).Distinct(StringComparer.Ordinal).ToList();

    public bool IsGovCloud => AssistantConnections.IsGovCloudRegion(Region);

    public bool UsesBedrockRuntime => Provider == AssistantProviderKind.Bedrock &&
        (string.Equals(BedrockApi, "runtime", StringComparison.OrdinalIgnoreCase) || (string.IsNullOrWhiteSpace(BedrockApi) && IsGovCloud));
}

/// <summary>Validates connections and describes them to administrators (without secrets).</summary>
public static class AssistantConnections
{
    public static bool IsGovCloudRegion(string? region) =>
        region?.Trim().StartsWith("us-gov-", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Problems with one connection; empty when it can be used.</summary>
    public static IReadOnlyList<string> Validate(string name, AssistantConnectionOptions? options)
    {
        var errors = new List<string>();
        if (options is null)
        {
            return [$"Assistant connection '{name}' isn't configured."];
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            errors.Add($"Assistant connection name '{name}' must be 1–64 letters, digits, '-' or '_'.");
        }

        if (string.Equals(name, AssistantOptions.FakeConnection, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The connection name 'fake' is reserved for the built-in test provider.");
        }

        switch (options.Provider)
        {
            case AssistantProviderKind.Anthropic:
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    errors.Add($"Assistant connection '{name}': set ApiKey (from a secret store, e.g. Assistant__Connections__{name}__ApiKey).");
                }

                if (!string.IsNullOrWhiteSpace(options.BaseUrl) && (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"Assistant connection '{name}': BaseUrl must be an absolute https URL.");
                }

                if (!string.IsNullOrWhiteSpace(options.Region) || !string.IsNullOrWhiteSpace(options.AwsProfile))
                {
                    errors.Add($"Assistant connection '{name}': Region and AwsProfile apply to Bedrock connections only.");
                }

                break;
            case AssistantProviderKind.Bedrock:
                if (string.IsNullOrWhiteSpace(options.Region))
                {
                    errors.Add($"Assistant connection '{name}': set Region (for CUI, an AWS GovCloud region such as us-gov-west-1).");
                }
                else if (!options.Region.Trim().All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                {
                    errors.Add($"Assistant connection '{name}': Region '{options.Region}' isn't an AWS region name.");
                }

                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    errors.Add($"Assistant connection '{name}': Bedrock connections use AWS credentials (an IAM role or AwsProfile), not ApiKey.");
                }

                if (!string.IsNullOrWhiteSpace(options.BedrockApi) && options.BedrockApi.Trim().ToLowerInvariant() is not ("mantle" or "runtime"))
                {
                    errors.Add($"Assistant connection '{name}': BedrockApi must be 'mantle' or 'runtime'.");
                }

                break;
            default:
                errors.Add($"Assistant connection '{name}': Provider must be Anthropic or Bedrock (the fake provider is turned on with Assistant:EnableFakeProvider).");
                break;
        }

        foreach (var model in options.AllowedModels)
        {
            if (model.Length > 128 || model.Any(char.IsWhiteSpace))
            {
                errors.Add($"Assistant connection '{name}': model id '{model}' isn't valid.");
            }
        }

        return errors;
    }

    /// <summary>Every problem with the configuration (used at startup and by the Production check).</summary>
    public static IReadOnlyList<string> Validate(AssistantOptions options) =>
        options.Connections.SelectMany(c => Validate(c.Key, c.Value)).ToList();

    public static AssistantConnectionDto Describe(string name, AssistantConnectionOptions options)
    {
        var label = !string.IsNullOrWhiteSpace(options.Label)
            ? options.Label.Trim()
            : options.Provider switch
            {
                AssistantProviderKind.Anthropic => "Anthropic API",
                AssistantProviderKind.Bedrock => $"Amazon Bedrock ({options.Region})",
                _ => name,
            };
        var handling = options.Provider switch
        {
            AssistantProviderKind.Anthropic =>
                "Prompts and the records the assistant reads are sent to Anthropic's API (commercial cloud, United States). Not for CUI unless your contracts allow it.",
            AssistantProviderKind.Bedrock when options.IsGovCloud =>
                $"Prompts and records stay in AWS GovCloud (US) ({options.Region}) through Amazon Bedrock, under your AWS account and its FedRAMP High / DoD IL authorizations.",
            AssistantProviderKind.Bedrock =>
                $"Prompts and records are sent to Amazon Bedrock in {options.Region} (commercial AWS), under your AWS account. For CUI, use a GovCloud region.",
            _ => "No data leaves Workbench: the built-in test provider writes canned answers from the tool results.",
        };
        return new AssistantConnectionDto(name, options.Provider, label, options.Region?.Trim(), options.IsGovCloud, options.EffectiveDefaultModel, options.AllowedModels, handling);
    }

    public static AssistantConnectionDto Fake { get; } = new(
        AssistantOptions.FakeConnection,
        AssistantProviderKind.Fake,
        "Built-in test responses (development only)",
        null,
        false,
        AssistantConnectionOptions.DefaultModelFor(AssistantProviderKind.Fake),
        [AssistantConnectionOptions.DefaultModelFor(AssistantProviderKind.Fake)],
        "No data leaves Workbench: canned answers are built from the tool results. For development and testing only.");

    /// <summary>The connections administrators may choose from: valid configured ones, plus the fake one when enabled.</summary>
    public static IReadOnlyList<AssistantConnectionDto> Available(AssistantOptions options)
    {
        var list = options.Connections
            .Where(c => Validate(c.Key, c.Value).Count == 0)
            .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(c => Describe(c.Key, c.Value))
            .ToList();
        if (options.EnableFakeProvider)
        {
            list.Add(Fake);
        }

        return list;
    }
}
