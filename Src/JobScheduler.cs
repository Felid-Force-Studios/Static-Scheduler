using System;
using System.Runtime.CompilerServices;
using FFS.Libraries.StaticPack;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace FFS.Libraries.StaticScheduler {
    /// <summary>
    /// Lock-free work-stealing job scheduler with a dependency graph, mirroring the Unity Job System
    /// for pure .NET. Owns a fixed pool of background worker threads (plus the owner thread as the
    /// last worker); schedule jobs with <c>Schedule</c> / <c>ScheduleParallel</c>, wire ordering with dependencies and
    /// <see cref="Combine(JobHandle,JobHandle)"/>, then block-help via <see cref="Complete"/>.
    /// <para>
    /// Thread model: the owner is the thread of the first call; <see cref="TransferOwnershipToCurrentThread"/> moves it
    /// while no job is in flight. <c>Schedule</c>, <c>ScheduleParallel</c>, <c>ScheduleParallelDeferred</c> and
    /// <c>Combine</c> run on the owner or inside a running job; <see cref="Complete"/>, <see cref="CompleteWithoutHelping"/>,
    /// <see cref="CompleteAll"/> and <see cref="ResetFrame"/> run on the owner outside any job body; <see cref="Dispose"/>
    /// runs on any thread outside a job body; <see cref="IsCompleted"/> runs on any thread.
    /// </para>
    /// <para>
    /// Checks: <c>FFS_SCHEDULER_DEBUG</c> (from <c>DEBUG</c> or <c>FFS_SCHEDULER_ENABLE_DEBUG</c>, off with
    /// <c>FFS_SCHEDULER_DISABLE_DEBUG</c>) adds the argument checks and reports jobs left pending at
    /// <see cref="ResetFrame"/>; the thread, disposal and handle checks are on at that level and otherwise unless
    /// <c>FFS_SCHEDULER_DISABLE_CHECKS</c>;
    /// the exceptions of job bodies are captured unless <c>FFS_SCHEDULER_DISABLE_CATCH</c>.
    /// </para>
    /// <para>Bodies are raw function pointers (<c>delegate*&lt;void*,int,int,void&gt;</c>); the <c>userData</c> pointer must
    /// stay alive until the job completes.</para>
    /// </summary>
    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    public sealed unsafe partial class JobScheduler : IDisposable {
        // Each call into the engine ends with GC.KeepAlive(this), so the finalizer never stops the pool during a call.
        internal readonly SchedulerEngine Engine;

        /// <summary>Creates a scheduler from <paramref name="config"/>; an invalid configuration or a failed allocation throws <see cref="SchedulerException"/>.</summary>
        public JobScheduler(in SchedulerConfig config) {
            Engine = new SchedulerEngine(config);
        }

        /// <summary>Stops the worker threads and releases the native memory of a scheduler that was never disposed.</summary>
        ~JobScheduler() {
            Engine?.DestroyFromFinalizer();
        }

        /// <summary>Number of workers, including the owner thread. 1 in synchronous mode.</summary>
        public int WorkerCount => Engine.WorkerCount;

        /// <summary>
        /// Whether the scheduler was created in the synchronous mode (<see cref="SchedulerConfig.WorkerCount"/> 0): no worker
        /// threads, the owner runs every job inside <c>Complete</c>.
        /// </summary>
        public bool IsSynchronous => Engine.IsSynchronous;

        /// <summary>
        /// Rank of the calling thread in this scheduler: a worker thread gets its rank (0 .. <see cref="WorkerCount"/> - 2),
        /// any other thread the owner rank (<see cref="WorkerCount"/> - 1). Per-worker scratch is indexed by it.
        /// </summary>
        public int CurrentThreadRank => Engine.CurrentThreadRank;

        /// <summary>
        /// Whether the calling thread runs inside a job body of this scheduler: a worker thread of the scheduler, or the
        /// owner while it runs batches inside <see cref="Complete"/>, <see cref="CompleteAll"/>, <see cref="ResetFrame"/>
        /// or <see cref="Dispose"/>, also from a help loop of another scheduler nested in such a body. Holds at every check
        /// level.
        /// </summary>
        public bool IsInsideJobBody => Engine.IsInsideJobBody;

        #region SINGLE JOB
        /// <summary>Schedules a single job with no dependencies; the body runs once as [0, 1).</summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle Schedule(delegate*<void*, int, int, void> body, void* userData) {
            var result = Engine.ScheduleNode(body, userData, 1, null, 1, null, 0);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>Schedules a single job that runs after <paramref name="dependency"/> completes.</summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle Schedule(delegate*<void*, int, int, void> body, void* userData, JobHandle dependency) {
            var result = Engine.ScheduleNode(body, userData, 1, null, 1, &dependency, 1);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>Schedules a single job that runs after all <paramref name="dependencies"/> complete.</summary>
        public JobHandle Schedule(delegate*<void*, int, int, void> body, void* userData, ReadOnlySpan<JobHandle> dependencies) {
            fixed (JobHandle* pointer = dependencies) {
                var result = Engine.ScheduleNode(body, userData, 1, null, 1, pointer, dependencies.Length);
                GC.KeepAlive(this);
                return result;
            }
        }

        /// <summary>Schedules a single job after <paramref name="dependencyCount"/> handles at <paramref name="dependencies"/>.</summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle Schedule(delegate*<void*, int, int, void> body, void* userData, JobHandle* dependencies, int dependencyCount) {
            var result = Engine.ScheduleNode(body, userData, 1, null, 1, dependencies, dependencyCount);
            GC.KeepAlive(this);
            return result;
        }
        #endregion

        #region PARALLEL FOR
        /// <summary>Schedules a parallel-for over [0, length) in batches of <paramref name="batchSize"/>, no dependencies.</summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle ScheduleParallel(delegate*<void*, int, int, void> body, void* userData, int length, int batchSize) {
            var result = Engine.ScheduleNode(body, userData, length, null, batchSize, null, 0);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>Schedules a parallel-for that runs after <paramref name="dependency"/> completes.</summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle ScheduleParallel(delegate*<void*, int, int, void> body, void* userData, int length, int batchSize, JobHandle dependency) {
            var result = Engine.ScheduleNode(body, userData, length, null, batchSize, &dependency, 1);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>Schedules a parallel-for that runs after all <paramref name="dependencies"/> complete.</summary>
        public JobHandle ScheduleParallel(delegate*<void*, int, int, void> body, void* userData, int length, int batchSize, ReadOnlySpan<JobHandle> dependencies) {
            fixed (JobHandle* pointer = dependencies) {
                var result = Engine.ScheduleNode(body, userData, length, null, batchSize, pointer, dependencies.Length);
                GC.KeepAlive(this);
                return result;
            }
        }

        /// <summary>
        /// Core parallel-for scheduling. <paramref name="body"/> is invoked once per batch as
        /// (userData, startInclusive, endExclusive); a batch runs entirely on one worker. A <paramref name="batchSize"/>
        /// below 1 counts as 1, a <paramref name="length"/> of 0 or below schedules no batch, a null body is a barrier.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle ScheduleParallel(delegate*<void*, int, int, void> body, void* userData, int length, int batchSize, JobHandle* dependencies, int dependencyCount) {
            var result = Engine.ScheduleNode(body, userData, length, null, batchSize, dependencies, dependencyCount);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>
        /// Schedules a parallel-for whose length is only known once its dependencies have completed:
        /// <paramref name="deferredLength"/> is read at that moment instead of now. Use it when a producing job
        /// computes how much work its consumer has to do. Batches are cut at <paramref name="batchSize"/>, so pick a
        /// granularity rather than relying on a per-worker split of a known length.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle ScheduleParallelDeferred(delegate*<void*, int, int, void> body, void* userData, int* deferredLength, int batchSize, JobHandle* dependencies, int dependencyCount) {
            var result = Engine.ScheduleDeferred(body, userData, deferredLength, batchSize, dependencies, dependencyCount);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>The deferred parallel job of <c>ScheduleParallelDeferred</c> after a single dependency.</summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle ScheduleParallelDeferred(delegate*<void*, int, int, void> body, void* userData, int* deferredLength, int batchSize, JobHandle dependency) {
            var result = Engine.ScheduleDeferred(body, userData, deferredLength, batchSize, &dependency, 1);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>The deferred parallel job of <c>ScheduleParallelDeferred</c> with no dependencies.</summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle ScheduleParallelDeferred(delegate*<void*, int, int, void> body, void* userData, int* deferredLength, int batchSize) {
            var result = Engine.ScheduleDeferred(body, userData, deferredLength, batchSize, null, 0);
            GC.KeepAlive(this);
            return result;
        }
        #endregion

        #region COMBINE
        /// <summary>Combines two handles into one that completes when both complete.</summary>
        public JobHandle Combine(JobHandle a, JobHandle b) {
            var pair = stackalloc JobHandle[2];
            pair[0] = a;
            pair[1] = b;
            var result = Engine.ScheduleNode(null, null, 0, null, 1, pair, 2);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>Combines handles into one that completes when all complete.</summary>
        public JobHandle Combine(ReadOnlySpan<JobHandle> dependencies) {
            fixed (JobHandle* pointer = dependencies) {
                var result = Engine.ScheduleNode(null, null, 0, null, 1, pointer, dependencies.Length);
                GC.KeepAlive(this);
                return result;
            }
        }

        /// <summary>Combines <paramref name="dependencyCount"/> handles at <paramref name="dependencies"/> into one.</summary>
        [MethodImpl(AggressiveInlining)]
        public JobHandle Combine(JobHandle* dependencies, int dependencyCount) {
            var result = Engine.ScheduleNode(null, null, 0, null, 1, dependencies, dependencyCount);
            GC.KeepAlive(this);
            return result;
        }
        #endregion

        #region COMPLETION
        /// <summary>Non-blocking check whether <paramref name="handle"/>'s job has completed; callable from any thread.</summary>
        public bool IsCompleted(JobHandle handle) {
            var result = Engine.IsCompleted(handle);
            GC.KeepAlive(this);
            return result;
        }

        /// <summary>
        /// Blocks the owner until <paramref name="handle"/> completes, running queued batches meanwhile (help-on-complete).
        /// Throws <see cref="SchedulerException"/> carrying the exceptions of the failed jobs <paramref name="handle"/>
        /// depends on, itself included.
        /// </summary>
        public void Complete(JobHandle handle) {
            Engine.Complete(handle, true);
            GC.KeepAlive(this);
        }

        /// <summary>
        /// Blocks the owner until <paramref name="handle"/> completes without running other batches meanwhile, so it returns
        /// as soon as the job is done; with no worker threads it runs the work like <see cref="Complete"/>. Reports the
        /// failures like <see cref="Complete"/>.
        /// </summary>
        public void CompleteWithoutHelping(JobHandle handle) {
            Engine.Complete(handle, false);
            GC.KeepAlive(this);
        }

        /// <summary>
        /// Blocks the owner until every scheduled job of the frame has completed, running queued batches meanwhile. Uses no
        /// node of the pool, so an unwinding caller can rely on it; failures stay for <see cref="ResetFrame"/> to report.
        /// </summary>
        public void CompleteAll() {
            Engine.CompleteAll();
            GC.KeepAlive(this);
        }

        /// <summary>
        /// Ends the current frame: completes every job still in flight, returns all node/edge pool slots and advances the
        /// handle generation (prior-frame handles become stale). Throws <see cref="SchedulerException"/> afterwards for the
        /// failures no <c>Complete</c> reported, and under <c>FFS_SCHEDULER_DEBUG</c> when jobs were still in flight; the
        /// scheduler is reset either way.
        /// </summary>
        public void ResetFrame() {
            Engine.ResetFrame();
            GC.KeepAlive(this);
        }
        #endregion

        #region OWNERSHIP AND LIFETIME
        /// <summary>
        /// Binds an unbound scheduler to the calling thread and throws <see cref="SchedulerException"/> when the calling
        /// thread is not the owner or runs inside a job body, the check <see cref="Complete"/> makes. A caller that changes
        /// its own state before its first scheduler call checks the thread with it first. A no-op under
        /// <c>FFS_SCHEDULER_DISABLE_CHECKS</c> outside the <c>FFS_SCHEDULER_DEBUG</c> level.
        /// </summary>
        public void EnsureOwnerThread() {
            Engine.EnsureOwnerThreadOutsideBodies();
            GC.KeepAlive(this);
        }

        /// <summary>Makes the calling thread the owner; allowed while no job is in flight.</summary>
        public void TransferOwnershipToCurrentThread() {
            Engine.TransferOwnershipToCurrentThread();
            GC.KeepAlive(this);
        }

        /// <summary>
        /// Completes the jobs still in flight, stops the worker pool and releases native memory. Idempotent. Runs on any
        /// thread outside a job body: the owner helps with the work in flight, another thread waits for the worker threads;
        /// with jobs in flight and no worker threads (<see cref="WorkerCount"/> 1) another thread throws
        /// <see cref="SchedulerException"/>, since only the owner runs the jobs then.
        /// </summary>
        public void Dispose() {
            Engine.Dispose();
            GC.SuppressFinalize(this);
        }
        #endregion
    }

    /// <summary>
    /// Opaque handle to a scheduled job (or a combined dependency barrier). A cheap value type
    /// (index + generation version). <c>default</c> equals <see cref="Null"/> — "no job" — and is treated as already
    /// completed by <see cref="JobScheduler.IsCompleted"/>, <see cref="JobScheduler.Complete"/> and as a dependency.
    /// Handles are valid only within the frame in which they were created; <see cref="JobScheduler.ResetFrame"/>
    /// invalidates all prior-frame handles (a stale handle is reported unless the checks are disabled).
    /// </summary>
    public readonly struct JobHandle : IEquatable<JobHandle> {
        internal readonly int NodeIndex;
        internal readonly int Version;

        internal JobHandle(int nodeIndex, int version) {
            NodeIndex = nodeIndex;
            Version = version;
        }

        /// <summary>Empty handle — represents "no job"; treated as already completed. Equal to <c>default</c>.</summary>
        public static readonly JobHandle Null = default;

        /// <summary>Whether this handle stands for no job.</summary>
        public bool IsNull => Version == 0;

        /// <summary>Whether both handles stand for the same job of the same frame.</summary>
        public bool Equals(JobHandle other) => NodeIndex == other.NodeIndex && Version == other.Version;

        /// <inheritdoc cref="Equals(JobHandle)"/>
        public override bool Equals(object obj) => obj is JobHandle other && Equals(other);

        /// <summary>Hash of the node index and the version.</summary>
        public override int GetHashCode() => (NodeIndex * 397) ^ Version;

        /// <inheritdoc cref="Equals(JobHandle)"/>
        public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);

        /// <summary>Whether the handles stand for different jobs.</summary>
        public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);
    }

    /// <summary>What happens to the jobs that depend on a job whose body threw an exception.</summary>
    public enum SchedulerFaultPolicy : byte {
        /// <summary>The dependent jobs do not run their bodies; the cancellation follows the dependency edges.</summary>
        CancelDependents,
        /// <summary>The dependent jobs run as if nothing failed.</summary>
        RunDependents,
        /// <summary>The process ends at once through <c>Environment.FailFast</c> with the body exception.</summary>
        FailFast,
    }

    /// <summary>Configuration for constructing a <see cref="JobScheduler"/>.</summary>
    public struct SchedulerConfig {
        /// <summary>
        /// Total worker count including the owner thread, 0 or above. <c>0</c> selects the synchronous mode: no worker
        /// threads, the owner runs every job inside <c>Complete</c> (single-core platforms, WebGL).
        /// Positive values are clamped to <see cref="Environment.ProcessorCount"/>.
        /// </summary>
        public int WorkerCount;

        /// <summary>
        /// Time in microseconds a worker keeps polling for work after it ran out of it, before it sleeps. <c>0</c> sleeps at
        /// once. Default 100.
        /// </summary>
        public int SpinMicroseconds;

        /// <summary>
        /// Initial per-worker deque capacity hint, 0 to 2^28 (rounded up to a power of two). The deques grow on demand up to
        /// 2^28 batches each. <c>0</c> selects 4096.
        /// </summary>
        public long InitialCapacity;

        /// <summary>Job-node pool capacity per frame, at most 2^24. <c>0</c> derives it from <see cref="InitialCapacity"/>.</summary>
        public int NodeCapacity;

        /// <summary>Job-edge pool capacity per frame, at most 2^26. <c>0</c> derives it from the node capacity.</summary>
        public int EdgeCapacity;

        /// <summary>Prefix for background worker thread names. Default <c>"FFS.StaticScheduler."</c>.</summary>
        public string ThreadNamePrefix;

        /// <summary>
        /// Native allocator of the scheduler's deques, ring buffers and job graph pools: a created allocator that outlives
        /// the scheduler. When unset, <see cref="PackMemory.Default()"/> is used.
        /// </summary>
        public PackAllocator? Allocator;

        /// <summary>What happens to the dependents of a job whose body threw. Default <see cref="SchedulerFaultPolicy.CancelDependents"/>.</summary>
        public SchedulerFaultPolicy FaultPolicy;

        /// <summary>Multi-threaded configuration using all logical processors.</summary>
        public static SchedulerConfig Default() => new() {
            WorkerCount = Environment.ProcessorCount,
            SpinMicroseconds = 100,
            InitialCapacity = 4096,
            NodeCapacity = 0,
            EdgeCapacity = 0,
            ThreadNamePrefix = "FFS.StaticScheduler.",
            FaultPolicy = SchedulerFaultPolicy.CancelDependents
        };

        /// <summary>Synchronous configuration: no worker threads, the owner runs every job inside <c>Complete</c>.</summary>
        public static SchedulerConfig SingleThreaded() => new() {
            WorkerCount = 0,
            SpinMicroseconds = 0,
            InitialCapacity = 64,
            NodeCapacity = 0,
            EdgeCapacity = 0,
            ThreadNamePrefix = "FFS.StaticScheduler.",
            FaultPolicy = SchedulerFaultPolicy.CancelDependents
        };
    }

    /// <summary>
    /// Exception thrown by <see cref="JobScheduler"/>: an exception of a job body reported by <c>Complete</c> or
    /// <c>ResetFrame</c> (inner exception), a node or edge pool overflow, an invalid configuration, a construction failure,
    /// a call from a thread or a place the contract does not allow, a stale handle, or a worker join timeout.
    /// </summary>
    public sealed class SchedulerException : Exception {
        /// <summary>An exception with <paramref name="message"/>.</summary>
        public SchedulerException(string message) : base(message) { }

        /// <summary>An exception with <paramref name="message"/> carrying the exception that caused it.</summary>
        public SchedulerException(string message, Exception innerException) : base(message, innerException) { }
    }
}
