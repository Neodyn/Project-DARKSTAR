using Grpc.Core;
using Grpc.Net.Client;
using RurouniJones.Dcs.Grpc.V0.Common;
using RurouniJones.Dcs.Grpc.V0.Trigger;

namespace Darkstar;

/// <summary>
/// Telling the pilots which frequencies exist, inside the game.
///
/// WHY THIS IS NOT OPTIONAL GARNISH: the tower frequencies are invented by <see cref="TowerPlan"/>,
/// because DCS-gRPC does not report an airfield's real radio frequency. They are therefore written
/// down nowhere a pilot can look - not in the briefing, not on the F10 map, not in the kneeboard.
/// Without this, the bot is a radio service nobody can find.
///
/// TWO CHANNELS, deliberately different in character:
///
///   * An F10 map marker per airfield, which stays for the whole mission and can be read whenever
///     somebody needs it. This is the one that matters.
///   * One on-screen message at startup, which is how somebody already flying finds out. It is
///     transient by nature, so it never carries anything that is only available there.
///
/// MARKER IDS ARE FIXED, not allocated. A restarted bot must REPLACE its markers rather than add a
/// second set - otherwise a server that restarts the bot three times in an evening ends up with
/// three overlapping markers per airfield and pilots reading whichever one happens to be on top. So
/// each airfield gets a deterministic id derived from its index, every id in the range is removed
/// before writing, and the range is kept well clear of what a mission would plausibly use itself.
/// </summary>
public static class FrequencyAnnouncer
{
    /// <summary>
    /// Start of the marker id range. High enough that a mission's own marks - which authors tend to
    /// number from zero or from a few hundred - will not collide with it.
    /// </summary>
    public const uint MarkerIdBase = 740_000;

    /// <summary>The marker id for the nth announced airfield. Deterministic, so a restart overwrites.</summary>
    public static uint MarkerIdFor(int index) => MarkerIdBase + (uint)index;

    /// <summary>What happened, for the log and the GUI.</summary>
    /// <param name="MarkersPlaced">How many F10 markers were written.</param>
    /// <param name="MessageSent">Whether the on-screen message went out.</param>
    /// <param name="Error">Null on success; a sentence naming what to do otherwise.</param>
    public sealed record Result(int MarkersPlaced, bool MessageSent, string? Error)
    {
        public bool Ok => Error == null;
    }

