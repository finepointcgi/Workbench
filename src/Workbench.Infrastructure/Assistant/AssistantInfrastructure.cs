using System.Collections.Concurrent;
using Anthropic;
using Anthropic.Bedrock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Application.Assistant;
using Workbench.Contracts.Assistant;

namespace Workbench.Infrastructure.Assistant;

public static class AssistantInfrastructure
{
    /// <summary>Lets the assistant reach the Anthropic API and Amazon Bedrock (connections under <c>Assistant:Connections</c>).</summary>
    public static IServiceCollection AddAssistantInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Replace(ServiceDescriptor.Singleton<IAssistantModelClientFactory, AssistantModelClientFactory>());
        return services;
    }
}

/// <summary>
/// One SDK client per connection, reused (they hold HTTP connection pools). Credentials come from configuration (the
/// Anthropic key) or the AWS credential chain (Bedrock: the instance, task or pod role, or a named profile).
/// </summary>
internal sealed class AssistantModelClientFactory(IOptionsMonitor<AssistantOptions> options, ILoggerFactory loggers) : IAssistantModelClientFactory
{
    private readonly ConcurrentDictionary<string, (string Fingerprint, Lazy<Task<IAnthropicClient>> Client)> _clients = new(StringComparer.OrdinalIgnoreCase);

    public IAssistantModelClient Create(string connectionName, AssistantConnectionOptions? connection)
    {
        if (connection is null || connection.Provider == AssistantProviderKind.Fake)
        {
            if (!options.CurrentValue.EnableFakeProvider)
            {
                throw new AssistantProviderException("The test provider is turned off on this server.");
            }

            return new FakeAssistantModel();
        }

        if (AssistantConnections.Validate(connectionName, connection).Count > 0)
        {
            throw new AssistantProviderException($"The assistant connection '{connectionName}' isn't configured correctly.");
        }

        var fingerprint = string.Join('|', connection.Provider, connection.Region, connection.AwsProfile, connection.BaseUrl, connection.UsesBedrockRuntime,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(connection.ApiKey ?? ""))));
        var entry = _clients.AddOrUpdate(connectionName,
            _ => (fingerprint, new Lazy<Task<IAnthropicClient>>(() => BuildAsync(connection))),
            (_, existing) => existing.Fingerprint == fingerprint ? existing : (fingerprint, new Lazy<Task<IAnthropicClient>>(() => BuildAsync(connection))));
        return new AnthropicModelClient(async () =>
        {
            try
            {
                return await entry.Client.Value;
            }
            catch
            {
                // Don't keep a failed client (missing credentials, wrong region): the next question tries again.
                _clients.TryRemove(new KeyValuePair<string, (string, Lazy<Task<IAnthropicClient>>)>(connectionName, entry));
                throw;
            }
        }, loggers.CreateLogger<AnthropicModelClient>());
    }

    private async Task<IAnthropicClient> BuildAsync(AssistantConnectionOptions c)
    {
        var timeout = options.CurrentValue.RequestTimeout;
        if (c.Provider == AssistantProviderKind.Anthropic)
        {
            return string.IsNullOrWhiteSpace(c.BaseUrl)
                ? new AnthropicClient { ApiKey = c.ApiKey!, Timeout = timeout, MaxRetries = 2 }
                : new AnthropicClient { ApiKey = c.ApiKey!, Timeout = timeout, MaxRetries = 2, BaseUrl = c.BaseUrl };
        }

        var region = c.Region!.Trim();
        if (!c.UsesBedrockRuntime)
        {
            // Bedrock's Messages API endpoint, signed with SigV4 from the profile or the default AWS credential chain.
            return new AnthropicBedrockMantleClient(new MantleAwsClientOptions { AwsRegion = region, AwsProfile = string.IsNullOrWhiteSpace(c.AwsProfile) ? null : c.AwsProfile });
        }

        // InvokeModel (bedrock-runtime), available in AWS GovCloud: credentials and region from the AWS environment
        // (the instance, task or pod role; AWS_REGION must be the connection's region).
        var configured = Environment.GetEnvironmentVariable("AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        if (!string.Equals(configured, region, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssistantProviderException($"Set AWS_REGION to {region} on the server to use this Bedrock connection.");
        }

        var credentials = await AnthropicBedrockCredentialsHelper.FromEnv()
            ?? throw new AssistantProviderException("No AWS credentials were found for Amazon Bedrock. Give the server an IAM role (or AWS_PROFILE).");
        return new AnthropicBedrockClient(credentials);
    }
}
