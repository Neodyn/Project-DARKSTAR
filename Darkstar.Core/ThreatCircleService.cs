using System.Collections.Concurrent;

namespace Darkstar;

/// <summary>One standing threat watch around a pilot's aircraft.</summary>
public sealed class ThreatCircle
{
    /// <summary>The pilot's unmodified SRS name - used to find their aircraft on every sweep.</summary>
    public required string RawPlayerName { get; init; }

    /// <summary>The pilot's callsign as spoken on the radio ("Enfield 1-1").</summary>
    public required string PilotCallsign { get; init; }

    /// <summary>Frequency the warnings are transmitted on - the one the request came in on.</summary>
    public required double FrequencyHz { get; init; }

    public required int SenderCoalition { get; init; }
    public required double RadiusNm { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public required DateTime ExpiresAt { get; init; }

    /// <summary>
    /// Units already reported to this pilot. Each contact is announced at most once for the life
    /// of the circle, so a contact loitering near the edge can't turn into a stream of warnings.
    /// </summary>
    public HashSet<string> AnnouncedUnits { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Consecutive sweeps in which the pilot could not be located (slot left, shot down, logged off).</summary>
    public int MissedPilotLookups { get; set; }
}

/// <summary>A warning that is ready to be transmitted.</summary>
public sealed class ThreatCircleAlert
{
    public required ThreatCircle Circle { get; init; }
    /// <summary>The spoken text, without the "&lt;pilot&gt;, this is &lt;callsign&gt;" prefix the bot adds.</summary>
    public required string SpokenText { get; init; }
}

/// <summary>
/// Keeps the standing threat circles and sweeps them on a timer: whenever a hostile aircraft or
/// helicopter is inside a pilot's circle for the first time, the pilot gets a warning on the
/// frequency they requested the circle on.
///
/// A circle ends when the pilot cancels it by radio, when its time limit runs out, or when the
/// pilot disappears from the mission (left the slot, logged off, was shot down) - checked by the
/// sweep itself, so nothing keeps running for an aircraft that no longer exists.
///
/// The service only produces the text; transmitting is left to the caller through the callback
/// passed to <see cref="RunAsync"/>, which keeps this class free of any radio/SRS dependency.
/// </summary>
public sealed class ThreatCircleService
{
    /// <summary>Consecutive failed pilot lookups before a circle is dropped (~3 sweeps of grace).</summary>
    private const int MaxMissedPilotLookups = 3;

    private readonly AppConfig _config;
    private readonly DcsIntelService _intel;

    /// <summary>Active circles, keyed by pilot name + frequency so one pilot can watch two radios.</summary>
    private readonly ConcurrentDictionary<string, ThreatCircle> _circles = new(StringComparer.OrdinalIgnoreCase);

    public ThreatCircleService(AppConfig config, DcsIntelService intel)
    {
        _config = config;
        _intel = intel;
    }

    public int ActiveCount => _circles.Count;

    private static string KeyFor(string rawPlayerName, double frequencyHz) =>
        $"{rawPlayerName.Trim()}@{Math.Round(frequencyHz)}";

    /// <summary>
    /// Starts (or replaces) a pilot's threat circle and returns the spoken confirmation. Contacts
    /// that are already inside when the circle is set up are counted in the confirmation and
    /// marked as announced, so the pilot isn't buried under warnings a second later.
    /// </summary>
    public async Task<string> StartAsync(string rawPlayerName, string pilotCallsign, double frequencyHz,
        int senderCoalition, string transcript, CancellationToken cancellationToken = default)
    {
        var radiusNm = _intel.ParseRequestedRadiusNm(transcript);

        // Replacing one's own circle is always allowed; the limit is about how many different
        // pilots can be watched at once, since every circle costs a mission query per sweep.
        var key = KeyFor(rawPlayerName, frequencyHz);
        if (!_circles.ContainsKey(key) && _circles.Count >= Math.Max(1, _config.DcsIntelThreatCircleMaxActive))
            return _config.DcsIntelThreatCircleBusyReply;

        var scan = await _intel.ScanForThreatsAsync(rawPlayerName, senderCoalition, radiusNm, cancellationToken);

        if (!scan.DataAvailable)
            return _config.DcsIntelUnavailableReply;

        // A circle that follows the pilot needs the pilot's aircraft - without it there is no centre.
        if (!scan.PilotFound)
            return _config.DcsIntelThreatCircleNoPilotReply;

        var circle = new ThreatCircle
        {
            RawPlayerName = rawPlayerName,
            PilotCallsign = pilotCallsign,
            FrequencyHz = frequencyHz,
            SenderCoalition = senderCoalition,
            RadiusNm = radiusNm,
            ExpiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _config.DcsIntelThreatCircleDurationMinutes))
        };

        foreach (var contact in scan.Contacts)
            circle.AnnouncedUnits.Add(contact.UnitName);

        _circles[key] = circle;

        Logger.Log($"[ThreatCircle] Active for \"{pilotCallsign}\" ({rawPlayerName}) on " +
                   $"{frequencyHz / 1_000_000:0.000} MHz: {radiusNm:0} NM, {scan.Contacts.Count} contact(s) already inside, " +
                   $"expires in {_config.DcsIntelThreatCircleDurationMinutes} min.");

        return _intel.FormatThreatCircleConfirmation(radiusNm, scan.Contacts.Count);
    }

