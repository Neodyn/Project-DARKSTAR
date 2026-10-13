using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using RurouniJones.Dcs.Grpc.V0.Mission;

namespace Darkstar;

/// <summary>
/// The F10 menu: the same calls the bot answers by voice, reachable by keypress.
///
/// WHY: speech recognition is the one part of this bot that fails for reasons the pilot cannot fix
/// mid-flight - an accent the model was not trained on, a noisy cockpit, a wake word that simply
/// did not register. Until now the answer to "the bot does not hear me" was to try again louder.
/// This is the way through that does not depend on being understood.
///
/// NO MOD REQUIRED. Everything here goes through DCS-gRPC, which a server running the tactical
/// replies has installed already: <c>AddGroupCommandSubMenu</c> and <c>AddGroupCommand</c> build
/// the menu, <c>StreamEvents</c> delivers the clicks. Nothing is installed on any pilot's machine.
///
/// THE ID TRAVELS, NOT THE LABEL. Each command carries a <c>details</c> struct holding the call's
/// id from <see cref="CallCatalog"/>, and DCS hands that struct back in the event. So the label can
/// be renamed or translated without touching the routing, and the bot never has to parse a menu
/// caption back into a meaning.
///
/// MENUS ARE REBUILT, NOT ACCUMULATED - the same lesson as the F10 markers in
/// <see cref="FrequencyAnnouncer"/>. A group's menu is removed by its root entry (DCS deletes the
/// children with it) and written again whenever what it should contain has changed. Without that,
/// a pilot who changes slot twice has three Darkstar menus and no way to tell which is live.
/// </summary>
public static class DcsMenu
{
    /// <summary>The key the call id travels under inside the command's details struct.</summary>
    public const string DetailsKey = "darkstar_call";

    /// <summary>One entry as it will appear in the menu.</summary>
    /// <param name="Label">What the pilot reads.</param>
    /// <param name="CallId">What comes back in the event.</param>
    public sealed record Entry(string Label, string CallId);

    /// <summary>
    /// What one group's menu should contain, in order. Pure: this is the part worth testing,
    /// because it decides what a pilot sees without anybody being in a cockpit.
    /// </summary>
    public static List<Entry> Plan(CallContext context) =>
        CallCatalog.Available(context)
            .Where(CallCatalog.MenuOffers)
            .Select(call => new Entry(call.MenuLabel, call.Id))
            .ToList();

    /// <summary>
    /// A short string standing for a planned menu, used to decide whether anything needs rewriting.
    ///
    /// Rebuilding costs two gRPC calls per entry and makes the menu blink shut under whoever has it
    /// open, so it must happen when the content changed and not merely when the sweep came round
    /// again. Comparing the plan's signature is how "changed" is decided, and it covers the order
    /// as well as the membership - a pilot who has learnt that F1 is the bogey dope should see the
    /// menu rewritten when that stops being true.
    /// </summary>
    public static string Signature(IEnumerable<Entry> plan) =>
        string.Join("|", plan.Select(e => e.CallId));

    /// <summary>
    /// Reads the call id back out of the details a <c>GroupCommandEvent</c> carried.
    ///
    /// Defensive on purpose: these details come from DCS, and a mission or another script can emit
    /// a group command event of its own. Anything that is not one of ours resolves to null and is
    /// ignored rather than guessed at.
    /// </summary>
    public static string? CallIdFromDetails(Struct? details)
    {
        if (details == null) return null;
        if (!details.Fields.TryGetValue(DetailsKey, out var value)) return null;
        return value.KindCase == Value.KindOneofCase.StringValue ? value.StringValue : null;
    }

    /// <summary>Builds the details struct that travels with a command and comes back in its event.</summary>
    public static Struct DetailsFor(string callId)
    {
        var details = new Struct();
        details.Fields[DetailsKey] = Value.ForString(callId);
        return details;
    }
}

