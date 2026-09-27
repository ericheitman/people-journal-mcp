using System.Security.Claims;
using System.Text.RegularExpressions;

namespace PeopleJournal.Mcp.Identity;

public interface IUserContextResolver
{
    /// <summary>
    /// Returns the ID whose journal this request may touch. Identity always comes from the
    /// server side (config or a validated token), never from a tool argument, so one user
    /// can't ask for another user's journal.
    /// </summary>
    string ResolveUserId(ClaimsPrincipal? user);
}

public sealed partial class UserContextResolver(AuthOptions auth) : IUserContextResolver
{
    public string ResolveUserId(ClaimsPrincipal? user)
    {
        var id = auth.Mode switch
        {
            AuthMode.None => auth.DevUserId,
            // "oid" is the user's stable object ID in the tenant; unlike email, it never changes.
            AuthMode.EntraId => user?.FindFirstValue("oid")
                ?? throw new UnauthorizedAccessException("Access token has no 'oid' claim."),
            _ => throw new InvalidOperationException($"Unknown auth mode {auth.Mode}."),
        };

        // The ID becomes a folder name, so allow only safe characters.
        if (!SafeId().IsMatch(id))
        {
            throw new UnauthorizedAccessException("User ID contains unsupported characters.");
        }

        return id;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();
}
