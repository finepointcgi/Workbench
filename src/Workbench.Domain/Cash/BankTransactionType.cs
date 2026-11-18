namespace Workbench.Domain.Cash;

// Stays in core with the statement file readers (Application/Cash/Statements): corporate card feeds read OFX and CSV statements with them.
public enum BankTransactionType
{
    Deposit,
    Check,
    Ach,
    Wire,
    Fee,
    Interest,
    Transfer,
    Other,
}