/// <summary>What the menu writer did, for the log and the GUI.</summary>
/// <param name="GroupsWritten">How many groups had their menu written or rewritten.</param>
/// <param name="GroupsRemoved">How many stale menus were taken away.</param>
/// <param name="GroupsSkipped">Groups passed over because the cap was reached.</param>
/// <param name="Error">Null on success; a sentence naming what to do otherwise.</param>
public sealed record MenuSyncResult(int GroupsWritten, int GroupsRemoved, int GroupsSkipped, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Writes and maintains the F10 menus over a live DCS-gRPC connection. The decisions live in
/// <see cref="DcsMenu"/>; this holds the connection, the bookkeeping of what is currently written,
/// and nothing that can be worked out on paper.
/// </summary>
public sealed class DcsMenuService
{
    private readonly AppConfig _config;

    /// <summary>
    /// The signature of the menu currently written for each group. A group missing from here has no
    /// menu; a group whose signature still matches its plan needs no work.
    /// </summary>
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);

    public DcsMenuService(AppConfig config)
    {
        _config = config;
    }

    /// <summary>The root entry every Darkstar command hangs under, so one removal clears the lot.</summary>
    public string RootName =>
        string.IsNullOrWhiteSpace(_config.F10MenuRootName) ? "Darkstar" : _config.F10MenuRootName.Trim();

    /// <summary>Groups that currently have a menu, for the log and for tests.</summary>
    public IReadOnlyCollection<string> GroupsWithMenu => _written.Keys.ToList();

    /// <summary>
    /// Brings the menus in line with what each group should currently see.
    /// </summary>
    /// <param name="plans">
    /// The planned menu per group name. Groups absent from this have their menu removed - that is
    /// how a player leaving, changing slot or losing the feature gets cleaned up.
    /// </param>
    public async Task<MenuSyncResult> SyncAsync(
        GrpcChannel channel, Metadata headers,
        IReadOnlyDictionary<string, List<DcsMenu.Entry>> plans,
        CancellationToken cancellationToken = default)
    {
        var mission = new MissionService.MissionServiceClient(channel);
        int written = 0, removed = 0, skipped = 0;

        try
        {
            // Gone first, so a server at the cap still frees room for whoever is actually flying.
            foreach (var groupName in _written.Keys.Where(g => !plans.ContainsKey(g)).ToList())
            {
                await RemoveAsync(mission, headers, groupName, cancellationToken);
                removed++;
            }

            foreach (var (groupName, plan) in plans)
            {
                var signature = DcsMenu.Signature(plan);
                if (_written.TryGetValue(groupName, out var current) && current == signature)
                    continue;

                // The cap exists because a full server would otherwise mean a menu per flight, each
                // costing a handful of gRPC calls every time its situation changes. Groups already
                // holding a menu are never pushed out by it - losing your menu because somebody else
                // took off is worse than not having had one.
                if (!_written.ContainsKey(groupName) && _written.Count >= Math.Max(1, _config.F10MenuMaxGroups))
                {
                    skipped++;
                    continue;
                }

                await RemoveAsync(mission, headers, groupName, cancellationToken);

                await mission.AddGroupCommandSubMenuAsync(new AddGroupCommandSubMenuRequest
                {
                    GroupName = groupName,
                    Name = RootName,
                }, headers, cancellationToken: cancellationToken);

                foreach (var entry in plan)
                {
                    var request = new AddGroupCommandRequest
                    {
                        GroupName = groupName,
                        Name = entry.Label,
                        Details = DcsMenu.DetailsFor(entry.CallId),
                    };
                    request.Path.Add(RootName);

                    await mission.AddGroupCommandAsync(request, headers, cancellationToken: cancellationToken);
                }

                _written[groupName] = signature;
                written++;
            }

            return new MenuSyncResult(written, removed, skipped, null);
        }
        catch (RpcException ex)
        {
            // A failed sweep must not leave the bookkeeping claiming menus that may not exist - the
            // next sweep then skips exactly the groups that need fixing. Forgetting everything costs
            // one redundant rebuild and is always correct.
            _written.Clear();
            return new MenuSyncResult(written, removed, skipped,
                $"DCS-gRPC refused a menu command ({ex.StatusCode}): {ex.Status.Detail}");
        }
    }

    /// <summary>Takes one group's menu away, root and children, and forgets it.</summary>
    private async Task RemoveAsync(MissionService.MissionServiceClient mission, Metadata headers,
        string groupName, CancellationToken cancellationToken)
    {
        var request = new RemoveGroupCommandItemRequest { GroupName = groupName };
        request.Path.Add(RootName);

        try
        {
            await mission.RemoveGroupCommandItemAsync(request, headers, cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // Removing a menu that was never there is the normal case on the first sweep after a
            // restart, and DCS does not distinguish it from a real failure. Writing happens next
            // either way, so there is nothing to recover from here.
        }

        _written.Remove(groupName);
    }

    /// <summary>
    /// Forgets every menu without touching DCS, for a reconnect: the mission that held those menus
    /// is gone, so the bookkeeping describing them is worse than nothing.
    /// </summary>
    public void Forget() => _written.Clear();

    /// <summary>One flight that should have a menu.</summary>
    /// <param name="Name">The group name, which is what menu commands are addressed to.</param>
    /// <param name="Id">The DCS group id, which is what on-screen text is addressed to.</param>
    /// <param name="NearAirfield">Whether the airfield calls should come first for this flight.</param>
    public sealed record PlayerGroup(string Name, uint Id, bool NearAirfield);

    /// <summary>
    /// The flights currently holding players, with enough about each to plan its menu.
    ///
    /// One gRPC call for the whole sweep rather than one per group: on a full server the per-group
    /// version would be dozens of round trips every refresh, for information that arrives together
    /// anyway.
    /// </summary>
    /// <param name="airfieldPositions">
    /// Where the airfields are, for deciding whether a flight is at one. Empty simply means no
    /// flight counts as near an airfield, which costs ordering and nothing else.
    /// </param>
    public async Task<List<PlayerGroup>> ListPlayerGroupsAsync(
        GrpcChannel channel, Metadata headers,
        IReadOnlyCollection<(double Lat, double Lon)> airfieldPositions,
        int atFieldNm,
        CancellationToken cancellationToken = default)
    {
        var groups = new Dictionary<string, PlayerGroup>(StringComparer.OrdinalIgnoreCase);
        var coalitions = new[]
        {
            RurouniJones.Dcs.Grpc.V0.Common.Coalition.Blue,
            RurouniJones.Dcs.Grpc.V0.Common.Coalition.Red,
        };

        var client = new RurouniJones.Dcs.Grpc.V0.Coalition.CoalitionService.CoalitionServiceClient(channel);

        foreach (var coalition in coalitions)
        {
            var response = await client.GetPlayerUnitsAsync(
                new RurouniJones.Dcs.Grpc.V0.Coalition.GetPlayerUnitsRequest { Coalition = coalition },
                headers, cancellationToken: cancellationToken);

            foreach (var unit in response.Units)
            {
                if (unit.Group == null || string.IsNullOrWhiteSpace(unit.Group.Name)) continue;
                if (groups.ContainsKey(unit.Group.Name)) continue;

                var near = unit.Position != null && airfieldPositions.Any(a =>
                    DistanceNm(a.Lat, a.Lon, unit.Position.Lat, unit.Position.Lon) <= Math.Max(1, atFieldNm));

                groups[unit.Group.Name] = new PlayerGroup(unit.Group.Name, unit.Group.Id, near);
            }
        }

        return groups.Values.ToList();
    }

    /// <summary>
    /// Great-circle distance in nautical miles. Good enough by a wide margin for "is this flight at
    /// an airfield" - the answer only has to be right to within a few miles, and the question is
    /// asked about points a few miles apart.
    /// </summary>
    internal static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusNm = 3440.065;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadiusNm * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// Yields one entry per click on a Darkstar menu command, for as long as the stream holds.
    ///
    /// Group command events are not ours alone - a mission or another script can raise them - so
    /// anything without our key in its details is skipped silently rather than guessed at.
    /// </summary>
    public async IAsyncEnumerable<(string GroupName, uint GroupId, string CallId)> StreamClicksAsync(
        GrpcChannel channel, Metadata headers,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var mission = new MissionService.MissionServiceClient(channel);
        using var stream = mission.StreamEvents(new StreamEventsRequest(), headers, cancellationToken: cancellationToken);

        while (await stream.ResponseStream.MoveNext(cancellationToken))
        {
            var groupCommand = stream.ResponseStream.Current?.GroupCommand;
            if (groupCommand?.Group == null) continue;

            var callId = DcsMenu.CallIdFromDetails(groupCommand.Details);
            if (callId == null) continue;

            yield return (groupCommand.Group.Name, groupCommand.Group.Id, callId);
        }
    }

    /// <summary>
    /// Puts the answer on screen for one flight.
    ///
    /// The half of <c>F10ReplyMode</c> that matters when SRS itself is the thing that is broken: a
    /// reply sent only over the radio, to somebody whose radio does not work, is no reply at all.
    /// </summary>
    public async Task ShowTextToGroupAsync(GrpcChannel channel, Metadata headers,
        uint groupId, string text, CancellationToken cancellationToken = default)
    {
        await new RurouniJones.Dcs.Grpc.V0.Trigger.TriggerService.TriggerServiceClient(channel)
            .OutTextForGroupAsync(new RurouniJones.Dcs.Grpc.V0.Trigger.OutTextForGroupRequest
            {
                GroupId = groupId,
                Text = text,
                DisplayTime = Math.Max(5, _config.F10TextSeconds),
                ClearView = false,
            }, headers, cancellationToken: cancellationToken);
    }
}
