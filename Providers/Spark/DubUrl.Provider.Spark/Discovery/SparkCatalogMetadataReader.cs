using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using DubUrl.Discovery;
using DubUrl.Providers.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Schema;

namespace DubUrl.Provider.Spark.Discovery;

public abstract class SparkCatalogMetadataReader : CatalogSchemaMetadataReader, IStructuredColumnMetadataReader
{
    private readonly ConditionalWeakTable<MetadataView, object> hiddenDefinitions = new();
    private readonly ConditionalWeakTable<MetadataColumn, StructuredColumnMetadata> structuredTypes = new();

    public async Task<StructuredSchemaMetadata> ReadWithStructuredTypesAsync(DbConnection connection,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var metadata = await ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var types = new Dictionary<StructuredColumnIdentity, StructuredColumnMetadata>();
        foreach (var table in metadata.Tables)
        foreach (var column in table.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (structuredTypes.TryGetValue(column, out var type))
                types.Add(new(table.Identity, column.Name), type);
        }
        return new(metadata, types);
    }

    public new virtual async Task<SchemaMetadata> ReadAsync(DbConnection connection, DiscoveryOptions options, CancellationToken cancellationToken = default)
    {
        var metadata = await ProviderSchemaDiagnostics.ReadAsync(() => base.ReadAsync(connection, options, cancellationToken))
            .ConfigureAwait(false);
        if (options.Tables.Count == 0 && options.Features.Contains(SchemaFeature.Views)
            && metadata.Views.Any(view => hiddenDefinitions.TryGetValue(view, out _)
                && (options.Catalogs.Count == 0 || options.Catalogs.Contains(view.Identity.Catalog, StringComparer.Ordinal))
                && (options.Schemas.Count == 0 || options.Schemas.Contains(view.Identity.Schema, StringComparer.Ordinal))))
        {
            var coverage = metadata.Coverage.ToDictionary(pair => pair.Key, pair => pair.Value);
            coverage[SchemaFeature.Views] = MetadataCoverage.Inaccessible;
            metadata = metadata with { Coverage = coverage, Diagnostics = [.. metadata.Diagnostics,
                new("SDS001", SchemaFeature.Views, MetadataCoverage.Inaccessible,
                    "At least one visible view has a definition that the current user cannot read.")] };
        }
        if (options.AllowPartial && metadata.Tables.Any(table => table.Columns.Any(IsUnmappedScalar)))
        {
            var coverage = metadata.Coverage.ToDictionary(pair => pair.Key, pair => pair.Value);
            coverage[SchemaFeature.Columns] = MetadataCoverage.Unrepresentable;
            metadata = metadata with { Coverage = coverage, Tables = metadata.Tables
                .Where(table => !table.Columns.Any(IsUnmappedScalar)).ToArray() };
        }
        return metadata;

        bool IsUnmappedScalar(MetadataColumn column) => (column.Type is null || column.Nullable is null)
            && !structuredTypes.TryGetValue(column, out _);
    }

    protected override MetadataView MapView(MetadataName identity, MetadataColumn[] inventory, string? sql)
    {
        var view = SparkCatalogDefinition.View(identity, inventory, sql);
        if (sql is null)
            hiddenDefinitions.Add(view, new());
        return view;
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var type = row.Text("type_name")?.Split('(')[0].Trim().ToUpperInvariant();
        var column = base.MapColumn(row);
        if (row.Bool("structured") == true)
            return column with { Type = DbType.Object, Length = null, Precision = null, Scale = null };
        return type switch
        {
            "TINYINT" => column with { Type = DbType.SByte },
            "FLOAT" => column with { Type = DbType.Single },
            "STRING" => column with { Type = DbType.String },
            "CHAR" => column with { Type = DbType.StringFixedLength },
            "VARCHAR" => column with { Type = DbType.String },
            "TIMESTAMP_NTZ" => column with { Type = DbType.DateTime2 },
            "TIMESTAMP_LTZ" => column with { Type = DbType.DateTimeOffset },
            _ => column,
        };
    }

