using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using RurouniJones.Dcs.Grpc.V0.Atmosphere;
using RurouniJones.Dcs.Grpc.V0.Coalition;
using RurouniJones.Dcs.Grpc.V0.Common;
using RurouniJones.Dcs.Grpc.V0.Controller;
using RurouniJones.Dcs.Grpc.V0.Custom;
using RurouniJones.Dcs.Grpc.V0.Group;
using RurouniJones.Dcs.Grpc.V0.Hook;
using RurouniJones.Dcs.Grpc.V0.Mission;
using RurouniJones.Dcs.Grpc.V0.Net;
using RurouniJones.Dcs.Grpc.V0.Timer;
using RurouniJones.Dcs.Grpc.V0.Trigger;
using RurouniJones.Dcs.Grpc.V0.Unit;
using RurouniJones.Dcs.Grpc.V0.World;

// "Group" and "Unit" are both a namespace segment and a common word - alias the two request
// types that would otherwise be ambiguous with RurouniJones.Dcs.Grpc.V0.Unit.GetRequest etc.
using UnitGetRequest = RurouniJones.Dcs.Grpc.V0.Unit.GetRequest;

namespace Darkstar;

/// <summary>Which inputs a query needs, so the GUI knows which fields to show.</summary>
public enum DcsGrpcParam
{
    /// <summary>Coalition filter, "All" allowed.</summary>
    Coalition,
    /// <summary>A single specific coalition (Red/Blue/Neutral) - "All" is rejected by the server.</summary>
    SpecificCoalition,
    /// <summary>Group category filter (Airplane, Helicopter, Ground, Ship, Train, or all).</summary>
    GroupCategory,
    /// <summary>A unit name as placed in the mission editor (not the player name).</summary>
    UnitName,
    /// <summary>A group name as placed in the mission editor.</summary>
    GroupName,
    /// <summary>Latitude/longitude/altitude on the map.</summary>
    Position,
    /// <summary>A trigger user flag name/number.</summary>
    FlagName,
    /// <summary>How many seconds to listen on a streaming call.</summary>
    StreamDuration,
    /// <summary>StreamUnits poll rate in seconds.</summary>
    PollRate
}

/// <summary>Static description of one available query (shown in the GUI's query picker).</summary>
public sealed class DcsGrpcQueryInfo
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>The DCS-gRPC service/method actually called, e.g. "CoalitionService.GetGroups".</summary>
    public required string Rpc { get; init; }
    public DcsGrpcParam[] Parameters { get; init; } = Array.Empty<DcsGrpcParam>();
    /// <summary>Included in the one-click "mission snapshot" (only parameterless / coalition=All queries).</summary>
    public bool InSnapshot { get; init; }
    public bool Has(DcsGrpcParam p) => Parameters.Contains(p);
}

/// <summary>User-supplied inputs for a query. Only the fields listed in the query's Parameters are used.</summary>
public sealed class DcsGrpcQueryInput
{
    /// <summary>"All", "Red", "Blue" or "Neutral".</summary>
    public string Coalition { get; set; } = "All";
    /// <summary>"All", "Airplane", "Helicopter", "Ground", "Ship" or "Train".</summary>
    public string GroupCategory { get; set; } = "All";
    public string UnitName { get; set; } = "";
    public string GroupName { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Meters above mean sea level.</summary>
    public double AltitudeMeters { get; set; }
    public string FlagName { get; set; } = "";
    public int StreamDurationSeconds { get; set; } = 10;
    public int PollRateSeconds { get; set; } = 2;
}

public sealed class DcsGrpcQueryResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Pretty-printed JSON of the raw response (all fields, including default values).</summary>
    public string Json { get; set; } = "";
    public long ElapsedMs { get; set; }
    /// <summary>
    /// Every "name" value found anywhere in the response (units, groups, airbases, players...) -
    /// the GUI offers these as suggestions for the unit/group name fields, so you can drill down
    /// from "list all groups" to "show me that group's units" without typing names by hand.
    /// </summary>
    public List<string> DiscoveredNames { get; set; } = new();
}

