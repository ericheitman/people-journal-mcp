namespace PeopleJournal.Mcp.Identity;

public enum AuthMode
{
    /// <summary>Local demos: no sign-in, everyone is <see cref="AuthOptions.DevUserId"/>.</summary>
    None,

    /// <summary>Real users: Entra ID access tokens, one journal per signed-in person.</summary>
    EntraId,
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.None;

    /// <summary>Journal folder used for every request when Mode is None.</summary>
    public string DevUserId { get; set; } = "local-dev";

    public string TenantId { get; set; } = "";

    /// <summary>Application (client) ID of the API app registration.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Scope exposed by the API app registration.</summary>
    public string Scope { get; set; } = "journal.readwrite";
}
