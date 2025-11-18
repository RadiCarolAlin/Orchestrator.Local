// DeployHub.cs
using Microsoft.AspNetCore.SignalR;

public class DeployHub : Hub
{
    public async Task SubscribeToOperation(string operationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, operationId);
        Console.WriteLine($"📡 Client subscribed to operation: {operationId}");
    }

    public async Task UnsubscribeFromOperation(string operationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, operationId);
    }
}