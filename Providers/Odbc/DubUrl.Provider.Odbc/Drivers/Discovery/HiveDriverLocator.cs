using DubUrl.Locating.OdbcDriver;
using DubUrl.Locating.RegexUtils;
using DubUrl.Provider.Hive.Metadata;
using DubUrl.Provider.Odbc.Mapping;

namespace DubUrl.Provider.Odbc.Drivers.Discovery;

[Driver<HiveDriverLocator.DriverRegex, HiveOdbcMapper, HiveDatabase>]
public sealed class HiveDriverLocator : IDriverLocator
{
    internal const string DriverName = "Cloudera ODBC Driver for Apache Hive";
    private readonly DriverLister lister;

    public HiveDriverLocator() : this(new DriverLister()) { }
    internal HiveDriverLocator(DriverLister lister) => this.lister = lister;

    public string Locate() => lister.LocateRegistered(DriverName);

    internal sealed class DriverRegex() : BaseDriverRegex([new LiteralMatch(DriverName)]);
}
