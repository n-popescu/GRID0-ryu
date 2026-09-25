using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;
using System.IO;

namespace Ryujinx.HLE.HOS
{
    class ServiceCtx
    {
        public Switch Device { get; }
        public KProcess Process { get; }
        public IVirtualMemoryManager Memory { get; }
        public KThread Thread { get; }
        public IpcMessage Request { get; }
        public IpcMessage Response { get; }
        public BinaryReader RequestData { get; }
        public BinaryWriter ResponseData { get; }
        public ulong ClientProcessId => Request.HandleDesc is { HasPId: true } ? Request.HandleDesc.PId : Process.Pid;

        /// <summary>
        /// Set when this call is the server re-running a request it earlier deferred, rather than
        /// a fresh request. See <see cref="Services.DeferredReply"/>.
        /// </summary>
        public Services.DeferredReply Retry { get; init; }

        /// <summary>
        /// Set by a handler that wants its reply held back instead of sent now. See
        /// <see cref="Services.DeferredReply"/>.
        /// </summary>
        public Services.DeferredReply DeferralRequest { get; private set; }

        public void Defer(Services.DeferredReply reply)
        {
            DeferralRequest = reply;
        }

        public ServiceCtx(
            Switch device,
            KProcess process,
            IVirtualMemoryManager memory,
            KThread thread,
            IpcMessage request,
            IpcMessage response,
            BinaryReader requestData,
            BinaryWriter responseData)
        {
            Device = device;
            Process = process;
            Memory = memory;
            Thread = thread;
            Request = request;
            Response = response;
            RequestData = requestData;
            ResponseData = responseData;
        }
    }
}
