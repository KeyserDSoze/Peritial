using System.Reflection;
using System.Text.Json;
using Peritial.Business;
using Xunit;

namespace Peritial.Business.Tests;

/// <summary>
/// Public MCP DTO fields are an allowlist, not a projection of database entities.
/// Exact CLR properties (including members added in a record body) are reviewed.
/// This does NOT establish MCP SDK JSON outputSchema or serializer compatibility.
/// </summary>
public sealed class McpOutputDtoContractTests
{
    private static readonly IReadOnlyDictionary<string, Type> Dtos =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [nameof(DocumentDto)] = typeof(DocumentDto),
            [nameof(DocumentPageDto)] = typeof(DocumentPageDto),
            [nameof(DocumentSummaryDto)] = typeof(DocumentSummaryDto),
            [nameof(LegacyXmlImportPlanDto)] = typeof(LegacyXmlImportPlanDto),
            [nameof(LegacyXmlPreviewDto)] = typeof(LegacyXmlPreviewDto),
            [nameof(PeriziaDto)] = typeof(PeriziaDto),
            [nameof(PeriziaSinistroAssociationDto)] = typeof(PeriziaSinistroAssociationDto),
            [nameof(PeriziaSinistroLinkDto)] = typeof(PeriziaSinistroLinkDto),
            [nameof(SinistroDto)] = typeof(SinistroDto),
            [nameof(SinistroPageDto)] = typeof(SinistroPageDto)
        };

    [Fact]
    public void Every_public_MCP_DTO_has_exact_reviewed_properties_and_types()
    {
        using var stream = typeof(McpOutputDtoContractTests).Assembly
            .GetManifestResourceStream("Peritial.McpOutputDtoContract");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream!);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("format").GetInt32());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.Equal("1.2.0", root.GetProperty("mcpCatalogVersion").GetString());
        var expectedTypes = root.GetProperty("types");
        Assert.Equal(Dtos.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            expectedTypes.EnumerateObject().Select(x => x.Name)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray());

        foreach (var (dtoName, clrType) in Dtos)
        {
            var expected = expectedTypes.GetProperty(dtoName).EnumerateObject()
                .ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal);
            var actual = GetShape(clrType);
            Assert.Equal(expected.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                actual.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            foreach (var (property, typeName) in expected)
                Assert.Equal(typeName, actual[property]);
        }
    }

    [Fact]
    public void Negative_contract_regressions_cover_extra_fields_types_and_nullability()
    {
        var actual = GetShape(typeof(SinistroDto));
        var extraField = new Dictionary<string, string>(actual, StringComparer.Ordinal)
        {
            ["TenantId"] = "Guid"
        };
        Assert.False(SameShape(actual, extraField));
        var changedType = new Dictionary<string, string>(actual, StringComparer.Ordinal)
        {
            ["OccurredOn"] = "string"
        };
        Assert.False(SameShape(actual, changedType));
        var changedNullable = new Dictionary<string, string>(actual, StringComparer.Ordinal)
        {
            ["ReportedOn"] = "DateOnly"
        };
        Assert.False(SameShape(actual, changedNullable));
        Assert.True(SameShape(actual, new Dictionary<string, string>(actual)));
    }

    private static bool SameShape(IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(x => right.TryGetValue(x.Key, out var value) && value == x.Value);

    private static IReadOnlyDictionary<string, string> GetShape(Type dto) =>
        dto.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetMethod?.IsPublic == true)
            .ToDictionary(p => p.Name, PropertyTypeSignature, StringComparer.Ordinal);

    private static string PropertyTypeSignature(PropertyInfo property)
    {
        if (property.PropertyType == typeof(string))
        {
            var state = new NullabilityInfoContext().Create(property).ReadState;
            return state switch
            {
                NullabilityState.NotNull => "string",
                NullabilityState.Nullable => "string?",
                _ => throw new InvalidOperationException(
                    $"Unknown nullability of {property.DeclaringType?.Name}.{property.Name}")
            };
        }
        return TypeSignature(property.PropertyType);
    }

    private static string TypeSignature(Type type)
    {
        var value = Nullable.GetUnderlyingType(type);
        if (value is not null)
            return TypeSignature(value) + "?";
        if (type == typeof(Guid)) return "Guid";
        if (type == typeof(DateTime)) return "DateTime";
        if (type == typeof(DateOnly)) return "DateOnly";
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(bool)) return "bool";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return $"IReadOnlyList<{TypeSignature(type.GenericTypeArguments[0])}>";
        if (Dtos.Values.Contains(type)) return type.Name;
        throw new InvalidOperationException($"Unreviewed MCP DTO property type {type}");
    }
}
