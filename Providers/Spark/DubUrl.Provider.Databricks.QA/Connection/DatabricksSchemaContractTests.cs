using DubUrl.Provider.Spark.QA;

namespace DubUrl.Provider.Databricks.QA.Connection;

public sealed class DatabricksSchemaContractTests : SparkSchemaProviderContract
{
    protected override bool IsDelta => true;
    protected override ConnectionUrl UnscopedUrl => new ConnectionUrl(DatabricksWarehouse.Url).WithAuthentication(DatabricksWarehouse.Authentication);
    protected override ConnectionUrl ScopedUrl(string catalog, string schema)
        => new ConnectionUrl($"{DatabricksWarehouse.Location}/{Uri.EscapeDataString(catalog)}/{Uri.EscapeDataString(schema)}?AuthMech=3")
            .WithAuthentication(DatabricksWarehouse.Authentication);
}
