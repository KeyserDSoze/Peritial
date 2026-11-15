using Peritial.Domains;

namespace Peritial.Business;

public static class DocumentKeys
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".jpg", ".jpeg", ".png", ".xml", ".docx" };

    public static string For(TenantId tenantId, Guid documentId, string fileName)
    {
        if (tenantId.Value == Guid.Empty || documentId == Guid.Empty)
            throw new ArgumentException("Invalid tenant or document.");
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("File name required.", nameof(fileName));
        var extension = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(extension)) throw new ArgumentException("Unsupported file extension.", nameof(fileName));
        return $"{tenantId.Value:N}/{documentId:N}{extension.ToLowerInvariant()}";
    }
}
