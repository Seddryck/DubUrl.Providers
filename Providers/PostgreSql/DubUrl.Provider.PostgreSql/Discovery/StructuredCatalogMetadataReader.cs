using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Discovery;

public abstract class StructuredCatalogMetadataReader : CatalogSchemaMetadataReader, IStructuredColumnMetadataReader
{
    private readonly ConditionalWeakTable<MetadataColumn, StructuredColumnMetadata> types = new();

    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures { get; } = Enum.GetValues<SchemaFeature>()
        .Where(feature => feature is not (SchemaFeature.Tables or SchemaFeature.Columns)).ToHashSet();

    protected override string KeysSql => "";

    public async Task<StructuredSchemaMetadata> ReadWithStructuredTypesAsync(DbConnection connection,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var metadata = await ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var structured = new Dictionary<StructuredColumnIdentity, StructuredColumnMetadata>();
        foreach (var table in metadata.Tables)
        foreach (var column in table.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (types.TryGetValue(column, out var type))
                structured.Add(new(table.Identity, column.Name), type);
        }
        return new(metadata, structured);
    }

    protected void Track(MetadataColumn column, StructuredColumnMetadata type) => types.Add(column, type);

    protected static MetadataColumn StructuredColumn(MetadataColumn column)
        => column with { Type = DbType.Object, Length = null, Precision = null, Scale = null };
}
