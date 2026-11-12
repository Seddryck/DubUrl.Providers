using DubUrl.Provider.Spark.QA.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA.Query;

[Category("Live")]
public sealed class SparkProviderTests : SparkOdbcQueryContract
{
    protected override string ConnectionUrl => SparkOdbcTests.ThriftUrl;
    protected override string AuthenticationConnectionUrl => SparkOdbcTests.ThriftLocation;
    protected override bool SupportsPositionalParameters => false;
    protected override bool SupportsTime => false;
    protected override bool SupportsInterval => false;
}
