<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-1.0.0-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticScheduler/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticScheduler-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
</p>

# Static Scheduler - C# work-stealing job scheduler
- Lock-free work stealing over per-worker Chase-Lev deques (Chase & Lev 2005, in the verified memory model of Lê et al., PPoPP 2013)
- Dependency graph: `JobHandle`, `Combine`, nested scheduling from a running job
- The calling thread helps finish the work it waits for
- No managed allocations in the steady state: native memory and a per-frame graph pool
- Synchronous mode for single-core platforms and WebGL with the same code
- The exception of a job body is reported by `Complete` of every handle that reaches the job
- Three levels of checks set by defines
- Compatible with Unity and other C# engines

#### Limitations and Features:
> - A job body is a function pointer `delegate*<void*, int, int, void>`; its state goes through the `void*` pointer, which stays valid until the job completes
> - Handles are valid within the frame they were created in; `ResetFrame` ends the frame

## Table of Contents
* [Contacts](#contacts)
* [Support the project](#support-the-project)
* [Requirements](#requirements)
* [Installation](#installation)
* [Concept](#concept)
* [Quick start](#quick-start)
* [API](#api)
  * [JobScheduler](#jobscheduler)
  * [JobHandle](#jobhandle)
  * [SchedulerConfig](#schedulerconfig)
* [Example: a frame of a particle simulation](#example-a-frame-of-a-particle-simulation)
* [Thread model](#thread-model)
* [Failures](#failures)
* [Checks](#checks)
* [Determinism](#determinism)
* [License](#license)

# Contacts
* [felid.force.studios@gmail.com](mailto:felid.force.studios@gmail.com)
* [Telegram](https://t.me/felid_force_studios)

# Support the project
If you like Static Scheduler and it helps your project, you can support its development:

<a href="https://www.buymeacoffee.com/felid.force.studios" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="60"></a>

# Requirements
* **Unity** 6 or newer.
* **.NET** 6.0 or newer, or netstandard2.1.
* [Static Pack](https://github.com/Felid-Force-Studios/StaticPack) `2.0.0` — the native allocator of the scheduler.

# Installation
* ### As source code
  From the release page or as an archive from the branch. In the `master` branch there is a stable tested version
* ### Installation for Unity
  Via git module in Unity PackageManager — the package together with Static Pack:
  ```
  https://github.com/Felid-Force-Studios/StaticPack.git
  https://github.com/Felid-Force-Studios/StaticScheduler.git
  ```
  Or adding to the manifest `Packages/manifest.json`:
  ```json
  "com.felid-force-studios.static-pack": "https://github.com/Felid-Force-Studios/StaticPack.git"
  "com.felid-force-studios.static-scheduler": "https://github.com/Felid-Force-Studios/StaticScheduler.git"
  ```
* ### NuGet
  ```
  dotnet add package FFS.StaticScheduler
  ```
  For debug build with assertions:
  ```
  dotnet add package FFS.StaticScheduler.Debug
  ```
  Packages: [FFS.StaticScheduler](https://www.nuget.org/packages/FFS.StaticScheduler/) · [FFS.StaticScheduler.Debug](https://www.nuget.org/packages/FFS.StaticScheduler.Debug/)

  NuGet installs Static Pack automatically.

# Concept
The scheduler follows the model of the Unity Job System — `Schedule` → dependencies → `Complete`:
> - A **job body** is invoked as `(userData, startInclusive, endExclusive)`
> - **`Schedule`** runs the body once as `[0, 1)`; **`ScheduleParallel`** splits `[0, length)` into batches of `batchSize`, each batch runs entirely on one worker
> - **`JobHandle`** identifies a scheduled job; handles passed as dependencies or to `Combine` build the graph
> - **`Complete(handle)`** blocks the owner thread until the job and its dependencies finish, running queued batches while it waits; **`CompleteWithoutHelping(handle)`** waits without running other batches and returns as soon as the job is done; **`CompleteAll()`** completes every job of the frame
> - **`ResetFrame()`** completes the work still in flight and returns the graph pool at the end of a frame

Each worker owns a deque: it takes its own batches from the bottom end, and an idle worker steals from the top end of another worker's deque. A worker that runs out of work polls for `SpinMicroseconds`, then sleeps until new batches arrive.

# Quick start
```csharp
using System.Runtime.InteropServices;
using FFS.Libraries.StaticScheduler;

static unsafe class QuickStart {
    struct AddState {
        public int* Data;
        public int Value;
    }

    static void AddBody(void* userData, int start, int end) {
        var state = (AddState*) userData;
        for (var index = start; index < end; index++) state->Data[index] += state->Value;
    }

    public static void Run() {
        const int length = 1_000_000;
        var data = (int*) NativeMemory.AllocZeroed((nuint) length, sizeof(int));

        using var scheduler = new JobScheduler(SchedulerConfig.Default());

        // A: +1 across the array
        var addOne = new AddState { Data = data, Value = 1 };
        var handleA = scheduler.ScheduleParallel(&AddBody, &addOne, length, batchSize: 4096);

        // B and C: +10 to the first half and +20 to the second half, both after A, in parallel
        var half = length / 2;
        var addB = new AddState { Data = data, Value = 10 };
        var addC = new AddState { Data = data + half, Value = 20 };
        var handleB = scheduler.ScheduleParallel(&AddBody, &addB, half, 4096, handleA);
        var handleC = scheduler.ScheduleParallel(&AddBody, &addC, length - half, 4096, handleA);

        // D: completes when B and C complete
        var handleD = scheduler.Combine(handleB, handleC);

        scheduler.Complete(handleD);
        scheduler.ResetFrame();

        NativeMemory.Free(data);
    }
}
```

## API

### JobScheduler
Namespace `FFS.Libraries.StaticScheduler`, `IDisposable`.

#### Creation
```csharp
JobScheduler(in SchedulerConfig config); // an invalid configuration throws SchedulerException before anything is allocated

int WorkerCount;       // workers including the owner thread, 1 in synchronous mode
bool IsSynchronous;    // created with WorkerCount 0
int CurrentThreadRank; // a worker thread gets its rank 0 .. WorkerCount - 2, any other thread the owner rank WorkerCount - 1
bool IsInsideJobBody;  // the calling thread runs inside a job body of this scheduler
```

#### Scheduling
```csharp
// body: delegate*<void*, int, int, void>
JobHandle Schedule(body, void* userData);
JobHandle Schedule(body, void* userData, JobHandle dependency);
JobHandle Schedule(body, void* userData, ReadOnlySpan<JobHandle> dependencies);
JobHandle Schedule(body, void* userData, JobHandle* dependencies, int dependencyCount);

// a batchSize below 1 counts as 1, a length of 0 or below schedules no batch
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, JobHandle dependency);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, ReadOnlySpan<JobHandle> dependencies);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, JobHandle* dependencies, int dependencyCount);

// the length is read from *deferredLength when the dependencies complete
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize);
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize, JobHandle dependency);
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize, JobHandle* dependencies, int dependencyCount);

// a barrier that completes when every input completes
JobHandle Combine(JobHandle a, JobHandle b);
JobHandle Combine(ReadOnlySpan<JobHandle> dependencies);
JobHandle Combine(JobHandle* dependencies, int dependencyCount);
```

#### Completion
```csharp
bool IsCompleted(JobHandle handle);            // any thread, non-blocking
void Complete(JobHandle handle);               // the owner runs queued batches while it waits
void CompleteWithoutHelping(JobHandle handle); // the owner waits without running other batches
void CompleteAll();                            // every job of the frame
void ResetFrame();                             // completes the work in flight, returns the graph pool, starts a new frame
```

#### Ownership and lifetime
```csharp
void EnsureOwnerThread();                // binds an unbound scheduler to the calling thread and checks it
void TransferOwnershipToCurrentThread(); // while no job is in flight
void Dispose();                          // completes the work in flight, stops the workers, frees the memory
```

### JobHandle
```csharp
static JobHandle Null; // equals default(JobHandle), stands for no job and counts as completed
bool IsNull;
// IEquatable<JobHandle>, ==, !=
```

### SchedulerConfig
```csharp
int WorkerCount;           // 0: synchronous mode; a positive value is clamped to Environment.ProcessorCount
int SpinMicroseconds;      // time a worker polls for work before it sleeps, default 100, 0 sleeps at once
long InitialCapacity;      // per-worker deque capacity, 0 to 2^28, default 4096; a deque grows on demand up to 2^28 batches
int NodeCapacity;          // jobs per frame, at most 2^24, 0 derives it from InitialCapacity
int EdgeCapacity;          // dependency edges per frame, at most 2^26, 0 derives it from the node capacity
string ThreadNamePrefix;   // default "FFS.StaticScheduler."
PackAllocator? Allocator;  // native memory of the scheduler, PackMemory.Default() when unset
SchedulerFaultPolicy FaultPolicy;

static SchedulerConfig Default();        // every logical processor
static SchedulerConfig SingleThreaded(); // synchronous mode
```

# Example: a frame of a particle simulation
A step beyond the quick start: one frame of a simulation of 100 000 particles, built as a small graph of jobs and run
for 120 frames.

```
Integrate ──► Compact ──► MeasureBounds ──► MergeBounds ──┐
 parallel      single      parallel,         single        ├──► Combine ──► Complete
                           deferred length                 │
UpdateWind ────────────────────────────────────────────────┘
 single, alongside the chain
```

- **Integrate** moves every particle — a parallel-for over the whole array.
- **Compact** gathers the indices of the particles that are still alive. It is a single job, and only it knows how many
  particles are left.
- **MeasureBounds** computes the bounding box of the living particles. Its length is that count, unknown at scheduling
  time, so it is scheduled with `ScheduleParallelDeferred`: the length is read from `AliveCount` once `Compact` has run.
- **MergeBounds** merges the boxes of the batches into one.
- **UpdateWind** prepares the wind of the next frame. It depends on nothing and runs on a free worker alongside the chain.
- **Combine** joins both branches into one handle, and `Complete` waits for it while the main thread runs batches too.

```csharp
using System;
using System.Runtime.InteropServices;
using FFS.Libraries.StaticScheduler;

public static unsafe class ParticleSimulation {
    const int Capacity = 100_000;
    const int BatchSize = 1024;
    const int MaxBatches = (Capacity + BatchSize - 1) / BatchSize;
    const float DeltaTime = 1f / 60f;
    const float Gravity = -9.8f;

    struct Bounds {
        public float MinX, MinY, MaxX, MaxY;

        public static Bounds Empty => new Bounds { MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };

        public void Include(float x, float y) {
            MinX = MathF.Min(MinX, x);
            MinY = MathF.Min(MinY, y);
            MaxX = MathF.Max(MaxX, x);
            MaxY = MathF.Max(MaxY, y);
        }

        public void Include(in Bounds other) {
            MinX = MathF.Min(MinX, other.MinX);
            MinY = MathF.Min(MinY, other.MinY);
            MaxX = MathF.Max(MaxX, other.MaxX);
            MaxY = MathF.Max(MaxY, other.MaxY);
        }
    }

    // All the state the jobs share. It lives in native memory, so every body gets it through one pointer.
    struct World {
        public float* PositionX, PositionY, VelocityX, VelocityY, Life;
        public int Count;

        public int* AliveIndices; // written by Compact
        public int AliveCount;    // written by Compact, read by MeasureBounds as its length

        public Bounds* BatchBounds; // one slot per batch of MeasureBounds
        public Bounds Bounds;       // written by MergeBounds

        public float Wind;     // read by Integrate
        public float NextWind; // written by UpdateWind while Integrate runs
        public int Frame;
    }

    // Parallel: each batch moves its own range of particles.
    static void Integrate(void* data, int start, int end) {
        var world = (World*) data;
        for (var index = start; index < end; index++) {
            world->VelocityX[index] += world->Wind * DeltaTime;
            world->VelocityY[index] += Gravity * DeltaTime;
            world->PositionX[index] += world->VelocityX[index] * DeltaTime;
            world->PositionY[index] += world->VelocityY[index] * DeltaTime;
            world->Life[index] -= DeltaTime;
        }
    }

    // Single: gathers the particles that are still alive. How many there are is known only here.
    static void Compact(void* data, int start, int end) {
        var world = (World*) data;
        var alive = 0;
        for (var index = 0; index < world->Count; index++) {
            if (world->Life[index] > 0f) world->AliveIndices[alive++] = index;
        }
        world->AliveCount = alive;
    }

    // Parallel over AliveCount: each batch writes only its own slot, so the result does not depend on which worker ran it.
    static void MeasureBounds(void* data, int start, int end) {
        var world = (World*) data;
        var bounds = Bounds.Empty;
        for (var position = start; position < end; position++) {
            var index = world->AliveIndices[position];
            bounds.Include(world->PositionX[index], world->PositionY[index]);
        }
        world->BatchBounds[start / BatchSize] = bounds;
    }

    // Single: merges the batch slots in order.
    static void MergeBounds(void* data, int start, int end) {
        var world = (World*) data;
        var batches = (world->AliveCount + BatchSize - 1) / BatchSize;
        var bounds = Bounds.Empty;
        for (var batch = 0; batch < batches; batch++) bounds.Include(world->BatchBounds[batch]);
        world->Bounds = bounds;
    }

    // Single, independent of the chain: it writes NextWind, never Wind, which Integrate reads at the same time.
    static void UpdateWind(void* data, int start, int end) {
        var world = (World*) data;
        world->NextWind = 2f * MathF.Sin(world->Frame * 0.1f);
    }

    public static void Main() {
        var world = (World*) NativeMemory.AllocZeroed((nuint) sizeof(World));
        world->PositionX = Allocate<float>(Capacity);
        world->PositionY = Allocate<float>(Capacity);
        world->VelocityX = Allocate<float>(Capacity);
        world->VelocityY = Allocate<float>(Capacity);
        world->Life = Allocate<float>(Capacity);
        world->AliveIndices = Allocate<int>(Capacity);
        world->BatchBounds = Allocate<Bounds>(MaxBatches);
        world->Count = Capacity;

        var random = new Random(1);
        for (var index = 0; index < Capacity; index++) {
            world->VelocityX[index] = (float) random.NextDouble() * 4f - 2f;
            world->VelocityY[index] = (float) random.NextDouble() * 10f;
            world->Life[index] = (float) random.NextDouble() * 2f;
        }

        using var scheduler = new JobScheduler(SchedulerConfig.Default());

        for (var frame = 0; frame < 120; frame++) {
            world->Frame = frame;
            try {
                var integrate = scheduler.ScheduleParallel(&Integrate, world, world->Count, BatchSize);
                var compact = scheduler.Schedule(&Compact, world, integrate);
                var measure = scheduler.ScheduleParallelDeferred(&MeasureBounds, world, &world->AliveCount, BatchSize, compact);
                var merge = scheduler.Schedule(&MergeBounds, world, measure);
                var wind = scheduler.Schedule(&UpdateWind, world);

                var frameDone = scheduler.Combine(merge, wind);
                scheduler.Complete(frameDone);
            }
            catch (SchedulerException exception) {
                Console.WriteLine($"Frame {frame} failed: {exception.InnerException?.Message}");
            }
            finally {
                scheduler.ResetFrame();
            }

            // Every job of the frame is done, so the main thread may touch the shared state again.
            world->Wind = world->NextWind;
            if (frame % 30 == 0) {
                var bounds = world->Bounds;
                Console.WriteLine($"Frame {frame,3}: {world->AliveCount,6} alive, bounds X [{bounds.MinX:F1}; {bounds.MaxX:F1}] Y [{bounds.MinY:F1}; {bounds.MaxY:F1}]");
            }
        }

        NativeMemory.Free(world->PositionX);
        NativeMemory.Free(world->PositionY);
        NativeMemory.Free(world->VelocityX);
        NativeMemory.Free(world->VelocityY);
        NativeMemory.Free(world->Life);
        NativeMemory.Free(world->AliveIndices);
        NativeMemory.Free(world->BatchBounds);
        NativeMemory.Free(world);
    }

    static T* Allocate<T>(int count) where T : unmanaged => (T*) NativeMemory.AllocZeroed((nuint) count, (nuint) sizeof(T));
}
```

Output — the same on every run and for every worker count, `SchedulerConfig.SingleThreaded()` included:

```
Frame   0:  99170 alive, bounds X [-0.0; 0.0] Y [-0.0; 0.2]
Frame  30:  73925 alive, bounds X [-0.9; 1.2] Y [-1.4; 3.8]
Frame  60:  48969 alive, bounds X [-1.7; 2.4] Y [-5.1; 5.0]
Frame  90:  23995 alive, bounds X [-2.6; 3.5] Y [-11.4; 3.8]
```

What keeps the frame correct:
- **Disjoint data.** The batches of `Integrate` touch only their own index ranges; `MeasureBounds` writes one slot per
  batch (`start / BatchSize`) and `MergeBounds` combines the slots in order, so the box is the same whichever worker ran
  which batch.
- **No race with the parallel branch.** `UpdateWind` runs while `Integrate` reads `Wind`, so it writes `NextWind`; the
  main thread copies it into `Wind` after `Complete`, when no job of the frame is running.
- **Frame boundary.** `ResetFrame` in `finally` ends the frame even when a job body threw, and `Complete` reports that
  exception as the inner exception of `SchedulerException`.

# Thread model
- The owner is the thread of the first call. `TransferOwnershipToCurrentThread()` moves it while no job is in flight; `EnsureOwnerThread()` binds an unbound scheduler and checks the calling thread.
- `Schedule`, `ScheduleParallel`, `ScheduleParallelDeferred` and `Combine` run on the owner thread or inside a running job.
- `Complete`, `CompleteWithoutHelping`, `CompleteAll` and `ResetFrame` run on the owner thread, outside any job body; `IsCompleted` runs on any thread. `IsInsideJobBody` tells whether the calling thread runs inside a job body of the scheduler: a worker thread, or the owner while it runs batches inside a wait.
- `Dispose` runs on any thread outside a job body and completes the work still in flight first: the owner helps, another thread waits for the worker threads. With jobs in flight and no worker threads (`WorkerCount` 1) only the owner runs them, so `Dispose` from another thread throws `SchedulerException`.
- The batches of one `ScheduleParallel` run concurrently on disjoint index ranges; a batch writes only the data of its own range.
- A scheduler that is never disposed stops its worker threads and frees its memory in its finalizer. When a worker is still inside a job body after the join timeout of 10 seconds, the memory stays allocated until the process ends.

# Failures
The exception of a job body is captured. `Complete(handle)` returns once every batch of the job has run and then throws `SchedulerException` with the exceptions of the failed jobs the handle reaches as `InnerException` (an `AggregateException` for several). `ResetFrame()` throws the failures no `Complete` reported, after it resets the frame.

Each of the first 63 failed jobs of a frame owns a fault slot and the failed jobs past them share the 64th, so a handle that reaches one of them reports the exceptions of all of them, as an `AggregateException` whose message says so. A frame keeps 64 exceptions; the first report after the limit counts the rest. A parallel-for with more batches than a deque holds (2^28) fails like a job whose body threw, without running a batch.

`SchedulerConfig.FaultPolicy` decides what the dependents of a failed job do:

| `SchedulerFaultPolicy` | Dependents of a failed job |
|---|---|
| `CancelDependents` (default) | complete without running their bodies, and pass the failure on |
| `RunDependents` | run their bodies; `Complete` still reports the failure |
| `FailFast` | the process ends through `Environment.FailFast` |

# Checks

| Level | Symbols | Checks |
|---|---|---|
| Full | `DEBUG` or `FFS_SCHEDULER_ENABLE_DEBUG` (off with `FFS_SCHEDULER_DISABLE_DEBUG`) | every check below, argument checks, a report of the jobs still in flight at `ResetFrame` |
| Essential | default, off with `FFS_SCHEDULER_DISABLE_CHECKS` | owner thread, calls from a job body, disposal, handle range and generation |
| Catch | default, off with `FFS_SCHEDULER_DISABLE_CATCH` | job body exceptions captured and reported |

Pool overflow, configuration validation and the completion of in-flight work by `ResetFrame` / `Dispose` hold at every level. With Catch off a job body runs without a `try` block. An exception of a body run by a worker thread is an unhandled exception of that thread: on .NET it ends the process; in a Unity player (Mono and IL2CPP) the thread ends, the process goes on, and the job stays uncompleted. An exception of a body run by the owner reaches the caller of `Complete` and leaves its job uncompleted. Either way the scheduler is unusable afterwards — `ResetFrame`, `CompleteAll` and `Dispose` wait for that job forever.

# Determinism
Work stealing changes which worker runs which batch and when, and leaves the result of distribution-invariant work unchanged: a batch whose output is a pure function of its own input range gives the same result for every worker count. Cross-batch reductions and ordering-sensitive side effects are made deterministic by the caller.

# License
[MIT license](./LICENSE.md)
