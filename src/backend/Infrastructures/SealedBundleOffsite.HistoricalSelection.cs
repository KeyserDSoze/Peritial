using System.Text.Json;

namespace Peritial.Infrastructures;

public static partial class SealedBundleOffsite
{
    /// <summary>
    /// Parse an OPERATOR-SUPPLIED exact selection of S3 versions for the
    /// historical recovery command. The selection is NOT an authorization
    /// or integrity proof: DownloadHistoricalAsync checks HMAC, SHA-256,
    /// AES-GCM and retention of each selected S3 object version.
    ///
    /// JSON: {"Version":1,"Versions":{"complete":"version-id", ...}}
    /// Keys are relative to sealed-v1/&lt;backup-id&gt;/.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadHistoricalVersionSelection(
        string selectionFile)
    {
        if (!Path.IsPathFullyQualified(selectionFile) ||
            !File.Exists(selectionFile) ||
            File.GetAttributes(selectionFile).HasFlag(FileAttributes.ReparsePoint) ||
            new FileInfo(selectionFile).Length is <= 0 or > 50_000_000)
            throw new InvalidOperationException(
                "Historical selection must be an existing bounded absolute regular file.");

        try
        {
            using var data = File.OpenRead(selectionFile);
            using var doc = JsonDocument.Parse(data,
                new JsonDocumentOptions { MaxDepth = 5, AllowTrailingCommas = false });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Historical selection must be a JSON object.");
            var seenFields = new HashSet<string>(StringComparer.Ordinal);
            JsonElement versions = default;
            var format = 0;
            foreach (var p in root.EnumerateObject())
            {
                if (!seenFields.Add(p.Name))
                    throw new InvalidOperationException("Duplicate historical selection field.");
                if (p.Name == "Version" && p.Value.ValueKind == JsonValueKind.Number &&
                    p.Value.TryGetInt32(out var n))
                    format = n;
                else if (p.Name == "Versions" && p.Value.ValueKind == JsonValueKind.Object)
                    versions = p.Value;
                else
                    throw new InvalidOperationException("Unexpected historical selection field.");
            }
            if (seenFields.Count != 2 || format != 1 ||
                versions.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Unsupported historical selection format.");

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in versions.EnumerateObject())
            {
                if (result.Count >= 100_005 || pair.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("Historical selection contains invalid versions.");
                var name = pair.Name;
                if (!IsCanonicalRelativeName(name) ||
                    !result.TryAdd(name, pair.Value.GetString()!))
                    throw new InvalidOperationException("Historical selection has unsafe or repeated names.");
                RequirePinnedVersion(result, name);
            }
            if (result.Count < 9) // 3 markers + index pair + minimum 4 PG/S3 payloads
                throw new InvalidOperationException("Historical selection is incomplete.");
            return result;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Historical selection has invalid JSON.");
        }
    }

    private static bool IsCanonicalRelativeName(string name)
    {
        if (name is "complete" or "transfer.json" or "transfer.hmac" or
            "index.json" or "index.hmac")
            return true;
        if (!name.StartsWith("payload/", StringComparison.Ordinal) ||
            name.Length != "payload/00000000.enc".Length ||
            !name.EndsWith(".enc", StringComparison.Ordinal))
            return false;
        return name.AsSpan("payload/".Length, 8)
            .IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
