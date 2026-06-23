using Hangfire.Dashboard;

namespace Api;

public sealed class LocalOnlyHangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var remoteIp = httpContext.Connection.RemoteIpAddress;
        return remoteIp is null || System.Net.IPAddress.IsLoopback(remoteIp);
    }
}
