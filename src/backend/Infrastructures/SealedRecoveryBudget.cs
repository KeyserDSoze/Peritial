namespace Peritial.Infrastructures;

/// <summary>
/// Fixed capacity ceilings for the current offline sealed-bundle protocol.
/// The transfer allowance covers the outer index (at most 50 MB), its MAC
/// and AES-GCM tags (at most one 16-byte tag per MiB plus one per file).
/// The separate remote transfer inventory and completion files are bounded
/// independently by the protocol.
/// </summary>
internal static class SealedRecoveryBudget
{
    internal const long MaxPlaintextBytes = 1024L * 1024 * 1024;
    internal const long MaxTransferBytes = MaxPlaintextBytes + 64L * 1024 * 1024;

    internal static long RequirePlaintext(IEnumerable<long> lengths) =>
        ObjectRecoveryBudget.RequireWithin(lengths, MaxPlaintextBytes,
            ObjectRecoveryBudget.DefaultMaxObjects + 4);

    internal static long RequireTransfer(IEnumerable<long> lengths) =>
        ObjectRecoveryBudget.RequireWithin(lengths, MaxTransferBytes,
            ObjectRecoveryBudget.DefaultMaxObjects + 6);
}
