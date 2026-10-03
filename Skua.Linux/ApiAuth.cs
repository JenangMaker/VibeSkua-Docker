using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Skua.Linux;

/// <summary>
/// SKUA_API_TOKEN: when set, the control APIs (each tab's, HostApi, and the
/// tab host's, TabHostApi) answer only requests that carry it, as
/// <c>Authorization: Bearer &lt;token&gt;</c> or <c>X-Api-Token</c>. Unset, they
/// answer anyone who reaches them, as before.
/// </summary>
public static class ApiAuth
{
    public static string? Token { get; } = SkuaRuntime.EnvRaw("SKUA_API_TOKEN");

    public static bool Allowed(HttpListenerRequest request)
    {
        if (Token is null)
            return true;
        string? given = request.Headers["X-Api-Token"];
        if (given is null && request.Headers["Authorization"] is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            given = auth["Bearer ".Length..].Trim();
        return given is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Token));
    }

    /// <summary>For this program's own calls to the tabs' APIs.</summary>
    public static void AddTo(HttpClient client)
    {
        if (Token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }
}
