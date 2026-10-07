using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFS.Libraries.StaticPack;
#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace FFS.Libraries.StaticScheduler {
    internal struct WorkItem {
        public int NodeIndex;
        public int StartInclusive;
        public int EndExclusive;
    }

    internal enum StealOutcome : byte {
        Empty,
        Abort,
        Success
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    [StructLayout(LayoutKind.Explicit, Size = 3 * PackMemory.CacheLineSize)]
    internal unsafe struct ChaseLevDeque {
        public const long MaxCapacity = 1L << 28;

        private const int ThiefWrittenLine = 0;
        private const int OwnerWrittenLine = PackMemory.CacheLineSize;
        private const int OwnerOnlyLine = 2 * PackMemory.CacheLineSize;

        [FieldOffset(ThiefWrittenLine)]
        private long _top;
        [FieldOffset(OwnerWrittenLine)]
        private long _bottom;
        [FieldOffset(OwnerWrittenLine + sizeof(long))]
        private IntPtr _bufferPointer;
        [FieldOffset(OwnerOnlyLine)]
        private uint _randomState;
        [FieldOffset(OwnerOnlyLine + sizeof(long))]
        private PackAllocator _allocator;

        public void Init(long capacity, uint randomSeed, in PackAllocator allocator) {
            _top = 0;
            _bottom = 0;
            _randomState = randomSeed;
            _allocator = allocator;
            _bufferPointer = (IntPtr) CircularBuffer.Alloc(allocator, SchedulerMath.NextPowerOfTwo(capacity), null);
        }

        public long AvailableCapacity => MaxCapacity - (_bottom - Volatile.Read(ref _top));

        public void Push(in WorkItem workItem) {
            var bottom = _bottom;
            var top = Volatile.Read(ref _top);
            var buffer = (CircularBuffer*) _bufferPointer;
            if (bottom - top > buffer->Capacity - 1) buffer = Grow(buffer, bottom, top);
            buffer->Items[bottom & buffer->Mask] = workItem;
            Volatile.Write(ref _bottom, bottom + 1);
        }

        public bool TryPop(out WorkItem workItem) {
            if (_bottom <= Volatile.Read(ref _top)) {
                workItem = default;
                return false;
            }

            var buffer = (CircularBuffer*) _bufferPointer;
            var bottom = _bottom - 1;
            Volatile.Write(ref _bottom, bottom);
            Interlocked.MemoryBarrier(); // bottom is written before top is read
            var top = Volatile.Read(ref _top);
            var thiefTookTheLastItem = top > bottom;
            if (thiefTookTheLastItem) {
                Volatile.Write(ref _bottom, top);
                workItem = default;
                return false;
            }

            workItem = buffer->Items[bottom & buffer->Mask];
            var isLastItem = top == bottom;
            if (!isLastItem) return true;
            #if FFS_SCHEDULER_TEST_HOOKS
            SchedulerTestHooks.Reach(SchedulerTestPoint.BeforePopCompareExchange, 0);
            #endif
            var wonAgainstThieves = Interlocked.CompareExchange(ref _top, top + 1, top) == top;
            Volatile.Write(ref _bottom, top + 1);
            if (!wonAgainstThieves) {
                workItem = default;
                return false;
            }

            return true;
        }

        public StealOutcome TrySteal(out WorkItem workItem) {
            workItem = default;
            var top = Volatile.Read(ref _top);
            Interlocked.MemoryBarrier(); // top is read before bottom and the buffer
            var bottom = Volatile.Read(ref _bottom);
            if (top >= bottom) return StealOutcome.Empty;
            var buffer = (CircularBuffer*) Volatile.Read(ref _bufferPointer);
            #if FFS_SCHEDULER_TEST_HOOKS
            SchedulerTestHooks.Reach(SchedulerTestPoint.BeforeStealItemRead, 0);
            #endif
            var candidate = buffer->Items[top & buffer->Mask]; // read before the CAS: once top moves on, the owner may overwrite the slot
            #if FFS_SCHEDULER_TEST_HOOKS
            SchedulerTestHooks.Reach(SchedulerTestPoint.BeforeStealCompareExchange, 0);
            #endif
            if (Interlocked.CompareExchange(ref _top, top + 1, top) != top) return StealOutcome.Abort;
            workItem = candidate;
            return StealOutcome.Success;
        }

        public int NextVictim(int workerCount, int selfRank) {
            var state = _randomState;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            _randomState = state;
            var victim = (int) (state % (uint) (workerCount - 1));
            return victim >= selfRank ? victim + 1 : victim;
        }

        private CircularBuffer* Grow(CircularBuffer* oldBuffer, long bottom, long top) {
            var newBuffer = CircularBuffer.Alloc(_allocator, oldBuffer->Capacity << 1, retiredBuffer: oldBuffer);
            for (var index = top; index < bottom; index++) newBuffer->Items[index & newBuffer->Mask] = oldBuffer->Items[index & oldBuffer->Mask];
            Volatile.Write(ref _bufferPointer, (IntPtr) newBuffer);
            return newBuffer;
        }

        public void FreeBuffers() {
            var buffer = (CircularBuffer*) _bufferPointer;
            while (buffer != null) {
                var previous = buffer->RetiredBuffer;
                _allocator.FreeSafe(buffer->Items);
                _allocator.FreeSafe(buffer);
                buffer = previous;
            }
            _bufferPointer = IntPtr.Zero;
        }
    }

    #if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    #endif
    internal unsafe struct CircularBuffer {
        public long Capacity;
        public long Mask;
        public WorkItem* Items;
        public CircularBuffer* RetiredBuffer;

        public static CircularBuffer* Alloc(in PackAllocator allocator, long capacity, CircularBuffer* retiredBuffer) {
            var buffer = allocator.Alloc<CircularBuffer>(1, clear: false);
            buffer->Capacity = capacity;
            buffer->Mask = capacity - 1;
            buffer->Items = allocator.Alloc<WorkItem>(capacity, clear: false);
            buffer->RetiredBuffer = retiredBuffer;
            return buffer;
        }
    }
}
