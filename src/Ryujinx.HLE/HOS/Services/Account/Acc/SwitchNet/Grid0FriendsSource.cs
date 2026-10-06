using Ryujinx.Common.Logging;
using Ryujinx.Horizon.Sdk.Friends;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.Acc.SwitchNet
{
    /// <summary>
    /// The friend service's friend list, from GRID0+: a console's friends sysmodule keeps its
    /// own copy synced from BAAS, and this is the emulator's.
    /// </summary>
    /// <remarks>
    /// The friend service answers on the game's own thread, so nothing here waits long. Splatoon 3
    /// asks for its list once, early, and keeps it for the session, which is why the first
    /// request may wait briefly for the first fetch; every other request takes the cache and
    /// refreshes it in the background.
    /// </remarks>
    internal sealed class Grid0FriendsSource : IFriendsSource
    {
        private static readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(20);

        private readonly Switch _device;
        private readonly object _lock = new();
        private readonly Dictionary<ulong, byte[]> _images = [];
        private readonly HashSet<ulong> _imagesPending = [];

        private List<FriendsSourceEntry> _cache = [];
        private List<Grid0Friend> _raw = [];
        private bool _haveList;
        private bool _warmWaited;
        private long _fetchesDone;
        private DateTime _lastFetch;
        private int _fetching;

        public Grid0FriendsSource(Switch device)
        {
            _device = device;
        }

        private SwitchNetAccountClient Client() =>
            SwitchNetAccountSession.TryGetClient(_device.Configuration, out SwitchNetAccountClient client) ? client : null;

        public ulong CurrentApplicationId => _device.Processes?.ActiveApplication?.ProgramId ?? 0;

        public IReadOnlyList<FriendsSourceEntry> GetFriends(TimeSpan wait)
        {
            if (Client() == null)
            {
                return [];
            }

            lock (_lock)
            {
                bool stale = DateTime.UtcNow - _lastFetch > _refreshInterval;

                if (!_haveList && !_warmWaited)
                {
                    _warmWaited = true;
                    long before = _fetchesDone;
                    RefreshInBackground();

                    DateTime deadline = DateTime.UtcNow + wait;
                    while (_fetchesDone == before && DateTime.UtcNow < deadline)
                    {
                        Monitor.Wait(_lock, deadline - DateTime.UtcNow);
                    }
                }
                else if (!_haveList || stale)
                {
                    RefreshInBackground();
                }

                return _cache;
            }
        }

        public byte[] GetProfileImage(ulong id, TimeSpan wait)
        {
            SwitchNetAccountClient client = Client();
            string url = null;

            lock (_lock)
            {
                if (_images.TryGetValue(id, out byte[] cached))
                {
                    return cached;
                }

                url = _raw.Find(f => f.NsaId == id)?.ImageUrl;

                if (client == null || string.IsNullOrEmpty(url))
                {
                    return null;
                }

                if (_imagesPending.Add(id))
                {
                    Task.Run(async () =>
                    {
                        byte[] jpeg = null;

                        try
                        {
                            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
                            jpeg = await client.FetchAsync(url, cts.Token);
                        }
                        catch (Exception)
                        {
                            // No picture; the game shows its placeholder.
                        }

                        lock (_lock)
                        {
                            _imagesPending.Remove(id);

                            if (jpeg != null)
                            {
                                _images[id] = jpeg;
                            }

                            Monitor.PulseAll(_lock);
                        }
                    });
                }

                DateTime deadline = DateTime.UtcNow + wait;
                while (!_images.ContainsKey(id) && _imagesPending.Contains(id) && DateTime.UtcNow < deadline)
                {
                    Monitor.Wait(_lock, deadline - DateTime.UtcNow);
                }

                return _images.GetValueOrDefault(id);
            }
        }

        public void PublishPresence(uint status, byte[] appKeyValueStorage)
        {
            Grid0Friends.SetGamePresence(Client, CurrentApplicationId, status, appKeyValueStorage);
        }

        private void RefreshInBackground()
        {
            if (Interlocked.Exchange(ref _fetching, 1) != 0)
            {
                return;
            }

            Task.Run(async () =>
            {
                List<Grid0Friend> friends = null;

                try
                {
                    SwitchNetAccountClient client = Client();

                    if (client != null)
                    {
                        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
                        friends = await Grid0Friends.FetchFriendsAsync(client, cts.Token);
                    }
                }
                catch (Exception e)
                {
                    Logger.Warning?.Print(LogClass.ServiceFriend, $"GRID0+: friend list not fetched: {e.Message}");
                }

                lock (_lock)
                {
                    _lastFetch = DateTime.UtcNow;
                    _fetchesDone++;

                    if (friends != null)
                    {
                        if (!_haveList || friends.Count != _raw.Count)
                        {
                            Logger.Info?.Print(LogClass.ServiceFriend, $"GRID0+: {friends.Count} friend(s)");
                        }

                        _raw = friends;
                        _cache = friends.ConvertAll(ToEntry);
                        _haveList = true;
                    }

                    Monitor.PulseAll(_lock);
                }

                Interlocked.Exchange(ref _fetching, 0);
            });
        }

        private static FriendsSourceEntry ToEntry(Grid0Friend f)
        {
            uint status = f.State switch { "PLAYING" => 2u, "ONLINE" => 1u, _ => 0u };
            ulong.TryParse(f.AppId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong appId);

            return new FriendsSourceEntry(f.NsaId, f.Nickname, status, appId, f.AppField, f.IsFavorite);
        }
    }
}
