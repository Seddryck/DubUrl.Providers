using System.Reflection;
using DubUrl.Registering;

namespace DubUrl.Providers.QA.Contracts.Connection;

internal sealed class QaFactoryDiscovery(Assembly assembly) : BinFolderDiscoverer([assembly])
{
    public override IEnumerable<Type> Execute()
        => base.Execute().Where(type => type.FullName != "DubUrl.Adbc.AdbcProviderFactory");
}
