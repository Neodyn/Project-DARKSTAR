using System.Text.RegularExpressions;
using Grpc.Core;
using Grpc.Net.Client;
using RurouniJones.Dcs.Grpc.V0.Coalition;
using RurouniJones.Dcs.Grpc.V0.Common;
using RurouniJones.Dcs.Grpc.V0.Controller;
using RurouniJones.Dcs.Grpc.V0.Custom;
using RurouniJones.Dcs.Grpc.V0.Group;

namespace Darkstar;

/// <summary>The kind of tactical request recognized in a transmission.</summary>
public enum IntelRequestKind
{
    None,
    /// <summary>Nearest hostile air contact, full BRAA (bearing, range, altitude, aspect).</summary>
    BogeyDope,
    /// <summary>Overview of the hostile air groups, positions given from the bullseye.</summary>
    Picture,
    /// <summary>Very short "anything near me?" answer - distance and bearing only.</summary>
    Threat,
    /// <summary>Set up a standing watch around the requesting pilot ("threat circle forty miles").</summary>
    ThreatCircleStart,
    /// <summary>Drop the requesting pilot's standing watch.</summary>
    ThreatCircleCancel,

    /// <summary>
    /// The pilot's own position, as a bearing and range from their coalition's bullseye - the
    /// standard way a controller confirms a pilot knows where they are.
    /// </summary>
    AlphaCheck,

    /// <summary>
    /// Where another human player on the caller's own side is. See <see cref="FriendlyPosition"/>
    /// for why this one is off by default and why it refuses a caller with no known coalition.
    /// </summary>
    FriendlyPosition
}

/// <summary>One hostile contact found inside a threat circle, ready to be read out.</summary>
public sealed class ThreatContactReport
{
    public string UnitName { get; init; } = "";
    public string GroupName { get; init; } = "";
    public string Type { get; init; } = "";
    public int BearingDegrees { get; init; }
    public double RangeNm { get; init; }
    public double AltitudeMeters { get; init; }
    /// <summary>Brevity aspect relative to the watched pilot: hot / flanking / beaming / cold.</summary>
    public string Aspect { get; init; } = "";
}

/// <summary>Result of one threat circle sweep.</summary>
public sealed class ThreatScanResult
{
    /// <summary>False when the pilot's aircraft could not be found - a moving circle has no centre then.</summary>
    public bool PilotFound { get; init; }
    /// <summary>False when the mission data could not be read at all (server down, mission not running).</summary>
    public bool DataAvailable { get; init; }
    public string? Error { get; init; }
    /// <summary>Hostile aircraft inside the radius, nearest first.</summary>
    public List<ThreatContactReport> Contacts { get; init; } = new();
}

/// <summary>Where hostile contacts may be taken from (config: DcsIntelContactSource).</summary>
public enum ContactSourceMode
{
    /// <summary>AWACS sensors when they deliver something, plain mission data otherwise.</summary>
    AwacsThenMissionData,
    /// <summary>Strictly what the configured unit detects - "detects nothing" means a clean picture.</summary>
    AwacsOnly,
    /// <summary>Always plain mission data, ignoring any configured AWACS unit.</summary>
    MissionDataOnly
}

/// <summary>What a given mode plus configuration allows on this request.</summary>
public sealed class ContactSourcePlan
{
    public bool TryAwacs { get; init; }
    /// <summary>Whether plain mission data may be used at all (as the primary source or as a fallback).</summary>
    public bool AllowMissionData { get; init; }
    /// <summary>
    /// Set when the configuration leaves no usable source at all (AwacsOnly without a unit name).
    /// The bot then answers "no tactical data" instead of pretending the sky is empty.
    /// </summary>
    public string? ConfigurationError { get; init; }
}

/// <summary>Hostile contacts plus where they came from.</summary>
public sealed class HostileContactSet
{
    public List<AirContact> Contacts { get; init; } = new();
    public string Source { get; init; } = "";
    /// <summary>
    /// False when no source could be consulted at all (misconfiguration, or the AWACS call failed
    /// in a mode without a fallback). An empty contact list with Usable=true genuinely means
    /// "nothing out there"; with Usable=false it means "we don't know".
    /// </summary>
    public bool Usable { get; init; }
}

/// <summary>One hostile aircraft the bot knows about, already reduced to what a radio call needs.</summary>
public sealed class AirContact
{
    public string UnitName { get; init; } = "";
    public string GroupName { get; init; } = "";
    /// <summary>DCS type name, e.g. "MiG-29A".</summary>
    public string Type { get; init; } = "";
    public double Lat { get; init; }
    public double Lon { get; init; }
    public double AltitudeMeters { get; init; }
    /// <summary>True heading the contact is travelling towards, in degrees.</summary>
    public double HeadingDegrees { get; init; }
    /// <summary>Horizontal speed in m/s.</summary>
    public double SpeedMps { get; init; }
}

public sealed class IntelResult
{
    public IntelRequestKind Kind { get; init; }
    /// <summary>The reply to speak, already in radio phrasing. Empty when nothing matched.</summary>
    public string Reply { get; init; } = "";
    /// <summary>One line for the log explaining where the data came from and what was found.</summary>
    public string Detail { get; init; } = "";
    public bool Handled => Kind != IntelRequestKind.None && !string.IsNullOrWhiteSpace(Reply);
}

/// <summary>
/// Answers tactical requests ("bogey dope", "picture", "threat") from live mission data via
/// DCS-gRPC, in radio phrasing ready to be handed to TTS.
///
/// Data source, in this order:
///   1. The sensors of the configured AWACS unit (ControllerService.GetDetectedTargets) - only
///      contacts that unit's group actually detects are reported, which is the realistic option.
///   2. If no AWACS unit is configured, it doesn't exist, or its detection table is empty
///      because the unit is player-controlled (DCS only fills the detection table for AI
///      controllers): all hostile air units straight from the mission ("god's eye"), via
///      CoalitionService.GetGroups + GroupService.GetUnits.
///
/// Read-only throughout: nothing here changes the running mission.
/// </summary>
public class DcsIntelService
{
    private const double MetersPerNauticalMile = 1852.0;
    private const double MetersPerFoot = 0.3048;

    /// <summary>Safety cap on how many hostile groups are queried in the god's-eye fallback.</summary>
    private const int MaxGroupsToQuery = 60;

    private readonly AppConfig _config;

    /// <summary>
    /// Magnetic declination barely changes within one mission/map, but costs an extra RPC every
    /// time - so it's fetched once and then reused for the rest of the session.
    /// </summary>
    private double? _cachedDeclination;

    public DcsIntelService(AppConfig config) => _config = config;

    /// <summary>
    /// Classifies a transcribed transmission. Returns None when it isn't a tactical request, in
    /// which case the normal phrase/Gemini pipeline should handle it as before.
    /// </summary>
    public IntelRequestKind Classify(string transcript) => Classify(transcript, out _);

