using Microsoft.Extensions.Configuration;
using Workbench.Contracts.Collaboration;

namespace Workbench.Infrastructure.Collaboration;

/// <summary>
/// TEMPORARY: stands in for the signed-in user until authentication exists. Everyone is the user named by
/// config <c>Dev:UserEmail</c> (default <c>dev@workbench.local</c>). Replace with a claims-based implementation
/// when OIDC is wired up.
/// </summary>
public sealed class DevCurrentUser(IConfiguration configuration) : ICurrentUser
{
    public const string DefaultEmail = "dev@workbench.local";

    public string Email { get; } = string.IsNullOrWhiteSpace(configuration["Dev:UserEmail"])
        ? DefaultEmail
        : configuration["Dev:UserEmail"]!.Trim().ToLowerInvariant();
}
