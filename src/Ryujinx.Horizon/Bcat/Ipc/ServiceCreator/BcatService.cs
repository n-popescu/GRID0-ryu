using LibHac.Bcat;
using Ryujinx.Horizon.Bcat.Types;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Bcat;
using Ryujinx.Horizon.Sdk.Sf;

namespace Ryujinx.Horizon.Bcat.Ipc
{
    partial class BcatService : IBcatService
    {
        public BcatService(BcatServicePermissionLevel permissionLevel) { }

        [CmifCommand(10100)]
        public Result RequestSyncDeliveryCache(out IDeliveryCacheProgressService deliveryCacheProgressService)
        {
            deliveryCacheProgressService = new DeliveryCacheProgressService();

            return Result.Success;
        }

        // Splatoon 3 asks for its fest directory by name once a fest is announced. The
        // delivery cache is already synced from GRID0+ before the game starts, so this
        // finishes at once, like 10100.
        [CmifCommand(10101)]
        public Result RequestSyncDeliveryCacheWithDirectoryName(DirectoryName directoryName, out IDeliveryCacheProgressService deliveryCacheProgressService)
        {
            deliveryCacheProgressService = new DeliveryCacheProgressService();

            return Result.Success;
        }

        [CmifCommand(10200)]
        public Result CancelSyncDeliveryCacheRequest()
        {
            return Result.Success;
        }
    }
}
