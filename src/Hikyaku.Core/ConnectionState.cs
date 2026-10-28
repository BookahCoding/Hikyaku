using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>Socket supervisor phases (BookahHermes GatewayConnection.ConnectionState).</summary>
public abstract record ConnectionPhase;

public sealed record ConnectionIdle : ConnectionPhase;
public sealed record ConnectionConnecting(int Attempt) : ConnectionPhase;
public sealed record ConnectionConnected(JsonRpcClient Client, JsonObject Ready) : ConnectionPhase;
public sealed record ConnectionReconnecting(int Attempt, DateTimeOffset RetryAt, string Reason) : ConnectionPhase;
public sealed record ConnectionSessionExpired : ConnectionPhase;
public sealed record ConnectionFailed(string Reason) : ConnectionPhase;
