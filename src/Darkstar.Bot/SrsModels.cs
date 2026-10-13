using System.Text.Json.Serialization;

namespace Darkstar;

// NOTE: This sync protocol is based on publicly known SRS client implementations
// (among others DCS-OverlordBot, gitlab.com/overlord-bot/srs-bot). It is NOT officially
// documented and may change between SRS server versions. Before production use, always
// verify against the actual server version (capturing the server log while connecting a
// real SRS client with Wireshark/netcat is the safest approach).

// Order confirmed via a real client capture: RADIO_UPDATE serializes as 2, which matches
// the doc table order SYNC=0, UPDATE=1, RADIO_UPDATE=2, ...
public enum SrsMsgType
{
    SYNC = 0,
    UPDATE = 1,
    RADIO_UPDATE = 2,
    PING = 3,
    SERVER_SETTINGS = 4,
    CLIENT_DISCONNECT = 5,
    VERSION_MISMATCH = 6,
    EXTERNAL_AWACS_MODE_PASSWORD = 7,   // value unconfirmed - adjust if needed
    EXTERNAL_AWACS_MODE_DISCONNECT = 8 // value unconfirmed - adjust if needed
}

public class SrsAmbient
{
    public string abType { get; set; } = "";
    public double vol { get; set; } = 0;
}

public class SrsIff
{
    public int control { get; set; } = 0;
    public int mic { get; set; } = -1;
    public int mode1 { get; set; } = -1;
    public int mode2 { get; set; } = -1;
    public int mode3 { get; set; } = -1;
    public bool mode4 { get; set; } = false;
    public int status { get; set; } = 0;
}

// Modulation values per server export: 0 = AM, 1 = FM, 2 = INTERCOM, 3 = DISABLED
public class SrsRadio
{
    public int IntercomUnitId { get; set; } = 0;
    public string Model { get; set; } = "";
    public string Name { get; set; } = "";
    public bool enc { get; set; } = false;
    public int encKey { get; set; } = 0;
    public double freq { get; set; } = 1;
    public int modulation { get; set; } = 3; // 3 = DISABLED (default for unused slots)
    public bool retransmit { get; set; } = false;
    public double secFreq { get; set; } = 1;
}

public class SrsRadioInfo
{
    public SrsAmbient ambient { get; set; } = new();
    public SrsIff iff { get; set; } = new();
    public List<SrsRadio> radios { get; set; } = new();
    public string unit { get; set; } = "";
    public int unitId { get; set; } = 0;
}

public class SrsLatLng
{
    public double alt { get; set; } = 0;
    public double lat { get; set; } = 0;
    public double lng { get; set; } = 0;
}

public class SrsClient
{
    public string ClientGuid { get; set; } = "";
    public string Name { get; set; } = "DARKSTAR";
    // 0 = Spectator, 1 = Red, 2 = Blue
    public int Coalition { get; set; } = 2;
    public bool AllowRecord { get; set; } = false;
    public int Seat { get; set; } = 0;
    public SrsRadioInfo RadioInfo { get; set; } = new();
    public SrsLatLng LatLngPosition { get; set; } = new();
    public bool Gateway { get; set; } = false;
    public int DISEntityId { get; set; } = -1;
    public bool GatewayClient { get; set; } = false;
}

public class SrsNetworkMessage
{
    public SrsClient? Client { get; set; }
    public List<SrsClient>? Clients { get; set; }
    // Declared as an enum (not a string!): System.Text.Json serializes enums as a number by
    // default - which is exactly what the server expects per the real capture ("MsgType":2).
    public SrsMsgType MsgType { get; set; } = SrsMsgType.UPDATE;
    public string? Version { get; set; } = "2.4.0.0"; // matched to the ServerVersion from your capture
    public string? ExternalAWACSModePassword { get; set; }
}
