using Microsoft.AspNetCore.SignalR;

namespace CodeIntel.Server.Hubs;

public class AnalysisHub : Hub
{
    /// <summary>
    /// Client subscribes to events for a specific analysis run.
    /// </summary>
    public Task JoinAnalysis(string analysisId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, analysisId);

    public Task LeaveAnalysis(string analysisId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, analysisId);
}
