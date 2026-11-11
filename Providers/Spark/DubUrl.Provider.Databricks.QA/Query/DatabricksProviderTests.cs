using DubUrl.Authentication;
using DubUrl.Provider.Databricks.QA.Connection;
using DubUrl.Provider.Spark.QA;
using NUnit.Framework;

namespace DubUrl.Provider.Databricks.QA.Query;

[Category("Live")]
public sealed class DatabricksProviderTests : SparkOdbcQueryContract
{
    protected override string ConnectionUrl => DatabricksWarehouse.Url;
    protected override ConnectionAuthentication LiveAuthentication => DatabricksWarehouse.Authentication;
}
