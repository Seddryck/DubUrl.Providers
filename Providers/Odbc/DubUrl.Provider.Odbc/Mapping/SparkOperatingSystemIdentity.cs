using System.Data.Common;
using DubUrl.Authentication;
using DubUrl.Provider.Odbc.Drivers.Discovery;

namespace DubUrl.Provider.Odbc.Mapping;

internal sealed class SparkOperatingSystemIdentity : IOperatingSystemIdentityAdapter
{
    public void ValidateUsername(string? username)
    {
        if (username is not null) Conflict();
    }

    public bool ValidateOption(string name, string value)
    {
        switch (name)
        {
            case "authmech":
            case "sasl":
                if (value != "1") Conflict();
                return true;
            case "thrifttransport":
                if (value is not ("1" or "2")) Conflict();
                return true;
            case "delegatekrbcreds":
            case "driverconfigtakeprecedence":
            case "useonlysspi":
            case "usejwtassertion":
                if (value != "0") Conflict();
                return true;
            case "integratedsecurity":
            case "trustedconnection":
                Conflict();
                return true;
            default:
                return false;
        }
    }

    public void Configure(DbConnectionStringBuilder builder)
    {
        if (builder.ContainsKey("DSN") || builder.ContainsKey("FileDSN")
            || !Get(builder, "Driver").Trim('{', '}').Equals(SparkDriverLocator.DriverName, StringComparison.OrdinalIgnoreCase))
            throw new ConnectionAuthenticationException(AuthenticationDiagnosticCodes.UnsupportedAuthentication);

        foreach (string name in builder.Keys)
            ValidateOption(new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()), Get(builder, name));

        if (string.IsNullOrWhiteSpace(Get(builder, "KrbHostFQDN")) || string.IsNullOrWhiteSpace(Get(builder, "KrbServiceName")))
            throw new ConnectionAuthenticationException(AuthenticationDiagnosticCodes.MissingCredentials);

        builder.Remove("sasl");
        builder["AuthMech"] = 1;
        builder["DelegateKrbCreds"] = 0;
        if (!builder.ContainsKey("ThriftTransport"))
            builder["ThriftTransport"] = 1;
    }

    private static string Get(DbConnectionStringBuilder builder, string name)
        => builder.TryGetValue(name, out var value) ? value.ToString() ?? "" : "";

    private static void Conflict()
        => throw new ConnectionAuthenticationException(AuthenticationDiagnosticCodes.CredentialConflict);
}
