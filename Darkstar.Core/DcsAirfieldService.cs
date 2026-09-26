using System.Globalization;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using RurouniJones.Dcs.Grpc.V0.Atmosphere;
using RurouniJones.Dcs.Grpc.V0.Common;
using RurouniJones.Dcs.Grpc.V0.Custom;
using RurouniJones.Dcs.Grpc.V0.Mission;
using RurouniJones.Dcs.Grpc.V0.World;

namespace Darkstar;

/// <summary>
/// Answers "runway in use" and ATIS calls from the running mission.
///
/// WHERE THE DATA COMES FROM, AND WHY IT IS AWKWARD: wind, temperature and pressure have proper
/// RPCs (AtmosphereService). Runways do not - DCS-gRPC's Airbase message carries only a name, a
/// position and a category. The runway headings exist in DCS itself, as
/// <c>Airbase.getRunways()</c>, and the only way to reach them is CustomService.Eval, which runs
/// Lua inside the mission and hands back JSON.
///
/// Eval is a loaded gun: it can run anything. Three things keep it safe here.
///
///  1. The Lua is a constant in this file. Nothing a pilot says, and nothing from any
///     configuration field, is ever pasted into it - so there is no way to talk the bot into
///     running something else. The snippet asks for ALL airfields at once precisely so that no
///     airfield name has to be interpolated.
///  2. It only reads. No unit is spawned, no flag is set, no message is sent.
///  3. The result is cached for the whole mission. Runways do not move, so the Lua runs once,
///     not once per request.
///
/// It also needs the server operator's consent, whether we like it or not: DCS-gRPC ships with
/// <c>evalEnabled = false</c>, and refuses the call otherwise. That refusal is reported as
/// something the operator can act on rather than as a failure.
///
/// Taxiways, before anyone asks: DCS does not expose them at all - not through gRPC, not through
/// its own scripting API. They live in the terrain model. Runways and parking are the limit.
/// </summary>
public sealed class DcsAirfieldService
{
    /// <summary>
    /// The one and only Lua this bot ever evaluates. Read-only, no interpolation, wrapped in
    /// pcall throughout so one odd airbase object can't take the whole answer down.
    ///
    /// Returns an array rather than a keyed table, because DCS's lua2json turns a table with
    /// sequential keys into a JSON array and that is the shape we can rely on.
    /// </summary>
    private const string RunwayQueryLua = """
        local bases = {}
        if world and world.getAirbases then
          local ok, list = pcall(world.getAirbases)
          if ok and list then for _, b in pairs(list) do bases[#bases + 1] = b end end
        end
        if #bases == 0 and coalition and coalition.getAirbases then
          for side = 0, 2 do
            local ok, list = pcall(coalition.getAirbases, side)
            if ok and list then for _, b in pairs(list) do bases[#bases + 1] = b end end
          end
        end
        local out = {}
        local seen = {}
        for _, b in pairs(bases) do
          local okName, name = pcall(function() return b:getName() end)
          if okName and name and not seen[name] then
            seen[name] = true
            local runways = {}
            local okRw, list = pcall(function() return b:getRunways() end)
            if okRw and list then
              for _, r in pairs(list) do
                runways[#runways + 1] = {
                  name = tostring(r.Name or ""),
                  course = r.course or 0,
                  length = r.length or 0,
                  width = r.width or 0
                }
              end
            end
            out[#out + 1] = { name = name, runways = runways }
          end
        end
        return out
        """;

    private const double MetersPerSecondToKnots = 1.943844;
    private const double PascalsPerHectopascal = 100.0;
    private const double PascalsPerInchHg = 3386.389;

    /// <summary>
    /// How far above the airfield the wind is sampled. DCS's wind at exactly ground level can be
    /// degenerate; ten metres is close enough to be the surface wind an ATIS reports.
    /// </summary>
    private const double WindSampleHeightMeters = 10.0;

    private readonly AppConfig _config;
    private readonly DcsIntelService _intel;

    /// <summary>Runways per airfield name, from the one Eval call. Runways don't move.</summary>
    private Dictionary<string, List<Runway>>? _runwayCache;

