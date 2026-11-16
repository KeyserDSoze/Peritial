using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Peritial.Infrastructures;

/// <summary>
/// Configured ClamAV signature-age admission gate. The VERSION command reports
/// the database timestamp, not proof that FreshClam updated it recently.
/// HTTP/Development demos may omit this policy; non-Development MUST configure
/// a bounded age at startup. The ClamAV daemon timezone MUST be UTC.
/// </summary>
public sealed class ClamdSignaturePolicy
{
    private static readonly TimeSpan FutureClockSkew = TimeSpan.FromMinutes(5);
    private readonly TimeSpan? _maxAge;

    private ClamdSignaturePolicy(TimeSpan? maxAge) => _maxAge = maxAge;
    public bool Enabled => _maxAge.HasValue;

    public static ClamdSignaturePolicy FromConfiguration(
        IConfiguration config, bool isDevelopment)
    {
        var setting = config["Malware:Clamd:MaxSignatureAgeHours"];
        if (setting is null)
        {
            if (!isDevelopment)
                throw new InvalidOperationException(
                    "Malware:Clamd:MaxSignatureAgeHours is required outside Development.");
            return new ClamdSignaturePolicy(null);
        }

        if (setting != setting.Trim() ||
            !int.TryParse(setting, NumberStyles.None, CultureInfo.InvariantCulture,
                out var hours) ||
            hours is < 1 or > 168)
            throw new InvalidOperationException(
                "Malware:Clamd:MaxSignatureAgeHours must be an integer 1–168.");
        return new ClamdSignaturePolicy(TimeSpan.FromHours(hours));
    }

    /// <summary>Parses the bounded Clamd VERSION response in UTC, never logging it.</summary>
    public bool IsFreshReply(string response, DateTimeOffset now)
    {
        if (!Enabled) return true;
        if (response.Length is < 30 or > 160 ||
            response.Any(c => c is < ' ' or > '~'))
            return false;

        var fields = response.Split('/');
        if (fields.Length != 3 ||
            !Regex.IsMatch(fields[0], @"\AClamAV [0-9]+\.[0-9]+(?:\.[0-9]+)?\z",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            !int.TryParse(fields[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out var databaseVersion) ||
            databaseVersion <= 0)
            return false;

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal |
                                      DateTimeStyles.AdjustToUniversal |
                                      DateTimeStyles.AllowInnerWhite;
        string[] formats = ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"];
        if (!DateTime.TryParseExact(fields[2], formats, CultureInfo.InvariantCulture,
                styles, out var built))
            return false;

        var age = now.ToUniversalTime() - new DateTimeOffset(built, TimeSpan.Zero);
        return age >= -FutureClockSkew && age <= _maxAge!.Value;
    }

    /// <summary>
    /// zVERSION over a separate private TCP connection, fail closed on bad
    /// framing, unrecognized content, timeout or unavailable command.
    /// </summary>
    public async Task<bool> CheckAsync(string host, int port,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled) return true;
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(host))
            return false;

        using var socket = new TcpClient();
        await socket.ConnectAsync(host, port, cancellationToken);
        await using var stream = socket.GetStream();
        await stream.WriteAsync("zVERSION\0"u8.ToArray(), cancellationToken);

        var reply = new byte[161];
        var one = new byte[1];
        var size = 0;
        while (size < reply.Length)
        {
            if (await stream.ReadAsync(one, cancellationToken) != 1)
                return false;
            if (one[0] == 0)
                return IsFreshReply(Encoding.ASCII.GetString(reply, 0, size),
                    DateTimeOffset.UtcNow);
            if (one[0] is < 32 or > 126)
                return false;
            reply[size++] = one[0];
        }
        return false;
    }
}
