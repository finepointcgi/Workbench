using Workbench.Contracts.Purchasing;

namespace Workbench.Ui.Purchasing.Sourcing;

/// <summary>Display helpers for RFQs, the supplier portal and EDI.</summary>
public static class SourcingFormat
{
    public static string Label(RfqState s) => s switch
    {
        RfqState.Open => "Out for quotes",
        RfqState.PartiallyAwarded => "Partly awarded",
        _ => s.ToString(),
    };

    public static MudBlazor.Color Color(RfqState s) => s switch
    {
        RfqState.Open => MudBlazor.Color.Info,
        RfqState.PartiallyAwarded => MudBlazor.Color.Warning,
        RfqState.Awarded => MudBlazor.Color.Success,
        RfqState.Cancelled => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(RfqInvitationState s) => s switch
    {
        RfqInvitationState.Invited => "Waiting for a quote",
        RfqInvitationState.Quoted => "Quoted",
        _ => "Declined",
    };

    public static string Label(AcknowledgementState s) => s switch
    {
        AcknowledgementState.Accepted => "Accepted",
        AcknowledgementState.AcceptedWithChanges => "Accepted with changes",
        _ => "Rejected",
    };

    public static string Label(ShipNoticeState s) => s switch
    {
        ShipNoticeState.InTransit => "In transit",
        ShipNoticeState.Received => "Received",
        _ => "Cancelled",
    };

    public static string Label(SupplierChannel c) => c == SupplierChannel.Edi ? "EDI" : "Portal";

    public static string Label(EdiOutcome o) => o switch
    {
        EdiOutcome.Processed => "Processed",
        EdiOutcome.Failed => "Failed",
        _ => "Duplicate (skipped)",
    };

    public static MudBlazor.Color Color(EdiOutcome o) => o switch
    {
        EdiOutcome.Processed => MudBlazor.Color.Success,
        EdiOutcome.Failed => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static string TransactionName(string set) => set switch
    {
        "850" => "850 Purchase order",
        "855" => "855 Acknowledgement",
        "856" => "856 Ship notice",
        "810" => "810 Invoice",
        _ => "Unreadable file",
    };
}