/// <summary>
/// Read-only exploration of what a running mission exposes via DCS-gRPC: runs a single chosen
/// query (or a combined "snapshot" of all the parameterless ones) and returns the raw response as
/// JSON, so you can see exactly which fields/values are available before building real bot
/// features (bullseye, bogey dope, picture, weather...) on top of them.
///
/// Deliberately ONLY calls Get*/Is*/Stream* RPCs. Nothing here changes the mission: no Eval, no
/// spawning, no messages/marks/smoke, no kicking/banning, no pause/stop/load - safe to point at a
/// live server with players on it.
///
/// Built against the "RurouniJones.Dcs.Grpc" 0.7.1 bindings (proto files of DCS-gRPC 0.7.1).
/// A server running a newer DCS-gRPC version still answers these calls; methods that were added
/// in later versions simply aren't listed here until the NuGet package is updated.
/// </summary>
public static class DcsGrpcExplorer
{
    /// <summary>Safety cap for streaming calls so a busy server can't flood the GUI.</summary>
    public const int MaxStreamMessages = 500;
    public const int MaxStreamDurationSeconds = 120;

    private static readonly JsonFormatter Formatter =
        new(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private delegate Task<JsonNode?> QueryRunner(GrpcChannel channel, CallContext ctx, DcsGrpcQueryInput input);

    private sealed record CallContext(Metadata Headers, DateTime Deadline, CancellationToken Token);

    private sealed record QueryDef(DcsGrpcQueryInfo Info, QueryRunner Run);

    private static readonly DcsGrpcParam[] None = Array.Empty<DcsGrpcParam>();

    private static readonly List<QueryDef> Defs = new()
    {
        // ---------------- Mission ----------------
        Def("mission.name", "Mission", "Mission name", "HookService.GetMissionName",
            "Name of the currently loaded mission. (Hook calls need DCS-gRPC's hook part running on the server.)", None, true,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).GetMissionNameAsync(new GetMissionNameRequest(), c.Headers, c.Deadline, c.Token))),
        Def("mission.filename", "Mission", "Mission file", "HookService.GetMissionFilename",
            "Full path of the loaded .miz file on the server.", None, true,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).GetMissionFilenameAsync(new GetMissionFilenameRequest(), c.Headers, c.Deadline, c.Token))),
        Def("mission.description", "Mission", "Mission description", "HookService.GetMissionDescription",
            "The mission briefing/description text.", None, true,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).GetMissionDescriptionAsync(new GetMissionDescriptionRequest(), c.Headers, c.Deadline, c.Token))),
        Def("mission.startTime", "Mission", "Scenario start time", "MissionService.GetScenarioStartTime",
            "In-game date/time at which the mission started (ISO 8601).", None, true,
            (ch, c, _) => Unary(new MissionService.MissionServiceClient(ch).GetScenarioStartTimeAsync(new GetScenarioStartTimeRequest(), c.Headers, c.Deadline, c.Token))),
        Def("mission.currentTime", "Mission", "Scenario current time", "MissionService.GetScenarioCurrentTime",
            "Current in-game date/time (ISO 8601).", None, true,
            (ch, c, _) => Unary(new MissionService.MissionServiceClient(ch).GetScenarioCurrentTimeAsync(new GetScenarioCurrentTimeRequest(), c.Headers, c.Deadline, c.Token))),
        Def("mission.sessionId", "Mission", "Session ID", "MissionService.GetSessionId",
            "Unique ID of the current mission session (changes on every mission (re)start).", None, true,
            (ch, c, _) => Unary(new MissionService.MissionServiceClient(ch).GetSessionIdAsync(new GetSessionIdRequest(), c.Headers, c.Deadline, c.Token))),
        Def("mission.paused", "Mission", "Paused?", "HookService.GetPaused",
            "Whether the mission is currently paused.", None, true,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).GetPausedAsync(new GetPausedRequest(), c.Headers, c.Deadline, c.Token))),
        Def("mission.userFlag", "Mission", "Trigger user flag", "TriggerService.GetUserFlag",
            "Value of a mission trigger flag (name or number as used in the mission editor).", new[] { DcsGrpcParam.FlagName }, false,
            (ch, c, i) => Unary(new TriggerService.TriggerServiceClient(ch).GetUserFlagAsync(new GetUserFlagRequest { Flag = Require(i.FlagName, "flag name") }, c.Headers, c.Deadline, c.Token))),

        // ---------------- Time ----------------
        Def("time.model", "Time", "Model time", "TimerService.GetTime",
            "Seconds since the mission started (simulation time, stops while paused).", None, true,
            (ch, c, _) => Unary(new TimerService.TimerServiceClient(ch).GetTimeAsync(new GetTimeRequest(), c.Headers, c.Deadline, c.Token))),
        Def("time.absolute", "Time", "Absolute time", "TimerService.GetAbsoluteTime",
            "In-game time of day in seconds since midnight, plus date.", None, true,
            (ch, c, _) => Unary(new TimerService.TimerServiceClient(ch).GetAbsoluteTimeAsync(new GetAbsoluteTimeRequest(), c.Headers, c.Deadline, c.Token))),
        Def("time.zero", "Time", "Time zero", "TimerService.GetTimeZero",
            "In-game time of day at mission start.", None, true,
            (ch, c, _) => Unary(new TimerService.TimerServiceClient(ch).GetTimeZeroAsync(new GetTimeZeroRequest(), c.Headers, c.Deadline, c.Token))),
        Def("time.real", "Time", "Real time", "HookService.GetRealTime",
            "Real (wall-clock) seconds the server has been running the mission.", None, true,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).GetRealTimeAsync(new GetRealTimeRequest(), c.Headers, c.Deadline, c.Token))),

        // ---------------- World ----------------
        Def("world.theatre", "World", "Theatre / map", "WorldService.GetTheatre",
            "Name of the map (Caucasus, Syria, PersianGulf, ...).", None, true,
            (ch, c, _) => Unary(new WorldService.WorldServiceClient(ch).GetTheatreAsync(new GetTheatreRequest(), c.Headers, c.Deadline, c.Token))),
        Def("world.airbases", "World", "Airbases / FARPs / carriers", "WorldService.GetAirbases",
            "All airbases incl. FARPs and ships with a deck, with position and owning coalition.", new[] { DcsGrpcParam.Coalition }, true,
            (ch, c, i) => Unary(new WorldService.WorldServiceClient(ch).GetAirbasesAsync(new GetAirbasesRequest { Coalition = ParseCoalition(i.Coalition) }, c.Headers, c.Deadline, c.Token))),
        Def("world.markPanels", "World", "F10 map marks", "WorldService.GetMarkPanels",
            "All marks players have placed on the F10 map (text, position, author).", None, true,
            (ch, c, _) => Unary(new WorldService.WorldServiceClient(ch).GetMarkPanelsAsync(new GetMarkPanelsRequest(), c.Headers, c.Deadline, c.Token))),

        // ---------------- Coalition ----------------
        Def("coalition.bullseye", "Coalition", "Bullseye", "CoalitionService.GetBullseye",
            "Bullseye position of one coalition - the basis for BRAA/bullseye calls.", new[] { DcsGrpcParam.SpecificCoalition }, false,
            (ch, c, i) => Unary(new CoalitionService.CoalitionServiceClient(ch).GetBullseyeAsync(new GetBullseyeRequest { Coalition = ParseSpecificCoalition(i.Coalition) }, c.Headers, c.Deadline, c.Token))),
        Def("coalition.groups", "Coalition", "Groups", "CoalitionService.GetGroups",
            "All groups, optionally filtered by coalition and category.", new[] { DcsGrpcParam.Coalition, DcsGrpcParam.GroupCategory }, true,
            (ch, c, i) => Unary(new CoalitionService.CoalitionServiceClient(ch).GetGroupsAsync(new GetGroupsRequest { Coalition = ParseCoalition(i.Coalition), Category = ParseCategory(i.GroupCategory) }, c.Headers, c.Deadline, c.Token))),
        Def("coalition.playerUnits", "Coalition", "Player-occupied units", "CoalitionService.GetPlayerUnits",
            "All units currently occupied by a human player, with position, heading, speed and player name.", new[] { DcsGrpcParam.Coalition }, true,
            (ch, c, i) => Unary(new CoalitionService.CoalitionServiceClient(ch).GetPlayerUnitsAsync(new GetPlayerUnitsRequest { Coalition = ParseCoalition(i.Coalition) }, c.Headers, c.Deadline, c.Token))),
        Def("coalition.statics", "Coalition", "Static objects", "CoalitionService.GetStaticObjects",
            "All static objects (buildings, parked aircraft, cargo...) placed in the mission.", new[] { DcsGrpcParam.Coalition }, true,
            (ch, c, i) => Unary(new CoalitionService.CoalitionServiceClient(ch).GetStaticObjectsAsync(new GetStaticObjectsRequest { Coalition = ParseCoalition(i.Coalition) }, c.Headers, c.Deadline, c.Token))),

        // ---------------- Players / server ----------------
        Def("net.players", "Players & server", "Connected players", "NetService.GetPlayers",
            "Everyone connected to the server, with slot, side and ping.", None, true,
            (ch, c, _) => Unary(new NetService.NetServiceClient(ch).GetPlayersAsync(new GetPlayersRequest(), c.Headers, c.Deadline, c.Token))),
        Def("server.multiplayer", "Players & server", "Multiplayer?", "HookService.IsMultiplayer",
            "Whether DCS is running a multiplayer session.", None, true,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).IsMultiplayerAsync(new IsMultiplayerRequest(), c.Headers, c.Deadline, c.Token))),
        Def("server.isServer", "Players & server", "Is server?", "HookService.IsServer",
            "Whether this DCS instance is the server (as opposed to a client).", None, true,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).IsServerAsync(new IsServerRequest(), c.Headers, c.Deadline, c.Token))),
        Def("server.bannedPlayers", "Players & server", "Banned players", "HookService.GetBannedPlayers",
            "Current server ban list.", None, false,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).GetBannedPlayersAsync(new GetBannedPlayersRequest(), c.Headers, c.Deadline, c.Token))),
        Def("server.ballistics", "Players & server", "Ballistics object count", "HookService.GetBallisticsCount",
            "Number of shells/bullets currently in flight - a rough server-load indicator.", None, true,
            (ch, c, _) => Unary(new HookService.HookServiceClient(ch).GetBallisticsCountAsync(new GetBallisticsCountRequest(), c.Headers, c.Deadline, c.Token))),

        // ---------------- Group / unit ----------------
        Def("group.units", "Group & unit", "Units of a group", "GroupService.GetUnits",
            "All units of one group (use 'Groups' first to find group names).", new[] { DcsGrpcParam.GroupName }, false,
            (ch, c, i) => Unary(new GroupService.GroupServiceClient(ch).GetUnitsAsync(new GetUnitsRequest { GroupName = Require(i.GroupName, "group name") }, c.Headers, c.Deadline, c.Token))),
        Def("unit.get", "Group & unit", "Unit details", "UnitService.Get",
            "Everything DCS-gRPC knows about one unit: type, coalition, position, orientation, velocity, player name...", new[] { DcsGrpcParam.UnitName }, false,
            (ch, c, i) => Unary(new UnitService.UnitServiceClient(ch).GetAsync(new UnitGetRequest { Name = Require(i.UnitName, "unit name") }, c.Headers, c.Deadline, c.Token))),
        Def("unit.position", "Group & unit", "Unit position", "UnitService.GetPosition",
            "Lat/lon/alt of one unit.", new[] { DcsGrpcParam.UnitName }, false,
            (ch, c, i) => Unary(new UnitService.UnitServiceClient(ch).GetPositionAsync(new GetPositionRequest { Name = Require(i.UnitName, "unit name") }, c.Headers, c.Deadline, c.Token))),
        Def("unit.transform", "Group & unit", "Unit transform", "UnitService.GetTransform",
            "Position plus orientation (heading/pitch/roll) and velocity of one unit.", new[] { DcsGrpcParam.UnitName }, false,
            (ch, c, i) => Unary(new UnitService.UnitServiceClient(ch).GetTransformAsync(new GetTransformRequest { Name = Require(i.UnitName, "unit name") }, c.Headers, c.Deadline, c.Token))),
        Def("unit.descriptor", "Group & unit", "Unit descriptor", "UnitService.GetDescriptor",
            "Static type info (attributes like 'Fighters', 'SAM', 'Tankers'...).", new[] { DcsGrpcParam.UnitName }, false,
            (ch, c, i) => Unary(new UnitService.UnitServiceClient(ch).GetDescriptorAsync(new GetDescriptorRequest { Name = Require(i.UnitName, "unit name") }, c.Headers, c.Deadline, c.Token))),
        Def("unit.playerName", "Group & unit", "Player in unit", "UnitService.GetPlayerName",
            "Name of the human player occupying the unit (empty for AI).", new[] { DcsGrpcParam.UnitName }, false,
            (ch, c, i) => Unary(new UnitService.UnitServiceClient(ch).GetPlayerNameAsync(new GetPlayerNameRequest { Name = Require(i.UnitName, "unit name") }, c.Headers, c.Deadline, c.Token))),
        Def("unit.radar", "Group & unit", "Radar status", "UnitService.GetRadar",
            "Whether the unit's radar is on and what it is currently tracking.", new[] { DcsGrpcParam.UnitName }, false,
            (ch, c, i) => Unary(new UnitService.UnitServiceClient(ch).GetRadarAsync(new GetRadarRequest { Name = Require(i.UnitName, "unit name") }, c.Headers, c.Deadline, c.Token))),
        Def("unit.detectedTargets", "Group & unit", "Detected targets", "ControllerService.GetDetectedTargets",
            "What a unit's sensors (radar, visual, RWR, datalink...) currently detect - what an AWACS 'sees'.", new[] { DcsGrpcParam.UnitName }, false,
            (ch, c, i) => Unary(new ControllerService.ControllerServiceClient(ch).GetDetectedTargetsAsync(new GetDetectedTargetsRequest { UnitName = Require(i.UnitName, "unit name"), IncludeObject = true }, c.Headers, c.Deadline, c.Token))),

        // ---------------- Environment ----------------
        Def("env.wind", "Environment", "Wind", "AtmosphereService.GetWind",
            "Wind heading/strength at a position and altitude.", new[] { DcsGrpcParam.Position }, false,
            (ch, c, i) => Unary(new AtmosphereService.AtmosphereServiceClient(ch).GetWindAsync(new GetWindRequest { Position = ToInputPosition(i) }, c.Headers, c.Deadline, c.Token))),
        Def("env.windTurbulence", "Environment", "Wind incl. turbulence", "AtmosphereService.GetWindWithTurbulence",
            "Wind at a position including turbulence.", new[] { DcsGrpcParam.Position }, false,
            (ch, c, i) => Unary(new AtmosphereService.AtmosphereServiceClient(ch).GetWindWithTurbulenceAsync(new GetWindWithTurbulenceRequest { Position = ToInputPosition(i) }, c.Headers, c.Deadline, c.Token))),
        Def("env.tempPressure", "Environment", "Temperature & pressure", "AtmosphereService.GetTemperatureAndPressure",
            "Temperature and pressure at a position and altitude (e.g. for altimeter settings).", new[] { DcsGrpcParam.Position }, false,
            (ch, c, i) => Unary(new AtmosphereService.AtmosphereServiceClient(ch).GetTemperatureAndPressureAsync(new GetTemperatureAndPressureRequest { Position = ToInputPosition(i) }, c.Headers, c.Deadline, c.Token))),
        Def("env.magDeclination", "Environment", "Magnetic declination", "CustomService.GetMagneticDeclination",
            "Magnetic variation at a position (true vs. magnetic headings).", new[] { DcsGrpcParam.Position }, false,
            (ch, c, i) =>
            {
                ValidatePosition(i);
                return Unary(new CustomService.CustomServiceClient(ch).GetMagneticDeclinationAsync(
                    new GetMagneticDeclinationRequest { Lat = i.Latitude, Lon = i.Longitude, Alt = i.AltitudeMeters }, c.Headers, c.Deadline, c.Token));
            }),

        // ---------------- Live streams ----------------
        Def("stream.events", "Live streams", "Mission events (live)", "MissionService.StreamEvents",
            "Listens for N seconds and records every event: takeoffs, landings, shots, hits, kills, player slot changes, chat, marks...",
            new[] { DcsGrpcParam.StreamDuration }, false,
            (ch, c, i) => Stream(c, i.StreamDurationSeconds,
                token => new MissionService.MissionServiceClient(ch).StreamEvents(new StreamEventsRequest(), c.Headers, cancellationToken: token))),
        Def("stream.units", "Live streams", "Unit movements (live)", "MissionService.StreamUnits",
            "Listens for N seconds and records unit position updates - the kind of feed a 'picture' call would be built on. The first update lists all units.",
            new[] { DcsGrpcParam.StreamDuration, DcsGrpcParam.PollRate, DcsGrpcParam.GroupCategory }, false,
            (ch, c, i) => Stream(c, i.StreamDurationSeconds,
                token => new MissionService.MissionServiceClient(ch).StreamUnits(
                    new StreamUnitsRequest
                    {
                        PollRate = (uint)Math.Clamp(i.PollRateSeconds, 1, 60),
                        MaxBackoff = (uint)Math.Clamp(i.PollRateSeconds, 1, 60), // no backoff: see every unit every poll
                        Category = ParseCategory(i.GroupCategory)
                    },
                    c.Headers, cancellationToken: token))),
    };

    /// <summary>All available queries, in display order.</summary>
    public static IReadOnlyList<DcsGrpcQueryInfo> Queries { get; } = Defs.Select(d => d.Info).ToList();

    public static IReadOnlyList<string> Coalitions { get; } = new[] { "All", "Blue", "Red", "Neutral" };
    public static IReadOnlyList<string> GroupCategories { get; } = new[] { "All", "Airplane", "Helicopter", "Ground", "Ship", "Train" };

    /// <summary>Runs one query and returns its raw response as JSON.</summary>
    public static async Task<DcsGrpcQueryResult> RunAsync(string address, string? apiKey, string queryId,
        DcsGrpcQueryInput input, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        var def = Defs.FirstOrDefault(d => d.Info.Id == queryId);
        if (def == null)
            return new DcsGrpcQueryResult { Success = false, Message = $"Unknown query '{queryId}'." };
        if (string.IsNullOrWhiteSpace(address))
            return new DcsGrpcQueryResult { Success = false, Message = "No DCS-gRPC address configured." };

        var sw = Stopwatch.StartNew();
        try
        {
            using var channel = GrpcChannel.ForAddress(address);
            var ctx = NewContext(apiKey, timeoutSeconds, cancellationToken);
            var node = await def.Run(channel, ctx, input);
            sw.Stop();

            var result = new DcsGrpcQueryResult
            {
                Success = true,
                ElapsedMs = sw.ElapsedMilliseconds,
                Json = node?.ToJsonString(Indented) ?? "{}",
                DiscoveredNames = CollectNames(node)
            };
            result.Message = node is JsonObject { } o && o.TryGetPropertyValue("messageCount", out var count)
                ? $"{def.Info.Rpc} - {count} message(s) received in {sw.ElapsedMilliseconds} ms."
                : $"{def.Info.Rpc} - OK in {sw.ElapsedMilliseconds} ms.";
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DcsGrpcQueryResult { Success = false, ElapsedMs = sw.ElapsedMilliseconds, Message = Describe(ex, address) };
        }
    }

    /// <summary>
    /// Runs every query marked InSnapshot (all parameterless ones plus coalition-wide lists with
    /// Coalition=All) and combines the results into one JSON document keyed by query id - the
    /// quickest way to see "everything this mission exposes" at a glance. A failing call doesn't
    /// abort the snapshot; its entry just contains the error instead.
    /// </summary>
    public static async Task<DcsGrpcQueryResult> RunSnapshotAsync(string address, string? apiKey,
        int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new DcsGrpcQueryResult { Success = false, Message = "No DCS-gRPC address configured." };

        var sw = Stopwatch.StartNew();
        var root = new JsonObject();
        int ok = 0, failed = 0;
        string? firstError = null;

        try
        {
            using var channel = GrpcChannel.ForAddress(address);
            var input = new DcsGrpcQueryInput(); // Coalition/Category = All

            // Bullseye needs a specific coalition, so it isn't InSnapshot - add both sides explicitly.
            var jobs = Defs.Where(d => d.Info.InSnapshot)
                .Select(d => (Key: d.Info.Id, Def: d, Input: input))
                .Concat(new[] { "Blue", "Red" }.Select(side =>
                    (Key: $"coalition.bullseye.{side.ToLowerInvariant()}", Def: Defs.First(d => d.Info.Id == "coalition.bullseye"),
                     Input: new DcsGrpcQueryInput { Coalition = side })))
                .ToList();

            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var node = await job.Def.Run(channel, NewContext(apiKey, timeoutSeconds, cancellationToken), job.Input);
                    root[job.Key] = node;
                    ok++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var msg = Describe(ex, address);
                    root[job.Key] = new JsonObject { ["error"] = msg };
                    firstError ??= msg;
                    failed++;

                    // Server not reachable at all - no point trying the remaining ~20 calls.
                    if (ex is RpcException { StatusCode: StatusCode.Unavailable } && ok == 0)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            return new DcsGrpcQueryResult { Success = false, ElapsedMs = sw.ElapsedMilliseconds, Message = Describe(ex, address) };
        }

        sw.Stop();
        var success = ok > 0;
        return new DcsGrpcQueryResult
        {
            Success = success,
            ElapsedMs = sw.ElapsedMilliseconds,
            Json = root.ToJsonString(Indented),
            DiscoveredNames = CollectNames(root),
            Message = success
                ? $"Snapshot: {ok} call(s) OK, {failed} failed, {sw.ElapsedMilliseconds} ms." +
                  (failed > 0 ? " Failed entries contain the error message (hook calls fail if DCS-gRPC's hook part isn't running)." : "")
                : firstError ?? "All calls failed."
        };
    }

    // ------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------

    private static QueryDef Def(string id, string category, string title, string rpc, string description,
        DcsGrpcParam[] parameters, bool inSnapshot, QueryRunner run) =>
        new(new DcsGrpcQueryInfo
        {
            Id = id, Category = category, Title = title, Rpc = rpc, Description = description,
            Parameters = parameters, InSnapshot = inSnapshot
        }, run);

    private static CallContext NewContext(string? apiKey, int timeoutSeconds, CancellationToken token)
    {
        var headers = new Metadata();
        if (!string.IsNullOrWhiteSpace(apiKey))
            headers.Add("X-API-Key", apiKey);
        return new CallContext(headers, DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds)), token);
    }

    private static async Task<JsonNode?> Unary<T>(AsyncUnaryCall<T> call) where T : IMessage
    {
        using (call)
        {
            var response = await call.ResponseAsync;
            return JsonNode.Parse(Formatter.Format(response));
        }
    }

    /// <summary>
    /// Collects messages from a server-streaming call for a fixed duration (or until
    /// MaxStreamMessages), then cancels the call. Hitting the time limit is the normal way this
    /// ends, not an error.
    /// </summary>
    private static async Task<JsonNode?> Stream<T>(CallContext ctx, int durationSeconds,
        Func<CancellationToken, AsyncServerStreamingCall<T>> start) where T : IMessage
    {
        var duration = Math.Clamp(durationSeconds, 1, MaxStreamDurationSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(duration));

        var messages = new JsonArray();
        var truncated = false;
        var started = DateTime.UtcNow;

        using (var call = start(cts.Token))
        {
            try
            {
                while (await call.ResponseStream.MoveNext(cts.Token))
                {
                    var item = JsonNode.Parse(Formatter.Format(call.ResponseStream.Current));
                    messages.Add(new JsonObject
                    {
                        ["receivedAfterSeconds"] = Math.Round((DateTime.UtcNow - started).TotalSeconds, 2),
                        ["message"] = item
                    });

                    if (messages.Count >= MaxStreamMessages)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cts.IsCancellationRequested && !ctx.Token.IsCancellationRequested)
            {
                // Our own time limit - normal end of the recording.
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ctx.Token.IsCancellationRequested)
            {
                // Same, surfaced as a plain cancellation instead of an RpcException.
            }
        }

        return new JsonObject
        {
            ["durationSeconds"] = duration,
            ["messageCount"] = messages.Count,
            ["truncatedAtLimit"] = truncated,
            ["messages"] = messages
        };
    }

    private static string Require(string value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Please enter a {what}.")
            : value.Trim();

    private static Coalition ParseCoalition(string value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "blue" => Coalition.Blue,
            "red" => Coalition.Red,
            "neutral" => Coalition.Neutral,
            _ => Coalition.All
        };

    private static Coalition ParseSpecificCoalition(string value)
    {
        var c = ParseCoalition(value);
        return c == Coalition.All
            ? throw new ArgumentException("This query needs a specific coalition (Blue, Red or Neutral), not 'All'.")
            : c;
    }

    private static GroupCategory ParseCategory(string value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "airplane" => GroupCategory.Airplane,
            "helicopter" => GroupCategory.Helicopter,
            "ground" => GroupCategory.Ground,
            "ship" => GroupCategory.Ship,
            "train" => GroupCategory.Train,
            _ => GroupCategory.Unspecified
        };

    private static void ValidatePosition(DcsGrpcQueryInput i)
    {
        if (i.Latitude is < -90 or > 90 || i.Longitude is < -180 or > 180)
            throw new ArgumentException("Latitude must be between -90 and 90, longitude between -180 and 180.");
        if (i.Latitude == 0 && i.Longitude == 0)
            throw new ArgumentException("Please enter a position (or use 'Take position from unit').");
    }

    private static InputPosition ToInputPosition(DcsGrpcQueryInput i)
    {
        ValidatePosition(i);
        return new InputPosition { Lat = i.Latitude, Lon = i.Longitude, Alt = i.AltitudeMeters };
    }

    /// <summary>
    /// Looks up a unit's current position - used by the GUI's "take position from unit" button so
    /// the environment queries (wind, temperature, declination) can be run at a real unit's
    /// location without copying coordinates by hand.
    /// </summary>
    public static async Task<(bool Success, string Message, double Lat, double Lon, double Alt)> GetUnitPositionAsync(
        string address, string? apiKey, string unitName, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitName))
            return (false, "Enter a unit name first.", 0, 0, 0);
        try
        {
            using var channel = GrpcChannel.ForAddress(address);
            var ctx = NewContext(apiKey, timeoutSeconds, cancellationToken);
            var response = await new UnitService.UnitServiceClient(channel)
                .GetPositionAsync(new GetPositionRequest { Name = unitName.Trim() }, ctx.Headers, ctx.Deadline, ctx.Token);
            var p = response.Position;
            if (p == null)
                return (false, $"No position returned for '{unitName}'.", 0, 0, 0);
            return (true, $"Position of '{unitName}' taken over.", p.Lat, p.Lon, p.Alt);
        }
        catch (Exception ex)
        {
            return (false, Describe(ex, address), 0, 0, 0);
        }
    }

    private static List<string> CollectNames(JsonNode? node)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonNode? n)
        {
            if (names.Count >= 2000) return;
            switch (n)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (key == "name" && value is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                            names.Add(s);
                        else
                            Walk(value);
                    }
                    break;
                case JsonArray arr:
                    foreach (var item in arr) Walk(item);
                    break;
            }
        }
        Walk(node);
        return names.ToList();
    }

    private static string Describe(Exception ex, string address) => ex switch
    {
        ArgumentException a => a.Message,
        RpcException { StatusCode: StatusCode.Unavailable } r =>
            $"Could not reach {address} - is DCS-gRPC running and the port reachable? ({r.Status.Detail})",
        RpcException { StatusCode: StatusCode.Unauthenticated or StatusCode.PermissionDenied } r =>
            $"Rejected by the server ({r.StatusCode}) - check the API key.",
        RpcException { StatusCode: StatusCode.DeadlineExceeded } =>
            "Timed out - the mission may be paused/loading, or the call is slow on a busy server.",
        RpcException { StatusCode: StatusCode.Unimplemented } =>
            "The server doesn't implement this call (different DCS-gRPC version, or the hook part isn't running).",
        RpcException { StatusCode: StatusCode.NotFound } r =>
            $"Not found: {r.Status.Detail} (check the exact unit/group name from the mission editor).",
        RpcException r => $"{r.StatusCode}: {r.Status.Detail}",
        OperationCanceledException => "Cancelled.",
        _ => $"Unexpected error: {ex.Message}"
    };
}