    protected override MetadataColumn MapCatalogColumn(CatalogRow row, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var structured = ReadStructuredType(row);
        if (row.Text("type_name") is { } sql && sql.Equals("STRING", StringComparison.OrdinalIgnoreCase)
            && row.Text("comment") is { } comment)
        {
            if (comment.StartsWith(SparkStructuredTypes.JsonTextCommentPrefix, StringComparison.Ordinal))
            {
                structured = new(new(new JsonType(), StructuredStorage.JsonText), MetadataCoverage.Available);
                var description = comment[SparkStructuredTypes.JsonTextCommentPrefix.Length..];
                row = row.With(("comment", description.Length == 0 ? null : description));
            }
            else if (comment.StartsWith(SparkStructuredTypes.LiteralCommentPrefix, StringComparison.Ordinal))
            {
                var description = comment[SparkStructuredTypes.LiteralCommentPrefix.Length..];
                if (SparkStructuredTypes.IsTaggedComment(description))
                    row = row.With(("comment", description));
            }
        }
        if (structured is null)
            return MapColumnDetails(row, options, diagnose);

        var column = MapColumnDetails(row.With(("structured", true)), options, diagnose);
        structuredTypes.Add(column, structured);
        return column;
    }

    private protected virtual StructuredColumnMetadata? ReadStructuredType(CatalogRow row) => null;

    private MetadataColumn MapColumnDetails(CatalogRow row, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var definition = row.Text("column_definition");
        if (definition is null && row.Bool("definition_required") == true)
        {
            foreach (var feature in new[] { SchemaFeature.Defaults, SchemaFeature.Generation }.Where(options.Features.Contains))
                diagnose(feature, "The column definition is unavailable from SHOW CREATE TABLE.");
        }

        var column = base.MapCatalogColumn(row.With(("default_expression", null)), options, diagnose);
        if (definition is not null && options.Features.Contains(SchemaFeature.Generation))
        {
            var generation = SparkCatalogDefinition.Generation(definition, out var unsupported);
            if (unsupported is not null)
                Report(SchemaFeature.Generation, "The generated column or identity mode cannot be represented without loss.", unsupported);
            column = column with { Generation = generation };
        }

        if (!options.Features.Contains(SchemaFeature.Defaults)
            || (definition is null ? row.Text("default_expression") : SparkCatalogDefinition.DefaultSql(definition)) is not { } defaultSql)
            return column;
        if (defaultSql.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return column with { Default = DBNull.Value };
        if (SparkCatalogDefinition.SymbolicDefault(defaultSql) is { } symbolic)
        {
            if (symbolic.Kind is DubUrl.Schema.ColumnDefaultKind.CurrentSchema or DubUrl.Schema.ColumnDefaultKind.CurrentDatabase)
            {
                Report(SchemaFeature.Defaults, "A catalog namespace default does not preserve the inserting session's namespace.", defaultSql);
                return column;
            }
            return column with { Default = symbolic };
        }
        if (SparkCatalogDefinition.TryString(defaultSql, out var text))
            return column with { Default = text };
        var defaultTokens = SparkSqlSyntax.Tokenize(defaultSql);
        if (defaultTokens.Count == 2 && SparkCatalogDefinition.TryString(defaultTokens[1].Text, out text))
        {
            if (defaultTokens[0].Text.ToUpperInvariant() is "DATE" or "TIMESTAMP" or "TIMESTAMP_NTZ"
                && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value))
                return column with { Default = value };
            if (defaultTokens[0].Is("X") && text is not null && text.Length % 2 == 0 && text.All(Uri.IsHexDigit))
                return column with { Default = Convert.FromHexString(text) };
        }
        if (defaultTokens.Any(token => token.Text.StartsWith('\'')))
        {
            Report(SchemaFeature.Defaults, "A quoted default uses an unsupported literal or escape.", defaultSql);
            return column;
        }
        return base.MapCatalogColumn(row.With(("default_expression", defaultSql)), options, (feature, message) =>
        {
            if (feature == SchemaFeature.Defaults) Report(feature, message, defaultSql);
        }) with { Generation = column.Generation };

        void Report(SchemaFeature feature, string message, string sql)
            => ProviderSchemaDiagnostics.Report(diagnose, Identity(row), feature, message,
                member: row.Text("column_name"), nativeDefinition: sql);
    }

    protected static bool NeedsDefinition(DiscoveryOptions options) =>
        options.Features.Contains(SchemaFeature.Defaults) || options.Features.Contains(SchemaFeature.Generation);

    protected static string Quote(string identifier) => new SparkIdentifierFormatter().Format(identifier);

    protected static string QualifiedName(MetadataName identity)
        => string.Join('.', new[] { identity.Catalog, identity.Schema, identity.Name }.OfType<string>().Select(Quote));

    protected static async Task<string?> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    protected static CatalogRow Row(params (string Name, object? Value)[] values)
        => new(values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
}