    /// <summary>
    /// The DCS session the cache belongs to. GetSessionId changes on a mission change or a server
    /// restart, which are exactly the two occasions when the runways might be different ones.
    /// </summary>
    private long _runwayCacheSession = long.MinValue;

    /// <summary>Set once Eval has been refused, so we stop asking every single time.</summary>
    private bool _evalRefused;

    public DcsAirfieldService(AppConfig config, DcsIntelService intel)
    {
        _config = config;
        _intel = intel;
    }

    // ---------------------------------------------------------------------------------------
    // Request classification
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Works out whether a transmission asked for airfield information. ATIS is checked first:
    /// "Batumi ATIS" often contains the word "runway" too, and the fuller answer should win.
    /// </summary>
    public AirfieldRequestKind Classify(string transcript) => Classify(transcript, out _);

    /// <summary>As <see cref="Classify(string)"/>, and also reports which trigger phrase fired.</summary>
    public AirfieldRequestKind Classify(string transcript, out string? matchedTrigger)
    {
        matchedTrigger = null;
        if (!_config.DcsAirfieldEnabled || string.IsNullOrWhiteSpace(transcript))
            return AirfieldRequestKind.None;

        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsAirfieldAtisTriggers);
        if (matchedTrigger != null) return AirfieldRequestKind.Atis;

        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsAirfieldRunwayTriggers);
        if (matchedTrigger != null) return AirfieldRequestKind.RunwayInUse;

        matchedTrigger = null;
        return AirfieldRequestKind.None;
    }

    // ---------------------------------------------------------------------------------------
    // Answering
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the spoken answer. The airfield comes from the transcript when the pilot named one,
    /// otherwise from whichever airfield is nearest their aircraft.
    /// </summary>
    public async Task<AirfieldResult> AnswerAsync(AirfieldRequestKind kind, string transcript,
        string rawPlayerName, int senderCoalition, CancellationToken cancellationToken = default)
    {
        if (kind == AirfieldRequestKind.None)
            return new AirfieldResult();

        try
        {
            using var channel = GrpcChannel.ForAddress(_config.DcsGrpcAddress);
            var headers = new Metadata();
            if (!string.IsNullOrWhiteSpace(_config.DcsGrpcApiKey))
                headers.Add("X-API-Key", _config.DcsGrpcApiKey);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _config.DcsIntelTimeoutSeconds));

            var friendly = _intel.ResolveFriendlyCoalition(senderCoalition);

            var airfields = await GetAirfieldsAsync(channel, headers, deadline, cancellationToken);
            if (airfields.Count == 0)
                return Unhandled(_config.DcsAirfieldUnavailableReply, "no airfields returned by the mission");

            // Named in the transmission, or else the one the pilot is closest to.
            var (airfield, how) = await ResolveAirfieldAsync(channel, headers, deadline, transcript,
                rawPlayerName, friendly, airfields, cancellationToken);

            if (airfield == null)
                return Unhandled(_config.DcsAirfieldUnknownReply, how);

            var conditions = await GetConditionsAsync(channel, headers, deadline, airfield, cancellationToken);

            var reply = kind == AirfieldRequestKind.Atis
                ? BuildAtis(conditions, _config)
                : BuildRunwayInUse(conditions, _config);

            var diagnostics =
                $"airfield: {airfield.Name} ({how}); " +
                $"wind {conditions.WindFromMagnetic:000}M at {conditions.WindKnots:0} kt; " +
                $"{conditions.RunwayEnds.Count} runway end(s)" +
                (conditions.Best != null
                    ? $"; chosen {conditions.Best.Designator} " +
                      $"(headwind {conditions.Best.HeadwindKnots:0.0} kt, crosswind {conditions.Best.CrosswindKnots:0.0} kt)"
                    : "; no runway data (is evalEnabled set on the DCS-gRPC server?)");

            return new AirfieldResult
            {
                Reply = reply,
                Handled = true,
                Diagnostics = diagnostics,
                Conditions = conditions,
            };
        }
        catch (RpcException ex)
        {
            Logger.Log($"[Airfield] DCS-gRPC error: {ex.Status.StatusCode} - {ex.Status.Detail}");
            return Unhandled(_config.DcsAirfieldUnavailableReply, $"gRPC error: {ex.Status.StatusCode}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Airfield] Unexpected error: {ex.Message}");
            Logger.Debug(ex.ToString());
            return Unhandled(_config.DcsAirfieldUnavailableReply, ex.Message);
        }
    }

    private static AirfieldResult Unhandled(string reply, string diagnostics) =>
        new() { Reply = reply, Handled = true, Diagnostics = diagnostics };

    // ---------------------------------------------------------------------------------------
    // Airfields and their runways
    // ---------------------------------------------------------------------------------------

    private async Task<List<Airfield>> GetAirfieldsAsync(GrpcChannel channel, Metadata headers,
        DateTime deadline, CancellationToken token)
    {
        var response = await new WorldService.WorldServiceClient(channel)
            .GetAirbasesAsync(new GetAirbasesRequest { Coalition = Coalition.All }, headers, deadline, token);

        var runways = await GetRunwaysAsync(channel, headers, deadline, token);

        return response.Airbases
            // Airdromes only: helipads have no runway to speak of and carriers are a different
            // conversation entirely.
            .Where(a => a.Category == AirbaseCategory.Airdrome)
            .Select(a => new Airfield
            {
                Name = a.Name,
                DisplayName = string.IsNullOrWhiteSpace(a.DisplayName) ? a.Name : a.DisplayName,
                Lat = a.Position?.Lat ?? 0,
                Lon = a.Position?.Lon ?? 0,
                ElevationMeters = a.Position?.Alt ?? 0,
                Runways = runways.TryGetValue(a.Name, out var list) ? list : new List<Runway>(),
            })
            .Where(a => a.Lat != 0 || a.Lon != 0)
            .ToList();
    }

    /// <summary>
    /// The runways of every airfield, from one cached Eval call. Returns an empty map when Eval is
    /// unavailable - the ATIS then reports the weather and says the runway is unknown, rather than
    /// failing outright.
    /// </summary>
    private async Task<Dictionary<string, List<Runway>>> GetRunwaysAsync(GrpcChannel channel,
        Metadata headers, DateTime deadline, CancellationToken token)
    {
        if (_evalRefused)
            return new Dictionary<string, List<Runway>>();

        var session = await GetSessionIdAsync(channel, headers, deadline, token);

        if (_runwayCache != null && _runwayCacheSession == session)
            return _runwayCache;

        try
        {
            var response = await new CustomService.CustomServiceClient(channel)
                .EvalAsync(new EvalRequest { Lua = RunwayQueryLua }, headers, deadline, token);

            _runwayCache = ParseRunwayJson(response.Json);
            _runwayCacheSession = session;

            Logger.Log($"[Airfield] Runway data read for {_runwayCache.Count} airfield(s) " +
                       $"({_runwayCache.Values.Sum(r => r.Count)} runway strip(s)).");

            return _runwayCache;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            // DCS-gRPC ships with eval turned off. Say exactly what to change, once.
            _evalRefused = true;
            Logger.Log("[Airfield] The DCS-gRPC server refuses Eval, so runway headings can't be read.");
            Logger.Log("  Set \"evalEnabled = true\" in the DCS-gRPC server configuration and restart the mission.");
            Logger.Log("  Wind, temperature and pressure still work; only the runway in use is missing.");
            return new Dictionary<string, List<Runway>>();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Airfield] Runway data could not be read: {ex.Message}");
            Logger.Debug(ex.ToString());
            return new Dictionary<string, List<Runway>>();
        }
    }

    /// <summary>
    /// The current DCS session, used only to decide whether the cached runways still belong to
    /// the mission that is running. A failure here is not worth reporting - it just means the
    /// cache is treated as still valid.
    /// </summary>
    private async Task<long> GetSessionIdAsync(GrpcChannel channel, Metadata headers,
        DateTime deadline, CancellationToken token)
    {
        try
        {
            var response = await new MissionService.MissionServiceClient(channel)
                .GetSessionIdAsync(new GetSessionIdRequest(), headers, deadline, token);
            return response.SessionId;
        }
        catch
        {
            return _runwayCacheSession;
        }
    }

    /// <summary>
    /// Turns the Eval result into runways per airfield. Written defensively: this is JSON produced
    /// by Lua, where an empty list can come back as an object, numbers can arrive as strings, and
    /// a single unexpected entry must not lose the rest.
    /// </summary>
    internal static Dictionary<string, List<Runway>> ParseRunwayJson(string? json)
    {
        var result = new Dictionary<string, List<Runway>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return result; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return result; // an empty Lua table serialises as an object - nothing to read

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("name", out var nameElement)) continue;

                var name = nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var runways = new List<Runway>();

                if (entry.TryGetProperty("runways", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var runway in list.EnumerateArray())
                    {
                        if (runway.ValueKind != JsonValueKind.Object) continue;

                        var course = ReadNumber(runway, "course");
                        var length = ReadNumber(runway, "length");
                        var width = ReadNumber(runway, "width");

                        runways.Add(new Runway
                        {
                            Name = runway.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                                ? (n.GetString() ?? "")
                                : "",
                            TrueHeadingDegrees = HeadingFromCourse(course),
                            LengthMeters = length,
                            WidthMeters = width,
                        });
                    }
                }

                result[name!] = runways;
            }
        }

        return result;
    }

    private static double ReadNumber(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element)) return 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>
    /// DCS reports a runway's <c>course</c> in radians with the opposite sign to a compass
    /// heading - the community wiki's advice is literally "multiply by -1 to make it useful".
    /// This turns it into degrees true, 0-360.
    /// </summary>
    internal static double HeadingFromCourse(double courseRadians) =>
        DcsIntelService.NormalizeBearing(-courseRadians * 180.0 / Math.PI);

    // ---------------------------------------------------------------------------------------
    // Which airfield did they mean?
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Works out which airfield the pilot means. The pilot's own position comes first, on purpose.
    ///
    /// Airfield names are the weakest part of this whole feature: "Mineralnye Vody", "Kobuleti",
    /// "Batumi" are exactly the words speech recognition gets wrong, and a mis-transcription that
    /// happens to match a different airfield produces a confidently wrong answer. The pilot's
    /// position can't be mis-heard. So if we know where they are and they are at or near a field,
    /// that is the answer - unless they clearly named a different one.
    /// </summary>
    private async Task<(Airfield?, string)> ResolveAirfieldAsync(GrpcChannel channel, Metadata headers,
        DateTime deadline, string transcript, string rawPlayerName, Coalition friendly,
        List<Airfield> airfields, CancellationToken token)
    {
        // The transcript is passed along as a second way to identify the pilot: saying "active
        // runway for Punch 1-1" names them, which works even when their SRS name and their DCS
        // name don't match.
        var unit = await _intel.FindRequesterUnitAsync(channel, headers, deadline,
            rawPlayerName, transcript, friendly, token);

        Airfield? nearest = null;
        var nearestDistance = double.MaxValue;

        if (unit?.Position != null)
        {
            foreach (var airfield in airfields)
            {
                var distance = DcsIntelService.DistanceNm(unit.Position.Lat, unit.Position.Lon, airfield.Lat, airfield.Lon);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = airfield;
                }
            }
        }

        // Did they name one? Checked against the pilot's own callsign as well, so a pilot called
        // "Batumi 1-1" doesn't name an airfield just by identifying themselves.
        var named = MatchAirfieldByName(transcript, airfields, rawPlayerName);

        // Sitting on the ramp or in the pattern: their own field is what they mean, whatever a
        // garbled word in the transcript looked like.
        if (nearest != null && nearestDistance <= _config.DcsAirfieldAtFieldNm)
        {
            if (named != null && named != nearest)
                return (named, $"named in the request, though the pilot is at {nearest.SpokenName}");

            return (nearest, $"the pilot is at it, {nearestDistance:0.#} NM from the centre");
        }

        // Airborne and away from any field: a named airfield is now the better signal.
        if (named != null)
            return (named, "named in the request");

        if (nearest == null)
            return (null, "no airfield named, and the requesting pilot could not be located");

        if (nearestDistance > _config.DcsAirfieldMaxDistanceNm)
            return (null, $"the pilot is {nearestDistance:0} NM from the nearest airfield " +
                          $"({nearest.SpokenName}), beyond the {_config.DcsAirfieldMaxDistanceNm:0} NM limit");

        return (nearest, $"nearest to the pilot, {nearestDistance:0} NM");
    }

    /// <summary>
    /// Finds an airfield named somewhere in the transcript. Matching is done on canonical keys, so
    /// spacing and punctuation don't matter, and the longest matching name wins - otherwise
    /// "Mineralnye Vody" would be beaten by any shorter name that happens to be a substring of it.
    /// </summary>
    /// <param name="rawPlayerName">
    /// The requesting pilot's SRS name, if known. A name that only matches because it is part of
    /// the pilot's own callsign is not them naming an airfield.
    /// </param>
    internal static Airfield? MatchAirfieldByName(string? transcript, List<Airfield> airfields,
        string? rawPlayerName = null)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var haystack = PilotNames.CanonicalKey(transcript);
        if (haystack.Length == 0) return null;

        var pilotKey = PilotNames.CanonicalKey(rawPlayerName);

        Airfield? best = null;
        var bestLength = 0;

        foreach (var airfield in airfields)
        {
            foreach (var candidate in new[] { airfield.Name, airfield.DisplayName })
            {
                var key = PilotNames.CanonicalKey(candidate);

                // Three characters is short enough to appear inside unrelated words.
                if (key.Length < 4) continue;

                // Their own callsign is not an airfield name, even when it reads like one.
                if (pilotKey.Length > 0 && pilotKey.Contains(key, StringComparison.Ordinal)) continue;

                if (haystack.Contains(key, StringComparison.Ordinal) && key.Length > bestLength)
                {
                    best = airfield;
                    bestLength = key.Length;
                }
            }
        }

        return best;
    }

    // ---------------------------------------------------------------------------------------
    // Conditions
    // ---------------------------------------------------------------------------------------

    private async Task<AirfieldConditions> GetConditionsAsync(GrpcChannel channel, Metadata headers,
        DateTime deadline, Airfield airfield, CancellationToken token)
    {
        var atmosphere = new AtmosphereService.AtmosphereServiceClient(channel);
        var samplePosition = new InputPosition
        {
            Lat = airfield.Lat,
            Lon = airfield.Lon,
            Alt = airfield.ElevationMeters + WindSampleHeightMeters,
        };

        var wind = await atmosphere.GetWindAsync(
            new GetWindRequest { Position = samplePosition }, headers, deadline, token);

        var air = await atmosphere.GetTemperatureAndPressureAsync(
            new GetTemperatureAndPressureRequest { Position = samplePosition }, headers, deadline, token);

        // DCS-gRPC already gives the wind as the direction it blows FROM, in degrees true. An ATIS
        // reports magnetic, so it has to be converted like every other bearing the bot speaks.
        var declination = await _intel.GetDeclinationAsync(channel, headers, deadline, airfield.Lat, airfield.Lon, token);
        var windTrue = DcsIntelService.NormalizeBearing(wind.Heading);
        var windMagnetic = _config.DcsIntelMagneticBearings
            ? DcsIntelService.NormalizeBearing(windTrue - declination)
            : windTrue;

        var windKnots = wind.Strength * MetersPerSecondToKnots;

        return new AirfieldConditions
        {
            Airfield = airfield,
            WindFromMagnetic = (int)Math.Round(windMagnetic),
            WindKnots = windKnots,
            TemperatureCelsius = air.Temperature - 273.15,
            QnhHectopascals = air.Pressure / PascalsPerHectopascal,
            QnhInchesHg = air.Pressure / PascalsPerInchHg,
            RunwayEnds = RankRunwayEnds(airfield.Runways, windTrue, windKnots, declination),
        };
    }

    // ---------------------------------------------------------------------------------------
    // The actual decision
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Turns each runway strip into its two usable directions and ranks them by how much headwind
    /// they offer - which is what "runway in use" means. Ties (dead calm, or a pure crosswind) are
    /// broken by the smaller crosswind first and then by the longer runway, so the answer is
    /// stable rather than dependent on the order DCS happened to list them in.
    /// </summary>
    /// <param name="windFromTrue">Direction the wind comes from, degrees true.</param>
    /// <param name="windKnots">Wind speed in knots.</param>
    /// <param name="declination">Magnetic declination, for the spoken designators.</param>
    internal static List<RunwayEnd> RankRunwayEnds(List<Runway> runways, double windFromTrue,
        double windKnots, double declination)
    {
        var ends = new List<RunwayEnd>();

        foreach (var runway in runways)
        {
            // A strip can be landed on from either direction.
            foreach (var heading in new[]
                     {
                         DcsIntelService.NormalizeBearing(runway.TrueHeadingDegrees),
                         DcsIntelService.NormalizeBearing(runway.TrueHeadingDegrees + 180),
                     })
            {
                var magnetic = DcsIntelService.NormalizeBearing(heading - declination);
                var offset = (windFromTrue - heading) * Math.PI / 180.0;

                ends.Add(new RunwayEnd
                {
                    Designator = DesignatorFor(magnetic, runway.Name),
                    TrueHeadingDegrees = heading,
                    MagneticHeadingDegrees = magnetic,
                    LengthMeters = runway.LengthMeters,
                    HeadwindKnots = windKnots * Math.Cos(offset),
                    CrosswindKnots = Math.Abs(windKnots * Math.Sin(offset)),
                });
            }
        }

        return ends
            .OrderByDescending(e => Math.Round(e.HeadwindKnots, 1))
            .ThenBy(e => Math.Round(e.CrosswindKnots, 1))
            .ThenByDescending(e => e.LengthMeters)
            .ThenBy(e => e.MagneticHeadingDegrees)
            .ToList();
    }

    /// <summary>
    /// The number painted on the threshold: the magnetic heading divided by ten and rounded, with
    /// 0 becoming 36.
    ///
    /// DCS's own <c>Name</c> for the strip is preferred when it agrees to within one - it is what
    /// that terrain's charts show, and trusting it keeps the bot consistent with the kneeboard
    /// even where the magnetic model differs slightly. When it names the other end of the strip,
    /// or is missing, the computed value is used.
    /// </summary>
    internal static string DesignatorFor(double magneticHeading, string? dcsName)
    {
        var computed = (int)Math.Round(DcsIntelService.NormalizeBearing(magneticHeading) / 10.0);
        if (computed == 0) computed = 36;
        if (computed > 36) computed -= 36;

        if (!string.IsNullOrWhiteSpace(dcsName))
        {
            // Names look like "13", "31L", "04R" - take the leading digits.
            var digits = new string(dcsName.TrimStart().TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var named) && named is >= 1 and <= 36)
            {
                var difference = Math.Min(Math.Abs(named - computed), 36 - Math.Abs(named - computed));
                if (difference <= 1)
                    return dcsName.Trim();
            }
        }

        return computed.ToString("00", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------------------------------
    // Saying it out loud
    // ---------------------------------------------------------------------------------------

    /// <summary>"Batumi, runway in use one three, wind one three zero at one two knots."</summary>
    internal static string BuildRunwayInUse(AirfieldConditions conditions, AppConfig config)
    {
        var airfield = conditions.Airfield;
        if (airfield == null) return config.DcsAirfieldUnknownReply;

        if (conditions.Best == null)
            return $"{airfield.SpokenName}, runway unknown, {SpeakWind(conditions, config)}.";

        return $"{airfield.SpokenName}, runway in use {SpeakDesignator(conditions.Best.Designator, config)}, " +
               $"{SpeakWind(conditions, config)}.";
    }

    /// <summary>The full report: wind, temperature, pressure, runway.</summary>
    internal static string BuildAtis(AirfieldConditions conditions, AppConfig config)
    {
        var airfield = conditions.Airfield;
        if (airfield == null) return config.DcsAirfieldUnknownReply;

        var parts = new List<string>
        {
            $"{airfield.SpokenName} information",
            SpeakWind(conditions, config),
            SpeakTemperature(conditions.TemperatureCelsius, config),
            SpeakPressure(conditions, config),
        };

        parts.Add(conditions.Best != null
            ? $"runway in use {SpeakDesignator(conditions.Best.Designator, config)}"
            : "runway in use unknown");

        return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p))) + ".";
    }

    /// <summary>"wind one three zero at one two knots", or "wind calm".</summary>
    internal static string SpeakWind(AirfieldConditions conditions, AppConfig config)
    {
        var knots = (int)Math.Round(conditions.WindKnots);
        if (knots <= 1) return "wind calm";

        var direction = config.DcsIntelSlowSpeech
            ? DcsIntelService.SpeakBearing(conditions.WindFromMagnetic)
            : conditions.WindFromMagnetic.ToString("000", CultureInfo.InvariantCulture);

        var speed = config.DcsIntelSlowSpeech
            ? SpeakDigits(knots)
            : knots.ToString(CultureInfo.InvariantCulture);

        return $"wind {direction} at {speed} knots";
    }

    internal static string SpeakTemperature(double celsius, AppConfig config)
    {
        var rounded = (int)Math.Round(celsius);
        var magnitude = Math.Abs(rounded);

        // Digit by digit ("one five"), not as a word ("fifteen") - it is a reading, not a count,
        // and it is what an ATIS sounds like.
        var spoken = config.DcsIntelSlowSpeech
            ? SpeakDigits(magnitude)
            : magnitude.ToString(CultureInfo.InvariantCulture);

        return rounded < 0 ? $"temperature minus {spoken}" : $"temperature {spoken}";
    }

    /// <summary>
    /// Pressure, in whichever unit the squadron's aircraft use. "Both" says QNH in hectopascals
    /// and then the altimeter setting in inches, which is the practical choice for a mixed flight
    /// of western and eastern types.
    /// </summary>
    internal static string SpeakPressure(AirfieldConditions conditions, AppConfig config)
    {
        var hpa = (int)Math.Round(conditions.QnhHectopascals);
        var inHgHundredths = (int)Math.Round(conditions.QnhInchesHg * 100);

        var qnh = config.DcsIntelSlowSpeech ? SpeakDigits(hpa) : hpa.ToString(CultureInfo.InvariantCulture);
        var altimeter = config.DcsIntelSlowSpeech
            ? SpeakDigits(inHgHundredths)
            : (inHgHundredths / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

        return config.DcsAirfieldPressureUnit switch
        {
            PressureUnit.InchesHg => $"altimeter {altimeter}",
            PressureUnit.Hectopascals => $"QNH {qnh}",
            _ => $"QNH {qnh}, altimeter {altimeter}",
        };
    }

    /// <summary>A runway designator read as separate digits: "13" becomes "one three".</summary>
    internal static string SpeakDesignator(string designator, AppConfig config)
    {
        if (!config.DcsIntelSlowSpeech) return designator;

        var builder = new List<string>();
        foreach (var c in designator)
        {
            if (char.IsDigit(c))
                builder.Add(DcsIntelService.NumberToWords(c - '0'));
            else if (char.IsLetter(c))
                builder.Add(c switch
                {
                    'L' or 'l' => "left",
                    'R' or 'r' => "right",
                    'C' or 'c' => "center",
                    _ => c.ToString(),
                });
        }

        return builder.Count > 0 ? string.Join(" ", builder) : designator;
    }

    /// <summary>
    /// Reads a number out digit by digit, as every radio number except a count is read - and with
    /// the same digit words the bearings use, so "9" is "niner" here too.
    /// </summary>
    internal static string SpeakDigits(int value)
    {
        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        return string.Join(" ", text.Select(c => DcsIntelService.SpeakDigit(c - '0')));
    }
}
