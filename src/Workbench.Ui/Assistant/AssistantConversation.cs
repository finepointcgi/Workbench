using Workbench.Contracts.Assistant;

namespace Workbench.Ui.Assistant;

/// <summary>The panel's conversation for this browser tab (kept while the panel is closed). The server keeps the audit copy.</summary>
public sealed class AssistantConversation
{
    public Guid? Id { get; set; }

    public List<AssistantTurn> Turns { get; } = [];

    public void Clear()
    {
        Id = null;
        Turns.Clear();
    }
}

public sealed class AssistantTurn(string question)
{
    public string Question { get; } = question;

    public System.Text.StringBuilder Answer { get; } = new();

    public string? Activity { get; set; }

    public string? Error { get; set; }

    public bool Streaming { get; set; } = true;

    public IReadOnlyList<AssistantCitation> Citations { get; set; } = [];
}