    /// <summary>Cancels the pilot's circle on this frequency and returns the spoken confirmation.</summary>
    public string Cancel(string rawPlayerName, double frequencyHz)
    {
        if (_circles.TryRemove(KeyFor(rawPlayerName, frequencyHz), out var circle))
        {
            Logger.Log($"[ThreatCircle] Cancelled by \"{circle.PilotCallsign}\" on {frequencyHz / 1_000_000:0.000} MHz.");
            return _config.DcsIntelThreatCircleCancelledReply;
        }

        return _config.DcsIntelThreatCircleNoneActiveReply;
    }

    /// <summary>Drops every circle - used on shutdown and when the bot loses its SRS connection.</summary>
    public void Clear() => _circles.Clear();

    /// <summary>
    /// Sweeps all active circles until cancelled. <paramref name="onAlert"/> is invoked for every
    /// contact that has newly entered a circle, in range order; the caller transmits it.
    /// </summary>
    public async Task RunAsync(Func<ThreatCircleAlert, Task> onAlert, CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_config.DcsIntelThreatCirclePollSeconds, 5, 300));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);

                if (_circles.IsEmpty) continue;

                foreach (var (key, circle) in _circles.ToArray())
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    if (DateTime.UtcNow >= circle.ExpiresAt)
                    {
                        _circles.TryRemove(key, out _);
                        Logger.Log($"[ThreatCircle] Expired for \"{circle.PilotCallsign}\" after " +
                                   $"{_config.DcsIntelThreatCircleDurationMinutes} min.");
                        continue;
                    }

                    await SweepAsync(key, circle, onAlert, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failing sweep must never kill the loop - the next one may well succeed
                // (mission reloading, server briefly unreachable, ...).
                Logger.Log($"[ThreatCircle] Sweep failed: {ex.Message}");
            }
        }
    }

    private async Task SweepAsync(string key, ThreatCircle circle, Func<ThreatCircleAlert, Task> onAlert,
        CancellationToken stoppingToken)
    {
        var scan = await _intel.ScanForThreatsAsync(circle.RawPlayerName, circle.SenderCoalition, circle.RadiusNm, stoppingToken);

        if (!scan.DataAvailable)
        {
            // Mission data temporarily unreadable - keep the circle, just skip this sweep.
            Logger.Debug($"[ThreatCircle] Sweep skipped for \"{circle.PilotCallsign}\": {scan.Error}");
            return;
        }

        if (!scan.PilotFound)
        {
            circle.MissedPilotLookups++;
            if (circle.MissedPilotLookups >= MaxMissedPilotLookups)
            {
                _circles.TryRemove(key, out _);
                Logger.Log($"[ThreatCircle] Dropped for \"{circle.PilotCallsign}\" - aircraft no longer in the mission.");
            }
            return;
        }

        circle.MissedPilotLookups = 0;

        // Only contacts that were never reported to this pilot before. Announcing the closest
        // ones first and capping each sweep keeps a whole incoming package from monopolizing the
        // frequency; the rest stay unannounced and are reported on the following sweeps.
        var newContacts = scan.Contacts
            .Where(c => !circle.AnnouncedUnits.Contains(c.UnitName))
            .Take(Math.Max(1, _config.DcsIntelThreatCircleMaxAlertsPerSweep))
            .ToList();

        foreach (var contact in newContacts)
        {
            stoppingToken.ThrowIfCancellationRequested();

            circle.AnnouncedUnits.Add(contact.UnitName);

            Logger.Log($"[ThreatCircle] \"{circle.PilotCallsign}\": {contact.UnitName} ({contact.Type}) entered at " +
                       $"{contact.RangeNm:0} NM, bearing {contact.BearingDegrees:000}, {contact.Aspect}.");

            await onAlert(new ThreatCircleAlert
            {
                Circle = circle,
                SpokenText = _intel.FormatThreatAlert(contact)
            });
        }
    }
}
