using System.Data.Common;
using DubUrl.Authentication;
using DubUrl.Provider.Odbc.Drivers.Discovery;

namespace DubUrl.Provider.Odbc.Authentication;

public static class DatabricksAuthentication
{
    /// <summary>Supplies an OAuth access token to the Databricks ODBC Driver when preparing a connection.</summary>
    public static ConnectionAuthentication OAuthAccessToken(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return new OAuthTokenAuthentication(accessToken);
    }

    private sealed class OAuthTokenAuthentication(string accessToken) : ConnectionAuthentication
    {
        public override ConnectionAuthenticationKind Kind => ConnectionAuthenticationKind.NativeToken;

        public override void ValidateContext(AuthenticationContext context)
        {
            if (context is not { Scheme: "odbc+databricks", ProviderInvariantName: "System.Data.Odbc" })
                throw new ConnectionAuthenticationException(AuthenticationDiagnosticCodes.UnsupportedAuthentication);
        }

        public override void Configure(DbConnection connection, AuthenticationContext context)
        {
            ValidateContext(context);
            var factory = DbProviderFactories.GetFactory(connection);
            if (factory?.GetType().FullName != "System.Data.Odbc.OdbcFactory"
                || factory.CreateConnectionStringBuilder() is not { } settings)
                throw new ConnectionAuthenticationException(AuthenticationDiagnosticCodes.UnsupportedAuthentication);

            settings.ConnectionString = connection.ConnectionString;
            if (settings["Driver"] is not string driver
                || !driver.Trim('{', '}').Equals(DatabricksDriverLocator.DriverName, StringComparison.OrdinalIgnoreCase))
                throw new ConnectionAuthenticationException(AuthenticationDiagnosticCodes.UnsupportedAuthentication);

            foreach (string name in settings.Keys)
            {
                if (!settings.ShouldSerialize(name))
                    continue;
                var key = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                var value = settings[name].ToString();
                var conflicts = key switch
                {
                    "authmech" => value != "11",
                    "authflow" => value != "0",
                    "ssl" => value != "1",
                    "thrifttransport" => value != "2",
                    "allowselfsignedservercert" or "allowhostnamecnmismatch" or "loglevel"
                        or "driverconfigtakeprecedence" or "usejwtassertion" => value != "0",
                    "dsn" or "filedsn" or "savefile" or "sasl" or "uid" or "pwd" or "krbhostfqdn" or "krbservicename" => true,
                    _ => key.StartsWith("auth", StringComparison.Ordinal),
                };
                if (conflicts)
                    throw new ConnectionAuthenticationException(AuthenticationDiagnosticCodes.CredentialConflict);
            }

            settings["AuthMech"] = "11";
            settings["Auth_Flow"] = "0";
            settings["LogLevel"] = "0";
            settings["Auth_AccessToken"] = accessToken;
            connection.ConnectionString = settings.ConnectionString;
        }
    }
}