    /// <summary>
    /// As <see cref="Classify(string)"/>, and also reports which trigger phrase fired. Worth
    /// logging: when the bot answers the wrong request, the phrase that matched - together with
    /// the transcript it matched in - is the whole diagnosis.
    /// </summary>
    public IntelRequestKind Classify(string transcript, out string? matchedTrigger)
    {
        matchedTrigger = null;
        if (string.IsNullOrWhiteSpace(transcript)) return IntelRequestKind.None;

        // Cancel first: "cancel threat circle" contains the start trigger as well, and the more
        // specific intent has to win. The threat circle in turn is checked before the plain
        // "threat check", for the same reason.
        if (_config.DcsIntelThreatCircleEnabled)
        {
            matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelThreatCircleCancelTriggers);
            if (matchedTrigger != null) return IntelRequestKind.ThreatCircleCancel;

            matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelThreatCircleTriggers);
            if (matchedTrigger != null) return IntelRequestKind.ThreatCircleStart;
        }

        // Alpha check first: it is about the pilot, not about contacts, and its phrases share no
        // words with the others.
        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelAlphaCheckTriggers);
        if (matchedTrigger != null) return IntelRequestKind.AlphaCheck;

        // Then "where is somebody" - also about people rather than the enemy, and checked before the
        // contact requests so that a call naming both ("where is Springfield, and bogey dope")
        // answers the more specific question first. Only when the feature is on at all, so its
        // fairly generic phrases ("locate", "where's") cannot swallow anything while it is off.
        if (_config.DcsIntelFriendlyPositionEnabled)
        {
            matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelFriendlyPositionTriggers);
            if (matchedTrigger != null) return IntelRequestKind.FriendlyPosition;
        }

        // Bogey dope before picture: "bogey dope, and picture" should give the more specific answer.
        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelBogeyDopeTriggers);
        if (matchedTrigger != null) return IntelRequestKind.BogeyDope;

        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelPictureTriggers);
        if (matchedTrigger != null) return IntelRequestKind.Picture;

        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelThreatTriggers);
        if (matchedTrigger != null) return IntelRequestKind.Threat;

        matchedTrigger = null;
        return IntelRequestKind.None;
    }

    /// <summary>
    /// Builds the reply for a tactical request. <paramref name="rawPlayerName"/> is the sender's
    /// unmodified SRS name - used to find the DCS unit they're flying, so bearings can be given
    /// from their own aircraft. If that lookup fails, positions are given from the bullseye
    /// instead of failing the request.
    /// </summary>
    public async Task<IntelResult> AnswerAsync(IntelRequestKind kind, string transcript, string rawPlayerName,
        int senderCoalition, CancellationToken cancellationToken = default)
    {
        if (kind == IntelRequestKind.None)
            return new IntelResult();

        try
        {
            // Shared, not per call: a channel owns an HTTP/2 connection, and this runs on every
            // request and every threat circle sweep. See DcsGrpcChannels.
            var channel = DcsGrpcChannels.For(_config.DcsGrpcAddress);
            var headers = DcsGrpcChannels.HeadersFor(_config.DcsGrpcApiKey);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _config.DcsIntelTimeoutSeconds));

            var friendlyCoalition = ResolveFriendlyCoalition(senderCoalition);
            var hostileCoalition = friendlyCoalition == Coalition.Blue ? Coalition.Red : Coalition.Blue;

            // Where do we measure from? Preferably the requesting pilot's own aircraft (BRAA),
            // otherwise their coalition's bullseye (still a useful, standard reference).
            var requester = await FindRequesterUnitAsync(channel, headers, deadline, rawPlayerName, transcript, friendlyCoalition, cancellationToken);
            var bullseye = await GetBullseyeAsync(channel, headers, deadline, friendlyCoalition, cancellationToken);

            // An alpha check is about the pilot, not about the enemy, so it answers here - before
            // the sensor or god's-eye query. That also means it still works when the contact
            // source is unusable, which is exactly when a pilot most wants to know the bot has
            // them on scope.
            if (kind == IntelRequestKind.AlphaCheck)
            {
                if (requester?.Position == null)
                    return new IntelResult
                    {
                        Kind = kind,
                        Reply = _config.DcsIntelNoPositionReply,
                        Detail = "pilot could not be matched to a unit"
                    };

                if (bullseye == null)
                    return new IntelResult
                    {
                        Kind = kind,
                        Reply = _config.DcsIntelUnavailableReply,
                        Detail = "no bullseye for this coalition"
                    };

                var alphaDeclination = _config.DcsIntelMagneticBearings
                    ? await GetDeclinationAsync(channel, headers, deadline, requester.Position.Lat, requester.Position.Lon, cancellationToken)
                    : 0.0;

                return new IntelResult
                {
                    Kind = kind,
                    Reply = BuildAlphaCheck(requester, bullseye, alphaDeclination, _config),
                    Detail = $"unit '{requester.Name}' from bullseye"
                };
            }

            // Where another player is. Answered here for the same reason the alpha check is: it needs
            // nothing from the hostile side, so a broken or empty contact source must not stop it.
            if (kind == IntelRequestKind.FriendlyPosition)
            {
                return await AnswerFriendlyPositionAsync(channel, headers, deadline, transcript,
                    requester, bullseye, friendlyCoalition, senderCoalition, cancellationToken);
            }

            var contactSet = await GetHostileContactsAsync(channel, headers, deadline, hostileCoalition, cancellationToken);

            // No source could be consulted at all - saying "picture clean" here would claim
            // knowledge the bot doesn't have.
            if (!contactSet.Usable)
                return new IntelResult { Kind = kind, Reply = _config.DcsIntelUnavailableReply, Detail = $"source={contactSet.Source} - unusable" };

            var contacts = contactSet.Contacts;
            var sourceLabel = contactSet.Source;

            // Reference point for range filtering and for "nearest": the pilot if known, else bullseye.
            var referenceLat = requester?.Position?.Lat ?? bullseye?.Lat ?? 0;
            var referenceLon = requester?.Position?.Lon ?? bullseye?.Lon ?? 0;
            var haveReference = requester != null || bullseye != null;

            if (haveReference && _config.DcsIntelMaxRangeNm > 0)
            {
                contacts = contacts
                    .Where(c => DistanceNm(referenceLat, referenceLon, c.Lat, c.Lon) <= _config.DcsIntelMaxRangeNm)
                    .ToList();
            }

            var detail = $"source={sourceLabel}, contacts={contacts.Count}, " +
                         $"reference={(requester != null ? $"unit '{requester.Name}'" : bullseye != null ? "bullseye" : "none")}";

            if (contacts.Count == 0)
                return new IntelResult { Kind = kind, Reply = _config.DcsIntelNoContactsReply, Detail = detail + " - reported clean" };

            // Magnetic bearings are what pilots read off their own instruments; true bearings
            // would be off by the map's declination (e.g. ~6 degrees on Caucasus).
            var declination = _config.DcsIntelMagneticBearings
                ? await GetDeclinationAsync(channel, headers, deadline, referenceLat, referenceLon, cancellationToken)
                : 0.0;

            var reply = kind switch
            {
                IntelRequestKind.BogeyDope => BuildBogeyDope(contacts, requester, bullseye, declination, transcript),
                IntelRequestKind.Threat => BuildThreat(contacts, requester, bullseye, declination),
                IntelRequestKind.Picture => BuildPicture(contacts, requester, bullseye, declination),
                _ => ""
            };

            return new IntelResult { Kind = kind, Reply = reply, Detail = detail };
        }
        catch (RpcException ex)
        {
            Logger.Log($"[Intel] DCS-gRPC call failed ({ex.StatusCode}: {ex.Status.Detail}).");
            return new IntelResult { Kind = kind, Reply = _config.DcsIntelUnavailableReply, Detail = $"gRPC error: {ex.StatusCode}" };
        }
        catch (Exception ex)
        {
            Logger.Log($"[Intel] Failed to build the tactical reply: {ex.Message}");
            return new IntelResult { Kind = kind, Reply = _config.DcsIntelUnavailableReply, Detail = $"error: {ex.Message}" };
        }
    }

    /// <summary>
    /// "Punch 1-1, where is Springfield 2-1?" - the position of another human player on the
    /// caller's own side, as a BRAA from the caller's aircraft when that is known and from the
    /// bullseye otherwise.
    /// </summary>
    /// <remarks>
    /// The coalition guard comes first and is absolute: everywhere else an unknown sender coalition
    /// falls back to the bot's own side, which is harmless when the answer concerns the enemy. Here
    /// it would let somebody in a spectator slot ask where the players on the bot's side are.
    /// </remarks>
    private async Task<IntelResult> AnswerFriendlyPositionAsync(GrpcChannel channel, Metadata headers,
        DateTime deadline, string transcript, Unit? requester, Position? bullseye,
        Coalition friendlyCoalition, int senderCoalition, CancellationToken cancellationToken)
    {
        var kind = IntelRequestKind.FriendlyPosition;

        if (!FriendlyPosition.CoalitionIsKnown(senderCoalition))
            return new IntelResult
            {
                Kind = kind,
                Reply = _config.DcsIntelFriendlyNoCoalitionReply,
                Detail = "sender coalition unknown - refused"
            };

        var request = FriendlyPosition.Parse(_config, transcript);
        if (request == null)
            return new IntelResult { Kind = kind, Reply = _config.DcsIntelFriendlyNoNameReply, Detail = "not a position request" };

        if (!request.NamesSomebody)
            return new IntelResult
            {
                Kind = kind,
                Reply = _config.DcsIntelFriendlyNoNameReply,
                Detail = $"\"{request.Trigger}\" with no aircraft named after it"
            };

        // Human players on the caller's own side, and nothing else. AI units are deliberately not
        // considered - see FriendlyPosition for why.
        var response = await new CoalitionService.CoalitionServiceClient(channel)
            .GetPlayerUnitsAsync(new GetPlayerUnitsRequest { Coalition = friendlyCoalition }, headers, deadline, cancellationToken);

        // Matched against the text AFTER the trigger only. That is what makes a transmission naming
        // two pilots answerable at all, without touching PilotNames' refusal to guess between them.
        var target = PilotNames.FindMatchInTranscript(response.Units, request.TargetText,
            u => u.PlayerName, u => u.Callsign, u => u.Name);

        if (!target.Found || target.Match?.Position == null)
            return new IntelResult
            {
                Kind = kind,
                Reply = _config.DcsIntelFriendlyNotFoundReply
                    .Replace("{pilot}", PilotNames.ForSpeech(request.TargetText)),
                Detail = $"\"{request.TargetText}\" not matched ({target.Explanation}), " +
                         $"{response.Units.Count} player unit(s) on {friendlyCoalition}"
            };

        // Never answer a request about the caller themselves with a BRAA to themselves - that is an
        // alpha check, and reads as nonsense here ("bearing 000, 0 miles").
        if (requester != null && requester.Name == target.Match.Name)
            return new IntelResult
            {
                Kind = kind,
                Reply = BuildAlphaCheck(requester, bullseye ?? new Position(),
                    bullseye == null ? 0 : await GetDeclinationAsync(channel, headers, deadline,
                        requester.Position.Lat, requester.Position.Lon, cancellationToken),
                    _config),
                Detail = $"caller asked about themselves ('{target.Match.Name}') - answered as an alpha check"
            };

        var referenceLat = requester?.Position?.Lat ?? bullseye?.Lat ?? 0;
        var referenceLon = requester?.Position?.Lon ?? bullseye?.Lon ?? 0;

        if (requester?.Position == null && bullseye == null)
            return new IntelResult
            {
                Kind = kind,
                Reply = _config.DcsIntelUnavailableReply,
                Detail = "neither the caller's aircraft nor a bullseye could be located"
            };

        var declination = _config.DcsIntelMagneticBearings
            ? await GetDeclinationAsync(channel, headers, deadline, referenceLat, referenceLon, cancellationToken)
            : 0.0;

        return new IntelResult
        {
            Kind = kind,
            Reply = BuildFriendlyPosition(target.Match, requester, bullseye, declination, _config),
            Detail = $"unit '{target.Match.Name}' via {target.Rule}, " +
                     $"reference={(requester?.Position != null ? "caller's aircraft" : "bullseye")}"
        };
    }

    /// <summary>
    /// Whether the bot can currently see the caller on scope - the one piece of mission data a
    /// radio check needs. Deliberately the cheapest possible question: one unit lookup, no contact
    /// query, no declination, so answering "loud and clear" is never delayed by the sensor side
    /// of the mission being slow or broken.
    /// </summary>
    /// <remarks>
    /// Any failure answers <see cref="RadioCheck.ScopeState.Unknown"/> rather than
    /// <c>NoContact</c>. A pilot whose radio works should not be told the bot cannot see them
    /// because DCS-gRPC happened to time out; the reply then simply leaves radar out of it.
    /// </remarks>
    /// <remarks>Virtual so the radio-check wiring can be exercised without a running mission.</remarks>
    public virtual async Task<RadioCheck.ScopeState> LookUpScopeStateAsync(string rawPlayerName, string? transcript,
        int senderCoalition, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = DcsGrpcChannels.For(_config.DcsGrpcAddress);
            var headers = DcsGrpcChannels.HeadersFor(_config.DcsGrpcApiKey);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _config.DcsIntelTimeoutSeconds));

            var friendlyCoalition = ResolveFriendlyCoalition(senderCoalition);
            var requester = await FindRequesterUnitAsync(channel, headers, deadline, rawPlayerName, transcript,
                friendlyCoalition, cancellationToken);

            return requester != null ? RadioCheck.ScopeState.Contact : RadioCheck.ScopeState.NoContact;
        }
        catch (Exception ex)
        {
            Logger.Debug($"[RadioCheck] Could not check whether the caller is on scope: {ex.Message}");
            return RadioCheck.ScopeState.Unknown;
        }
    }

    // ---------------------------------------------------------------------------------
    // Threat circle
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// One sweep of a threat circle: locates the watched pilot's aircraft and returns every
    /// hostile aircraft currently inside <paramref name="radiusNm"/> around it, nearest first.
    /// The circle moves with the pilot, so the centre is looked up fresh on every sweep.
    /// </summary>
    /// <remarks>Virtual so the sweep logic in <see cref="ThreatCircleService"/> can be exercised
    /// against scripted scan results without a running mission.</remarks>
    public virtual async Task<ThreatScanResult> ScanForThreatsAsync(string rawPlayerName, int senderCoalition,
        double radiusNm, CancellationToken cancellationToken = default)
    {
        try
        {
            // Shared, not per call: a channel owns an HTTP/2 connection, and this runs on every
            // request and every threat circle sweep. See DcsGrpcChannels.
            var channel = DcsGrpcChannels.For(_config.DcsGrpcAddress);
            var headers = DcsGrpcChannels.HeadersFor(_config.DcsGrpcApiKey);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _config.DcsIntelTimeoutSeconds));

            var friendlyCoalition = ResolveFriendlyCoalition(senderCoalition);
            var hostileCoalition = friendlyCoalition == Coalition.Blue ? Coalition.Red : Coalition.Blue;

            var pilot = await FindRequesterUnitAsync(channel, headers, deadline, rawPlayerName, friendlyCoalition, cancellationToken);
            if (pilot?.Position == null)
                return new ThreatScanResult { DataAvailable = true, PilotFound = false };

            var contactSet = await GetHostileContactsAsync(channel, headers, deadline, hostileCoalition, cancellationToken);

            // Treat an unusable source like a failed sweep: the circle stays armed and tries again,
            // rather than quietly implying the airspace is clear.
            if (!contactSet.Usable)
                return new ThreatScanResult { DataAvailable = false, Error = $"no usable contact source ({contactSet.Source})" };

            var contacts = contactSet.Contacts;

            var centreLat = pilot.Position.Lat;
            var centreLon = pilot.Position.Lon;

            var declination = _config.DcsIntelMagneticBearings
                ? await GetDeclinationAsync(channel, headers, deadline, centreLat, centreLon, cancellationToken)
                : 0.0;

            var inside = contacts
                .Select(c => new { Contact = c, Range = DistanceNm(centreLat, centreLon, c.Lat, c.Lon) })
                .Where(x => x.Range <= radiusNm)
                .OrderBy(x => x.Range)
                .Select(x => new ThreatContactReport
                {
                    UnitName = x.Contact.UnitName,
                    GroupName = x.Contact.GroupName,
                    Type = x.Contact.Type,
                    BearingDegrees = MagneticBearing(centreLat, centreLon, x.Contact.Lat, x.Contact.Lon, declination),
                    RangeNm = x.Range,
                    AltitudeMeters = x.Contact.AltitudeMeters,
                    Aspect = Aspect(centreLat, centreLon, x.Contact)
                })
                .ToList();

            return new ThreatScanResult { DataAvailable = true, PilotFound = true, Contacts = inside };
        }
        catch (RpcException ex)
        {
            return new ThreatScanResult { DataAvailable = false, Error = $"{ex.StatusCode}: {ex.Status.Detail}" };
        }
        catch (Exception ex)
        {
            return new ThreatScanResult { DataAvailable = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// The radius asked for in the transmission ("threat circle forty miles"), as digits or as
    /// spoken words. Falls back to the configured default when no usable number is in the text,
    /// and is always clamped to the configured maximum.
    /// </summary>
    public double ParseRequestedRadiusNm(string transcript)
    {
        var radius = ExtractNumber(transcript) ?? _config.DcsIntelThreatCircleDefaultRadiusNm;
        var max = Math.Max(1, _config.DcsIntelThreatCircleMaxRadiusNm);
        return Math.Clamp(radius, 1, max);
    }

    /// <summary>
    /// Pulls the first number out of a transcription - either as digits ("40") or spelled out
    /// ("forty", "twenty five", "one hundred"), because a speech-to-text result may contain
    /// either form depending on the engine and how the pilot said it.
    /// </summary>
    internal static double? ExtractNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lower = text.ToLowerInvariant();

        var digits = Regex.Match(lower, @"\b(\d{1,3})\b");
        if (digits.Success && double.TryParse(digits.Groups[1].Value, out var parsed))
            return parsed;

        var words = new Dictionary<string, int>
        {
            ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
            ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
            ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
            ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50,
            ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
        };

        var tokens = Regex.Split(lower, @"[^a-z]+").Where(t => t.Length > 0).ToList();
        int? total = null;
        var sawNumber = false;

        foreach (var token in tokens)
        {
            if (token == "hundred" && total.HasValue)
            {
                total *= 100;
                continue;
            }

            if (!words.TryGetValue(token, out var value))
            {
                // Stop at the first non-number word once a number has started, so "forty miles
                // and picture" doesn't accidentally pick up a second number later in the sentence.
                if (sawNumber) break;
                continue;
            }

            sawNumber = true;
            // "twenty five" -> 25, but "forty forty" is not a thing: only a tens value followed by
            // a ones value combines.
            total = total is >= 20 && total % 10 == 0 && value < 10 ? total + value : (total ?? 0) + value;
        }

        return total;
    }

    /// <summary>Spoken confirmation that a threat circle is now running.</summary>
    public string FormatThreatCircleConfirmation(double radiusNm, int contactsAlreadyInside)
    {
        var radius = _config.DcsIntelSlowSpeech ? NumberToWords((int)Math.Round(radiusNm)) : ((int)Math.Round(radiusNm)).ToString();
        var text = $"Threat circle active, {radius} miles.";

        if (contactsAlreadyInside > 0)
            text += $" {char.ToUpperInvariant(SpeakCount(contactsAlreadyInside)[0])}{SpeakCount(contactsAlreadyInside)[1..]} " +
                    $"contact{(contactsAlreadyInside == 1 ? "" : "s")} already inside.";

        return text;
    }

    /// <summary>Spoken warning for one contact that has just entered a threat circle.</summary>
    public string FormatThreatAlert(ThreatContactReport contact)
    {
        var type = TypeOf(contact.Type);
        var typePart = string.IsNullOrEmpty(type) ? "" : $", type {type}";

        return $"Threat, bearing {SpeakBearing(contact.BearingDegrees, BearingSeparator)}, " +
               $"{SpeakRange((int)Math.Round(contact.RangeNm))} miles, {SpeakAltitude(contact.AltitudeMeters)}, " +
               $"{contact.Aspect}{typePart}.";
    }

    // ---------------------------------------------------------------------------------
    // Data gathering
    // ---------------------------------------------------------------------------------

    internal Coalition ResolveFriendlyCoalition(int senderCoalition) => senderCoalition switch
    {
        1 => Coalition.Red,
        2 => Coalition.Blue,
        // Sender coalition unknown (spectator or not in the SRS client list): fall back to the
        // bot's own side, which is what its replies represent anyway.
        _ => _config.Coalition == 1 ? Coalition.Red : Coalition.Blue
    };

    /// <summary>
    /// Finds the DCS unit the requesting pilot is flying. SRS names and DCS player names usually
    /// match exactly, but not always - so this also tries the part after the callsign separator
    /// ("Enfield 1-1 | neodym" -> "neodym") and finally the unit's own callsign/name.
    /// </summary>
    internal Task<Unit?> FindRequesterUnitAsync(GrpcChannel channel, Metadata headers, DateTime deadline,
        string rawPlayerName, Coalition friendly, CancellationToken token) =>
        FindRequesterUnitAsync(channel, headers, deadline, rawPlayerName, null, friendly, token);

    /// <summary>
    /// As above, but also accepts the transcript. A pilot who says "active runway for Punch 1-1"
    /// has named themselves, and that is a second, independent way to find their aircraft when
    /// their SRS name and their DCS name don't line up - which is the usual reason a BRAA call
    /// silently turns into a bullseye call.
    /// </summary>
    internal async Task<Unit?> FindRequesterUnitAsync(GrpcChannel channel, Metadata headers, DateTime deadline,
        string rawPlayerName, string? transcript, Coalition friendly, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(rawPlayerName) && string.IsNullOrWhiteSpace(transcript)) return null;

        var response = await new CoalitionService.CoalitionServiceClient(channel)
            .GetPlayerUnitsAsync(new GetPlayerUnitsRequest { Coalition = friendly }, headers, deadline, token);

        var units = response.Units;
        if (units.Count == 0) return null;

        // Tolerant matching: the SRS name and the DCS player name are typed in different places
        // and rarely agree character for character - squadron tags, "1-1" against "11", spaces.
        // PilotNames tries the strict rules first and only then loosens, refusing to guess when
        // more than one pilot would fit. Fields in order of how much they can be trusted.
        var match = PilotNames.FindMatch(
            units,
            rawPlayerName,
            _config.PlayerNameCallsignSeparator,
            u => u.PlayerName,
            u => u.Callsign,
            u => u.Name);

        // The SRS name didn't lead anywhere, so try the transmission itself: pilots routinely say
        // their own callsign, and that costs nothing to check.
        if (!match.Found && !string.IsNullOrWhiteSpace(transcript))
        {
            var spoken = PilotNames.FindMatchInTranscript(units, transcript,
                u => u.PlayerName, u => u.Callsign, u => u.Name);

            if (spoken.Found)
            {
                Logger.Debug($"[Intel] Pilot \"{rawPlayerName}\" identified from the transmission instead " +
                             $"({spoken.Explanation}).");
                return spoken.Match;
            }
        }

        // Logged either way: a failure here is why a BRAA call silently becomes a bullseye call,
        // and until now it left no trace at all.
        if (match.Found)
            Logger.Debug($"[Intel] Pilot \"{rawPlayerName}\" matched unit \"{match.Match!.Name}\" ({match.Rule}: {match.Explanation}).");
        else
            Logger.Debug($"[Intel] Pilot \"{rawPlayerName}\" could not be matched to a unit - {match.Explanation}. " +
                         "Replies will use the bullseye format.");

        return match.Match;
    }

    private static async Task<Position?> GetBullseyeAsync(GrpcChannel channel, Metadata headers, DateTime deadline,
        Coalition friendly, CancellationToken token)
    {
        try
        {
            var response = await new CoalitionService.CoalitionServiceClient(channel)
                .GetBullseyeAsync(new GetBullseyeRequest { Coalition = friendly }, headers, deadline, token);
            return response.Position;
        }
        catch (RpcException)
        {
            // Not every mission defines a usable bullseye - that's not fatal, BRAA from the
            // pilot's own aircraft still works.
            return null;
        }
    }

    /// <summary>
    /// Hostile air contacts, from the AWACS unit's detection table when possible, otherwise
    /// straight from the mission. Returns the contacts plus a label describing which path was used.
    /// </summary>
    private async Task<HostileContactSet> GetHostileContactsAsync(GrpcChannel channel,
        Metadata headers, DateTime deadline, Coalition hostile, CancellationToken token)
    {
        var awacsUnit = (_config.DcsIntelAwacsUnitName ?? "").Trim();
        var plan = PlanContactSource(ParseContactSourceMode(_config.DcsIntelContactSource), awacsUnit);

        if (plan.ConfigurationError != null)
        {
            Logger.Log($"[Intel] {plan.ConfigurationError}");
            return new HostileContactSet { Usable = false, Source = "none (configuration)" };
        }

        if (plan.TryAwacs)
        {
            try
            {
                var response = await new ControllerService.ControllerServiceClient(channel)
                    .GetDetectedTargetsAsync(new GetDetectedTargetsRequest
                    {
                        UnitName = awacsUnit,
                        IncludeObject = true
                    }, headers, deadline, token);

                var detected = response.Contacts
                    .Where(c => c.Unit != null && c.Unit.Coalition == hostile && IsAircraft(c.Unit))
                    .Select(c => ToContact(c.Unit))
                    .ToList();

                // An empty detection table is normal for a player-flown AWACS (DCS only fills it
                // for AI controllers) and indistinguishable from "genuinely sees nothing". Which
                // of the two the bot assumes is exactly what the source mode decides.
                if (detected.Count > 0 || !plan.AllowMissionData)
                {
                    if (detected.Count == 0)
                        Logger.Debug($"[Intel] '{awacsUnit}' detects nothing - reported as a clean picture (AwacsOnly).");

                    return new HostileContactSet
                    {
                        Contacts = detected,
                        Source = $"AWACS '{awacsUnit}' sensors",
                        Usable = true
                    };
                }

                Logger.Debug($"[Intel] '{awacsUnit}' reported no contacts - using mission data instead.");
            }
            catch (RpcException ex)
            {
                // Without a fallback this is a hard failure: answering "picture clean" because the
                // sensor call broke would be the dangerous kind of wrong.
                if (!plan.AllowMissionData)
                {
                    Logger.Log($"[Intel] Could not read '{awacsUnit}' sensors ({ex.StatusCode}) and mission data is disabled " +
                               "(DcsIntelContactSource=AwacsOnly) - no tactical data. Check DcsIntelAwacsUnitName against the mission editor.");
                    return new HostileContactSet { Usable = false, Source = $"AWACS '{awacsUnit}' sensors (failed)" };
                }

                Logger.Log($"[Intel] Could not read '{awacsUnit}' sensors ({ex.StatusCode}) - " +
                           "using mission data instead. Check DcsIntelAwacsUnitName against the mission editor.");
            }
        }

        return new HostileContactSet
        {
            Contacts = await GetAllHostileAircraftAsync(channel, headers, deadline, hostile, token),
            Source = "mission data (god's eye)",
            Usable = true
        };
    }

    /// <summary>Reads the configured source mode, falling back to the default for anything unknown.</summary>
    internal static ContactSourceMode ParseContactSourceMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "awacsonly" => ContactSourceMode.AwacsOnly,
            "missiondataonly" => ContactSourceMode.MissionDataOnly,
            _ => ContactSourceMode.AwacsThenMissionData
        };

    /// <summary>
    /// Works out which sources a request may use. Kept pure and separate from the RPC calls so the
    /// rules - especially "god's eye disabled but no AWACS configured" - can be verified directly.
    /// </summary>
    internal static ContactSourcePlan PlanContactSource(ContactSourceMode mode, string awacsUnitName)
    {
        var hasUnit = !string.IsNullOrWhiteSpace(awacsUnitName);

        return mode switch
        {
            ContactSourceMode.MissionDataOnly => new ContactSourcePlan { TryAwacs = false, AllowMissionData = true },

            ContactSourceMode.AwacsOnly when !hasUnit => new ContactSourcePlan
            {
                TryAwacs = false,
                AllowMissionData = false,
                ConfigurationError = "DcsIntelContactSource is AwacsOnly but DcsIntelAwacsUnitName is empty - " +
                                     "there is no source left to answer tactical requests from. Name a unit, or pick another source mode."
            },

            ContactSourceMode.AwacsOnly => new ContactSourcePlan { TryAwacs = true, AllowMissionData = false },

            // Default: sensors when they deliver, mission data otherwise.
            _ => new ContactSourcePlan { TryAwacs = hasUnit, AllowMissionData = true }
        };
    }

    private async Task<List<AirContact>> GetAllHostileAircraftAsync(GrpcChannel channel, Metadata headers,
        DateTime deadline, Coalition hostile, CancellationToken token)
    {
        var coalitionClient = new CoalitionService.CoalitionServiceClient(channel);
        var groupClient = new GroupService.GroupServiceClient(channel);

        var categories = _config.DcsIntelIncludeHelicopters
            ? new[] { GroupCategory.Airplane, GroupCategory.Helicopter }
            : new[] { GroupCategory.Airplane };

        var groupNames = new List<string>();
        foreach (var category in categories)
        {
            var groups = await coalitionClient.GetGroupsAsync(
                new GetGroupsRequest { Coalition = hostile, Category = category }, headers, deadline, token);
            groupNames.AddRange(groups.Groups.Select(g => g.Name));
        }

        if (groupNames.Count > MaxGroupsToQuery)
        {
            Logger.Debug($"[Intel] {groupNames.Count} hostile air groups in the mission - only the first {MaxGroupsToQuery} are queried.");
            groupNames = groupNames.Take(MaxGroupsToQuery).ToList();
        }

        // GetGroups only returns group metadata, so each group's units have to be fetched
        // separately - done concurrently to keep the whole request within a few hundred ms.
        var tasks = groupNames.Select(async name =>
        {
            try
            {
                var units = await groupClient.GetUnitsAsync(
                    new GetUnitsRequest { GroupName = name, Active = true }, headers, deadline, token);
                return units.Units.Where(IsAircraft).Select(ToContact).ToList();
            }
            catch (RpcException)
            {
                // A group can disappear between listing and querying (shot down, despawned).
                return new List<AirContact>();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    private static bool IsAircraft(Unit unit) =>
        unit.Group?.Category is GroupCategory.Airplane or GroupCategory.Helicopter;

    private static AirContact ToContact(Unit unit) => new()
    {
        UnitName = unit.Name,
        GroupName = unit.Group?.Name ?? unit.Name,
        Type = unit.Type,
        Lat = unit.Position?.Lat ?? 0,
        Lon = unit.Position?.Lon ?? 0,
        AltitudeMeters = unit.Position?.Alt ?? 0,
        HeadingDegrees = unit.Velocity?.Heading ?? unit.Orientation?.Heading ?? 0,
        SpeedMps = unit.Velocity?.Speed ?? 0
    };

    internal async Task<double> GetDeclinationAsync(GrpcChannel channel, Metadata headers, DateTime deadline,
        double lat, double lon, CancellationToken token)
    {
        if (_cachedDeclination.HasValue) return _cachedDeclination.Value;

        try
        {
            var response = await new CustomService.CustomServiceClient(channel).GetMagneticDeclinationAsync(
                new GetMagneticDeclinationRequest { Lat = lat, Lon = lon, Alt = 0 }, headers, deadline, token);
            _cachedDeclination = response.Declination;
            return _cachedDeclination.Value;
        }
        catch (RpcException)
        {
            // Older/limited servers may not offer this call - true bearings are still usable,
            // just a few degrees off what the pilot reads on their HSI.
            _cachedDeclination = 0;
            return 0;
        }
    }

    // ---------------------------------------------------------------------------------
    // Radio phrasing
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Comma between the digits of a bearing when slow speech is on. A comma is the one pause
    /// marker every TTS engine honours, and it's what turns a rushed "zeroninerzero" into three
    /// distinguishable digits.
    /// </summary>
    private string BearingSeparator => _config.DcsIntelSlowSpeech ? ", " : " ";

    /// <summary>Ranges as words ("thirty five") when slow speech is on, otherwise as digits.</summary>
    private string SpeakRange(int nauticalMiles) =>
        _config.DcsIntelSlowSpeech ? NumberToWords(nauticalMiles) : nauticalMiles.ToString();

    private string SpeakAltitude(double altitudeMeters) =>
        AltitudeThousands(altitudeMeters, _config.DcsIntelSlowSpeech);

    /// <summary>The contact's aircraft type, if it should be announced at all.</summary>
    private string TypeOf(string type) =>
        _config.DcsIntelSayContactType ? SpeakType(type) : "";

    private string BuildBogeyDope(List<AirContact> contacts, Unit? requester, Position? bullseye, double declination, string transcript)
    {
        // "bullseye" anywhere in the request forces the bullseye format even when BRAA would be
        // possible - that's a common way to ask for it explicitly.
        var forceBullseye = TriggerMatcher.MatchesAny(transcript, _config.DcsIntelBullseyeTriggers);

        var (fromLat, fromLon) = requester != null && !forceBullseye
            ? (requester.Position.Lat, requester.Position.Lon)
            : (bullseye?.Lat ?? 0, bullseye?.Lon ?? 0);

        var nearest = contacts.OrderBy(c => DistanceNm(fromLat, fromLon, c.Lat, c.Lon)).First();
        var rangeNm = (int)Math.Round(DistanceNm(fromLat, fromLon, nearest.Lat, nearest.Lon));
        var bearing = MagneticBearing(fromLat, fromLon, nearest.Lat, nearest.Lon, declination);
        var altitude = SpeakAltitude(nearest.AltitudeMeters);

        // How many aircraft are flying with it - "group of three" matters more to the pilot than
        // the individual unit name.
        var groupSize = contacts.Count(c => c.GroupName == nearest.GroupName);
        var groupPart = groupSize > 1 ? $", group of {SpeakCount(groupSize)}" : ", single";

        var useBraa = requester != null && !forceBullseye;
        var reference = useBraa
            ? $"bearing {SpeakBearing(bearing, BearingSeparator)}"
            : $"bullseye {SpeakBearing(bearing, BearingSeparator)}";

        var geometry = useBraa
            ? $", {Aspect(requester!.Position.Lat, requester.Position.Lon, nearest)}"
            : $", tracking {SpeakBearing((int)Math.Round(NormalizeBearing(nearest.HeadingDegrees - declination)), BearingSeparator)}";

        var type = TypeOf(nearest.Type);
        var typePart = string.IsNullOrEmpty(type) ? "" : $", type {type}";

        return $"Bogey, {reference}, {SpeakRange(rangeNm)} miles, {altitude}{geometry}{groupPart}{typePart}.";
    }

    private string BuildThreat(List<AirContact> contacts, Unit? requester, Position? bullseye, double declination)
    {
        var (fromLat, fromLon) = requester != null
            ? (requester.Position.Lat, requester.Position.Lon)
            : (bullseye?.Lat ?? 0, bullseye?.Lon ?? 0);

        var nearest = contacts.OrderBy(c => DistanceNm(fromLat, fromLon, c.Lat, c.Lon)).First();
        var rangeNm = (int)Math.Round(DistanceNm(fromLat, fromLon, nearest.Lat, nearest.Lon));
        var bearing = MagneticBearing(fromLat, fromLon, nearest.Lat, nearest.Lon, declination);

        var prefix = requester != null ? "Nearest contact" : "Nearest contact from bullseye";
        var type = TypeOf(nearest.Type);
        var typePart = string.IsNullOrEmpty(type) ? "" : $", type {type}";

        return $"{prefix} {SpeakBearing(bearing, BearingSeparator)} at {SpeakRange(rangeNm)} miles, " +
               $"{SpeakAltitude(nearest.AltitudeMeters)}{typePart}.";
    }

    private string BuildPicture(List<AirContact> contacts, Unit? requester, Position? bullseye, double declination)
    {
        // Positions in a picture are given from the bullseye by convention; without a bullseye
        // the pilot's own position is the next best reference.
        var useBullseye = bullseye != null;
        var (fromLat, fromLon) = useBullseye
            ? (bullseye!.Lat, bullseye.Lon)
            : (requester?.Position.Lat ?? 0, requester?.Position.Lon ?? 0);

        // Sort by what matters to the pilot: closest to them first, even when the positions
        // themselves are then given from the bullseye.
        var (sortLat, sortLon) = requester != null
            ? (requester.Position.Lat, requester.Position.Lon)
            : (fromLat, fromLon);

        var groups = contacts
            .GroupBy(c => c.GroupName)
            .Select(g => new
            {
                Count = g.Count(),
                Lat = g.Average(c => c.Lat),
                Lon = g.Average(c => c.Lon),
                Altitude = g.Max(c => c.AltitudeMeters),
                // A group usually flies one type; if it doesn't, the most common one is the
                // useful thing to call rather than whichever unit happened to be first.
                Type = g.GroupBy(c => c.Type).OrderByDescending(t => t.Count()).First().Key,
                MixedTypes = g.Select(c => c.Type).Distinct().Count() > 1
            })
            .OrderBy(g => DistanceNm(sortLat, sortLon, g.Lat, g.Lon))
            .Take(Math.Max(1, _config.DcsIntelMaxGroups))
            .ToList();

        var totalGroups = contacts.Select(c => c.GroupName).Distinct().Count();
        var parts = new List<string> { $"Picture: {SpeakCount(totalGroups)} group{(totalGroups == 1 ? "" : "s")}." };

        var labels = new[] { "Lead group", "Second group", "Third group", "Fourth group", "Fifth group", "Sixth group" };
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var label = i < labels.Length ? labels[i] : $"Group {i + 1}";
            var bearing = MagneticBearing(fromLat, fromLon, g.Lat, g.Lon, declination);
            var rangeNm = (int)Math.Round(DistanceNm(fromLat, fromLon, g.Lat, g.Lon));
            var reference = useBullseye ? "bullseye" : "bearing";

            var type = TypeOf(g.Type);
            var typePart = string.IsNullOrEmpty(type) ? "" : g.MixedTypes ? $", mixed, lead {type}" : $", {type}";

            parts.Add($"{label}, {reference} {SpeakBearing(bearing, BearingSeparator)}, for {SpeakRange(rangeNm)} miles, " +
                      $"{SpeakAltitude(g.Altitude)}, {SpeakCount(g.Count)} contact{(g.Count == 1 ? "" : "s")}{typePart}.");
        }

        if (totalGroups > groups.Count)
            parts.Add($"{SpeakCount(totalGroups - groups.Count)} further group{(totalGroups - groups.Count == 1 ? "" : "s")} not reported.");

        return string.Join(" ", parts);
    }

    // ---------------------------------------------------------------------------------
    // Geometry and speech helpers (internal so they can be unit-tested)
    // ---------------------------------------------------------------------------------

    /// <summary>Great-circle distance in nautical miles.</summary>
    /// <summary>
    /// "Punch 1-1, alpha check, bullseye zero one zero, one two two." Bearing and range measured
    /// FROM the bullseye TO the aircraft, which is the direction a bullseye call always runs.
    /// </summary>
    internal static string BuildAlphaCheck(Unit requester, Position bullseye, double declination, AppConfig config)
    {
        var bearing = config.DcsIntelMagneticBearings
            ? MagneticBearing(bullseye.Lat, bullseye.Lon, requester.Position.Lat, requester.Position.Lon, declination)
            : (int)Math.Round(TrueBearing(bullseye.Lat, bullseye.Lon, requester.Position.Lat, requester.Position.Lon));

        var range = DistanceNm(bullseye.Lat, bullseye.Lon, requester.Position.Lat, requester.Position.Lon);

        var spokenBearing = config.DcsIntelSlowSpeech
            ? SpeakBearing(bearing, config.DcsIntelSlowSpeech ? ", " : " ")
            : BearingText(bearing);

        var spokenRange = config.DcsIntelSlowSpeech
            ? SpeakCount((int)Math.Round(range))
            : ((int)Math.Round(range)).ToString();

        var altitude = AltitudeThousands(requester.Position.Alt, config.DcsIntelSlowSpeech);

        return $"Alpha check, bullseye {spokenBearing}, {spokenRange} miles, {altitude}.";
    }

    /// <summary>
    /// "Springfield 2-1, bearing zero four zero, twenty five miles, eighteen thousand, heading zero
    /// niner zero." Measured FROM the caller's aircraft when that is known, which is what somebody
    /// trying to rejoin needs - otherwise from the bullseye, the same fallback a bogey dope uses.
    /// </summary>
    /// <remarks>
    /// The target's own heading is included because where they are is only half of a rejoin. Aspect
    /// is deliberately not: that describes whether a contact is closing on you, which is a question
    /// about an enemy and would be nonsense about a wingman.
    /// </remarks>
    internal static string BuildFriendlyPosition(Unit target, Unit? requester, Position? bullseye,
        double declination, AppConfig config)
    {
        // The instance BearingSeparator isn't reachable from a static method; same rule, spelled out.
        var separator = config.DcsIntelSlowSpeech ? ", " : " ";

        var fromCaller = requester?.Position != null;
        var fromLat = fromCaller ? requester!.Position.Lat : bullseye!.Lat;
        var fromLon = fromCaller ? requester!.Position.Lon : bullseye!.Lon;

        var bearing = config.DcsIntelMagneticBearings
            ? MagneticBearing(fromLat, fromLon, target.Position.Lat, target.Position.Lon, declination)
            : (int)Math.Round(TrueBearing(fromLat, fromLon, target.Position.Lat, target.Position.Lon));

        var range = DistanceNm(fromLat, fromLon, target.Position.Lat, target.Position.Lon);

        var spokenBearing = config.DcsIntelSlowSpeech
            ? SpeakBearing(bearing, separator)
            : BearingText(bearing);

        var spokenRange = config.DcsIntelSlowSpeech
            ? SpeakCount((int)Math.Round(range))
            : ((int)Math.Round(range)).ToString();

        var altitude = AltitudeThousands(target.Position.Alt, config.DcsIntelSlowSpeech);

        // The flight callsign DCS knows them by reads better on the radio than a player name with
        // squadron tags in it; ForSpeech turns "2-1" into "two one" rather than "twenty one".
        var name = PilotNames.ForSpeech(!string.IsNullOrWhiteSpace(target.Callsign)
            ? target.Callsign
            : PilotNames.DisplayCallsign(target.PlayerName, config.PlayerNameCallsignSeparator));

        var reference = fromCaller ? "bearing" : "bullseye";

        var heading = "";
        if (config.DcsIntelFriendlySayHeading)
        {
            var targetHeading = target.Velocity?.Heading ?? target.Orientation?.Heading ?? 0;
            var magnetic = (int)Math.Round(NormalizeBearing(targetHeading - declination));
            heading = config.DcsIntelSlowSpeech
                ? $", heading {SpeakBearing(magnetic, separator)}"
                : $", heading {BearingText(magnetic)}";
        }

        return $"{name}, {reference} {spokenBearing}, {spokenRange} miles, {altitude}{heading}.";
    }

    internal static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6371000.0;
        var φ1 = ToRadians(lat1);
        var φ2 = ToRadians(lat2);
        var dφ = ToRadians(lat2 - lat1);
        var dλ = ToRadians(lon2 - lon1);

        var a = Math.Sin(dφ / 2) * Math.Sin(dφ / 2) +
                Math.Cos(φ1) * Math.Cos(φ2) * Math.Sin(dλ / 2) * Math.Sin(dλ / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMeters * c / MetersPerNauticalMile;
    }

    /// <summary>Initial true bearing from point 1 to point 2, in degrees (0-359).</summary>
    internal static double TrueBearing(double lat1, double lon1, double lat2, double lon2)
    {
        var φ1 = ToRadians(lat1);
        var φ2 = ToRadians(lat2);
        var dλ = ToRadians(lon2 - lon1);

        var y = Math.Sin(dλ) * Math.Cos(φ2);
        var x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(dλ);
        return NormalizeBearing(ToDegrees(Math.Atan2(y, x)));
    }

    /// <summary>True bearing corrected by the magnetic declination, as read off a cockpit instrument.</summary>
    internal static int MagneticBearing(double lat1, double lon1, double lat2, double lon2, double declination) =>
        (int)Math.Round(NormalizeBearing(TrueBearing(lat1, lon1, lat2, lon2) - declination)) % 360;

    internal static double NormalizeBearing(double degrees)
    {
        var d = degrees % 360;
        return d < 0 ? d + 360 : d;
    }

    /// <summary>
    /// Standard brevity aspect of a contact relative to the fighter, derived from the angle
    /// between the contact's heading and the bearing back to the fighter: HOT (nose on, within
    /// 30 degrees), FLANKING (30-60), BEAM (60-120), COLD (more than 120, i.e. running away).
    /// </summary>
    internal static string Aspect(double fighterLat, double fighterLon, AirContact contact)
    {
        var bearingContactToFighter = TrueBearing(contact.Lat, contact.Lon, fighterLat, fighterLon);
        var off = Math.Abs(NormalizeBearing(contact.HeadingDegrees - bearingContactToFighter));
        if (off > 180) off = 360 - off;

        return off switch
        {
            <= 30 => "hot",
            <= 60 => "flanking",
            <= 120 => "beaming",
            _ => "cold"
        };
    }

    /// <summary>
    /// Altitude in the usual "22 thousand" radio form; below 1000 ft it's given in hundreds.
    /// With <paramref name="spellOut"/> the number is written as words, which keeps TTS from
    /// rattling it off as fast as it reads digits.
    /// </summary>
    internal static string AltitudeThousands(double altitudeMeters, bool spellOut = false)
    {
        var feet = altitudeMeters / MetersPerFoot;

        if (feet < 1000)
        {
            var hundreds = Math.Max(0, (int)Math.Round(feet / 100.0) * 100);
            return spellOut ? $"{NumberToWords(hundreds)} feet" : $"{hundreds} feet";
        }

        var thousands = (int)Math.Round(feet / 1000.0);
        return spellOut ? $"{NumberToWords(thousands)} thousand" : $"{thousands} thousand";
    }

    /// <summary>
    /// Bearings are read digit by digit on the radio ("zero niner zero", not "ninety"). Spelling
    /// the digits out as words also stops TTS engines from reading "090" as "ninety".
    ///
    /// The separator is what makes the difference between an intelligible and a rushed-sounding
    /// bearing: with a comma between the digits, TTS engines insert a short pause after each one
    /// instead of running "zeroninerzero" together.
    /// </summary>
    /// <summary>
    /// A bearing as it is read on the radio: 1 to 360, never 0. Due north is "three six zero" -
    /// "zero zero zero" is not something a controller says, and a pilot hearing it would wonder
    /// whether the bot had lost the track.
    /// </summary>
    internal static int BearingForSpeech(int bearing)
    {
        var normalized = ((bearing % 360) + 360) % 360;
        return normalized == 0 ? 360 : normalized;
    }

    /// <summary>The same bearing written out, for the fast (non-spelled) replies.</summary>
    internal static string BearingText(int bearing) => BearingForSpeech(bearing).ToString("000");

    internal static string SpeakBearing(int bearing, string separator = " ")
    {
        var normalized = BearingForSpeech(bearing);
        return string.Join(separator, normalized.ToString("000").Select(d => SpeakDigit(d - '0')));
    }

    /// <summary>
    /// The aviation pronunciation of a single digit. "niner" rather than "nine", because "nine"
    /// and "five" are the classic pair to confuse over a noisy radio - the whole point of the
    /// brevity word. Used for every number read digit by digit: bearings, wind, pressure.
    /// </summary>
    internal static string SpeakDigit(int digit) =>
        digit is >= 0 and <= 9 ? DigitWords[digit] : digit.ToString();

    private static readonly string[] DigitWords =
        { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner" };

    /// <summary>Small counts sound better as words ("two groups" rather than "2 groups").</summary>
    internal static string SpeakCount(int count) => count is >= 0 and < 1000 ? NumberToWords(count) : count.ToString();

    /// <summary>
    /// Spells a number out in words, so the TTS engine says "thirty five" instead of racing
    /// through the digits of "35". Anything above 999 is left as digits - it never occurs for the
    /// ranges, altitudes and counts used here.
    /// </summary>
    internal static string NumberToWords(int number)
    {
        if (number < 0) return number.ToString();

        var ones = new[]
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"
        };
        var tens = new[] { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

        if (number < 20) return ones[number];
        if (number < 100)
        {
            var rest = number % 10;
            return rest == 0 ? tens[number / 10] : $"{tens[number / 10]} {ones[rest]}";
        }
        if (number < 1000)
        {
            var rest = number % 100;
            var hundreds = $"{ones[number / 100]} hundred";
            return rest == 0 ? hundreds : $"{hundreds} {NumberToWords(rest)}";
        }

        return number.ToString();
    }

    /// <summary>
    /// Makes a DCS type name speakable. DCS type ids carry variant suffixes that are meaningless
    /// on the radio and are read out letter by letter ("F-16C_50" becomes "f sixteen c underscore
    /// fifty"), so everything from the first underscore on is dropped. Hyphens are turned into
    /// spaces by the TTS preparation in the bot anyway.
    /// </summary>
    internal static string SpeakType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "";

        var cleaned = type.Trim();
        var underscore = cleaned.IndexOf('_');
        if (underscore > 0) cleaned = cleaned[..underscore];

        return cleaned.Trim();
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
