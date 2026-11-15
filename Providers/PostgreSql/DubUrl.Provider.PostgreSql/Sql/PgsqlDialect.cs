using DubUrl.Provider.PostgreSql.Schema.Generation;
using DubUrl.Provider.PostgreSql.Sql.Functions;
using DubUrl.Provider.PostgreSql.Sql.Rendering;
using DubUrl.Provider.PostgreSql.Sql.Types;
using DubUrl.Querying;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Casters;
using DubUrl.Querying.Dialects.Functions;
using DubUrl.Querying.Dialects.Renderers;
using DubUrl.Querying.TypeMapping;
using DubUrl.Schema;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubUrl.Provider.PostgreSql.Sql;

[DbTypeMapper<PgsqlTypeMapper>]
[SqlFunctionMapper<PgsqlFunctionMapper>]
[Renderer<PgsqlRenderer>()]
[ReturnCaster<DateTimeCaster<DateOnly>>]
[ReturnCaster<TimeSpanCaster<TimeOnly>>]
[ParentLanguage<SqlLanguage>]
public class PgsqlDialect : BaseDialect
{
    public override ISchemaSqlRenderer SchemaSqlRenderer => PgsqlSchemaSqlRenderer.Default;

    internal Version? ServerVersion { get; private set; }
    private IReadOnlyList<PostgreSqlCompositeType> CompositeTypes { get; set; } = [];

    /// <summary>Binds a portable struct shape to a named PostgreSQL composite. Register nested types first.</summary>
    public PgsqlDialect WithCompositeType(DatabaseObjectName name, StructType shape)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(shape);
        var existing = CompositeTypes.FirstOrDefault(type => type.Name == name || type.Shape == shape);
        if (existing is not null)
            return existing.Name == name && existing.Shape == shape ? this
                : throw new ArgumentException("Each composite name and shape must have a single binding.");

        var type = PostgreSqlCompositeType.Create(name, shape, FindCompositeType);
        var dialect = (PgsqlDialect)MemberwiseClone();
        dialect.CompositeTypes = [.. CompositeTypes, type];
        return dialect;
    }

    /// <summary>Returns CREATE TYPE statements for provisioning the registered composites in dependency order.</summary>
    public IReadOnlyList<string> RenderCompositeTypeDefinitions()
        => CompositeTypes.Select(type => type.Definition).ToArray();

    internal string? ResolveCompositeType(StructType shape)
        => FindCompositeType(shape)?.SqlName;

    private PostgreSqlCompositeType? FindCompositeType(StructType shape)
        => CompositeTypes.FirstOrDefault(type => type.Shape == shape);

    /// <summary>Sets the PostgreSQL server version for planning version-dependent schema features.</summary>
    public PgsqlDialect WithServerVersion(Version serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        var dialect = (PgsqlDialect)MemberwiseClone();
        dialect.ServerVersion = serverVersion;
        dialect.SchemaCapabilities = SchemaCapabilities with { SupportsVirtualGeneratedColumns = serverVersion.Major >= 18 };
        return dialect;
    }

    private DialectCapabilities SchemaCapabilities { get; set; } = PgsqlSchemaCapabilities.Create();
    public override DialectCapabilities Capabilities => SchemaCapabilities;

    internal PgsqlDialect(ILanguage language, string[] aliases, IRenderer renderer, ICaster[] casters, IDbTypeMapper dbTypeMapper, ISqlFunctionMapper sqlFunctionMapper)
        : base(language, aliases, renderer, casters, dbTypeMapper, sqlFunctionMapper) { }
}
