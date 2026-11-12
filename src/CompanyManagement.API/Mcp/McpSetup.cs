namespace CompanyManagement.API.Mcp;

public static class McpSetup
{
    /// <summary>The MCP endpoint Claude connects to: https://&lt;taskveta-host&gt;/mcp</summary>
    public const string Path = "/mcp";
    /// <summary>Authorization + rate-limit policy: a valid personal key, 120 calls per minute per user.</summary>
    public const string Policy = "claude";

    /// <summary>Rate-limit bucket: a hash of the Authorization header (one bucket per key), or the IP when there is none.</summary>
    public static string PartitionKey(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        return auth.Length > 0
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(auth)))[..24]
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
