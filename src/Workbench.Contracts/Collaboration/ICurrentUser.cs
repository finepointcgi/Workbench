namespace Workbench.Contracts.Collaboration;

/// <summary>The signed-in user. Until authentication exists the server uses a configured dev user.</summary>
public interface ICurrentUser
{
    string Email { get; }
}
