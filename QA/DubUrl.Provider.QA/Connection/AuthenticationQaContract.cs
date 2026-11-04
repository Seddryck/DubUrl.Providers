using DubUrl.Mapping;
namespace DubUrl.Providers.QA.Contracts.Connection;
public abstract class AuthenticationQaContract
{
    protected abstract string AuthenticationConnectionUrl { get; }
    protected abstract ISchemeRegistry AuthenticationRegistry { get; }
}
