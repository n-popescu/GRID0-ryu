using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.Acc.SwitchNet
{
    /// <summary>A user as GRID0+'s emulator API describes one.</summary>
    public sealed record Grid0Friend(
        ulong NsaId,
        string Nickname,
        string Login,
        string FriendCode,
        string State,
        string AppId,
        string ImageUrl,
        bool IsFavorite,
        byte[] AppField);

    public sealed record Grid0FriendRequest(string Id, Grid0Friend Other);

    /// <summary>
    /// Friends on a GRID0+ server, through its emulator API (<c>/emulator/v1/...</c>),
    /// signed in with the same login the games use.
    /// </summary>
    /// <remarks>
    /// A console's friends sysmodule keeps its own copy of the friend list, synced from BAAS,
    /// and reports presence for whatever is running. The emulator has neither, so the Friends
    /// window reads the list from here, and a running game is reported as presence so friends
    /// see what this player is playing. The same API, and the same shapes, as citron's GRID0+
    /// build.
    /// </remarks>
    public static class Grid0Friends
    {
        /// <summary>Re-sent this often while nothing changes, so the server never expires it.</summary>
        private static readonly TimeSpan _presenceKeepAlive = TimeSpan.FromSeconds(60);

        private static readonly object _presenceLock = new();
        private static Func<SwitchNetAccountClient> _presenceClient;
        private static string _presenceBody;
        private static CancellationTokenSource _presenceCts;

        public static async Task<Grid0Friend> FetchMeAsync(SwitchNetAccountClient client, CancellationToken ct)
        {
            using JsonDocument body = await GetJsonAsync(client, "/emulator/v1/me", ct).ConfigureAwait(false);

            return ParseFriend(body.RootElement);
        }

        public static async Task<List<Grid0Friend>> FetchFriendsAsync(SwitchNetAccountClient client, CancellationToken ct)
        {
            using JsonDocument body = await GetJsonAsync(client, "/emulator/v1/friends", ct).ConfigureAwait(false);

            List<Grid0Friend> friends = [];

            if (body.RootElement.TryGetProperty("friends", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    Grid0Friend friend = ParseFriend(entry);

                    if (friend.NsaId != 0)
                    {
                        friends.Add(friend);
                    }
                }
            }

            return friends;
        }

        public static async Task<(List<Grid0FriendRequest> Incoming, List<Grid0FriendRequest> Outgoing)> FetchRequestsAsync(
            SwitchNetAccountClient client, CancellationToken ct)
        {
            using JsonDocument body = await GetJsonAsync(client, "/emulator/v1/friend-requests", ct).ConfigureAwait(false);

            return (ReadRequests(body.RootElement, "incoming"), ReadRequests(body.RootElement, "outgoing"));
        }

        /// <summary>Returns an error message for the player, or null.</summary>
        public static Task<string> SendRequestAsync(SwitchNetAccountClient client, string friendCode, CancellationToken ct) =>
            ProblemAsync(client, HttpMethod.Post, "/emulator/v1/friend-requests",
                JsonSerializer.Serialize(new Dictionary<string, string> { ["friendCode"] = friendCode }), ct);

        /// <summary><paramref name="action"/> is accept, deny or cancel. Returns an error message, or null.</summary>
        public static Task<string> SettleRequestAsync(SwitchNetAccountClient client, string id, string action, CancellationToken ct) =>
            ProblemAsync(client, HttpMethod.Post, $"/emulator/v1/friend-requests/{Uri.EscapeDataString(id)}/{action}", "{}", ct);

        /// <summary>Returns an error message, or null.</summary>
        public static Task<string> RemoveFriendAsync(SwitchNetAccountClient client, ulong nsaId, CancellationToken ct) =>
            ProblemAsync(client, HttpMethod.Delete, $"/emulator/v1/friends/{nsaId:x16}", null, ct);

        public static Task<byte[]> FetchImageAsync(SwitchNetAccountClient client, string url, CancellationToken ct) =>
            client.FetchAsync(url, ct);

        /// <summary>
        /// Shows this player as playing <paramref name="titleId"/>, or as merely online when it
        /// is 0, until changed. Sent in the background and kept alive.
        /// </summary>
        /// <param name="clientSource">
        /// Asked for the client on every send, so a login changed in the settings meanwhile is
        /// the one used. Returns null when GRID0+ is not configured.
        /// </param>
        public static void SetPresence(Func<SwitchNetAccountClient> clientSource, ulong titleId) =>
            SetPresence(clientSource, titleId, titleId != 0 ? 2u : 1u, null, gameSet: false);

        /// <summary>
        /// The presence a game set through the friend service: its nn::friends status (0
        /// offline, 1 online, 2 online play) and its app key-value storage, which is what lets a
        /// friend join this player.
        /// </summary>
        public static void SetGamePresence(Func<SwitchNetAccountClient> clientSource, ulong titleId, uint status, byte[] appField) =>
            SetPresence(clientSource, titleId, status, appField, gameSet: true);

        private static bool _gameSetPresence;

        private static void SetPresence(Func<SwitchNetAccountClient> clientSource, ulong titleId, uint status, byte[] appField, bool gameSet)
        {
            // A game that sets its own presence keeps it until it stops; the generic "playing"
            // from the game starting must not overwrite it.
            if (!gameSet && titleId != 0 && _gameSetPresence)
            {
                return;
            }

            _gameSetPresence = gameSet && titleId != 0;

            // Trailing zeroes are the unused part of the fixed-size storage, not data.
            int length = appField?.Length ?? 0;
            while (length > 0 && appField[length - 1] == 0)
            {
                length--;
            }

            string body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["state"] = status switch { 2 => "PLAYING", 1 => "ONLINE", _ => "OFFLINE" },
                ["appId"] = titleId != 0 ? titleId.ToString("x16") : "",
                ["appField"] = length > 0 ? Convert.ToBase64String(appField, 0, length) : "",
            });

            lock (_presenceLock)
            {
                _presenceClient = clientSource;

                if (_presenceBody == body && _presenceCts != null)
                {
                    return;
                }

                _presenceBody = body;
                _presenceCts?.Cancel();
                _presenceCts = new CancellationTokenSource();

                CancellationToken token = _presenceCts.Token;

                Task.Run(() => PresenceLoopAsync(body, token));
            }
        }

        private static async Task PresenceLoopAsync(string body, CancellationToken ct)
        {
            bool reported = false;

            while (!ct.IsCancellationRequested)
            {
                SwitchNetAccountClient client;

                lock (_presenceLock)
                {
                    client = _presenceClient?.Invoke();
                }

                if (client != null)
                {
                    try
                    {
                        (int status, _) = await client.SendAsync(HttpMethod.Post, "/emulator/v1/presence", body, ct).ConfigureAwait(false);

                        if (status >= 300 && !reported)
                        {
                            reported = true;
                            Logger.Warning?.Print(LogClass.ServiceFriend, $"GRID0+: presence not accepted ({status})");
                        }
                    }
                    catch (Exception e) when (e is SwitchNetLoginException or HttpRequestException)
                    {
                        if (!reported)
                        {
                            reported = true;
                            Logger.Warning?.Print(LogClass.ServiceFriend, $"GRID0+: presence not sent: {e.Message}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                try
                {
                    await Task.Delay(_presenceKeepAlive, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static List<Grid0FriendRequest> ReadRequests(JsonElement root, string key)
        {
            List<Grid0FriendRequest> requests = [];

            if (root.TryGetProperty(key, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    string id = entry.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? "" : "";
                    Grid0Friend other = entry.TryGetProperty("other", out JsonElement o) ? ParseFriend(o) : ParseFriend(default);

                    requests.Add(new Grid0FriendRequest(id, other));
                }
            }

            return requests;
        }

        private static Grid0Friend ParseFriend(JsonElement j)
        {
            string Str(string name) =>
                j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : "";

            bool favorite = j.ValueKind == JsonValueKind.Object
                && j.TryGetProperty("isFavorite", out JsonElement f)
                && f.ValueKind == JsonValueKind.True;

            ulong.TryParse(Str("id"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong nsaId);

            string login = Str("login");
            string nickname = Str("nickname");

            return new Grid0Friend(
                nsaId,
                string.IsNullOrEmpty(nickname) ? login : nickname,
                login,
                Str("friendCode"),
                Str("state"),
                Str("appId"),
                Str("imageUrl"),
                favorite,
                DecodeAppField(Str("appField")));
        }

        /// <summary>The game's presence blob, as the friend reported it; base64, either alphabet.</summary>
        private static byte[] DecodeAppField(string field)
        {
            if (string.IsNullOrEmpty(field))
            {
                return [];
            }

            string b64 = field.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');

            try
            {
                return Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                return [];
            }
        }

        private static async Task<JsonDocument> GetJsonAsync(SwitchNetAccountClient client, string path, CancellationToken ct)
        {
            (int status, string body) = await client.SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);

            if (status != 200)
            {
                throw new SwitchNetLoginException(DescribeProblem(status, body));
            }

            return JsonDocument.Parse(body);
        }

        private static async Task<string> ProblemAsync(
            SwitchNetAccountClient client, HttpMethod method, string path, string json, CancellationToken ct)
        {
            try
            {
                (int status, string body) = await client.SendAsync(method, path, json, ct).ConfigureAwait(false);

                return status is >= 200 and < 300 ? null : DescribeProblem(status, body);
            }
            catch (SwitchNetLoginException e)
            {
                return e.Message;
            }
        }

        /// <summary>The server's own message when it gave one, or a generic one.</summary>
        private static string DescribeProblem(int status, string body)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);

                foreach (string key in new[] { "message", "error" })
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty(key, out JsonElement e)
                        && e.ValueKind == JsonValueKind.String)
                    {
                        return e.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON; fall through.
            }

            return $"The server refused ({status}).";
        }
    }
}
