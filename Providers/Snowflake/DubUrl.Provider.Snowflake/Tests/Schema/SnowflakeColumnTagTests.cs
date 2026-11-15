using System.Data;
using DubUrl.Provider.Snowflake.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Snowflake.Tests.Schema;

public sealed class SnowflakeColumnTagTests
{
    [Test]
    public void PlansQualifiedColumnTagsAlongsideNativeComments()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SnowflakeDialect>(["snowflake"]);
        var column = new Column("item code", DbType.Int32, description: "Identifier")
        { Annotations = new Dictionary<string, string> { ["GOV.TAGS.OWNER"] = "team's data" } };
        var table = new Table(new DatabaseObjectName("ITEMS", "SALES", "WAREHOUSE"), [column]);

        var plan = new SchemaScriptRenderer(builder.Build().Get<SnowflakeDialect>()).Plan(new([table]));
        Assert.That(plan[1].CommandText, Is.EqualTo("""ALTER TABLE "WAREHOUSE"."SALES"."ITEMS" MODIFY COLUMN "item code" SET TAG "GOV"."TAGS"."OWNER" = 'team''s data';"""));
        Assert.That(plan[2].CommandText, Does.Contain("""COMMENT ON COLUMN "WAREHOUSE"."SALES"."ITEMS"."item code" IS 'Identifier'"""));
    }
}
