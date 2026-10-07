#if ((DEBUG || FFS_SCHEDULER_ENABLE_DEBUG) && !FFS_SCHEDULER_DISABLE_DEBUG)
#define FFS_SCHEDULER_DEBUG
#endif
#if (FFS_SCHEDULER_DEBUG || !FFS_SCHEDULER_DISABLE_CHECKS)
#define FFS_SCHEDULER_CHECKS
#endif
#if !FFS_SCHEDULER_DISABLE_CATCH
#define FFS_SCHEDULER_CATCH
#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using FFS.Libraries.StaticPack;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace FFS.Libraries.StaticScheduler {
    public sealed unsafe partial class JobScheduler {
        #if ENABLE_IL2CPP
        [Il2CppSetOption(Option.NullChecks, false)]
        [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
        #endif
        internal sealed class SchedulerEngine {
            internal const long MaxInitialCapacity = ChaseLevDeque.MaxCapacity;
            internal const int MaxNodeCapacity = 1 << 24;
            internal const int MaxEdgeCapacity = 1 << 26;
            private const long DefaultInitialCapacity = 4096;
            private const long MinDequeCapacity = 64;
            private const string DefaultThreadNamePrefix = "FFS.StaticScheduler.";
            private const int WorkerJoinTimeoutMilliseconds = 10_000;
            private const int RegistrationGuard = 1;
            private const int SpinStepIterations = 16;
            private const int BackoffSpinSteps = 32;
            private const int BackoffMaxSpinIterations = 16;
            private const int MaxFaultRecords = 64;
            private const int SharedFaultSlot = 63;

            [ThreadStatic] private static SchedulerEngine _workerEngine;
            [ThreadStatic] private static int _workerRank;

            private readonly PackAllocator _allocator;
            private readonly int _workerCount;
            private readonly int _ownerRank;
            private readonly bool _synchronous;
            private readonly long _spinTicks;
            private readonly string _threadNamePrefix;
            private readonly SchedulerFaultPolicy _faultPolicy;
            private readonly int _nodeCapacity;
            private readonly int _edgeCapacity;

            private ChaseLevDeque* _deques;
            private Thread[] _threads;
            private WorkerSleepGate _sleepGate;

            private JobNode* _nodes;
            private JobEdge* _edges;
            private PaddedCounter _nodeCount;
            private PaddedCounter _edgeCount;
            private int _generation;
            private PaddedCounter _activeNodes;

            private PaddedCounter _idleCount;
            private volatile bool _disposing;
            private bool _disposed;
            private int _ownerThreadId;
            private int _helpingThreadId;
            private int _helpDepth;

            private readonly List<FaultRecord> _faultRecords = new();
            private int _faultSlotCount;
            private int _droppedFaultCount;

            internal SchedulerEngine(in SchedulerConfig config) {
                ValidateConfig(config);
                _allocator = config.Allocator ?? PackMemory.Default();
                _synchronous = config.WorkerCount <= 0;
                _workerCount = _synchronous ? 1 : Math.Min(Environment.ProcessorCount, config.WorkerCount);
                _ownerRank = _workerCount - 1;
                _spinTicks = config.SpinMicroseconds * Stopwatch.Frequency / 1_000_000;
                _threadNamePrefix = string.IsNullOrEmpty(config.ThreadNamePrefix) ? DefaultThreadNamePrefix : config.ThreadNamePrefix;
                _faultPolicy = config.FaultPolicy;

                var initialCapacity = config.InitialCapacity > 0 ? config.InitialCapacity : DefaultInitialCapacity;
                var dequeCapacity = SchedulerMath.NextPowerOfTwo(Math.Max(MinDequeCapacity, initialCapacity));
                _nodeCapacity = config.NodeCapacity > 0 ? config.NodeCapacity : (int) Math.Min(MaxNodeCapacity, dequeCapacity * 2);
                _edgeCapacity = config.EdgeCapacity > 0 ? config.EdgeCapacity : (int) Math.Min(MaxEdgeCapacity, (long) _nodeCapacity * 4);
                _generation = 1;

                try {
                    _deques = _allocator.AllocAligned<ChaseLevDeque>(_workerCount);
                    for (var workerRank = 0; workerRank < _workerCount; workerRank++) {
                        var nonZeroRandomSeed = ((uint) workerRank * 2654435761u) | 1u;
                        _deques[workerRank].Init(dequeCapacity, nonZeroRandomSeed, _allocator);
                    }
                    _nodes = _allocator.Alloc<JobNode>(_nodeCapacity, clear: false);
                    _edges = _allocator.Alloc<JobEdge>(_edgeCapacity, clear: false);
                    StartWorkers();
                }
                catch (Exception exception) {
                    Destroy(false);
                    throw new SchedulerException("The job scheduler could not be created", exception);
                }
            }

            private static void ValidateConfig(in SchedulerConfig config) {
                if (config.WorkerCount < 0)
                    throw new SchedulerException("SchedulerConfig.WorkerCount must not be negative, got " + config.WorkerCount);
                if (config.InitialCapacity < 0 || config.InitialCapacity > MaxInitialCapacity)
                    throw new SchedulerException("SchedulerConfig.InitialCapacity must be between 0 and 2^28, got " + config.InitialCapacity);
                if (config.NodeCapacity < 0 || config.NodeCapacity > MaxNodeCapacity)
                    throw new SchedulerException("SchedulerConfig.NodeCapacity must be between 0 and 2^24, got " + config.NodeCapacity);
                if (config.EdgeCapacity < 0 || config.EdgeCapacity > MaxEdgeCapacity)
                    throw new SchedulerException("SchedulerConfig.EdgeCapacity must be between 0 and 2^26, got " + config.EdgeCapacity);
                if (config.SpinMicroseconds < 0)
                    throw new SchedulerException("SchedulerConfig.SpinMicroseconds must not be negative, got " + config.SpinMicroseconds);
                if (config.FaultPolicy > SchedulerFaultPolicy.FailFast)
                    throw new SchedulerException("SchedulerConfig.FaultPolicy has no value " + (int) config.FaultPolicy);
                if (config.Allocator.HasValue && !config.Allocator.Value.IsCreated)
                    throw new SchedulerException("SchedulerConfig.Allocator is an allocator that was never created");
            }

            private void StartWorkers() {
                var backgroundWorkerCount = _workerCount - 1;
                if (backgroundWorkerCount == 0) return;
                _sleepGate = new WorkerSleepGate();
                _threads = new Thread[backgroundWorkerCount];
                for (var index = 0; index < backgroundWorkerCount; index++) {
                    var workerRank = index;
                    var thread = new Thread(() => WorkerLoop(workerRank)) {
                        IsBackground = true,
                        Name = _threadNamePrefix + "Worker#" + workerRank
                    };
                    #if FFS_SCHEDULER_TEST_HOOKS
                    SchedulerTestHooks.Reach(SchedulerTestPoint.BeforeWorkerThreadStart, workerRank);
                    #endif
                    thread.Start();
                    _threads[index] = thread;
                }
            }

            internal int WorkerCount => _workerCount;
            internal bool IsSynchronous => _synchronous;
            internal int CurrentThreadRank => _workerEngine == this ? _workerRank : _ownerRank;
            internal bool IsInsideJobBody => _workerEngine == this || Volatile.Read(ref _helpingThreadId) == Environment.CurrentManagedThreadId;

            #region THREAD ROLES
            [MethodImpl(AggressiveInlining)]
            private int SchedulingRank() {
                if (_workerEngine == this) return _workerRank;
                #if FFS_SCHEDULER_CHECKS
                EnsureOwnerThread("Schedule");
                #endif
                return _ownerRank;
            }

            internal void EnsureOwnerThreadOutsideBodies() {
                #if FFS_SCHEDULER_CHECKS
                ThrowIfDisposed();
                EnsureOwnerOutsideBodies("EnsureOwnerThread");
                #endif
            }

            [MethodImpl(AggressiveInlining)]
            private void EnsureOwnerOutsideBodies(string method) {
                #if FFS_SCHEDULER_CHECKS
                if (IsInsideJobBody) SchedulerErrors.InsideBody(method);
                EnsureOwnerThread(method);
                #endif
            }

            #if FFS_SCHEDULER_CHECKS
            [MethodImpl(AggressiveInlining)]
            private void EnsureOwnerThread(string method) {
                var threadId = Environment.CurrentManagedThreadId;
                var ownerThreadId = Volatile.Read(ref _ownerThreadId);
                if (ownerThreadId == threadId) return;
                if (ownerThreadId != 0 || Interlocked.CompareExchange(ref _ownerThreadId, threadId, 0) != 0) SchedulerErrors.NotOwnerThread(method);
            }

            [MethodImpl(AggressiveInlining)]
            private void ThrowIfDisposed() {
                if (_disposed) SchedulerErrors.Disposed();
            }
            #endif

            internal void TransferOwnershipToCurrentThread() {
                if (_disposed) SchedulerErrors.Disposed();
                if (IsInsideJobBody) SchedulerErrors.InsideBody("TransferOwnershipToCurrentThread");
                if (Volatile.Read(ref _activeNodes.Value) != 0) {
                    if (AnyNodeUncompleted()) SchedulerErrors.WorkInFlight("TransferOwnershipToCurrentThread");
                    WaitUntilNoActiveNodes();
                }
                Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);
            }
            #endregion

            #region LIFECYCLE
            internal void Dispose() {
                if (_disposed) return;
                #if FFS_SCHEDULER_CHECKS
                if (IsInsideJobBody) SchedulerErrors.InsideBody("Dispose");
                #endif
                if (_nodes != null && Volatile.Read(ref _activeNodes.Value) != 0) {
                    var ownerThreadId = Volatile.Read(ref _ownerThreadId);
                    var callerIsOwner = ownerThreadId == 0 || ownerThreadId == Environment.CurrentManagedThreadId;
                    if (callerIsOwner) HelpUntilIdle(_ownerRank);
                    else if (_workerCount > 1) WaitUntilNoActiveNodes();
                    else SchedulerErrors.WorkInFlight("Dispose from a thread other than the owner");
                }
                Destroy(false);
            }

            internal void DestroyFromFinalizer() => Destroy(true);

            private void Destroy(bool fromFinalizer) {
                if (_disposed) return;
                _disposing = true;
                if (_threads != null) {
                    if (!JoinWorkers(throwOnTimeout: !fromFinalizer)) return;
                    _threads = null;
                }
                _disposed = true;
                _sleepGate?.Dispose();
                _sleepGate = null;

                if (_deques != null) {
                    for (var workerRank = 0; workerRank < _workerCount; workerRank++) _deques[workerRank].FreeBuffers();
                    _allocator.FreeAligned(_deques);
                    _deques = null;
                }
                if (_nodes != null) {
                    _allocator.FreeSafe(_nodes);
                    _nodes = null;
                }
                if (_edges != null) {
                    _allocator.FreeSafe(_edges);
                    _edges = null;
                }
            }

            private bool JoinWorkers(bool throwOnTimeout) {
                _sleepGate.Release(_threads.Length);
                #if FFS_SCHEDULER_TEST_HOOKS
                var joinTimeoutMilliseconds = SchedulerTestHooks.JoinTimeoutMilliseconds;
                #else
                const int joinTimeoutMilliseconds = WorkerJoinTimeoutMilliseconds;
                #endif
                var everyWorkerExited = true;
                foreach (var thread in _threads) {
                    if (thread == null || thread.Join(joinTimeoutMilliseconds)) continue;
                    if (throwOnTimeout) throw new SchedulerException("Job worker did not finish in " + joinTimeoutMilliseconds / 1000.0 + " seconds");
                    everyWorkerExited = false;
                }
                return everyWorkerExited;
            }
            #endregion

            #region GRAPH
            private int AllocNode() {
                int nodeIndex;
                do {
                    nodeIndex = Volatile.Read(ref _nodeCount.Value);
                    if (nodeIndex >= _nodeCapacity) SchedulerErrors.NodePoolOverflow();
                } while (Interlocked.CompareExchange(ref _nodeCount.Value, nodeIndex + 1, nodeIndex) != nodeIndex);

                ref var node = ref _nodes[nodeIndex];
                node = default;
                node.FirstEdgeIndex = JobNode.EmptyEdgeList;
                node.FaultSlot = JobNode.NoFaultSlot;
                node.FrameVersion = Volatile.Read(ref _generation);
                Interlocked.Increment(ref _activeNodes.Value);
                return nodeIndex;
            }

            private int ReserveEdges(int count) {
                if (count <= 0) return 0;
                int firstEdgeIndex;
                do {
                    firstEdgeIndex = Volatile.Read(ref _edgeCount.Value);
                    if (count > _edgeCapacity - firstEdgeIndex) SchedulerErrors.EdgePoolOverflow();
                } while (Interlocked.CompareExchange(ref _edgeCount.Value, firstEdgeIndex + count, firstEdgeIndex) != firstEdgeIndex);
                return firstEdgeIndex;
            }
            #endregion

            #region SCHEDULING
            internal JobHandle ScheduleDeferred(delegate*<void*, int, int, void> body, void* userData, int* deferredLength, int batchSize,
                                                JobHandle* dependencies, int dependencyCount) {
                #if FFS_SCHEDULER_DEBUG
                if (deferredLength == null) SchedulerErrors.InvalidArgument("ScheduleParallelDeferred needs a deferred length pointer");
                #endif
                return ScheduleNode(body, userData, 0, deferredLength, batchSize, dependencies, dependencyCount);
            }

            internal JobHandle ScheduleNode(delegate*<void*, int, int, void> body, void* userData, int length, int* deferredLength, int batchSize,
                                            JobHandle* dependencies, int dependencyCount) {
                #if FFS_SCHEDULER_CHECKS
                ThrowIfDisposed();
                #endif
                #if FFS_SCHEDULER_DEBUG
                if (dependencyCount < 0) SchedulerErrors.InvalidArgument("The dependency count must not be negative");
                if (dependencies == null && dependencyCount > 0) SchedulerErrors.InvalidArgument("The dependency pointer is null while the count is " + dependencyCount);
                #endif
                var workerRank = SchedulingRank();
                #if FFS_SCHEDULER_CHECKS
                for (var index = 0; index < dependencyCount; index++) ValidateHandle(dependencies[index]);
                #endif

                var firstEdgeIndex = ReserveEdges(dependencyCount);
                var nodeIndex = AllocNode();
                ref var node = ref _nodes[nodeIndex];
                node.Payload = new JobPayload {
                    Body = body,
                    UserData = userData,
                    DeferredLength = deferredLength,
                    Length = length,
                    BatchSize = batchSize < 1 ? 1 : batchSize
                };
                var frameVersion = node.FrameVersion;
                node.PendingDependencies = RegistrationGuard;
                for (var index = 0; index < dependencyCount; index++) {
                    if (dependencies[index].IsNull) continue;
                    LinkDependency(nodeIndex, dependencies[index].NodeIndex, firstEdgeIndex + index);
                }
                var everyDependencyCompleted = Interlocked.Add(ref node.PendingDependencies, -RegistrationGuard) == 0;
                if (everyDependencyCompleted && !PushBatches(nodeIndex, workerRank, callerTakesBatch: false)) {
                    CompleteNode(nodeIndex, workerRank, callerTakesBatch: false);
                }
                return new JobHandle(nodeIndex, frameVersion);
            }

            private void LinkDependency(int nodeIndex, int dependencyNodeIndex, int edgeIndex) {
                ref var dependencyNode = ref _nodes[dependencyNodeIndex];
                ref var node = ref _nodes[nodeIndex];
                ref var edge = ref _edges[edgeIndex];
                edge.DependentNodeIndex = nodeIndex;
                Interlocked.Increment(ref node.PendingDependencies);
                #if FFS_SCHEDULER_TEST_HOOKS
                SchedulerTestHooks.Reach(SchedulerTestPoint.BeforeDependencyLink, dependencyNodeIndex);
                #endif
                while (true) {
                    var head = Volatile.Read(ref dependencyNode.FirstEdgeIndex);
                    if (head == JobNode.ClosedEdgeList) {
                        InheritFaults(ref node, Volatile.Read(ref dependencyNode.ReachedFaultSlots));
                        Interlocked.Decrement(ref node.PendingDependencies);
                        return;
                    }
                    edge.NextEdgeIndex = head;
                    if (Interlocked.CompareExchange(ref dependencyNode.FirstEdgeIndex, edgeIndex, head) == head) return;
                }
            }

            private bool PushBatches(int nodeIndex, int workerRank, bool callerTakesBatch) {
                ref var node = ref _nodes[nodeIndex];
                if (node.Payload.IsBarrier) return false;
                #if FFS_SCHEDULER_CATCH
                if (_faultPolicy == SchedulerFaultPolicy.CancelDependents && Volatile.Read(ref node.ReachedFaultSlots) != 0) return false;
                #endif
                var length = node.Payload.DeferredLength != null ? *node.Payload.DeferredLength : node.Payload.Length;
                if (length <= 0) return false;
                var batchSize = node.Payload.BatchSize;
                var batchCount = length / batchSize + (length % batchSize != 0 ? 1 : 0);
                ref var deque = ref _deques[workerRank];
                if (batchCount > deque.AvailableCapacity) {
                    var overflow = new SchedulerException("A parallel-for of " + batchCount + " batches does not fit the deque, which holds at most 2^28 batches");
                    #if FFS_SCHEDULER_CATCH
                    RecordFault(nodeIndex, overflow);
                    return false;
                    #else
                    throw overflow;
                    #endif
                }
                Volatile.Write(ref node.RemainingBatches, batchCount);
                var startInclusive = 0;
                while (startInclusive < length) {
                    var endExclusive = startInclusive + Math.Min(batchSize, length - startInclusive);
                    deque.Push(new WorkItem { NodeIndex = nodeIndex, StartInclusive = startInclusive, EndExclusive = endExclusive });
                    startInclusive = endExclusive;
                }
                WakeSleepersFor(callerTakesBatch ? batchCount - 1 : batchCount);
                return true;
            }

            #if FFS_SCHEDULER_CHECKS
            private void ValidateHandle(JobHandle handle) {
                if (handle.IsNull) return;
                var outsideThisFrame = (uint) handle.NodeIndex >= (uint) Volatile.Read(ref _nodeCount.Value) || handle.Version != Volatile.Read(ref _generation);
                if (outsideThisFrame) SchedulerErrors.StaleHandle();
            }
            #endif
            #endregion

            #region EXECUTION
            private void ExecuteWorkItem(in WorkItem workItem, int workerRank, bool callerReturnsToItsDeque) {
                ref var node = ref _nodes[workItem.NodeIndex];
                #if FFS_SCHEDULER_CATCH
                try {
                    node.Payload.Body(node.Payload.UserData, workItem.StartInclusive, workItem.EndExclusive);
                }
                catch (Exception exception) {
                    RecordFault(workItem.NodeIndex, exception);
                }
                #else
                node.Payload.Body(node.Payload.UserData, workItem.StartInclusive, workItem.EndExclusive);
                #endif
                if (Interlocked.Decrement(ref node.RemainingBatches) == 0) CompleteNode(workItem.NodeIndex, workerRank, callerTakesBatch: callerReturnsToItsDeque);
            }

            private void CompleteNode(int nodeIndex, int workerRank, bool callerTakesBatch) {
                _nodes[nodeIndex].NextInCompletionList = JobNode.EmptyEdgeList;
                var completionListHead = nodeIndex;
                while (completionListHead >= 0) {
                    var currentIndex = completionListHead;
                    ref var node = ref _nodes[currentIndex];
                    completionListHead = node.NextInCompletionList;
                    var reachedFaultSlots = Volatile.Read(ref node.ReachedFaultSlots);
                    var edgeIndex = Interlocked.Exchange(ref node.FirstEdgeIndex, JobNode.ClosedEdgeList);
                    #if FFS_SCHEDULER_TEST_HOOKS
                    SchedulerTestHooks.Reach(SchedulerTestPoint.AfterCompletionPublished, currentIndex);
                    #endif
                    while (edgeIndex >= 0) {
                        var dependentNodeIndex = _edges[edgeIndex].DependentNodeIndex;
                        var nextEdgeIndex = _edges[edgeIndex].NextEdgeIndex;
                        ref var dependent = ref _nodes[dependentNodeIndex];
                        InheritFaults(ref dependent, reachedFaultSlots);
                        if (Interlocked.Decrement(ref dependent.PendingDependencies) == 0) {
                            if (PushBatches(dependentNodeIndex, workerRank, callerTakesBatch)) {
                                callerTakesBatch = false;
                            }
                            else {
                                dependent.NextInCompletionList = completionListHead;
                                completionListHead = dependentNodeIndex;
                            }
                        }
                        edgeIndex = nextEdgeIndex;
                    }
                    Interlocked.Decrement(ref _activeNodes.Value);
                }
            }

            [MethodImpl(AggressiveInlining)]
            private static void InheritFaults(ref JobNode node, long faultSlots) {
                if (faultSlots != 0) AtomicBits.Or(ref node.ReachedFaultSlots, faultSlots);
            }
            #endregion

            #region COMPLETION
            internal bool IsCompleted(JobHandle handle) {
                #if FFS_SCHEDULER_CHECKS
                ThrowIfDisposed();
                ValidateHandle(handle);
                #endif
                return handle.IsNull || IsClosed(ref _nodes[handle.NodeIndex]);
            }

            internal void Complete(JobHandle handle, bool help) {
                #if FFS_SCHEDULER_CHECKS
                ThrowIfDisposed();
                EnsureOwnerOutsideBodies(help ? "Complete" : "CompleteWithoutHelping");
                ValidateHandle(handle);
                #endif
                if (handle.IsNull) return;
                ref var node = ref _nodes[handle.NodeIndex];
                if (!IsClosed(ref node)) {
                    if (help || _workerCount == 1) HelpUntilClosed(ref node);
                    else WaitUntilClosed(ref node);
                }
                #if FFS_SCHEDULER_CATCH
                ThrowIfFaulted(Volatile.Read(ref node.ReachedFaultSlots));
                #endif
            }

            internal void CompleteAll() {
                #if FFS_SCHEDULER_CHECKS
                ThrowIfDisposed();
                EnsureOwnerOutsideBodies("CompleteAll");
                #endif
                HelpUntilIdle(_ownerRank);
            }

            internal void ResetFrame() {
                #if FFS_SCHEDULER_CHECKS
                ThrowIfDisposed();
                EnsureOwnerOutsideBodies("ResetFrame");
                #endif
                #if FFS_SCHEDULER_DEBUG
                var jobsInFlight = CountUncompletedNodes();
                #endif
                if (Volatile.Read(ref _activeNodes.Value) != 0) HelpUntilIdle(_ownerRank);
                Volatile.Write(ref _nodeCount.Value, 0);
                Volatile.Write(ref _edgeCount.Value, 0);
                var generation = _generation + 1;
                Volatile.Write(ref _generation, generation == 0 ? 1 : generation);
                #if FFS_SCHEDULER_CATCH
                var unobservedFaults = TakeUnobservedFaults();
                if (unobservedFaults != null) throw unobservedFaults;
                #endif
                #if FFS_SCHEDULER_DEBUG
                if (jobsInFlight != 0) {
                    throw new SchedulerException(jobsInFlight + " jobs were still in flight at ResetFrame; they ran to completion before the reset");
                }
                #endif
            }

            [MethodImpl(AggressiveInlining)]
            private static bool IsClosed(ref JobNode node) => Volatile.Read(ref node.FirstEdgeIndex) == JobNode.ClosedEdgeList;

            private bool AnyNodeUncompleted() {
                var nodeCount = Volatile.Read(ref _nodeCount.Value);
                for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++) {
                    if (!IsClosed(ref _nodes[nodeIndex])) return true;
                }
                return false;
            }

            #if FFS_SCHEDULER_DEBUG
            private int CountUncompletedNodes() {
                var nodeCount = Volatile.Read(ref _nodeCount.Value);
                var uncompleted = 0;
                for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++) {
                    if (!IsClosed(ref _nodes[nodeIndex])) uncompleted++;
                }
                return uncompleted;
            }
            #endif

            private void HelpUntilClosed(ref JobNode node) {
                EnterHelpLoop();
                try {
                    var backoffStep = 0;
                    while (!IsClosed(ref node)) {
                        if (TryGetWork(_ownerRank, out var workItem)) {
                            ExecuteWorkItem(workItem, _ownerRank, callerReturnsToItsDeque: false);
                            backoffStep = 0;
                        }
                        else Backoff(ref backoffStep);
                    }
                }
                finally {
                    LeaveHelpLoop();
                }
            }

            private static void WaitUntilClosed(ref JobNode node) {
                var backoffStep = 0;
                while (!IsClosed(ref node)) Backoff(ref backoffStep);
            }

            private void HelpUntilIdle(int workerRank) {
                EnterHelpLoop();
                try {
                    var backoffStep = 0;
                    while (Volatile.Read(ref _activeNodes.Value) != 0) {
                        if (TryGetWork(workerRank, out var workItem)) {
                            ExecuteWorkItem(workItem, workerRank, callerReturnsToItsDeque: true);
                            backoffStep = 0;
                        }
                        else Backoff(ref backoffStep);
                    }
                }
                finally {
                    LeaveHelpLoop();
                }
            }

            private void WaitUntilNoActiveNodes() {
                var backoffStep = 0;
                while (Volatile.Read(ref _activeNodes.Value) != 0) Backoff(ref backoffStep);
            }

            private void EnterHelpLoop() {
                if (_helpDepth++ == 0) Volatile.Write(ref _helpingThreadId, Environment.CurrentManagedThreadId);
            }

            private void LeaveHelpLoop() {
                if (--_helpDepth == 0) Volatile.Write(ref _helpingThreadId, 0);
            }

            [MethodImpl(AggressiveInlining)]
            private static void Backoff(ref int backoffStep) {
                if (backoffStep < BackoffSpinSteps) {
                    Thread.SpinWait(backoffStep < 4 ? 1 << backoffStep : BackoffMaxSpinIterations);
                    backoffStep++;
                }
                else Thread.Yield();
            }
            #endregion

            #region WORKERS
            private void WorkerLoop(int workerRank) {
                _workerEngine = this;
                _workerRank = workerRank;
                while (!_disposing) {
                    if (TryGetWork(workerRank, out var workItem) || SpinForWork(workerRank, out workItem)) {
                        ExecuteWorkItem(workItem, workerRank, callerReturnsToItsDeque: true);
                        continue;
                    }
                    #if FFS_SCHEDULER_TEST_HOOKS
                    SchedulerTestHooks.Reach(SchedulerTestPoint.BeforeIdleIncrement, workerRank);
                    #endif
                    RegisterAsSleeper();
                    #if FFS_SCHEDULER_TEST_HOOKS
                    SchedulerTestHooks.Reach(SchedulerTestPoint.AfterIdleIncrement, workerRank);
                    #endif
                    if (TryStealFromEveryOtherDeque(workerRank, out workItem)) {
                        WithdrawSleeperRegistration();
                        ExecuteWorkItem(workItem, workerRank, callerReturnsToItsDeque: true);
                        continue;
                    }
                    if (_disposing) break;
                    _sleepGate.Wait();
                }
            }

            [MethodImpl(AggressiveInlining)]
            private void RegisterAsSleeper() => Interlocked.Increment(ref _idleCount.Value);

            [MethodImpl(AggressiveInlining)]
            private void WithdrawSleeperRegistration() => Interlocked.Decrement(ref _idleCount.Value);

            private bool TryGetWork(int workerRank, out WorkItem workItem) {
                ref var ownDeque = ref _deques[workerRank];
                if (ownDeque.TryPop(out workItem)) return true;
                if (_workerCount == 1) return false;
                var victimRank = ownDeque.NextVictim(_workerCount, workerRank);
                for (var attempt = 1; attempt < _workerCount; attempt++) {
                    if (_deques[victimRank].TrySteal(out workItem) == StealOutcome.Success) return true;
                    victimRank = NextRankAfter(victimRank, workerRank);
                }
                workItem = default;
                return false;
            }

            private bool TryStealFromEveryOtherDeque(int workerRank, out WorkItem workItem) {
                var victimRank = workerRank;
                for (var attempt = 1; attempt < _workerCount; attempt++) {
                    victimRank = NextRankAfter(victimRank, workerRank);
                    StealOutcome outcome;
                    do {
                        outcome = _deques[victimRank].TrySteal(out workItem);
                        if (outcome == StealOutcome.Success) return true;
                    } while (outcome == StealOutcome.Abort);
                }
                workItem = default;
                return false;
            }

            private int NextRankAfter(int rank, int skippedRank) {
                rank = rank + 1 == _workerCount ? 0 : rank + 1;
                if (rank == skippedRank) rank = rank + 1 == _workerCount ? 0 : rank + 1;
                return rank;
            }

            private bool SpinForWork(int workerRank, out WorkItem workItem) {
                workItem = default;
                if (_spinTicks <= 0) return false;
                var deadline = Stopwatch.GetTimestamp() + _spinTicks;
                while (!_disposing) {
                    Thread.SpinWait(SpinStepIterations);
                    if (TryGetWork(workerRank, out workItem)) return true;
                    if (Stopwatch.GetTimestamp() >= deadline) return false;
                }
                return false;
            }

            private void WakeSleepersFor(int readyBatchCount) {
                if (_sleepGate == null || readyBatchCount <= 0) return;
                Interlocked.MemoryBarrier(); // the last push is visible before the sleeper count is read
                while (true) {
                    var sleepers = Volatile.Read(ref _idleCount.Value);
                    if (sleepers <= 0) return;
                    var sleepersToWake = Math.Min(readyBatchCount, sleepers);
                    if (Interlocked.CompareExchange(ref _idleCount.Value, sleepers - sleepersToWake, sleepers) == sleepers) {
                        #if FFS_SCHEDULER_TEST_HOOKS
                        SchedulerTestHooks.Reach(SchedulerTestPoint.PermitsReleased, sleepersToWake);
                        #endif
                        _sleepGate.Release(sleepersToWake);
                        return;
                    }
                }
            }
            #endregion

            #region FAULTS
            private void RecordFault(int nodeIndex, Exception exception) {
                if (_faultPolicy == SchedulerFaultPolicy.FailFast) {
                    Environment.FailFast("A job body of the StaticScheduler threw an exception", exception);
                }
                ref var node = ref _nodes[nodeIndex];
                int slot;
                lock (_faultRecords) {
                    if (node.FaultSlot == JobNode.NoFaultSlot) node.FaultSlot = _faultSlotCount < SharedFaultSlot ? _faultSlotCount++ : SharedFaultSlot;
                    slot = node.FaultSlot;
                    if (_faultRecords.Count < MaxFaultRecords) _faultRecords.Add(new FaultRecord { Slot = slot, Exception = exception });
                    else _droppedFaultCount++;
                }
                AtomicBits.Or(ref node.ReachedFaultSlots, 1L << slot);
            }

            private void ThrowIfFaulted(long reachedFaultSlots) {
                if (reachedFaultSlots == 0) return;
                List<Exception> exceptions = null;
                int dropped;
                lock (_faultRecords) {
                    foreach (var record in _faultRecords) {
                        if ((reachedFaultSlots & (1L << record.Slot)) == 0) continue;
                        record.Observed = true;
                        (exceptions ??= new List<Exception>()).Add(record.Exception);
                    }
                    dropped = _droppedFaultCount;
                    _droppedFaultCount = 0;
                }
                if ((reachedFaultSlots & (1L << SharedFaultSlot)) != 0) {
                    throw CreateFaultException(exceptions, dropped, singleMessage: null,
                                               "job bodies threw exceptions; see InnerException. The last fault slot is shared by every failed job past the 63rd of the frame, " +
                                               "so the exceptions include those of the jobs past the 63rd this handle does not reach");
                }
                throw CreateFaultException(exceptions, dropped, "A job body threw an exception; see InnerException", "job bodies threw exceptions; see InnerException");
            }

            private SchedulerException TakeUnobservedFaults() {
                List<Exception> exceptions = null;
                int dropped;
                lock (_faultRecords) {
                    foreach (var record in _faultRecords) {
                        if (!record.Observed) (exceptions ??= new List<Exception>()).Add(record.Exception);
                    }
                    dropped = _droppedFaultCount;
                    _faultRecords.Clear();
                    _faultSlotCount = 0;
                    _droppedFaultCount = 0;
                }
                if (exceptions == null && dropped == 0) return null;
                return CreateFaultException(exceptions, dropped, "A job body threw an exception no Complete reported; see InnerException",
                                            "job bodies threw exceptions no Complete reported; see InnerException");
            }

            private static SchedulerException CreateFaultException(List<Exception> exceptions, int dropped, string singleMessage, string severalMessageTail) {
                var droppedTail = dropped == 0 ? "" : "; " + dropped + " more exceptions past the limit of " + MaxFaultRecords + " were counted only";
                if (exceptions == null) return new SchedulerException("Job bodies threw more than " + MaxFaultRecords + " exceptions this frame" + droppedTail);
                var reportsSingleException = exceptions.Count == 1 && dropped == 0 && singleMessage != null;
                return reportsSingleException
                    ? new SchedulerException(singleMessage, exceptions[0])
                    : new SchedulerException(exceptions.Count + " " + severalMessageTail + droppedTail, new AggregateException(exceptions));
            }

            private sealed class FaultRecord {
                public int Slot;
                public Exception Exception;
                public bool Observed;
            }
            #endregion
        }
    }

    internal unsafe struct JobPayload {
        public delegate*<void*, int, int, void> Body;
        public void* UserData;
        public int* DeferredLength;
        public int Length;
        public int BatchSize;

        public bool IsBarrier => Body == null;
    }

    internal struct JobNode {
        public const int EmptyEdgeList = -1;
        public const int ClosedEdgeList = -2;
        public const int NoFaultSlot = -1;

        public JobPayload Payload;
        public long ReachedFaultSlots;
        public int RemainingBatches;
        public int PendingDependencies;
        public int FirstEdgeIndex;
        public int NextInCompletionList;
        public int FaultSlot;
        public int FrameVersion;
    }

    internal struct JobEdge {
        public int DependentNodeIndex;
        public int NextEdgeIndex;
    }
}
