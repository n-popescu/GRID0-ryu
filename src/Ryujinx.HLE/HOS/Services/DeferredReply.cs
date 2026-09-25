namespace Ryujinx.HLE.HOS.Services
{
    /// <summary>
    /// A request whose reply the server holds back, then re-runs until it has an answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every emulated service runs on one host thread per server. A handler that blocks inside a
    /// request blocks that whole server, including every other request that could have unblocked
    /// it. That is a deadlock, not a slowdown, when the thing being waited on can only be produced
    /// by a later request to the same server.
    /// </para>
    /// <para>
    /// This is exactly what a gRPC client does with <c>bsd:</c>: one thread calls
    /// <c>poll()</c> on its sockets plus a wakeup eventfd with no timeout, and another thread
    /// wakes it by writing that eventfd -- a <c>Write</c> request to the same <c>bsd:</c> server
    /// that is busy blocking in the poll. Splatoon 3's NPLN client is built on gRPC, which is why
    /// it sat on "connecting" forever while every NEX title, which has no eventfd in its polls,
    /// was fine.
    /// </para>
    /// <para>
    /// A handler that would block calls <see cref="ServiceCtx.Defer"/> and returns without
    /// answering. The server loop keeps serving other requests, and re-runs the deferred one
    /// (with <see cref="ServiceCtx.Retry"/> set) whenever something may have changed, until
    /// the handler reports <see cref="Ready"/> or <see cref="DeadlineMs"/> passes.
    /// </para>
    /// </remarks>
    class DeferredReply
    {
        /// <summary>Absolute <c>PerformanceCounter.ElapsedMilliseconds</c>; <c>long.MaxValue</c> for none.</summary>
        public long DeadlineMs { get; init; }

        /// <summary>
        /// The request's input, copied at deferral time. A pointer (X) buffer is delivered into
        /// the server's shared receive area, which the very next request overwrites, so a retry
        /// has to read this rather than guest memory.
        /// </summary>
        public byte[] Input { get; init; }

        /// <summary>Set by a retry that produced a real answer.</summary>
        public bool Ready { get; set; }

        internal int SessionHandle;
        internal Ipc.IpcMessage Request;
    }
}
