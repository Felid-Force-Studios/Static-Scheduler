<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-1.0.0-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticScheduler/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticScheduler-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
</p>

# Static Scheduler - C# 工作窃取任务调度器
- 基于每个工作线程独立的 Chase-Lev 双端队列的无锁工作窃取（Chase & Lev 2005，采用 Lê 等人 PPoPP 2013 中经过验证的内存模型）
- 依赖图：`JobHandle`、`Combine`，以及在运行中的任务内嵌套调度
- 调用线程会协助完成它所等待的工作
- 稳定态下没有托管分配：原生内存与每帧图池
- 同步模式，单核平台与 WebGL 使用同一份代码
- 任务体抛出的异常由每个能到达该任务的句柄的 `Complete` 报告
- 由宏定义设置的三个检查级别
- 兼容 Unity 及其他 C# 引擎

#### 限制与特性：
> - 任务体是函数指针 `delegate*<void*, int, int, void>`；其状态通过 `void*` 指针传递，该指针在任务完成前保持有效
> - 句柄在创建它的帧内有效；`ResetFrame` 结束该帧

## 目录
* [联系方式](#联系方式)
* [支持项目](#支持项目)
* [环境要求](#环境要求)
* [安装](#安装)
* [概念](#概念)
* [快速开始](#快速开始)
* [API](#api)
  * [JobScheduler](#jobscheduler)
  * [JobHandle](#jobhandle)
  * [SchedulerConfig](#schedulerconfig)
* [示例：粒子模拟的一帧](#示例粒子模拟的一帧)
* [线程模型](#线程模型)
* [失败](#失败)
* [检查](#检查)
* [确定性](#确定性)
* [许可证](#许可证)

# 联系方式
* [felid.force.studios@gmail.com](mailto:felid.force.studios@gmail.com)
* [Telegram](https://t.me/felid_force_studios)

# 支持项目
如果您喜欢 Static Scheduler 并且它对您的项目有所帮助，您可以支持开发：

<a href="https://www.buymeacoffee.com/felid.force.studios" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="60"></a>

# 环境要求
* **Unity** 6 或更高版本。
* **.NET** 6.0 或更高版本，或 netstandard2.1。
* [Static Pack](https://github.com/Felid-Force-Studios/StaticPack) `2.0.0` —— 调度器的原生分配器。

# 安装
* ### 以源代码形式
  从发布页面或从分支下载归档文件。`master` 分支包含稳定测试版本
* ### Unity 安装
  通过 Unity PackageManager 的 git 模块安装该包及 Static Pack：
  ```
  https://github.com/Felid-Force-Studios/StaticPack.git
  https://github.com/Felid-Force-Studios/StaticScheduler.git
  ```
  或添加到 `Packages/manifest.json` 清单文件：
  ```json
  "com.felid-force-studios.static-pack": "https://github.com/Felid-Force-Studios/StaticPack.git"
  "com.felid-force-studios.static-scheduler": "https://github.com/Felid-Force-Studios/StaticScheduler.git"
  ```
* ### NuGet
  ```
  dotnet add package FFS.StaticScheduler
  ```
  用于带断言的调试构建：
  ```
  dotnet add package FFS.StaticScheduler.Debug
  ```
  包：[FFS.StaticScheduler](https://www.nuget.org/packages/FFS.StaticScheduler/) · [FFS.StaticScheduler.Debug](https://www.nuget.org/packages/FFS.StaticScheduler.Debug/)

  NuGet 会自动安装 Static Pack。

# 概念
调度器遵循 Unity Job System 的模型 —— `Schedule` → 依赖 → `Complete`：
> - **任务体** 以 `(userData, startInclusive, endExclusive)` 调用
> - **`Schedule`** 将任务体作为 `[0, 1)` 执行一次；**`ScheduleParallel`** 把 `[0, length)` 切成大小为 `batchSize` 的批次，每个批次完整地在一个工作线程上运行
> - **`JobHandle`** 标识一个已调度的任务；作为依赖传入或传给 `Combine` 的句柄构成图
> - **`Complete(handle)`** 阻塞持有者线程，直到任务及其依赖完成，等待期间执行队列中的批次；**`CompleteWithoutHelping(handle)`** 等待时不执行其他批次，任务一完成就返回；**`CompleteAll()`** 完成本帧的所有任务
> - **`ResetFrame()`** 在帧末完成仍在执行的工作，并归还图池

每个工作线程拥有自己的队列：它从底端取自己的批次，空闲的工作线程从其他工作线程队列的顶端窃取。工作线程没有工作时会在 `SpinMicroseconds` 内继续查找，然后休眠直到有新批次到来。

# 快速开始
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

        // A：整个数组 +1
        var addOne = new AddState { Data = data, Value = 1 };
        var handleA = scheduler.ScheduleParallel(&AddBody, &addOne, length, batchSize: 4096);

        // B 与 C：前半部分 +10、后半部分 +20，都在 A 之后并行执行
        var half = length / 2;
        var addB = new AddState { Data = data, Value = 10 };
        var addC = new AddState { Data = data + half, Value = 20 };
        var handleB = scheduler.ScheduleParallel(&AddBody, &addB, half, 4096, handleA);
        var handleC = scheduler.ScheduleParallel(&AddBody, &addC, length - half, 4096, handleA);

        // D：B 与 C 都完成时完成
        var handleD = scheduler.Combine(handleB, handleC);

        scheduler.Complete(handleD);
        scheduler.ResetFrame();

        NativeMemory.Free(data);
    }
}
```

## API

### JobScheduler
命名空间 `FFS.Libraries.StaticScheduler`，`IDisposable`。

#### 创建
```csharp
JobScheduler(in SchedulerConfig config); // 无效配置会在分配任何内存之前抛出 SchedulerException

int WorkerCount;       // 包括持有者线程在内的工作线程数，同步模式下为 1
bool IsSynchronous;    // 以 WorkerCount 0 创建
int CurrentThreadRank; // 工作线程得到其序号 0 .. WorkerCount - 2，其他线程得到持有者序号 WorkerCount - 1
bool IsInsideJobBody;  // 调用线程正在此调度器的任务体内运行
```

#### 调度
```csharp
// body: delegate*<void*, int, int, void>
JobHandle Schedule(body, void* userData);
JobHandle Schedule(body, void* userData, JobHandle dependency);
JobHandle Schedule(body, void* userData, ReadOnlySpan<JobHandle> dependencies);
JobHandle Schedule(body, void* userData, JobHandle* dependencies, int dependencyCount);

// batchSize 小于 1 时按 1 计算，length 为 0 或更小时不调度任何批次
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, JobHandle dependency);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, ReadOnlySpan<JobHandle> dependencies);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, JobHandle* dependencies, int dependencyCount);

// 依赖完成时从 *deferredLength 读取长度
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize);
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize, JobHandle dependency);
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize, JobHandle* dependencies, int dependencyCount);

// 所有输入完成时完成的屏障
JobHandle Combine(JobHandle a, JobHandle b);
JobHandle Combine(ReadOnlySpan<JobHandle> dependencies);
JobHandle Combine(JobHandle* dependencies, int dependencyCount);
```

#### 完成
```csharp
bool IsCompleted(JobHandle handle);            // 任意线程，非阻塞
void Complete(JobHandle handle);               // 持有者在等待期间执行队列中的批次
void CompleteWithoutHelping(JobHandle handle); // 持有者等待时不执行其他批次
void CompleteAll();                            // 本帧的所有任务
void ResetFrame();                             // 完成执行中的工作，归还图池，开始新的一帧
```

#### 持有权与生命周期
```csharp
void EnsureOwnerThread();                // 将尚未绑定的调度器绑定到调用线程并检查该线程
void TransferOwnershipToCurrentThread(); // 在没有任务执行时
void Dispose();                          // 完成执行中的工作，停止工作线程，释放内存
```

### JobHandle
```csharp
static JobHandle Null; // 等于 default(JobHandle)，表示没有任务，视为已完成
bool IsNull;
// IEquatable<JobHandle>、==、!=
```

### SchedulerConfig
```csharp
int WorkerCount;           // 0：同步模式；正值上限为 Environment.ProcessorCount
int SpinMicroseconds;      // 工作线程休眠前查找工作的时间，默认 100，为 0 时立即休眠
long InitialCapacity;      // 每个工作线程的队列容量，0 到 2^28，默认 4096；队列按需增长，最多 2^28 个批次
int NodeCapacity;          // 每帧任务数，最多 2^24，为 0 时由 InitialCapacity 推导
int EdgeCapacity;          // 每帧依赖边数，最多 2^26，为 0 时由节点容量推导
string ThreadNamePrefix;   // 默认 "FFS.StaticScheduler."
PackAllocator? Allocator;  // 调度器的原生内存，未设置时为 PackMemory.Default()
SchedulerFaultPolicy FaultPolicy;

static SchedulerConfig Default();        // 所有逻辑处理器
static SchedulerConfig SingleThreaded(); // 同步模式
```

# 示例：粒子模拟的一帧
比快速开始更进一步：模拟 100 000 个粒子的一帧，构建为一个小型任务图，连续运行 120 帧。

```
Integrate ──► Compact ──► MeasureBounds ──► MergeBounds ──┐
 并行          单个        并行，            单个          ├──► Combine ──► Complete
                           延迟长度                        │
UpdateWind ────────────────────────────────────────────────┘
 单个任务，与链并行
```

- **Integrate** 移动每个粒子 —— 对整个数组的 parallel-for。
- **Compact** 收集仍然存活的粒子的索引。它是单个任务，只有它知道还剩多少粒子。
- **MeasureBounds** 计算存活粒子的包围盒。它的长度就是这个数量，在调度时未知，因此用 `ScheduleParallelDeferred`
  调度：`Compact` 运行完后才从 `AliveCount` 读取长度。
- **MergeBounds** 把各批次的包围盒合并为一个。
- **UpdateWind** 准备下一帧的风。它不依赖任何任务，在空闲的工作线程上与链并行运行。
- **Combine** 把两个分支合并为一个句柄，`Complete` 等待它，同时主线程也在执行批次。

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

    // 任务共享的全部状态。它位于原生内存中，每个任务体通过同一个指针获取它。
    struct World {
        public float* PositionX, PositionY, VelocityX, VelocityY, Life;
        public int Count;

        public int* AliveIndices; // 由 Compact 写入
        public int AliveCount;    // 由 Compact 写入，MeasureBounds 将其作为长度读取

        public Bounds* BatchBounds; // MeasureBounds 每个批次一个槽
        public Bounds Bounds;       // 由 MergeBounds 写入

        public float Wind;     // 由 Integrate 读取
        public float NextWind; // Integrate 运行期间由 UpdateWind 写入
        public int Frame;
    }

    // 并行：每个批次移动自己区间内的粒子。
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

    // 单个任务：收集仍然存活的粒子。只有这里知道还剩多少。
    static void Compact(void* data, int start, int end) {
        var world = (World*) data;
        var alive = 0;
        for (var index = 0; index < world->Count; index++) {
            if (world->Life[index] > 0f) world->AliveIndices[alive++] = index;
        }
        world->AliveCount = alive;
    }

    // 按 AliveCount 并行：每个批次只写自己的槽，因此结果与哪个工作线程执行它无关。
    static void MeasureBounds(void* data, int start, int end) {
        var world = (World*) data;
        var bounds = Bounds.Empty;
        for (var position = start; position < end; position++) {
            var index = world->AliveIndices[position];
            bounds.Include(world->PositionX[index], world->PositionY[index]);
        }
        world->BatchBounds[start / BatchSize] = bounds;
    }

    // 单个任务：按顺序合并各批次的槽。
    static void MergeBounds(void* data, int start, int end) {
        var world = (World*) data;
        var batches = (world->AliveCount + BatchSize - 1) / BatchSize;
        var bounds = Bounds.Empty;
        for (var batch = 0; batch < batches; batch++) bounds.Include(world->BatchBounds[batch]);
        world->Bounds = bounds;
    }

    // 单个任务，独立于链：它写 NextWind，而不是 Integrate 同时在读的 Wind。
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

            // 本帧的所有任务都已完成，主线程可以再次访问共享状态。
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

输出 —— 每次运行、任意工作线程数量（包括 `SchedulerConfig.SingleThreaded()`）下都相同：

```
Frame   0:  99170 alive, bounds X [-0.0; 0.0] Y [-0.0; 0.2]
Frame  30:  73925 alive, bounds X [-0.9; 1.2] Y [-1.4; 3.8]
Frame  60:  48969 alive, bounds X [-1.7; 2.4] Y [-5.1; 5.0]
Frame  90:  23995 alive, bounds X [-2.6; 3.5] Y [-11.4; 3.8]
```

保证这一帧正确的要点：
- **互不相交的数据。** `Integrate` 的批次只访问自己的索引区间；`MeasureBounds` 每个批次写一个槽（`start / BatchSize`），
  `MergeBounds` 按顺序合并这些槽，因此无论哪个工作线程执行哪个批次，包围盒都相同。
- **与并行分支没有竞争。** `UpdateWind` 运行时 `Integrate` 正在读取 `Wind`，因此它写入 `NextWind`；主线程在 `Complete`
  之后、本帧没有任务运行时把它复制到 `Wind`。
- **帧边界。** `finally` 中的 `ResetFrame` 即使在任务体抛出异常时也会结束该帧，`Complete` 将该异常作为
  `SchedulerException` 的内部异常报告。

# 线程模型
- 持有者是首次调用所在的线程。`TransferOwnershipToCurrentThread()` 可在没有任务执行时转移持有权；`EnsureOwnerThread()` 绑定尚未绑定的调度器并检查调用线程。
- `Schedule`、`ScheduleParallel`、`ScheduleParallelDeferred` 与 `Combine` 在持有者线程或运行中的任务内执行。
- `Complete`、`CompleteWithoutHelping`、`CompleteAll` 与 `ResetFrame` 在持有者线程、任务体之外执行；`IsCompleted` 可在任意线程执行。`IsInsideJobBody` 表示调用线程是否在调度器的任务体内运行：工作线程，或持有者在等待中执行批次时。
- `Dispose` 可在任意线程、任务体之外执行，并会先完成仍在执行的工作：持有者协助执行，其他线程等待工作线程。若有任务在执行而没有工作线程（`WorkerCount` 为 1），这些任务只能由持有者执行，因此从其他线程调用 `Dispose` 会抛出 `SchedulerException`。
- 同一次 `ScheduleParallel` 的批次在互不相交的索引区间上并发运行；批次只写入自身区间的数据。
- 从未被释放的调度器会在其终结器中停止工作线程并释放内存。若等待 10 秒超时后某个工作线程仍在任务体内，内存会保留到进程结束。

# 失败
任务体的异常会被捕获。`Complete(handle)` 会等到该任务的所有批次都运行完毕后返回，随后抛出 `SchedulerException`，其 `InnerException` 为该句柄所能到达的失败任务的异常（多个时为 `AggregateException`）。`ResetFrame()` 在重置帧之后，抛出未被任何 `Complete` 报告的失败。

一帧中前 63 个失败任务各自拥有一个故障槽，其后的失败任务共享第 64 个槽，因此到达其中任一任务的句柄会报告所有这些任务的异常，以 `AggregateException` 给出，并在消息中说明这一点。一帧保留 64 个异常；超过上限后的第一次报告会给出其余异常的数量。批次数超过队列容量（2^28）的 parallel-for 会像任务体抛出异常的任务一样失败，不运行任何批次。

`SchedulerConfig.FaultPolicy` 决定失败任务的依赖任务如何处理：

| `SchedulerFaultPolicy` | 失败任务的依赖任务 |
|---|---|
| `CancelDependents`（默认） | 不运行任务体即完成，并继续传递该失败 |
| `RunDependents` | 运行任务体；`Complete` 仍会报告该失败 |
| `FailFast` | 进程通过 `Environment.FailFast` 终止 |

# 检查

| 级别 | 符号 | 检查内容 |
|---|---|---|
| 完整 | `DEBUG` 或 `FFS_SCHEDULER_ENABLE_DEBUG`（用 `FFS_SCHEDULER_DISABLE_DEBUG` 关闭） | 下列全部检查、参数检查、`ResetFrame` 时仍在执行的任务的报告 |
| 必要 | 默认开启，用 `FFS_SCHEDULER_DISABLE_CHECKS` 关闭 | 持有者线程、来自任务体的调用、释放状态、句柄范围与代数 |
| 捕获 | 默认开启，用 `FFS_SCHEDULER_DISABLE_CATCH` 关闭 | 捕获并报告任务体异常 |

池溢出检查、配置校验以及 `ResetFrame` / `Dispose` 对执行中工作的完成在所有级别下均生效。关闭捕获后，任务体在没有 `try` 块的情况下运行。由工作线程执行的任务体抛出的异常是该线程的未处理异常：在 .NET 上会终止进程；在 Unity 播放器（Mono 与 IL2CPP）中该线程结束，进程继续运行，任务保持未完成。由持有者执行的任务体抛出的异常会传给 `Complete` 的调用方，并使其任务无法完成。无论哪种情况，此后调度器都不可再用——`ResetFrame`、`CompleteAll` 与 `Dispose` 会一直等待该任务。

# 确定性
工作窃取会改变哪个工作线程在何时运行哪个批次，而分布无关工作的结果保持不变：输出是其自身输入区间纯函数的批次，在任意工作线程数量下都给出相同的结果。跨批次归约与对顺序敏感的副作用由调用方保证确定性。

# 许可证
[MIT license](./LICENSE.md)
