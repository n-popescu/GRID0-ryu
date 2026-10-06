using System;
using System.Collections.Generic;

namespace Ryujinx.Horizon.Sdk.Friends
{
    /// <summary>A friend as a private server reports one, for the friend service to answer with.</summary>
    public sealed record FriendsSourceEntry(
        ulong NetworkServiceAccountId,
        string Nickname,
        uint PresenceStatus,
        ulong ApplicationId,
        byte[] AppKeyValueStorage,
        bool IsFavorite);

    /// <summary>
    /// Where the friend service gets a real friend list from, when the emulator is signed in to a
    /// server that has one (GRID0+). Set by HLE, which owns the login; null leaves the service
    /// answering with no friends, as it always has.
    /// </summary>
    public interface IFriendsSource
    {
        /// <summary>The cached list, waiting up to <paramref name="wait"/> for a first fetch.</summary>
        IReadOnlyList<FriendsSourceEntry> GetFriends(TimeSpan wait);

        /// <summary>A friend's profile picture (JPEG), or null.</summary>
        byte[] GetProfileImage(ulong networkServiceAccountId, TimeSpan wait);

        /// <summary>The application currently running, to tell a friend in the same game.</summary>
        ulong CurrentApplicationId { get; }

        /// <summary>This player's presence, as the game sets it: status and its key-value storage.</summary>
        void PublishPresence(uint status, byte[] appKeyValueStorage);
    }

    public static class FriendsSource
    {
        public static IFriendsSource Current { get; set; }
    }
}
