using DubUrl.Locating.OdbcDriver;
using DubUrl.Locating.RegexUtils;
using DubUrl.Provider.Odbc.Mapping;
using DubUrl.Provider.Spark.Metadata;

namespace DubUrl.Provider.Odbc.Drivers.Discovery;

[Driver<SparkDriverLocator.DriverRegex, SparkOdbcMapper, SparkDatabase>]
public sealed class SparkDriverLocator : IDriverLocator
{
    internal const string DriverName = "Simba Spark ODBC Driver";
    private readonly DriverLister lister;

    public SparkDriverLocator() : this(new DriverLister()) { }

    internal SparkDriverLocator(DriverLister lister) => this.lister = lister;

    public string Locate() => lister.LocateRegistered(DriverName);

    internal sealed class DriverRegex() : BaseDriverRegex([new LiteralMatch(DriverName)]);
}

[Driver<DatabricksDriverLocator.DriverRegex, DatabricksOdbcMapper, DatabricksDatabase>]
public sealed class DatabricksDriverLocator : IDriverLocator
{
    internal const string DriverName = "Databricks ODBC Driver";
    private readonly DriverLister lister;

    public DatabricksDriverLocator() : this(new DriverLister()) { }

    internal DatabricksDriverLocator(DriverLister lister) => this.lister = lister;

    public string Locate() => lister.LocateRegistered(DriverName);

    internal sealed class DriverRegex() : BaseDriverRegex([new LiteralMatch(DriverName)]);
}
