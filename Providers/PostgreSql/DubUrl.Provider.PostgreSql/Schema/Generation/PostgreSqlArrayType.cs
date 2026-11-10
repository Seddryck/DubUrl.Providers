using System.Security.Cryptography;
using System.Text;
using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

internal static class PostgreSqlArrayType
{
    internal const string RankCheckPrefix = "__duburl_array_rank_";

    internal static string? Render(ArrayType array, string name)
    {
        if (array.ElementNullable != true)
            return null;
        var element = PostgreSqlStructuredLeaf.Render(array.Element);
        if (element is null)
            return null;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16)).ToLowerInvariant();
        var column = PostgreSqlSchemaSqlRenderer.QuoteIdentifier(name);
        // PostgreSQL returns NULL rank for NULL and empty arrays. Both satisfy a CHECK.
        return $"pg_catalog.{element}[] CONSTRAINT {RankCheckPrefix}{hash} CHECK (pg_catalog.array_ndims({column}) OPERATOR(pg_catalog.=) 1)";
    }

    internal static StructuredColumnMetadata Decode(int? rank, int? typeModifier, string? elementSchema, string? element,
        bool? defaultCollation, bool checkedRank)
    {
        if (rank != 1 || typeModifier != -1 || elementSchema != "pg_catalog" || element is null || defaultCollation != true)
            return new(null, MetadataCoverage.Unrepresentable);

        var leaf = PostgreSqlStructuredLeaf.Decode(element);
        return leaf is null ? new(null, MetadataCoverage.Unrepresentable)
            : new(new(new ArrayType(leaf, true), StructuredStorage.Native),
                checkedRank ? MetadataCoverage.Available : MetadataCoverage.Unrepresentable);
    }
}
