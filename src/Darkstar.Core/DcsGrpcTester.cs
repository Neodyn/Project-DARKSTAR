using Grpc.Core;
using Grpc.Net.Client;
using RurouniJones.Dcs.Grpc.V0.Mission;

namespace Darkstar;

public sealed class DcsGrpcTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? MissionDateTime { get; set; }
}

/// <summary>
/// A minimal DCS-gRPC connectivity check, meant purely to answer "is the server reachable AND
/// actually receiving live data from a running mission?" - not part of the reply pipeline yet
/// (that comes later, once real features like bullseye/bogey-dope are built on top of this).
///
/// Uses MissionService.GetScenarioCurrentTime as the test call: it's a simple, fast, unary RPC
/// that only succeeds if a mission is actually running and DCS-gRPC is receiving data from it -
/// a plain "can I open a socket" check wouldn't tell you that.
///
/// NOTE: this depends on the "RurouniJones.Dcs.Grpc" NuGet package (the official DCS-gRPC C#
/// bindings). Confirmed via IntelliSense that GetScenarioCurrentTimeResponse exposes the
/// mission's in-game date/time as "Datetime" (not "Time" as originally guessed) - read via
/// ToString() here so this keeps working regardless of whether that field turns out to be a
/// plain string or a structured message type.
/// </summary>
public static class DcsGrpcTester
{
    public static async Task<DcsGrpcTestResult> TestConnectionAsync(string address, string? apiKey, int timeoutSeconds = 5)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new DcsGrpcTestResult { Success = false, Message = "No address configured." };

        try
        {
            using var channel = GrpcChannel.ForAddress(address);
            var client = new MissionService.MissionServiceClient(channel);

            var headers = new Metadata();
            if (!string.IsNullOrWhiteSpace(apiKey))
                headers.Add("X-API-Key", apiKey);

            var response = await client.GetScenarioCurrentTimeAsync(
                new GetScenarioCurrentTimeRequest(),
                headers: headers,
                deadline: DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds)));

            var missionDateTime = response.Datetime?.ToString() ?? "(unknown)";

            return new DcsGrpcTestResult
            {
                Success = true,
                Message = $"Connected - a mission is running and DCS-gRPC is receiving live data (mission date/time: {missionDateTime}).",
                MissionDateTime = missionDateTime
            };
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unavailable)
        {
            return new DcsGrpcTestResult
            {
                Success = false,
                Message = $"Could not reach {address} - is DCS-gRPC running on the server and is the port reachable/firewalled correctly? ({rpcEx.Status.Detail})"
            };
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            return new DcsGrpcTestResult
            {
                Success = false,
                Message = $"The server rejected the request ({rpcEx.StatusCode}) - check DcsGrpcApiKey against the server's GRPC.authorization_required setting."
            };
        }
        catch (RpcException rpcEx)
        {
            return new DcsGrpcTestResult
            {
                Success = false,
                Message = $"Server reachable, but the call failed ({rpcEx.StatusCode}: {rpcEx.Status.Detail}). This usually means DCS-gRPC is running but no mission is currently active."
            };
        }
        catch (Exception ex)
        {
            return new DcsGrpcTestResult { Success = false, Message = $"Unexpected error: {ex.Message}" };
        }
    }
}
