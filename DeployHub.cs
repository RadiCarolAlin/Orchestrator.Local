using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

public class DeployHub : Hub
{
    // Track which operation maps to which buildId
    private static readonly ConcurrentDictionary<string, string> OperationToBuildId = new();

    public async Task SubscribeToOperation(string operationId)
    {
        Console.WriteLine(
            $"📡 Client {Context.ConnectionId} subscribing to operation: {operationId}"
        );

        // Subscribe to the operation group
        await Groups.AddToGroupAsync(Context.ConnectionId, operationId);

        // IMPORTANT: Also try to map to buildId if we know it
        // The buildId is extracted from the operation name
        // Format: "projects/{project}/locations/{region}/operations/{buildId}"
        var parts = operationId.Split('/');
        if (parts.Length > 0)
        {
            var encodedBuildId = parts[^1]; // Get last part (base64 encoded buildId)
            Console.WriteLine($"📡 Encoded buildId: {encodedBuildId}");

            // Subscribe to the encoded version
            await Groups.AddToGroupAsync(Context.ConnectionId, encodedBuildId);

            // DECODE the base64 buildId to get the actual UUID
            try
            {
                var decodedBytes = Convert.FromBase64String(encodedBuildId);
                var decodedBuildId = System.Text.Encoding.UTF8.GetString(decodedBytes);
                Console.WriteLine($"📡 Decoded buildId: {decodedBuildId}");

                // Also subscribe to the decoded version (this is what /progress uses!)
                await Groups.AddToGroupAsync(Context.ConnectionId, decodedBuildId);

                // Track this mapping
                OperationToBuildId[operationId] = decodedBuildId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to decode buildId: {ex.Message}");
                // Fallback: just use the encoded version
                OperationToBuildId[operationId] = encodedBuildId;
            }
        }
    }

    public async Task UnsubscribeFromOperation(string operationId)
    {
        Console.WriteLine(
            $"📡 Client {Context.ConnectionId} unsubscribing from operation: {operationId}"
        );

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, operationId);

        // Also unsubscribe from buildId group
        if (OperationToBuildId.TryGetValue(operationId, out var buildId))
        {
            Console.WriteLine($"📡 Also unsubscribing from buildId: {buildId}");
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, buildId);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Console.WriteLine($"📡 Client {Context.ConnectionId} disconnected");
        await base.OnDisconnectedAsync(exception);
    }
}
