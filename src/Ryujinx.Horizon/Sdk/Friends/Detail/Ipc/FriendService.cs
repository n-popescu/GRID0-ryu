using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Account;
using Ryujinx.Horizon.Sdk.OsTypes;
using Ryujinx.Horizon.Sdk.Settings;
using Ryujinx.Horizon.Sdk.Sf;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Horizon.Sdk.Friends.Detail.Ipc
{
    partial class FriendService : IFriendService, IDisposable
    {
        private readonly IEmulatorAccountManager _accountManager;
        private SystemEventType _completionEvent;

        public FriendService(IEmulatorAccountManager accountManager, FriendsServicePermissionLevel permissionLevel)
        {
            _accountManager = accountManager;

            Os.CreateSystemEvent(out _completionEvent, EventClearMode.ManualClear, interProcess: true).AbortOnFailure();
            Os.SignalSystemEvent(ref _completionEvent); // TODO: Figure out where we are supposed to signal this.
        }

        [CmifCommand(0)]
        public Result GetCompletionEvent([CopyHandle] out int completionEventHandle)
        {
            completionEventHandle = Os.GetReadableHandleOfSystemEvent(ref _completionEvent);

            return Result.Success;
        }

        [CmifCommand(1)]
        public Result Cancel()
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend);

            return Result.Success;
        }

        [CmifCommand(10100)]
        public Result GetFriendListIds(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer)] Span<NetworkServiceAccountId> friendIds,
            Uid userId,
            int offset,
            SizedFriendFilter filter,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            count = 0;

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            // A game asks once, early (Splatoon 3 at boot), and keeps what it got for the whole
            // session; a short, bounded wait for the first fetch beats an empty list forever.
            IReadOnlyList<FriendsSourceEntry> friends = FriendsSource.Current?.GetFriends(TimeSpan.FromSeconds(2)) ?? [];

            for (int i = Math.Max(offset, 0); i < friends.Count && count < friendIds.Length; i++, count++)
            {
                friendIds[count] = new NetworkServiceAccountId(friends[i].NetworkServiceAccountId);
            }

            Logger.Info?.Print(LogClass.ServiceFriend, $"GetFriendListIds offset={offset} -> {count}");

            return Result.Success;
        }

        [CmifCommand(10101)]
        public Result GetFriendList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendImpl> friendList,
            Uid userId,
            int offset,
            SizedFriendFilter filter,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            count = 0;

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            IFriendsSource source = FriendsSource.Current;
            IReadOnlyList<FriendsSourceEntry> friends = source?.GetFriends(TimeSpan.Zero) ?? [];
            ulong application = source?.CurrentApplicationId ?? 0;

            for (int i = Math.Max(offset, 0); i < friends.Count && count < friendList.Length; i++, count++)
            {
                friendList[count] = MakeFriend(friends[i], application);
            }

            Logger.Info?.Print(LogClass.ServiceFriend, $"GetFriendList offset={offset} -> {count}");

            return Result.Success;
        }

        [CmifCommand(10102)]
        public Result UpdateFriendInfo(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendImpl> info,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            // One record per requested id, in the same order: the caller pairs answers with its
            // ids by position, so an id that cannot be resolved stays an invalid record.
            IFriendsSource source = FriendsSource.Current;
            IReadOnlyList<FriendsSourceEntry> friends = source?.GetFriends(TimeSpan.Zero) ?? [];
            ulong application = source?.CurrentApplicationId ?? 0;
            int resolved = 0;

            for (int i = 0; i < friendIds.Length && i < info.Length; i++)
            {
                info[i] = default;

                foreach (FriendsSourceEntry friend in friends)
                {
                    if (friend.NetworkServiceAccountId == friendIds[i].Id)
                    {
                        info[i] = MakeFriend(friend, application);
                        resolved++;

                        break;
                    }
                }
            }

            Logger.Debug?.Print(LogClass.ServiceFriend, $"UpdateFriendInfo {friendIds.Length} id(s) -> {resolved} resolved");

            return Result.Success;
        }

        [CmifCommand(10110)]
        public Result GetFriendProfileImage(
            out int size,
            Uid userId,
            NetworkServiceAccountId friendId,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> profileImage)
        {
            size = 0;

            // The game does not ask again: a short wait beats a placeholder for the session.
            byte[] jpeg = FriendsSource.Current?.GetProfileImage(friendId.Id, TimeSpan.FromMilliseconds(1500));

            if (jpeg != null && jpeg.Length > 0 && jpeg.Length <= profileImage.Length)
            {
                jpeg.CopyTo(profileImage);
                size = jpeg.Length;
            }

            return Result.Success;
        }

        [CmifCommand(10120)]
        public Result CheckFriendListAvailability(out bool listAvailable, Uid userId)
        {
            listAvailable = true;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(10121)]
        public Result EnsureFriendListAvailable(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            // nn::friends' AsyncContext waits on the completion event before reading a result.
            // A game that clears it after one wait would wait forever on the next, and
            // Splatoon 3 then never fetches its friend list; this command completes at once.
            Os.SignalSystemEvent(ref _completionEvent);

            FriendsSource.Current?.GetFriends(TimeSpan.Zero);

            return Result.Success;
        }

        [CmifCommand(10200)]
        public Result SendFriendRequestForApplication(
            Uid userId,
            NetworkServiceAccountId friendId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg2,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg3,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2, arg3, pidPlaceholder, pid });

            return Result.Success;
        }

        [CmifCommand(10211)]
        public Result AddFacedFriendRequestForApplication(
            Uid userId,
            FacedFriendRequestRegistrationKey key,
            Nickname nickname,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> arg3,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg5,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, key, nickname, arg4, arg5, pidPlaceholder, pid });

            return Result.Success;
        }

        [CmifCommand(10400)]
        public Result GetBlockedUserListIds(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer)] Span<NetworkServiceAccountId> blockedIds,
            Uid userId,
            int offset)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, offset });

            return Result.Success;
        }

        [CmifCommand(10420)]
        public Result CheckBlockedUserListAvailability(out bool listAvailable, Uid userId)
        {
            listAvailable = true;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(10421)]
        public Result EnsureBlockedUserListAvailable(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(10500)]
        public Result GetProfileList(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<ProfileImpl> profileList,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds)
        {
            string friendIdList = string.Join(", ", friendIds.ToArray());

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendIdList });

            return Result.Success;
        }

        [CmifCommand(10600)]
        public Result DeclareOpenOnlinePlaySession(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            _accountManager.OpenUserOnlinePlay(userId);

            return Result.Success;
        }

        [CmifCommand(10601)]
        public Result DeclareCloseOnlinePlaySession(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            if (userId.IsNull)
            {
                return FriendResult.InvalidArgument;
            }

            _accountManager.CloseUserOnlinePlay(userId);

            return Result.Success;
        }

        [CmifCommand(10610)]
        public Result UpdateUserPresence(
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0xE0)] in UserPresenceImpl userPresence,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, userPresence, pidPlaceholder, pid });

            // The status and the app key-value storage are what a friend needs to see this
            // player online and join them.
            ReadOnlySpan<byte> storage = userPresence.AppKeyValueStorage;
            FriendsSource.Current?.PublishPresence((uint)userPresence.Status, storage.ToArray());

            return Result.Success;
        }

        [CmifCommand(10700)]
        public Result GetPlayHistoryRegistrationKey(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x40)] out PlayHistoryRegistrationKey registrationKey,
            Uid userId,
            bool arg2)
        {
            if (userId.IsNull)
            {
                registrationKey = default;

                return FriendResult.InvalidArgument;
            }

            // NOTE: Calls nn::friends::detail::service::core::PlayHistoryManager::GetInstance and stores the instance.

            // NOTE: Calls nn::friends::detail::service::core::UuidManager::GetInstance and stores the instance.
            //       Then calls nn::friends::detail::service::core::AccountStorageManager::GetInstance and stores the instance.
            //       Then it checks if an Uuid is already stored for the UserId, if not it generates a random Uuid,
            //       and stores it in the savedata 8000000000000080 in the friends:/uid.bin file.

            /*

            NOTE: The service uses the KeyIndex to get a random key from a keys buffer (since the key index is stored in the returned buffer).
                  We currently don't support play history and online services so we can use a blank key for now.
                  Code for reference:

            byte[] hmacKey = new byte[0x20];

            HMACSHA256 hmacSha256 = new HMACSHA256(hmacKey);
            byte[]     hmacHash   = hmacSha256.ComputeHash(playHistoryRegistrationKeyBuffer);

            */

            Uid randomGuid = new();

            Guid.NewGuid().TryWriteBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref randomGuid, 1)));

            registrationKey = new()
            {
                Type = 0x101,
                KeyIndex = (byte)(Random.Shared.Next() & 7),
                UserIdBool = 0, // TODO: Find it.
                UnknownBool = (byte)(arg2 ? 1 : 0), // TODO: Find it.
                Reserved = new(),
                Uuid = randomGuid,
                HmacHash = new(),
            };

            return Result.Success;
        }

        [CmifCommand(10701)]
        public Result GetPlayHistoryRegistrationKeyWithNetworkServiceAccountId(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x40)] out PlayHistoryRegistrationKey registrationKey,
            NetworkServiceAccountId friendId,
            bool arg2)
        {
            registrationKey = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { friendId, arg2 });

            return Result.Success;
        }

        [CmifCommand(10702)]
        public Result AddPlayHistory(
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x40)] in PlayHistoryRegistrationKey registrationKey,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg2,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg3,
            ulong pidPlaceholder,
            [ClientProcessId] ulong pid)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, registrationKey, arg2, arg3, pidPlaceholder, pid });

            return Result.Success;
        }

        [CmifCommand(11000)]
        public Result GetProfileImageUrl(out Url imageUrl, Url url, int arg2)
        {
            imageUrl = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { url, arg2 });

            return Result.Success;
        }

        [CmifCommand(20100)]
        public Result GetFriendCount(out int count, Uid userId, SizedFriendFilter filter, ulong pidPlaceholder, [ClientProcessId] ulong pid)
        {
            count = FriendsSource.Current?.GetFriends(TimeSpan.Zero).Count ?? 0;

            return Result.Success;
        }

        [CmifCommand(20101)]
        public Result GetNewlyFriendCount(out int count, Uid userId)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20102)]
        public Result GetFriendDetailedInfo(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x800)] out FriendDetailedInfoImpl detailedInfo,
            Uid userId,
            NetworkServiceAccountId friendId)
        {
            detailedInfo = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(20103)]
        public Result SyncFriendList(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20104)]
        public Result RequestSyncFriendList(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20110)]
        public Result LoadFriendSetting(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x40)] out FriendSettingImpl friendSetting,
            Uid userId,
            NetworkServiceAccountId friendId)
        {
            friendSetting = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(20200)]
        public Result GetReceivedFriendRequestCount(out int count, out int count2, Uid userId)
        {
            count = 0;
            count2 = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20201)]
        public Result GetFriendRequestList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendRequestImpl> requestList,
            Uid userId,
            int arg3,
            int arg4)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg3, arg4 });

            return Result.Success;
        }

        [CmifCommand(20300)]
        public Result GetFriendCandidateList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendCandidateImpl> candidateList,
            Uid userId,
            int arg3)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20301)]
        public Result GetNintendoNetworkIdInfo(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x38)] out NintendoNetworkIdUserInfo networkIdInfo,
            out int arg1,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<NintendoNetworkIdFriendImpl> friendInfo,
            Uid userId,
            int arg4)
        {
            networkIdInfo = default;
            arg1 = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg4 });

            return Result.Success;
        }

        [CmifCommand(20302)]
        public Result GetSnsAccountLinkage(out SnsAccountLinkage accountLinkage, Uid userId)
        {
            accountLinkage = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20303)]
        public Result GetSnsAccountProfile(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x380)] out SnsAccountProfile accountProfile,
            Uid userId,
            NetworkServiceAccountId friendId,
            int arg3)
        {
            accountProfile = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20304)]
        public Result GetSnsAccountFriendList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<SnsAccountFriendImpl> friendList,
            Uid userId,
            int arg3)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20400)]
        public Result GetBlockedUserList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<BlockedUserImpl> blockedUsers,
            Uid userId,
            int arg3)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20401)]
        public Result SyncBlockedUserList(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20500)]
        public Result GetProfileExtraList(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<ProfileExtraImpl> extraList,
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds)
        {
            string friendIdList = string.Join(", ", friendIds.ToArray());

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendIdList });

            return Result.Success;
        }

        [CmifCommand(20501)]
        public Result GetRelationship(out Relationship relationship, Uid userId, NetworkServiceAccountId friendId)
        {
            relationship = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(20600)]
        public Result GetUserPresenceView([Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0xE0)] out UserPresenceViewImpl userPresenceView, Uid userId)
        {
            userPresenceView = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20700)]
        public Result GetPlayHistoryList(out int count, [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<PlayHistoryImpl> playHistoryList, Uid userId, int arg3)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg3 });

            return Result.Success;
        }

        [CmifCommand(20701)]
        public Result GetPlayHistoryStatistics(out PlayHistoryStatistics statistics, Uid userId)
        {
            statistics = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20800)]
        public Result LoadUserSetting([Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x800)] out UserSettingImpl userSetting, Uid userId)
        {
            userSetting = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20801)]
        public Result SyncUserSetting(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(20900)]
        public Result RequestListSummaryOverlayNotification()
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend);

            return Result.Success;
        }

        [CmifCommand(21000)]
        public Result GetExternalApplicationCatalog(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x4B8)] out ExternalApplicationCatalog catalog,
            ExternalApplicationCatalogId catalogId,
            LanguageCode language)
        {
            catalog = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { catalogId, language });

            return Result.Success;
        }

        [CmifCommand(22000)]
        public Result GetReceivedFriendInvitationList(
            out int count,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<FriendInvitationForViewerImpl> invitationList,
            Uid userId)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(22001)]
        public Result GetReceivedFriendInvitationDetailedInfo(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0x1400)] out FriendInvitationGroupImpl invicationGroup,
            Uid userId,
            FriendInvitationGroupId groupId)
        {
            invicationGroup = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, groupId });

            return Result.Success;
        }

        [CmifCommand(22010)]
        public Result GetReceivedFriendInvitationCountCache(out int count, Uid userId)
        {
            count = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(30100)]
        public Result DropFriendNewlyFlags(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(30101)]
        public Result DeleteFriend(Uid userId, NetworkServiceAccountId friendId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30110)]
        public Result DropFriendNewlyFlag(Uid userId, NetworkServiceAccountId friendId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30120)]
        public Result ChangeFriendFavoriteFlag(Uid userId, NetworkServiceAccountId friendId, bool favoriteFlag)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, favoriteFlag });

            return Result.Success;
        }

        [CmifCommand(30121)]
        public Result ChangeFriendOnlineNotificationFlag(Uid userId, NetworkServiceAccountId friendId, bool onlineNotificationFlag)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, onlineNotificationFlag });

            return Result.Success;
        }

        [CmifCommand(30200)]
        public Result SendFriendRequest(Uid userId, NetworkServiceAccountId friendId, int arg2)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2 });

            return Result.Success;
        }

        [CmifCommand(30201)]
        public Result SendFriendRequestWithApplicationInfo(
            Uid userId,
            NetworkServiceAccountId friendId,
            int arg2,
            ApplicationInfo applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg5)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2, applicationInfo, arg4, arg5 });

            return Result.Success;
        }

        [CmifCommand(30202)]
        public Result CancelFriendRequest(Uid userId, RequestId requestId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, requestId });

            return Result.Success;
        }

        [CmifCommand(30203)]
        public Result AcceptFriendRequest(Uid userId, RequestId requestId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, requestId });

            return Result.Success;
        }

        [CmifCommand(30204)]
        public Result RejectFriendRequest(Uid userId, RequestId requestId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, requestId });

            return Result.Success;
        }

        [CmifCommand(30205)]
        public Result ReadFriendRequest(Uid userId, RequestId requestId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, requestId });

            return Result.Success;
        }

        [CmifCommand(30210)]
        public Result GetFacedFriendRequestRegistrationKey(out FacedFriendRequestRegistrationKey registrationKey, Uid userId)
        {
            registrationKey = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(30211)]
        public Result AddFacedFriendRequest(
            Uid userId,
            FacedFriendRequestRegistrationKey registrationKey,
            Nickname nickname,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> arg3)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, registrationKey, nickname });

            return Result.Success;
        }

        [CmifCommand(30212)]
        public Result CancelFacedFriendRequest(Uid userId, NetworkServiceAccountId friendId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30213)]
        public Result GetFacedFriendRequestProfileImage(
            out int size,
            Uid userId,
            NetworkServiceAccountId friendId,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> profileImage)
        {
            size = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30214)]
        public Result GetFacedFriendRequestProfileImageFromPath(
            out int size,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<byte> path,
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<byte> profileImage)
        {
            size = 0;

            string pathString = Encoding.UTF8.GetString(path);

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { pathString });

            return Result.Success;
        }

        [CmifCommand(30215)]
        public Result SendFriendRequestWithExternalApplicationCatalogId(
            Uid userId,
            NetworkServiceAccountId friendId,
            int arg2,
            ExternalApplicationCatalogId catalogId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg5)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2, catalogId, arg4, arg5 });

            return Result.Success;
        }

        [CmifCommand(30216)]
        public Result ResendFacedFriendRequest(Uid userId, NetworkServiceAccountId friendId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30217)]
        public Result SendFriendRequestWithNintendoNetworkIdInfo(
            Uid userId,
            NetworkServiceAccountId friendId,
            int arg2,
            MiiName arg3,
            MiiImageUrlParam arg4,
            MiiName arg5,
            MiiImageUrlParam arg6)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2, arg3, arg4, arg5, arg6 });

            return Result.Success;
        }

        [CmifCommand(30300)]
        public Result GetSnsAccountLinkPageUrl([Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0x1000)] out WebPageUrl url, Uid userId, int arg2)
        {
            url = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg2 });

            return Result.Success;
        }

        [CmifCommand(30301)]
        public Result UnlinkSnsAccount(Uid userId, int arg1)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, arg1 });

            return Result.Success;
        }

        [CmifCommand(30400)]
        public Result BlockUser(Uid userId, NetworkServiceAccountId friendId, int arg2)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2 });

            return Result.Success;
        }

        [CmifCommand(30401)]
        public Result BlockUserWithApplicationInfo(
            Uid userId,
            NetworkServiceAccountId friendId,
            int arg2,
            ApplicationInfo applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer, 0x48)] in InAppScreenName arg4)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId, arg2, applicationInfo, arg4 });

            return Result.Success;
        }

        [CmifCommand(30402)]
        public Result UnblockUser(Uid userId, NetworkServiceAccountId friendId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendId });

            return Result.Success;
        }

        [CmifCommand(30500)]
        public Result GetProfileExtraFromFriendCode(
            [Buffer(HipcBufferFlags.Out | HipcBufferFlags.Pointer, 0x400)] out ProfileExtraImpl profileExtra,
            Uid userId,
            FriendCode friendCode)
        {
            profileExtra = default;

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendCode });

            return Result.Success;
        }

        [CmifCommand(30700)]
        public Result DeletePlayHistory(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(30810)]
        public Result ChangePresencePermission(Uid userId, int permission)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, permission });

            return Result.Success;
        }

        [CmifCommand(30811)]
        public Result ChangeFriendRequestReception(Uid userId, bool reception)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, reception });

            return Result.Success;
        }

        [CmifCommand(30812)]
        public Result ChangePlayLogPermission(Uid userId, int permission)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, permission });

            return Result.Success;
        }

        [CmifCommand(30820)]
        public Result IssueFriendCode(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(30830)]
        public Result ClearPlayLog(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(30900)]
        public Result SendFriendInvitation(
            Uid userId,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<NetworkServiceAccountId> friendIds,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias, 0xC00)] in FriendInvitationGameModeDescription description,
            ApplicationInfo applicationInfo,
            [Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<byte> arg4,
            bool arg5)
        {
            string friendIdList = string.Join(", ", friendIds.ToArray());

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, friendIdList, description, applicationInfo, arg5 });

            return Result.Success;
        }

        [CmifCommand(30910)]
        public Result ReadFriendInvitation(Uid userId, [Buffer(HipcBufferFlags.In | HipcBufferFlags.Pointer)] ReadOnlySpan<FriendInvitationId> invitationIds)
        {
            string invitationIdList = string.Join(", ", invitationIds.ToArray());

            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId, invitationIdList });

            return Result.Success;
        }

        [CmifCommand(30911)]
        public Result ReadAllFriendInvitations(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(40100)]
        public Result DeleteFriendListCache(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(40400)]
        public Result DeleteBlockedUserListCache(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        [CmifCommand(49900)]
        public Result DeleteNetworkServiceAccountCache(Uid userId)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceFriend, new { userId });

            return Result.Success;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Os.DestroySystemEvent(ref _completionEvent);
            }
        }

        private static Nickname MakeNickname(string name)
        {
            Array33<byte> bytes = new();
            byte[] utf8 = Encoding.UTF8.GetBytes(name ?? "");
            // 32 bytes at most, leaving the terminating NUL.
            utf8.AsSpan(0, Math.Min(utf8.Length, 32)).CopyTo(bytes.AsSpan());

            return new Nickname(bytes);
        }

        /// <summary>nn::friends::detail::FriendImpl for one GRID0+ friend.</summary>
        private static FriendImpl MakeFriend(FriendsSourceEntry friend, ulong currentApplication)
        {
            Uid uid = new(0x1100000000000000UL, friend.NetworkServiceAccountId);

            FriendImpl record = new()
            {
                UserId = uid,
                NetworkUserId = new NetworkServiceAccountId(friend.NetworkServiceAccountId),
                Nickname = MakeNickname(friend.Nickname),
                IsFavourite = friend.IsFavorite,
                IsValid = true,
            };

            record.Presence.UserId = uid;
            record.Presence.LastTimeOnlineTimestamp = long.MaxValue;
            record.Presence.Status = (PresenceStatus)friend.PresenceStatus;
            // A friend in the same game is one the game asks about joining.
            record.Presence.SamePresenceGroupApplication =
                friend.PresenceStatus == 2 && friend.ApplicationId != 0 && friend.ApplicationId == currentApplication;

            Span<byte> storage = record.Presence.AppKeyValueStorage;
            friend.AppKeyValueStorage.AsSpan(0, Math.Min(friend.AppKeyValueStorage.Length, storage.Length)).CopyTo(storage);

            return record;
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
