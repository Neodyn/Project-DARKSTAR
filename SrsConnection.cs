using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Darkstar;

/// <summary>
/// Connects to a DCS-SRS server as an "External AWACS Mode" client.
/// - TCP: sync protocol (JSON, line-based) for registering and setting the frequencies.
/// - UDP: audio (Opus-encoded) sending/receiving on the same port number.
/// Supports monitoring multiple radios (frequency + modulation pairs) simultaneously.
/// </summary>
public sealed class SrsConnection : IAsyncDisposable
{
    public event Action<byte[], double, string, int>? OnAudioReceived; // (PCM16 mono 16-bit @ 48kHz, frequency, sender name, sender coalition)

    private readonly string _host;
    private readonly int _port;
    private readonly string _clientGuid;
    private readonly string _clientName;
    private readonly IReadOnlyList<RadioConfig> _radios;
    private readonly string? _eamPassword;

    // ClientGuid -> (Name, Coalition), populated from the TCP sync messages (SYNC client list,
    // RADIO_UPDATE/UPDATE from other clients). Lets us resolve the display name and coalition
    // (0 = Spectator, 1 = Red, 2 = Blue) of the sender for a received audio packet.
    private readonly Dictionary<string, (string Name, int Coalition)> _clients = new();

    private TcpClient? _tcp;
    private NetworkStream? _tcpStream;
    private UdpClient? _udp;
    private IPEndPoint? _udpRemoteEndPoint;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _firstConnectTcs;

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    public SrsConnection(string host, int port, string clientName, IReadOnlyList<RadioConfig> radios, string? externalAwacsPassword = null)
    {
        _host = host;
        _port = port;
        _clientName = clientName;
        _radios = radios.Count > 0 ? radios : new List<RadioConfig> { new() };
        _eamPassword = externalAwacsPassword;
        _clientGuid = ToShortGuid(Guid.NewGuid());
    }

    // SRS uses "ShortGuid" strings: a 16-byte GUID Base64Url-encoded without padding -> 22 characters,
    // e.g. "e61Ouxhow0yLI4lrhUqz6g". A plain hex substring (as used before) does not match this format
    // and was presumably rejected by the server as invalid.
    private static string ToShortGuid(Guid guid)
    {
        var base64 = Convert.ToBase64String(guid.ToByteArray());
        return base64.Replace("/", "_").Replace("+", "-")[..22];
    }

    /// <summary>
    /// Connects to the SRS server and starts a background supervisor that automatically and
    /// permanently reconnects on connection loss (server restarted, network error, ...) - every
    /// few seconds until it succeeds again. This method itself only waits for the FIRST
    /// successful connection before returning.
    /// </summary>
    public Task ConnectAsync(CancellationToken token = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _firstConnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(() => SupervisorLoopAsync(_cts.Token));

        return _firstConnectTcs.Task;
    }

    /// <summary>
    /// Runs in the background for the entire lifetime of the bot: connects, waits until the
    /// connection ends (TCP or UDP loop terminates), cleans up, and reconnects.
    /// </summary>
    private async Task SupervisorLoopAsync(CancellationToken outerToken)
    {
        var attempt = 0;
        var everConnected = false; // true once we've had at least one successful connection
        var downNotified = false;  // true while a "connection lost" Discord notification is outstanding

        while (!outerToken.IsCancellationRequested)
        {
            attempt++;
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);

            try
            {
                Logger.Log(attempt == 1
                    ? $"[SRS] Connecting to {_host}:{_port}..."
                    : $"[SRS] Connection attempt {attempt} to {_host}:{_port}...");

                var (tcpLoopTask, udpLoopTask) = await ConnectOnceAsync(connectionCts.Token);

                var freqList = string.Join(", ", _radios.Select(r => $"{r.FrequencyHz / 1_000_000:0.000} MHz ({r.Modulation})"));
                Logger.Log($"[SRS] Connected. Monitoring {_radios.Count} radio(s): {freqList}");

                if (everConnected && downNotified)
                {
                    // This is a genuine "back up after an outage" event, not just the normal
                    // first-time startup connect.
                    DiscordNotifier.Notify($"✅ Reconnected to SRS server `{_host}:{_port}`.");
                    downNotified = false;
                }
                everConnected = true;

                _firstConnectTcs?.TrySetResult(true);

                // Waits until one of the two core loops ends - that's our signal that the
                // connection was lost (server down, network error, TCP closed by server, ...).
                await Task.WhenAny(tcpLoopTask, udpLoopTask);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SRS] Connection attempt failed: {ex.Message}");
            }

