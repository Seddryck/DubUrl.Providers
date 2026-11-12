using DubUrl.Provider.Spark.QA;

namespace DubUrl.Provider.Spark.QA.Connection;

public sealed class SparkSchemaContractTests : SparkSchemaProviderContract
{
    protected override bool IsDelta => false;
    protected override ConnectionUrl UnscopedUrl => new(SparkOdbcTests.ThriftUrl + "&UseNativeQuery=1");
    protected override ConnectionUrl ScopedUrl(string catalog, string schema)
        => new($"odbc+spark+tcp://127.0.0.1/schema/{Uri.EscapeDataString(schema)}?AuthMech=0&sasl=0&UseNativeQuery=1");
}
