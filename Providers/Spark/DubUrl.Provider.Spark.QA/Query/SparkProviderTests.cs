using DubUrl.Provider.Spark.QA.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA.Query;

[Category("Live")]
public sealed class SparkProviderTests : SparkOdbcQueryContract
{
    protected override string ConnectionUrl => SparkOdbcTests.ThriftUrl;
    protected override bool SupportsTime => false;
}
