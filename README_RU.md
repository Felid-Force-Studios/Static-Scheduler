<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-1.0.0-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticScheduler/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticScheduler-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
</p>

# Static Scheduler - C# планировщик задач с work stealing
- Lock-free work stealing на Chase-Lev deque у каждого воркера (Chase & Lev 2005, в проверенной модели памяти Lê et al., PPoPP 2013)
- Граф зависимостей: `JobHandle`, `Combine`, вложенное планирование из выполняющейся задачи
- Вызывающий поток помогает выполнять работу, которую ждёт
- Без управляемых аллокаций в установившемся режиме: нативная память и пул графа на кадр
- Синхронный режим для одноядерных платформ и WebGL с тем же кодом
- Исключение тела задачи сообщает `Complete` каждого handle, который достигает задачи
- Три уровня проверок, задаваемые дефайнами
- Совместим с Unity и другими C# движками

#### Ограничения и особенности:
> - Тело задачи — указатель на функцию `delegate*<void*, int, int, void>`; его состояние передаётся через указатель `void*`, который остаётся валидным до завершения задачи
> - Handle действителен в пределах кадра, в котором создан; кадр завершает `ResetFrame`

## Оглавление
* [Контакты](#контакты)
* [Поддержать проект](#поддержать-проект)
* [Требования](#требования)
* [Установка](#установка)
* [Концепция](#концепция)
* [Быстрый старт](#быстрый-старт)
* [API](#api)
  * [JobScheduler](#jobscheduler)
  * [JobHandle](#jobhandle)
  * [SchedulerConfig](#schedulerconfig)
* [Пример: кадр симуляции частиц](#пример-кадр-симуляции-частиц)
* [Потоковая модель](#потоковая-модель)
* [Ошибки](#ошибки)
* [Проверки](#проверки)
* [Детерминизм](#детерминизм)
* [Лицензия](#лицензия)

# Контакты
* [felid.force.studios@gmail.com](mailto:felid.force.studios@gmail.com)
* [Telegram](https://t.me/felid_force_studios)

# Поддержать проект
Если вам нравится Static Scheduler и он помогает вашему проекту, вы можете поддержать разработку:

<a href="https://www.buymeacoffee.com/felid.force.studios" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" height="60"></a>

# Требования
* **Unity** 6 или новее.
* **.NET** 6.0 или новее, либо netstandard2.1.
* [Static Pack](https://github.com/Felid-Force-Studios/StaticPack) `2.0.0` — нативный аллокатор планировщика.

# Установка
* ### В виде исходников
  Со страницы релизов или как архив из нужной ветки. В ветке `master` стабильная проверенная версия
* ### Установка для Unity
  Через git модуль в Unity PackageManager — пакет вместе со Static Pack:
  ```
  https://github.com/Felid-Force-Studios/StaticPack.git
  https://github.com/Felid-Force-Studios/StaticScheduler.git
  ```
  Или добавление в манифест `Packages/manifest.json`:
  ```json
  "com.felid-force-studios.static-pack": "https://github.com/Felid-Force-Studios/StaticPack.git"
  "com.felid-force-studios.static-scheduler": "https://github.com/Felid-Force-Studios/StaticScheduler.git"
  ```
* ### NuGet
  ```
  dotnet add package FFS.StaticScheduler
  ```
  Для debug-сборки с проверками:
  ```
  dotnet add package FFS.StaticScheduler.Debug
  ```
  Пакеты: [FFS.StaticScheduler](https://www.nuget.org/packages/FFS.StaticScheduler/) · [FFS.StaticScheduler.Debug](https://www.nuget.org/packages/FFS.StaticScheduler.Debug/)

  NuGet устанавливает Static Pack автоматически.

# Концепция
Планировщик следует модели Unity Job System — `Schedule` → зависимости → `Complete`:
> - **Тело задачи** вызывается как `(userData, startInclusive, endExclusive)`
> - **`Schedule`** выполняет тело один раз как `[0, 1)`; **`ScheduleParallel`** делит `[0, length)` на батчи по `batchSize`, каждый батч целиком выполняется на одном воркере
> - **`JobHandle`** обозначает запланированную задачу; handle, переданные как зависимости или в `Combine`, строят граф
> - **`Complete(handle)`** блокирует поток-владелец, пока задача и её зависимости не завершатся, выполняя батчи из очереди во время ожидания; **`CompleteWithoutHelping(handle)`** ждёт, не выполняя другие батчи, и возвращает управление, как только задача завершена; **`CompleteAll()`** завершает все задачи кадра
> - **`ResetFrame()`** завершает работу, которая ещё выполняется, и возвращает пул графа в конце кадра

У каждого воркера своя очередь: свои батчи он берёт с нижнего конца, а воркер без работы крадёт с верхнего конца чужой очереди. Воркер, у которого кончилась работа, ищет её в течение `SpinMicroseconds`, затем засыпает до появления новых батчей.

# Быстрый старт
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

        // A: +1 ко всему массиву
        var addOne = new AddState { Data = data, Value = 1 };
        var handleA = scheduler.ScheduleParallel(&AddBody, &addOne, length, batchSize: 4096);

        // B и C: +10 к первой половине и +20 ко второй, обе после A, параллельно
        var half = length / 2;
        var addB = new AddState { Data = data, Value = 10 };
        var addC = new AddState { Data = data + half, Value = 20 };
        var handleB = scheduler.ScheduleParallel(&AddBody, &addB, half, 4096, handleA);
        var handleC = scheduler.ScheduleParallel(&AddBody, &addC, length - half, 4096, handleA);

        // D: завершается, когда завершены B и C
        var handleD = scheduler.Combine(handleB, handleC);

        scheduler.Complete(handleD);
        scheduler.ResetFrame();

        NativeMemory.Free(data);
    }
}
```

## API

### JobScheduler
Пространство имён `FFS.Libraries.StaticScheduler`, `IDisposable`.

#### Создание
```csharp
JobScheduler(in SchedulerConfig config); // некорректная конфигурация бросает SchedulerException до выделения памяти

int WorkerCount;       // воркеры вместе с потоком-владельцем, 1 в синхронном режиме
bool IsSynchronous;    // создан с WorkerCount 0
int CurrentThreadRank; // рабочий поток получает свой ранг 0 .. WorkerCount - 2, любой другой поток — ранг владельца WorkerCount - 1
bool IsInsideJobBody;  // вызывающий поток выполняется внутри тела задачи этого планировщика
```

#### Планирование
```csharp
// body: delegate*<void*, int, int, void>
JobHandle Schedule(body, void* userData);
JobHandle Schedule(body, void* userData, JobHandle dependency);
JobHandle Schedule(body, void* userData, ReadOnlySpan<JobHandle> dependencies);
JobHandle Schedule(body, void* userData, JobHandle* dependencies, int dependencyCount);

// batchSize меньше 1 считается равным 1, length 0 и меньше не планирует ни одного батча
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, JobHandle dependency);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, ReadOnlySpan<JobHandle> dependencies);
JobHandle ScheduleParallel(body, void* userData, int length, int batchSize, JobHandle* dependencies, int dependencyCount);

// длина читается из *deferredLength, когда завершаются зависимости
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize);
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize, JobHandle dependency);
JobHandle ScheduleParallelDeferred(body, void* userData, int* deferredLength, int batchSize, JobHandle* dependencies, int dependencyCount);

// барьер, который завершается, когда завершены все входы
JobHandle Combine(JobHandle a, JobHandle b);
JobHandle Combine(ReadOnlySpan<JobHandle> dependencies);
JobHandle Combine(JobHandle* dependencies, int dependencyCount);
```

#### Завершение
```csharp
bool IsCompleted(JobHandle handle);            // любой поток, без блокировки
void Complete(JobHandle handle);               // владелец выполняет батчи из очереди во время ожидания
void CompleteWithoutHelping(JobHandle handle); // владелец ждёт, не выполняя другие батчи
void CompleteAll();                            // все задачи кадра
void ResetFrame();                             // завершает работу в полёте, возвращает пул графа, начинает новый кадр
```

#### Владение и время жизни
```csharp
void EnsureOwnerThread();                // привязывает непривязанный планировщик к вызывающему потоку и проверяет его
void TransferOwnershipToCurrentThread(); // пока ни одна задача не выполняется
void Dispose();                          // завершает работу в полёте, останавливает воркеры, освобождает память
```

### JobHandle
```csharp
static JobHandle Null; // равен default(JobHandle), обозначает отсутствие задачи и считается завершённым
bool IsNull;
// IEquatable<JobHandle>, ==, !=
```

### SchedulerConfig
```csharp
int WorkerCount;           // 0: синхронный режим; положительное значение ограничивается Environment.ProcessorCount
int SpinMicroseconds;      // время, в течение которого воркер ищет работу перед сном, по умолчанию 100, при 0 засыпает сразу
long InitialCapacity;      // ёмкость очереди воркера, от 0 до 2^28, по умолчанию 4096; очередь растёт по мере надобности до 2^28 батчей
int NodeCapacity;          // задач на кадр, не больше 2^24, 0 выводит её из InitialCapacity
int EdgeCapacity;          // рёбер зависимостей на кадр, не больше 2^26, 0 выводит её из ёмкости узлов
string ThreadNamePrefix;   // по умолчанию "FFS.StaticScheduler."
PackAllocator? Allocator;  // нативная память планировщика, PackMemory.Default(), если не задан
SchedulerFaultPolicy FaultPolicy;

static SchedulerConfig Default();        // все логические процессоры
static SchedulerConfig SingleThreaded(); // синхронный режим
```

# Пример: кадр симуляции частиц
Шаг дальше быстрого старта: один кадр симуляции 100 000 частиц, собранный как небольшой граф задач и выполняемый
120 кадров подряд.

```
Integrate ──► Compact ──► MeasureBounds ──► MergeBounds ──┐
 параллельно   одна        параллельно,      одна          ├──► Combine ──► Complete
                           отложенная длина                │
UpdateWind ────────────────────────────────────────────────┘
 одна задача, рядом с цепочкой
```

- **Integrate** двигает каждую частицу — parallel-for по всему массиву.
- **Compact** собирает индексы частиц, которые ещё живы. Это одна задача, и только она знает, сколько частиц осталось.
- **MeasureBounds** считает ограничивающий прямоугольник живых частиц. Его длина — это число, неизвестное в момент
  планирования, поэтому он планируется через `ScheduleParallelDeferred`: длина читается из `AliveCount`, когда `Compact`
  выполнен.
- **MergeBounds** объединяет прямоугольники батчей в один.
- **UpdateWind** готовит ветер следующего кадра. Он ни от чего не зависит и выполняется на свободном воркере рядом с цепочкой.
- **Combine** объединяет обе ветви в один handle, а `Complete` ждёт его, пока главный поток тоже выполняет батчи.

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

    // Всё общее состояние задач. Оно лежит в нативной памяти, и каждое тело получает его через один указатель.
    struct World {
        public float* PositionX, PositionY, VelocityX, VelocityY, Life;
        public int Count;

        public int* AliveIndices; // пишет Compact
        public int AliveCount;    // пишет Compact, MeasureBounds читает как свою длину

        public Bounds* BatchBounds; // по слоту на батч MeasureBounds
        public Bounds Bounds;       // пишет MergeBounds

        public float Wind;     // читает Integrate
        public float NextWind; // пишет UpdateWind, пока выполняется Integrate
        public int Frame;
    }

    // Параллельно: каждый батч двигает свой диапазон частиц.
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

    // Одна задача: собирает частицы, которые ещё живы. Сколько их, известно только здесь.
    static void Compact(void* data, int start, int end) {
        var world = (World*) data;
        var alive = 0;
        for (var index = 0; index < world->Count; index++) {
            if (world->Life[index] > 0f) world->AliveIndices[alive++] = index;
        }
        world->AliveCount = alive;
    }

    // Параллельно по AliveCount: каждый батч пишет только свой слот, поэтому результат не зависит от того, какой воркер его выполнил.
    static void MeasureBounds(void* data, int start, int end) {
        var world = (World*) data;
        var bounds = Bounds.Empty;
        for (var position = start; position < end; position++) {
            var index = world->AliveIndices[position];
            bounds.Include(world->PositionX[index], world->PositionY[index]);
        }
        world->BatchBounds[start / BatchSize] = bounds;
    }

    // Одна задача: объединяет слоты батчей по порядку.
    static void MergeBounds(void* data, int start, int end) {
        var world = (World*) data;
        var batches = (world->AliveCount + BatchSize - 1) / BatchSize;
        var bounds = Bounds.Empty;
        for (var batch = 0; batch < batches; batch++) bounds.Include(world->BatchBounds[batch]);
        world->Bounds = bounds;
    }

    // Одна задача, независимая от цепочки: пишет NextWind, а не Wind, который в это же время читает Integrate.
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

            // Все задачи кадра завершены, поэтому главный поток снова может работать с общим состоянием.
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

Вывод — одинаковый при каждом запуске и при любом числе воркеров, включая `SchedulerConfig.SingleThreaded()`:

```
Frame   0:  99170 alive, bounds X [-0.0; 0.0] Y [-0.0; 0.2]
Frame  30:  73925 alive, bounds X [-0.9; 1.2] Y [-1.4; 3.8]
Frame  60:  48969 alive, bounds X [-1.7; 2.4] Y [-5.1; 5.0]
Frame  90:  23995 alive, bounds X [-2.6; 3.5] Y [-11.4; 3.8]
```

Что делает кадр корректным:
- **Непересекающиеся данные.** Батчи `Integrate` трогают только свои диапазоны индексов; `MeasureBounds` пишет по слоту
  на батч (`start / BatchSize`), а `MergeBounds` объединяет слоты по порядку, поэтому прямоугольник одинаков, какой бы
  воркер ни выполнил какой батч.
- **Нет гонки с параллельной ветвью.** `UpdateWind` выполняется, пока `Integrate` читает `Wind`, поэтому пишет `NextWind`;
  главный поток копирует его в `Wind` после `Complete`, когда ни одна задача кадра не выполняется.
- **Граница кадра.** `ResetFrame` в `finally` завершает кадр, даже если тело задачи бросило исключение, а `Complete`
  сообщает это исключение как внутреннее исключение `SchedulerException`.

# Потоковая модель
- Владелец — поток первого вызова. `TransferOwnershipToCurrentThread()` переносит владение, пока ни одна задача не выполняется; `EnsureOwnerThread()` привязывает непривязанный планировщик и проверяет вызывающий поток.
- `Schedule`, `ScheduleParallel`, `ScheduleParallelDeferred` и `Combine` выполняются на потоке-владельце или внутри выполняющейся задачи.
- `Complete`, `CompleteWithoutHelping`, `CompleteAll` и `ResetFrame` выполняются на потоке-владельце вне тела задачи; `IsCompleted` выполняется на любом потоке. `IsInsideJobBody` сообщает, выполняется ли вызывающий поток внутри тела задачи планировщика: рабочий поток или владелец, пока он выполняет батчи внутри ожидания.
- `Dispose` выполняется на любом потоке вне тела задачи и сначала завершает ещё выполняющуюся работу: владелец помогает её выполнять, другой поток ждёт рабочие потоки. Когда задачи в полёте, а рабочих потоков нет (`WorkerCount` 1), их выполняет только владелец, поэтому `Dispose` с другого потока бросает `SchedulerException`.
- Батчи одного `ScheduleParallel` выполняются конкурентно на непересекающихся диапазонах индексов; батч пишет только данные своего диапазона.
- Планировщик, для которого так и не вызван `Dispose`, останавливает свои рабочие потоки и освобождает память в финализаторе. Если рабочий поток по истечении таймаута ожидания в 10 секунд всё ещё внутри тела задачи, память остаётся выделенной до конца процесса.

# Ошибки
Исключение тела задачи перехватывается. `Complete(handle)` возвращает управление, когда выполнены все батчи задачи, и затем бросает `SchedulerException`, в `InnerException` которого — исключения упавших задач, которых достигает handle (`AggregateException`, если их несколько). `ResetFrame()` после сброса кадра бросает ошибки, о которых не сообщил ни один `Complete`.

Каждая из первых 63 упавших задач кадра получает свой слот сбоя, а упавшие задачи после них делят 64-й, поэтому handle, достигающий одной из них, сообщает исключения всех таких задач — как `AggregateException` с сообщением об этом. Кадр хранит 64 исключения; первый отчёт после предела сообщает число остальных. Parallel-for, у которого батчей больше, чем вмещает очередь (2^28), завершается как задача, тело которой бросило исключение, не выполнив ни одного батча.

`SchedulerConfig.FaultPolicy` определяет, что делают задачи, зависящие от упавшей:

| `SchedulerFaultPolicy` | Задачи, зависящие от упавшей |
|---|---|
| `CancelDependents` (по умолчанию) | завершаются, не выполняя свои тела, и передают ошибку дальше |
| `RunDependents` | выполняют свои тела; `Complete` всё равно сообщает об ошибке |
| `FailFast` | процесс завершается через `Environment.FailFast` |

# Проверки

| Уровень | Символы | Проверки |
|---|---|---|
| Полный | `DEBUG` или `FFS_SCHEDULER_ENABLE_DEBUG` (отключается `FFS_SCHEDULER_DISABLE_DEBUG`) | все проверки ниже, проверки аргументов, отчёт о задачах, ещё выполняющихся на момент `ResetFrame` |
| Основной | по умолчанию, отключается `FFS_SCHEDULER_DISABLE_CHECKS` | поток-владелец, вызовы из тела задачи, освобождение планировщика, диапазон и поколение handle |
| Перехват | по умолчанию, отключается `FFS_SCHEDULER_DISABLE_CATCH` | исключения тел задач перехватываются и сообщаются |

Переполнение пула, валидация конфигурации и завершение работы в полёте в `ResetFrame` / `Dispose` действуют на всех уровнях. С отключённым перехватом тело задачи выполняется без блока `try`. Исключение тела, которое выполняет рабочий поток, — необработанное исключение этого потока: в .NET оно завершает процесс, в Unity-плеере (Mono и IL2CPP) поток завершается, процесс продолжает работу, а задача остаётся незавершённой. Исключение тела, которое выполняет владелец, доходит до вызвавшего `Complete` и оставляет его задачу незавершённой. В обоих случаях планировщик после этого непригоден — `ResetFrame`, `CompleteAll` и `Dispose` ждут эту задачу бесконечно.

# Детерминизм
Work stealing меняет, какой воркер выполняет какой батч и когда, и сохраняет результат работы, не зависящей от распределения: батч, чей результат — чистая функция от его собственного входного диапазона, даёт один и тот же результат при любом числе воркеров. Межбатчевые редукции и побочные эффекты, зависящие от порядка, делает детерминированными вызывающий код.

# Лицензия
[MIT license](./LICENSE.md)
