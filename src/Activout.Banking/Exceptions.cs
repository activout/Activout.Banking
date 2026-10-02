using System.Net;

namespace Activout.Banking;

/// <summary>Missing or invalid configuration, keys or local files.</summary>
public class BankingConfigurationException(string message) : Exception(message);

/// <summary>Unknown or ambiguous connection/account selection or invalid arguments.</summary>
public class BankingUsageException(string message) : Exception(message);

/// <summary>Human bank authorisation is required, was refused or could not be verified.</summary>
public class AuthorisationException(string message) : Exception(message);

/// <summary>Another local process holds the sync lock.</summary>
public class SyncLockedException(string message) : Exception(message);

/// <summary>Error response from the banking API.</summary>
public class BankingApiException(HttpStatusCode statusCode, string? code, string message)
    : Exception(code == null ? $"{(int)statusCode} {message}" : $"{(int)statusCode} {code}: {message}")
{
    private static readonly HashSet<string> SessionCodes =
        ["EXPIRED_SESSION", "REVOKED_SESSION", "CLOSED_SESSION", "SESSION_DOES_NOT_EXIST", "ASPSP_PSU_ACTION_REQUIRED"];

    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;

    /// <summary>The bank session is no longer usable; the owner must authorise again.</summary>
    public bool RequiresReauthorisation => Code != null && SessionCodes.Contains(Code);

    /// <summary>The application itself was not accepted (JWT, key, application ID or status).</summary>
    public bool IsApplicationAuthenticationFailure =>
        StatusCode == HttpStatusCode.Unauthorized ||
        Code is "UNAUTHORIZED_ACCESS" or "AUTHORIZATION_NOT_PROVIDED" or "NO_ACCOUNTS_ADDED";
}