            // This connection is over - cleanly stop all associated background loops (ping,
            // keepalive) and release the sockets before possibly reconnecting.
            connectionCts.Cancel();
            CleanupSockets();

            if (outerToken.IsCancellationRequested) break;

            if (everConnected && !downNotified)
            {
                // Notify exactly once per outage, not on every 5-second retry attempt while it
                // stays down.
                DiscordNotifier.Notify($"⚠️ Lost connection to SRS server `{_host}:{_port}`. Attempting to reconnect...");
                downNotified = true;
            }

            Logger.Log($"[SRS] No connection to the server. Retrying in {ReconnectDelay.TotalSeconds:0}s...");
            try { await Task.Delay(ReconnectDelay, outerToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void CleanupSockets()
    {
        try { _tcpStream?.Dispose(); } catch { /* best effort */ }
        try { _tcp?.Dispose(); } catch { /* best effort */ }
        try { _udp?.Dispose(); } catch { /* best effort */ }
        _tcpStream = null;
        _tcp = null;
        _udp = null;
        _udpRemoteEndPoint = null;
    }

    /// <summary>
    /// A single connection attempt: connect via TCP, create the UDP socket, register with the
    /// server, and start all background loops for this connection. Returns the TCP and UDP
    /// receive loop tasks so the supervisor can detect when the connection ends.
    /// </summary>
    private async Task<(Task TcpLoop, Task UdpLoop)> ConnectOnceAsync(CancellationToken token)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port, token);
        _tcpStream = _tcp.GetStream();

        _udp = new UdpClient(0);
        _udpRemoteEndPoint = new IPEndPoint((await Dns.GetHostAddressesAsync(_host, token))[0], _port);

        if (!string.IsNullOrEmpty(_eamPassword))
        {
            await SendTcpAsync(new SrsNetworkMessage
            {
                MsgType = SrsMsgType.EXTERNAL_AWACS_MODE_PASSWORD,
                ExternalAWACSModePassword = _eamPassword,
                Client = BuildClientState()
            });
        }

        // IMPORTANT: A client registers its own radio data via RADIO_UPDATE, not SYNC!
        // Per the protocol, SYNC returns Clients/ServerSettings (a state query), not the client's own registration.
        await SendTcpAsync(new SrsNetworkMessage
        {
            MsgType = SrsMsgType.RADIO_UPDATE,
            Client = BuildClientState()
        });

        var tcpLoopTask = TcpReadLoopAsync(token);
        var udpLoopTask = UdpReadLoopAsync(token);
        _ = Task.Run(() => PingLoopAsync(token));
        _ = Task.Run(() => UdpKeepAliveLoopAsync(token));

        return (tcpLoopTask, udpLoopTask);
    }

