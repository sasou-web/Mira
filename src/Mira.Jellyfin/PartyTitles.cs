using System.Collections;
using System.Reflection;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.Extensions.Logging;

namespace Mira.Jellyfin;

/// <summary>
/// The title a guest's group plays, as SyncPlay holds it on this server: what the gateway lets a guest's session reach
/// (<see cref="GuestRoutes.Access.Title"/>). Jellyfin's API gives no group's queue, so it is read where SyncPlay keeps
/// it: the group of the guest's session (its manager's map of sessions to groups, found by type, whatever its name),
/// then that group's queue through Jellyfin's own <see cref="IGroupStateContext"/>, under the group's lock as Jellyfin
/// takes it. Anything not found there is refused: a guest who reaches no title rather than every one.
/// </summary>
public sealed class PartyTitles
{
    private static FieldInfo? _map;
    private static bool _looked, _told;
    private static readonly object Gate = new();

    private readonly ISyncPlayManager _syncPlay;
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly ILogger<PartyTitles> _logger;

    public PartyTitles(ISyncPlayManager syncPlay, ISessionManager sessions, ILibraryManager library, ILogger<PartyTitles> logger)
    {
        _syncPlay = syncPlay;
        _sessions = sessions;
        _library = library;
        _logger = logger;
    }

    /// <summary>Whether every title a request names belongs to what the group of this guest (account, device) plays.</summary>
    public bool Allows(string userId, string device, IReadOnlyList<Guid> named)
    {
        if (named.Count == 0 || !Guid.TryParse(userId, out var user))
        {
            return false;
        }

        var queue = Queue(user, device);
        if (queue.Count == 0)
        {
            return false;
        }

        var series = queue.Select(SeriesOf).OfType<Guid>().ToHashSet();
        return GuestRoutes.InParty(named, queue, series, SeriesOf);
    }

    /// <summary>The series of an episode or a season; null for anything else (a film, a series itself, nothing).</summary>
    private Guid? SeriesOf(Guid id)
    {
        try
        {
            return _library.GetItemById(id) switch
            {
                Episode episode when !episode.SeriesId.Equals(Guid.Empty) => episode.SeriesId,
                Season season when !season.SeriesId.Equals(Guid.Empty) => season.SeriesId,
                _ => null,
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The titles in the queue of the group this guest's session is in; empty when it is in none.</summary>
    private HashSet<Guid> Queue(Guid user, string device)
    {
        var titles = new HashSet<Guid>();
        if (Map() is not { } map)
        {
            return titles;
        }

        IDictionary? groups;
        try
        {
            groups = map.GetValue(_syncPlay) as IDictionary;
        }
        catch (Exception error) when (error is ArgumentException or TargetException or FieldAccessException)
        {
            groups = null;
        }

        if (groups is null)
        {
            return titles;
        }

        foreach (var session in _sessions.Sessions.Where(x => x.UserId.Equals(user) && string.Equals(x.DeviceId, device, StringComparison.Ordinal)))
        {
            object? found;
            try
            {
                found = session.Id is null ? null : groups[session.Id];
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                found = null;
            }

            if (found is not IGroupStateContext group)
            {
                continue;
            }

            // Jellyfin changes a group's queue under the group's lock (SyncPlayManager.HandleRequest).
            lock (found)
            {
                foreach (var item in group.PlayQueue.GetPlaylist())
                {
                    titles.Add(item.ItemId);
                }
            }
        }

        return titles;
    }

    /// <summary>
    /// SyncPlay's map of each session (by its id) to its group, found once among its manager's fields by its type:
    /// a dictionary of text to a group of Jellyfin. Null, said once in Jellyfin's log, when this Jellyfin keeps it otherwise.
    /// </summary>
    private FieldInfo? Map()
    {
        lock (Gate)
        {
            if (!_looked)
            {
                _looked = true;
                for (var type = _syncPlay.GetType(); type is not null && _map is null; type = type.BaseType)
                {
                    _map = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                        .FirstOrDefault(field => IsSessionMap(field.FieldType));
                }
            }

            if (_map is null && !_told)
            {
                _told = true;
                _logger.LogError("Mira: SyncPlay's groups could not be read on this Jellyfin; a party's guests reach no title");
            }

            return _map;
        }
    }

    private static bool IsSessionMap(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type)
        && type.GetInterfaces().Append(type).Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            && x.GetGenericArguments()[0] == typeof(string) && typeof(IGroupStateContext).IsAssignableFrom(x.GetGenericArguments()[1]));
}
