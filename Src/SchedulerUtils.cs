#if ENABLE_MONO && !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN || UNITY_WSA)
#define FFS_SCHEDULER_MONO_SEMAPHORE_WAKES_EVERY_WAITER
#endif

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FFS.Libraries.StaticPack;
using static System.Runtime.CompilerServices.MethodImplOptions;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace FFS.Libraries.StaticScheduler {
    [StructLayout(LayoutKind.Explicit, Size = 2 * PackMemory.CacheLineSize)]
    internal struct PaddedCounter {
        [FieldOffset(PackMemory.CacheLineSize)]
        public int Value;
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    internal sealed class WorkerSleepGate : IDisposable {
        private int _permits;

        public int Permits => Volatile.Read(ref _permits);

        #if FFS_SCHEDULER_MONO_SEMAPHORE_WAKES_EVERY_WAITER
        private readonly object _lock = new object();
        private int _waiters;

        public void Wait() {
            lock (_lock) {
                while (_permits == 0) {
                    _waiters++;
                    Monitor.Wait(_lock);
                    _waiters--;
                }
                _permits--;
            }
        }

        public void Release(int count) {
            lock (_lock) {
                _permits += count;
                if (_waiters == 0) return;
                if (count >= _waiters) Monitor.PulseAll(_lock);
                else for (var pulse = 0; pulse < count; pulse++) Monitor.Pulse(_lock);
            }
        }

        public void Dispose() { }
        #else
        private readonly Semaphore _semaphore = new(0, int.MaxValue);

        public void Wait() {
            _semaphore.WaitOne();
            Interlocked.Decrement(ref _permits);
        }

        public void Release(int count) {
            Interlocked.Add(ref _permits, count);
            _semaphore.Release(count);
        }

        public void Dispose() => _semaphore.Dispose();
        #endif
    }

    internal static class AtomicBits {
        [MethodImpl(AggressiveInlining)]
        public static void Or(ref long target, long bits) {
            var current = Volatile.Read(ref target);
            while ((current & bits) != bits) {
                var observed = Interlocked.CompareExchange(ref target, current | bits, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    internal static class SchedulerMath {
        public static long NextPowerOfTwo(long value) {
            var power = 1L;
            while (power < value) power <<= 1;
            return power;
        }
    }

    internal static class SchedulerErrors {
        [MethodImpl(NoInlining)]
        public static void NodePoolOverflow() =>
            throw new SchedulerException("Job node pool overflow (increase SchedulerConfig.NodeCapacity or call ResetFrame more often)");

        [MethodImpl(NoInlining)]
        public static void EdgePoolOverflow() =>
            throw new SchedulerException("Job edge pool overflow (increase SchedulerConfig.EdgeCapacity or call ResetFrame more often)");

        [MethodImpl(NoInlining)]
        public static void Disposed() => throw new ObjectDisposedException(nameof(JobScheduler));

        [MethodImpl(NoInlining)]
        public static void NotOwnerThread(string method) =>
            throw new SchedulerException("JobScheduler." + method + " was called from a thread that does not own the scheduler; the owner is the thread of " +
                                         "the first call (TransferOwnershipToCurrentThread moves it while no job is in flight)");

        [MethodImpl(NoInlining)]
        public static void InsideBody(string method) => throw new SchedulerException("JobScheduler." + method + " is not available inside a job body");

        [MethodImpl(NoInlining)]
        public static void StaleHandle() => throw new SchedulerException("The job handle belongs to an earlier frame or to no job of this frame");

        [MethodImpl(NoInlining)]
        public static void WorkInFlight(string method) => throw new SchedulerException("JobScheduler." + method + " needs every scheduled job to be completed");

        [MethodImpl(NoInlining)]
        public static void InvalidArgument(string message) => throw new ArgumentException(message);
    }

    #if FFS_SCHEDULER_TEST_HOOKS
    internal enum SchedulerTestPoint : byte {
        BeforePopCompareExchange,
        BeforeStealItemRead,
        BeforeStealCompareExchange,
        BeforeDependencyLink,
        AfterIdleIncrement,
        BeforeWorkerThreadStart,
        AfterCompletionPublished,
        BeforeIdleIncrement,
        PermitsReleased,
    }

    internal static class SchedulerTestHooks {
        internal static Action<SchedulerTestPoint, int> Reached;
        internal static int JoinTimeoutMilliseconds = 10_000;

        [MethodImpl(AggressiveInlining)]
        internal static void Reach(SchedulerTestPoint point, int argument) => Reached?.Invoke(point, argument);

        internal static void Reset() {
            Reached = null;
            JoinTimeoutMilliseconds = 10_000;
        }
    }
    #endif
}

#if ENABLE_IL2CPP
namespace Unity.IL2CPP.CompilerServices {
    using System;

    internal enum Option {
        NullChecks = 1,
        ArrayBoundsChecks = 2,
        DivideByZeroChecks = 3
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true)]
    internal class Il2CppSetOptionAttribute : Attribute {
        public Option Option { get; }
        public object Value { get; }

        public Il2CppSetOptionAttribute(Option option, object value) {
            Option = option;
            Value = value;
        }
    }
}
#endif