    /// <summary>
    /// SRS only learns the client's public IP:port for voice traffic once the client itself
    /// sends at least one UDP packet to the server (classic NAT hole-punching problem). Without
    /// this, the client might show up in the sync list but won't receive/send any audio - and
    /// depending on the server version, it may not even be treated as fully connected without
    /// this "ping". So send one right after connecting and then periodically afterwards.
    /// </summary>
    private async Task UdpKeepAliveLoopAsync(CancellationToken token)
    {
        if (_udp == null || _udpRemoteEndPoint == null) return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Empty "I'm here" signal for the NAT route, WITHOUT a frequency (freqCount=0).
                // This gives the server no frequency to relay the packet to - so no audible
                // PTT click for other listeners on the frequency.
                var pingPacket = SrsAudioPacket.Build(Array.Empty<byte>(), Array.Empty<double>(), _clientGuid, 0);
                await _udp.SendAsync(pingPacket, pingPacket.Length, _udpRemoteEndPoint);
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Log($"[SRS] UDP keepalive error: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure debugging aid: shows the raw bytes of an unparseable UDP packet as hex and searches
    /// specifically for known values (any of our configured frequencies as an 8-byte double,
    /// long ASCII runs = presumably GUIDs). Lets you read the actual byte layout off a real
    /// packet instead of guessing.
    /// </summary>
    private void DumpForensics(byte[] buffer)
    {
        var hex = string.Join(" ", buffer.Select(b => b.ToString("X2")));
        Logger.Debug($"[SRS UDP HEX] {hex}");

        var foundAny = false;
        foreach (var radio in _radios)
        {
            var freqBytes = BitConverter.GetBytes(radio.FrequencyHz);
            int freqIndex = IndexOf(buffer, freqBytes);
            if (freqIndex >= 0)
            {
                Logger.Debug($"[SRS UDP HEX] Frequency {radio.FrequencyHz} found at byte offset {freqIndex} " +
                                   $"(out of {buffer.Length} bytes total).");
                foundAny = true;
            }
        }
        if (!foundAny)
            Logger.Debug("[SRS UDP HEX] None of the configured frequencies were found as an exact byte pattern " +
                               "(possibly a different frequency, a different type, or a different byte order).");

        // Mark long printable ASCII runs - typically GUID strings
        int runStart = -1;
        for (int i = 0; i <= buffer.Length; i++)
        {
            bool printable = i < buffer.Length && buffer[i] >= 0x20 && buffer[i] < 0x7F;
            if (printable && runStart < 0) runStart = i;
            if (!printable && runStart >= 0)
            {
                int len = i - runStart;
                if (len >= 8)
                {
                    var text = Encoding.ASCII.GetString(buffer, runStart, len);
                    Logger.Debug($"[SRS UDP HEX] ASCII run at offset {runStart}, length {len}: \"{text}\"");
                }
                runStart = -1;
            }
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    /// Parses a received TCP sync line and maintains the ClientGuid->(Name, Coalition) mapping:
    /// - "Clients" (full state, e.g. in a SYNC response): adopt all contained clients.
    /// - "Client" with MsgType RADIO_UPDATE/UPDATE (a single client registering/updating itself): adopt.
    /// - "Client" with MsgType CLIENT_DISCONNECT: remove from the mapping.
    /// Parse errors are deliberately swallowed - this is just supplementary info for nicer
    /// replies, not a critical path.
    /// </summary>
    private void UpdateClientNames(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("Clients", out var clientsElement) && clientsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in clientsElement.EnumerateArray())
                    TryStoreClient(c);
            }

            if (root.TryGetProperty("Client", out var clientElement) && clientElement.ValueKind == JsonValueKind.Object)
            {
                bool isDisconnect = root.TryGetProperty("MsgType", out var msgTypeElement) &&
                                     msgTypeElement.ValueKind == JsonValueKind.Number &&
                                     msgTypeElement.GetInt32() == (int)SrsMsgType.CLIENT_DISCONNECT;

                if (isDisconnect)
                {
                    if (clientElement.TryGetProperty("ClientGuid", out var guidEl) && guidEl.ValueKind == JsonValueKind.String)
                        _clients.Remove(guidEl.GetString() ?? "");
                }
                else
                {
                    TryStoreClient(clientElement);
                }
            }
        }
        catch
        {
            // Not a valid/relevant JSON line (e.g. a plain-text server error message) - ignore.
        }
    }

    private void TryStoreClient(JsonElement clientElement)
    {
        if (clientElement.ValueKind != JsonValueKind.Object) return;
        if (!clientElement.TryGetProperty("ClientGuid", out var guidEl) || guidEl.ValueKind != JsonValueKind.String) return;
        if (!clientElement.TryGetProperty("Name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return;

        var guid = guidEl.GetString();
        var name = nameEl.GetString();
        if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(name)) return;

        var coalition = clientElement.TryGetProperty("Coalition", out var coalitionEl) && coalitionEl.ValueKind == JsonValueKind.Number
            ? coalitionEl.GetInt32()
            : 0;

        _clients[guid] = (name, coalition);
    }

    /// <summary>Resolves a ClientGuid to the last known display name, or returns the GUID itself if unknown.</summary>
    public string GetClientName(string clientGuid) =>
        _clients.TryGetValue(clientGuid, out var info) ? info.Name : clientGuid;

    /// <summary>Resolves a ClientGuid to the last known coalition (0 = Spectator, 1 = Red, 2 = Blue). Defaults to 0 (Spectator/unknown) if not known.</summary>
    public int GetClientCoalition(string clientGuid) =>
        _clients.TryGetValue(clientGuid, out var info) ? info.Coalition : 0;

    private SrsClient BuildClientState() => new()
    {
        ClientGuid = _clientGuid,
        Name = _clientName,
        Coalition = 2, // Blue - adjust based on server configuration/password
        AllowRecord = false,
        Seat = 0,
        RadioInfo = new SrsRadioInfo
        {
            unit = _clientName,
            unitId = 100001,
            ambient = new SrsAmbient { abType = "", vol = 0 },
            iff = new SrsIff(),
            radios = _radios.Select((radio, index) => new SrsRadio
            {
                Name = $"RADIO {index + 1}",
                freq = radio.FrequencyHz,
                modulation = string.Equals(radio.Modulation, "FM", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                secFreq = 1,
                encKey = 0,
                enc = false,
                retransmit = false
            }).ToList()
        },
        LatLngPosition = new SrsLatLng { alt = 0, lat = 0, lng = 0 },
        Gateway = false,
        DISEntityId = -1,
        GatewayClient = false
    };

    private async Task SendTcpAsync(SrsNetworkMessage msg)
    {
        if (_tcpStream == null) return;
        var json = JsonSerializer.Serialize(msg, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Logger.Debug($"[SRS -> Server] {json}");
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _tcpStream.WriteAsync(bytes);
    }

    private async Task TcpReadLoopAsync(CancellationToken token)
    {
        if (_tcpStream == null) return;
        using var reader = new StreamReader(_tcpStream, Encoding.UTF8, leaveOpen: true);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null)
                {
                    Logger.Log("[SRS] TCP connection was closed by the server.");
                    break;
                }

                // Log the raw data - this lets you immediately see if the server responds with
                // e.g. VERSION_MISMATCH or an error message instead of a real SYNC response.
                Logger.Debug($"[SRS <- Server] {line}");

                UpdateClientNames(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            Logger.Log($"[SRS] TCP read error: {ex.Message}");
        }
    }

    private async Task PingLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await SendTcpAsync(new SrsNetworkMessage { MsgType = SrsMsgType.PING });
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Whether two frequencies refer to the same radio, tolerating floating-point noise by
    /// rounding to the nearest whole Hz - matches the rounding BotService uses as its
    /// RadioSession dictionary key, so the two stay consistent.
    /// </summary>
    private static bool IsSameFrequency(double a, double b) => Math.Round(a) == Math.Round(b);

    private async Task UdpReadLoopAsync(CancellationToken token)
    {
        if (_udp == null) return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(token);

                var packet = SrsAudioPacket.TryParse(result.Buffer);
                if (packet == null)
                {
                    // Guarded: this runs per UDP packet, and building the string is most of what
                    // it costs. Measured at ~123 MB of garbage an hour per talking pilot.
                    if (Logger.IsDebugEnabled)
                        Logger.Debug($"[SRS UDP] Packet received ({result.Buffer.Length} bytes) - " +
                                     "parsing failed (format differs or it's not a voice packet).");
                    DumpForensics(result.Buffer);
                    continue;
                }

                if (Logger.IsDebugEnabled)
                    Logger.Debug($"[SRS UDP] Packet parsed: {result.Buffer.Length} bytes total, " +
                                 $"{packet.OpusAudio.Length} bytes Opus audio, " +
                                 $"frequencies=[{string.Join(",", packet.Frequencies)}], " +
                                 $"modulation=[{string.Join(",", packet.Modulations)}], " +
                                 $"packetNumber={packet.PacketNumber}, " +
                                 $"clientGuid={packet.OriginalClientGuid}");

                if (packet.OpusAudio.Length == 0)
                {
                    // Empty packet (e.g. our own keepalive ping) - nothing to decode.
                    continue;
                }

                var pcm = OpusCodec.Decode(packet.OriginalClientGuid, packet.OpusAudio);
                if (Logger.IsDebugEnabled)
                    Logger.Debug($"[SRS UDP] Opus decoded -> {pcm.Length} bytes PCM16.");

                if (pcm.Length > 0)
                {
                    var senderName = GetClientName(packet.OriginalClientGuid);
                    var senderCoalition = GetClientCoalition(packet.OriginalClientGuid);

                    // BUG FIX: a transmission's Frequencies array can list more than one entry
                    // (e.g. the radio frequency plus an active intercom channel, or several
                    // radios the sender currently has selected) and does NOT guarantee that the
                    // radio the pilot actually intended is first. Previously this always used
                    // Frequencies[0], which silently misrouted the audio to whichever
                    // RadioSession happened to be first/monitored-by-default whenever the real
                    // frequency wasn't first in the array - meaning a second (or third...)
                    // configured radio's own wake word was never actually checked, because its
                    // audio kept being evaluated against the first radio's hotword detector
                    // instead. Matching every frequency in the packet against our configured
                    // radios (rounded to the nearest Hz, same as the session lookup key in
                    // BotService) and firing once per actual match fixes that, and also lets one
                    // transmission correctly wake more than one monitored radio at once if it's
                    // genuinely sent on multiple monitored frequencies simultaneously.
                    var matchedAnyRadio = false;
                    foreach (var freq in packet.Frequencies)
                    {
                        if (_radios.Any(r => IsSameFrequency(r.FrequencyHz, freq)))
                        {
                            matchedAnyRadio = true;
                            OnAudioReceived?.Invoke(pcm, freq, senderName, senderCoalition);
                        }
                    }

                    if (!matchedAnyRadio)
                    {
                        // None of this transmission's frequencies match a monitored radio (e.g.
                        // a relay/intercom-only frequency, or a legacy single-radio setup where
                        // the sender's exact frequency doesn't quite match) - fall back to the
                        // first entry so audio still reaches a session rather than being silently
                        // dropped outright, matching the previous behavior for that edge case.
                        var fallbackFreq = packet.Frequencies.Length > 0 ? packet.Frequencies[0] : _radios[0].FrequencyHz;
                        if (Logger.IsDebugEnabled)
                            Logger.Debug($"[SRS UDP] None of frequencies=[{string.Join(",", packet.Frequencies)}] matched a configured radio - " +
                                         $"falling back to {fallbackFreq} Hz.");
                        OnAudioReceived?.Invoke(pcm, fallbackFreq, senderName, senderCoalition);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex)
        {
            Logger.Log($"[SRS UDP] Connection to the server was lost: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[SRS UDP] Unexpected error in the receive loop: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Sends recorded PCM16 audio data (48kHz, mono) as a reply on the given frequency.</summary>
    public async Task SendAudioAsync(byte[] pcm16Mono48k, double frequencyHz, CancellationToken token = default)
    {
        if (_udp == null || _udpRemoteEndPoint == null) return;

        const int frameSamples = 960; // 20ms at 48kHz
        var opusChunks = OpusCodec.EncodeInFrames(pcm16Mono48k, frameSamples);

        ulong packetId = 0;
        foreach (var opus in opusChunks)
        {
            var packet = SrsAudioPacket.Build(opus, new[] { frequencyHz }, _clientGuid, packetId++);
            await _udp.SendAsync(packet, packet.Length, _udpRemoteEndPoint);
            await Task.Delay(20, token); // Real-time pacing, like actual radio traffic
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_tcpStream != null)
                await SendTcpAsync(new SrsNetworkMessage { MsgType = SrsMsgType.CLIENT_DISCONNECT, Client = BuildClientState() });
        }
        catch { /* best effort */ }

        _cts?.Cancel(); // permanently stops the supervisor, no further reconnect attempts
        CleanupSockets();
    }
}