    /// <summary>
    /// The text of one airfield's map marker. Pure, so the wording can be tested without a mission.
    /// </summary>
    /// <param name="airfieldName">Spelled as the pilots see it on the map.</param>
    /// <param name="towerFrequencyHz">The tower serving this airfield, or null when none does.</param>
    /// <param name="otherRadios">
    /// The frequencies that are not tied to an airfield - the AWACS, a tanker. Included because a
    /// pilot reading a marker at their departure field is exactly the person who also needs those.
    /// </param>
    public static string MarkerText(string airfieldName, double? towerFrequencyHz,
        IEnumerable<(double FrequencyHz, string Callsign)> otherRadios, AppConfig config)
    {
        var lines = new List<string> { $"{config.BotCallsign} — {airfieldName}" };

        if (towerFrequencyHz != null)
            lines.Add($"Tower / ATIS: {Mhz(towerFrequencyHz.Value)} MHz");

        foreach (var (frequencyHz, callsign) in otherRadios)
            lines.Add($"{callsign}: {Mhz(frequencyHz)} MHz");

        // The wake word belongs on the marker: knowing the frequency is useless without it, and this
        // is the one place a pilot can read both at once.
        lines.Add($"Say \"{config.VoskKeyword}\" first, e.g. \"{config.VoskKeyword}, active runway\".");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The one-time on-screen message. Deliberately a summary rather than a full list: a dozen
    /// airfields would scroll off the screen, and the markers hold the detail.
    /// </summary>
    public static string StartupMessage(IEnumerable<(double FrequencyHz, string Callsign, bool Airfield)> radios,
        int airfieldMarkers, AppConfig config)
    {
        var list = radios.ToList();
        var lines = new List<string> { $"{config.BotCallsign} is on the air." };

        // Non-airfield radios by name, because there are few of them and they are the ones a pilot
        // needs in the air rather than on the ground.
        foreach (var (frequencyHz, callsign, _) in list.Where(r => !r.Airfield))
            lines.Add($"  {callsign}: {Mhz(frequencyHz)} MHz");

        var towers = list.Count(r => r.Airfield);
        if (towers > 0)
        {
            lines.Add(towers == 1
                ? "  1 tower frequency - see the airfield marker on the F10 map."
                : $"  {towers} tower frequencies - see the airfield markers on the F10 map.");
        }

        if (airfieldMarkers > 0)
            lines.Add($"({airfieldMarkers} marker(s) placed. Say \"{config.VoskKeyword}\" to be heard.)");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Writes the markers and sends the message. Never throws: a failure here must not stop the bot,
    /// which works perfectly well without anyone knowing its frequencies.
    /// </summary>
    /// <param name="towerByAirfield">
    /// Which tower frequency serves which airfield, keyed by the DCS airfield name. Empty is valid -
    /// then the markers carry only the non-airfield radios.
    /// </param>
    public static async Task<Result> AnnounceAsync(
        AppConfig config,
        IReadOnlyList<Airfield> airfields,
        IReadOnlyDictionary<string, double> towerByAirfield,
        IReadOnlyList<(double FrequencyHz, string Callsign, bool Airfield)> radios,
        CancellationToken cancellationToken = default)
    {
        if (!config.AnnounceFrequenciesEnabled)
            return new Result(0, false, null);

        try
        {
            var channel = DcsGrpcChannels.For(config.DcsGrpcAddress);
            var headers = DcsGrpcChannels.HeadersFor(config.DcsGrpcApiKey);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, config.DcsIntelTimeoutSeconds * 2));
            var trigger = new TriggerService.TriggerServiceClient(channel);

            var placed = 0;

            if (config.AnnounceFrequenciesMarkers)
            {
                var ordered = airfields
                    .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                    .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var others = radios.Where(r => !r.Airfield)
                    .Select(r => (r.FrequencyHz, r.Callsign))
                    .ToList();

                // Clear the whole id range first, not just the ids about to be written. A mission with
                // fewer airfields than the last run would otherwise leave orphaned markers behind.
                for (var i = 0; i < MaxMarkers; i++)
                {
                    try
                    {
                        await trigger.RemoveMarkAsync(
                            new RemoveMarkRequest { Id = MarkerIdFor(i) },
                            headers, deadline, cancellationToken);
                    }
                    catch (RpcException)
                    {
                        // An id that was never written is not an error worth reporting.
                    }
                }

                for (var i = 0; i < ordered.Count && i < MaxMarkers; i++)
                {
                    var airfield = ordered[i];
                    towerByAirfield.TryGetValue(airfield.Name, out var towerHz);

                    var request = new MarkToCoalitionRequest
                    {
                        Id = MarkerIdFor(i),
                        Text = MarkerText(airfield.SpokenName, towerHz > 0 ? towerHz : null, others, config),
                        Position = new InputPosition
                        {
                            Lat = airfield.Lat,
                            Lon = airfield.Lon,
                            Alt = airfield.ElevationMeters
                        },
                        Coalition = config.Coalition == 1 ? Coalition.Red : Coalition.Blue,
                        ReadOnly = true,
                        Message = ""
                    };

                    await trigger.MarkToCoalitionAsync(request, headers, deadline, cancellationToken);
                    placed++;
                }
            }

            var messageSent = false;
            if (config.AnnounceFrequenciesMessage)
            {
                await trigger.OutTextAsync(new OutTextRequest
                {
                    Text = StartupMessage(radios, placed, config),
                    DisplayTime = Math.Max(5, config.AnnounceFrequenciesMessageSeconds),
                    ClearView = false
                }, headers, deadline, cancellationToken);

                messageSent = true;
            }

            return new Result(placed, messageSent, null);
        }
        catch (RpcException ex)
        {
            return new Result(0, false, $"DCS-gRPC refused the announcement ({ex.StatusCode}: {ex.Status.Detail}).");
        }
        catch (Exception ex)
        {
            return new Result(0, false, $"Could not announce the frequencies: {ex.Message}");
        }
    }

    /// <summary>Upper bound on markers, which is also the id range that gets cleared on every run.</summary>
    public const int MaxMarkers = 32;

    private static string Mhz(double frequencyHz) =>
        (frequencyHz / 1_000_000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
}
