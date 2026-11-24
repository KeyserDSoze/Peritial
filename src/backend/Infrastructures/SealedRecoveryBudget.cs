using System.Globalization;

namespace Peritial.Infrastructures;

/// <summary>
/// Operator-selected plaintext capacity, with a fixed hard ceiling. The
/// ciphertext allowance covers the outer index, its MAC and GCM tags;
/// remote transfer inventory and completion files are bounded separately.
/// </summary>
public static class SealedRecoveryBudget
{
    public const long MaxPlaintextBytes = 1024L * 1024 * 1024; // default
    public const long MaxSupportedPlaintextBytes = 4L * 1024 * 1024 * 1024;
    private const long TransferOverheadBytes = 64L * 1024 * 1024;
    internal const long MaxTransferBytes = MaxPlaintextBytes + TransferOverheadBytes;

    public static long ParsePlaintextLimit(string? value)
    {
        if (value is null) return MaxPlaintextBytes;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var limit))
            throw new InvalidOperationException("Invalid sealed recovery capacity budget.");
        RequireLimit(limit);
        return limit;
    }

    internal static void RequireLimit(long maxPlaintextBytes)
    {
        if (maxPlaintextBytes <= 0 || maxPlaintextBytes > MaxSupportedPlaintextBytes)
            throw new InvalidOperationException("Invalid sealed recovery capacity budget.");
    }

    internal static long RequirePlaintext(IEnumerable<long> lengths,
        long maxPlaintextBytes = MaxPlaintextBytes)
    {
        RequireLimit(maxPlaintextBytes);
        return ObjectRecoveryBudget.RequireWithin(lengths, maxPlaintextBytes,
            ObjectRecoveryBudget.DefaultMaxObjects + 4);
    }

    internal static long RequireTransfer(IEnumerable<long> lengths,
        long maxPlaintextBytes = MaxPlaintextBytes)
    {
        RequireLimit(maxPlaintextBytes);
        return ObjectRecoveryBudget.RequireWithin(lengths,
            maxPlaintextBytes + TransferOverheadBytes,
            ObjectRecoveryBudget.DefaultMaxObjects + 6);
    }
}
