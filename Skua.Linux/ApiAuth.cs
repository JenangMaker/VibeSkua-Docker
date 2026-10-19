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
        // Windows: the APIs listen on 127.0.0.1 with no token, and a web page in
        // the user's browser can still send them requests (a form post or a
        // no-cors fetch needs no permission) - and /script/start compiles and
        // runs the C# it is given. Browsers mark such requests with Origin or
        // Sec-Fetch-* headers; this program's own calls (HttpClient) carry
        // neither. Without a token, a Host that is not loopback is a page that
        // rebound its own name to 127.0.0.1 (with one, the API may be opened to
        // the LAN on purpose: SKUA_API_PREFIX). (Docker: the manager's and game's pages call these
        // APIs from a browser on purpose; they are reached over the LAN, with
        // SKUA_API_TOKEN where that matters.)
        // The same for the Linux desktop client.
        if (SkuaRuntime.DesktopClient && FromBrowser(request, checkHost: Token is null))
            return false;
        if (Token is null)
            return true;
        string? given = request.Headers["X-Api-Token"];
        if (given is null && request.Headers["Authorization"] is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            given = auth["Bearer ".Length..].Trim();
        return given is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Token));
    }

    private static bool FromBrowser(HttpListenerRequest request, bool checkHost)
    {
        if (request.Headers["Origin"] is not null
            || request.Headers["Sec-Fetch-Site"] is not null
            || request.Headers["Sec-Fetch-Mode"] is not null)
            return true;
        string host = request.Url?.Host ?? "";
        return checkHost && !(host is "127.0.0.1" or "localhost" or "[::1]" or "::1");
    }

    /// <summary>For this program's own calls to the tabs' APIs.</summary>
    public static void AddTo(HttpClient client)
    {
        if (Token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }
}
