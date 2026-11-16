using Peritial.Domains;

namespace Peritial.Applications;

/// <summary>
/// Stable human-readable REST/MCP contract: exactly five workflow targets,
/// never numeric enums, terminal cancel states or case-insensitive aliases.
/// Domain checks that the requested target is the one legal next step.
/// </summary>
public static class PeriziaAdvanceTarget
{
    public static PeriziaStatus Parse(string? target) => target switch
    {
        "ToBeRevised" => PeriziaStatus.ToBeRevised,
        "Revised" => PeriziaStatus.Revised,
        "ToSend" => PeriziaStatus.ToSend,
        "Sent" => PeriziaStatus.Sent,
        "Billed" => PeriziaStatus.Billed,
        _ => throw new ArgumentException("Invalid target perizia status.", nameof(target))
    };
}
