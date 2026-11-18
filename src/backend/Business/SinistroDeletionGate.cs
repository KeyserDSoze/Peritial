namespace Peritial.Business;

/// <summary>
/// Explicitly temporary, development-only hard-delete gate. This is not a
/// legal-retention engine or a production approval mechanism. Production
/// remains disabled even if a deployment accidentally supplies the flag.
/// </summary>
public sealed class SinistroDeletionGate
{
    private SinistroDeletionGate(bool enabled) => Enabled = enabled;

    public bool Enabled { get; }

    public static SinistroDeletionGate Disabled { get; } = new(false);

    public static SinistroDeletionGate ForEnvironment(bool isDevelopment, bool enabled)
    {
        if (!isDevelopment && enabled)
            throw new InvalidOperationException(
                "Sinistri hard deletion cannot be enabled outside Development " +
                "until legal holds, retention and approval policy are implemented.");
        return enabled ? new SinistroDeletionGate(true) : Disabled;
    }
}
