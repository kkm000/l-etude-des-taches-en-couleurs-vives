using System;
using System.Collections.Generic;
//using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Linq;
using System.Reactive.Linq;
//using System.Reactive.Threading;
using System.Reactive.Concurrency;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using static System.Console;

/*
TODO(kkm) write.

We perform fewer tests, as there is no "synchronous" executions within the
Parallel.ForEach context. It seems that yielding at the very start is important
for performance.
*/

public static class Program {
  const int MAXDOP = 400;

  // ## BUFFER MANAGEMENT ##
  // Keep in mind that LOH-sized objects start at 83K bytes. This is a
  // sensible buffer size, and the decision to put the buffers into the LOH
  // or not may be the matter of nudging the size above or beyond this point.
  // Also, Microsoft notes that the 85000 byte threshold is in effect for .NET
  // Frameworks, .NET Core and (starting with .NET 5.0, just .NET) _only_ on
  // Windows. Other platforms may have a different sweet spot.
  const uint BUFSIZE = 256 * 1024;

  static IEnumerable<string> Filenames =>
    Directory.EnumerateFiles(Environment.SystemDirectory, "*.dll");

  static void Main(string[] args) {
    PrintRunStats();

  // It's ok to set the LowLatency mode while streaming, unless it's a hour-long
  // job. This will increase memory footprint for the time. Resetting it to
  // Normal when the large streaming job completes and the next is, e.g., being
  // prepared will recover the uncollected memory.
#if RISKY_LOWLATENCY_GC_DURING_STREAMING
    System.Runtime.GCSettings.LatencyMode =
      System.Runtime.GCLatencyMode.LowLatency;
    WriteLine("WARNING: Using low-latency GC while streaming." +
              " RAM reclaiming may severely lag behind allocation."
#endif

    C01_Count("warm up run 0");
    C01_Count("run 1");
    WriteLine();

    for (int i = 0; i < 50; i++) {
      C04_AsyncReadWithYield($"run {i + 1}");
    }
    WriteLine();
    C04_AsyncReadWithYield("run 1", 1);
    C04_AsyncReadWithYield("run 2", 1);
    WriteLine();

    for (int i = 0; i < 50; i++) {
      C03_AsyncReadWithoutYield($"run {i + 1}");
    }
    WriteLine();
    C03_AsyncReadWithoutYield("run 1", 1);
    C03_AsyncReadWithoutYield("run 2", 1);
    WriteLine();
  }

  static long g_curdop, g_reached_dop, g_resurrects;

  static void Clock<T>(string what, int maxdop, Func<string, Task<T>> action) {
    WriteLine("Start {0}: max_dop={1}", what, maxdop);
    g_curdop = g_reached_dop = g_resurrects = 0;
    var clock = Stopwatch.StartNew();
    Filenames
      .ToObservable()
      .SubscribeOn(TaskPoolScheduler.Default)
      .Select(file => Observable.FromAsync(() => action(file))
        /**/                    .SubscribeOn(TaskPoolScheduler.Default))
      .Merge(maxdop)
      .Wait();
    Write("Completed {0}: t_ms={1} resurrects={2}",
          what, clock.ElapsedMilliseconds, g_resurrects);
    if (g_reached_dop > 0) {
      Write($" reached_dop={g_reached_dop}");
    }
    if (g_curdop != 0) {
      Write($" INTERR: g_curdop={g_curdop}≠0");
    }
    PrintThreadStats();
  }

  static long g_nfiles, g_nbytes, g_nerrors;

  static void C01_Count(string pass, int maxdop = MAXDOP) {
    g_nfiles = 0;
#pragma warning disable 1998  // Using a very short not-awaiting task here.
    Clock($"counting files ({pass})", maxdop,
      async _ => Interlocked.Increment(ref g_nfiles));
#pragma warning restore 1998
    WriteLine($"File count: {g_nfiles}");
  }

  static void C03_AsyncReadWithoutYield(string pass, int maxdop = MAXDOP) {
    g_nfiles = g_nbytes = g_nerrors = 0;
    Clock($"read files without Yield ({pass})", maxdop, file =>
      WorkerForAsyncRead(file, false));
    WriteLine($"files={g_nfiles} bytes={g_nbytes} errors={g_nerrors}");
  }

  static void C04_AsyncReadWithYield(string pass, int maxdop = MAXDOP) {
    g_nfiles = g_nbytes = g_nerrors = 0;
    Clock($"read files with Yield ({pass})", maxdop, file =>
      WorkerForAsyncRead(file, true));
    WriteLine($"files={g_nfiles} bytes={g_nbytes} errors={g_nerrors}");
  }


  [ThreadStatic]
  static WeakReference tls_weakbuf;  // Opportunistically cache allocated buffers.

  static async Task<Unit> WorkerForAsyncRead(string file, bool yieldFirst) {
    Interlocked.Increment(ref g_nfiles);
    if (yieldFirst) {
      // The behavior changes noticeably when the task meas
      await Task.Yield();
    }
    // Do not move this line above the Yield, or your count will be nonsensical
    // and mislead you into thinking your DOP is high.
    //
    // The part of code preceding the first 'await' is synchronous with the
    // caller, so you'll get the MAXDOP of the pipeline: Observable.Merge will
    // observe your task quite eagerly. The task serving the existing connection
    // better yield often. Alternatively, a connection request here will make
    // perfect sense, *as soon as it yields quickly*. If your client tends to
    // linger in the connect request, spawning a task into the pool using the
    // with the scheduler, or Factory.Run with options may be a solution better
    // that the Yield, since it results in queuing the new connections at the
    // same fair-share as servicing the existing one, which is not good for
    // reaching the high connection count, which the server from our problem
    // statements prefers.
    UpdateMaxReachedDop(Interlocked.Increment(ref g_curdop));

#if true  // RESURRECT
    // The resurrection results in a _tremendous_ decrease in the number of
    // background Gen2 GCs. It's worth noting that this sample is able to
    // reach the DOP of about 2x the number of hyperthreads. This is so because
    // no other processing is going on in this worker. The larger is the buffer,
    // the lower is the concurrency, but this is certainly _not_ the way to
    // improve DOP! The DOP increases because of more wasteful use of resources.
    // and the decreased throughput.
    //
    // For example, disabling the resurrection of buffer allocation bumps the
    // DOP to 400 in no time. You need not other tool but Windows Task Manager
    // to see that the working set figures whizzing past faster than the slots
    // in Las Vegas. You do not need a profiler to see how much GC is going on.
    //
    // Also, buffer reuse surprisingly improves memory locality. On my laptop
    // with 4 physical Gen8 cores, CPU usage approaches 100% and hovers well
    // above 50%. Such a high gain from hyperthreading is a rare sighting in
    // nature. dotnet task scheduler is quite sophisticated. It's the Task
    // implicit invasiveness than makes their use in a highly-parallel scenarios
    // that involve interaction with existing, non-async-await-based codebase
    // a nightmare.
    //
    // Fortunately, it is very easy to control the number of streams and the
    // fairness of their service with the Rx.NET. It is possible that the tasks
    // alone may not cut it. But this is the subject of our next episode.
    //
    // You should _most certainly_ use resurrection. Don't allow the task thread
    // pool grow beyond sensible limits, however. The second WeakReference's
    // ctor parameter is unimportant, as arrays do not have finalizers.
    byte[] buf = tls_weakbuf?.Target as byte[];
    if (buf != null) {
      Interlocked.Increment(ref g_resurrects);
    }
    else {
      buf = new byte[BUFSIZE];
      if (tls_weakbuf == null) {
        tls_weakbuf = new WeakReference(buf);
      }
      else {
        tls_weakbuf.Target = buf;
      }
    }
#else
    byte[] buf = new byte[kBufSize];
#endif

    // This is the main loop that reads the file and, in a production setting,
    // streams it using the client library task-based API. Everything is the
    // bona fide async-await here, no tricks.
    long total_bytes = 0;
    try {
      using (var stm = File.OpenRead(file)) {
        for (; ; ) {
          int cb = await stm.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
          if (cb == 0) break;
          total_bytes += cb;
        }
      }
      Interlocked.Add(ref g_nbytes, total_bytes);
    }
    catch {
      Interlocked.Increment(ref g_nerrors);
    }

    UpdateMaxReachedDop(Interlocked.Decrement(ref g_curdop));
    return Unit.Default;  // Sorely missing .NET valueish inferrable void type.
  }

  static void UpdateMaxReachedDop(long n) {
#if true
    long o = Interlocked.Read(ref g_reached_dop);
    // The classic pattern has it like 'n != o', but if the o had been updated
    // to become larger than the new max value we want to store, we bail out of
    // the loop. Otherwise, we'll update the new max count with the already
    // stale n.
    while (n > o) {
      o = Interlocked.CompareExchange(ref g_reached_dop, n, o);
    }
#endif
  }

#region Helpers, mostly runtime-dependent.

  static void PrintThreadStats() {
    Write($" cpu_threads={Process.GetCurrentProcess().Threads.Count}");
#if NETCOREAPP3_1_OR_GREATER
    Write($" pool_threads={ThreadPool.ThreadCount}");
#endif
    WriteLine();
  }

  static void PrintRunStats() {
    var build =
#if DEBUG
      "Debug";
#else
      "Release";
#endif

    var osname =
#if NETFRAMEWORK
      "Windows";
#else
      System.Runtime.InteropServices.RuntimeInformation.OSDescription;
#endif

    // Credit:
    // https://weblog.west-wind.com/posts/2018/Apr/12/Getting-the-NET-Core-Runtime-Version-in-a-Running-Application
    // This code retrieved the framework ID on any framework in existence.
    string framework = Assembly.GetEntryAssembly()?
      .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?
      .FrameworkName ?? "unknown";

    string bitness = Environment.Is64BitProcess ? "64-bit" : "32-bit";

    WriteLine(
      "Running on {0} at {1} os='{2} ({3})' fwk={4} ncpu={5} build={6} buf_kb={7}",
      DateTime.Now.ToString("yyMMdd'-'HHmm"), Environment.MachineName, osname,
      bitness, framework, Environment.ProcessorCount, build, BUFSIZE/1024);

    // These are parameters of the ThreadPool, which may or may not be the same
    // as the task scheduler's thread pool. Assume they are.
    ThreadPool.GetMinThreads(out int min_wkt, out int min_cqp);
    ThreadPool.GetMaxThreads(out int max_wkt, out int max_cqp);
    WriteLine("Thread pool: min=(wk={0} cq={1}) max=(wk={2} cq={3})\n",
              min_wkt, min_cqp, max_wkt, max_cqp);
  }

#endregion
}

#region === RESULTS ===

// TODO(kkm): Summary

#region .NET 5.0
#region 32K buffers
/*

Running on 210415-0220 at QUUX os='Microsoft Windows 10.0.19042 (64-bit)' fwk=.NETCoreApp,Version=v5.0 ncpu=8 build=Release buf_kb=32
Thread pool: min=(wk=8 cq=8) max=(wk=32767 cq=1000)

Start counting files (warm up run 0): max_dop=400
Completed counting files (warm up run 0): t_ms=161 resurrects=0 cpu_threads=26 pool_threads=9
File count: 3605
Start counting files (run 1): max_dop=400
Completed counting files (run 1): t_ms=25 resurrects=0 cpu_threads=26 pool_threads=9
File count: 3605

Start read files with Yield (run 1): max_dop=400
Completed read files with Yield (run 1): t_ms=1219 resurrects=3586 reached_dop=19 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 2): max_dop=400
Completed read files with Yield (run 2): t_ms=203 resurrects=3589 reached_dop=18 cpu_threads=36 pool_threads=18
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 3): max_dop=400
Completed read files with Yield (run 3): t_ms=191 resurrects=3604 reached_dop=19 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 4): max_dop=400
Completed read files with Yield (run 4): t_ms=242 resurrects=3593 reached_dop=19 cpu_threads=36 pool_threads=18
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 5): max_dop=400
Completed read files with Yield (run 5): t_ms=231 resurrects=3605 reached_dop=18 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 6): max_dop=400
Completed read files with Yield (run 6): t_ms=187 resurrects=3597 reached_dop=18 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 7): max_dop=400
Completed read files with Yield (run 7): t_ms=182 resurrects=3605 reached_dop=18 cpu_threads=36 pool_threads=18
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 8): max_dop=400
Completed read files with Yield (run 8): t_ms=187 resurrects=3601 reached_dop=17 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 9): max_dop=400
Completed read files with Yield (run 9): t_ms=190 resurrects=3604 reached_dop=17 cpu_threads=36 pool_threads=17
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 10): max_dop=400
Completed read files with Yield (run 10): t_ms=197 resurrects=3604 reached_dop=17 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 11): max_dop=400
Completed read files with Yield (run 11): t_ms=202 resurrects=3605 reached_dop=17 cpu_threads=36 pool_threads=17
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 12): max_dop=400
Completed read files with Yield (run 12): t_ms=196 resurrects=3605 reached_dop=17 cpu_threads=36 pool_threads=17
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 13): max_dop=400
Completed read files with Yield (run 13): t_ms=212 resurrects=3600 reached_dop=14 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 14): max_dop=400
Completed read files with Yield (run 14): t_ms=191 resurrects=3605 reached_dop=17 cpu_threads=36 pool_threads=17
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 15): max_dop=400
Completed read files with Yield (run 15): t_ms=187 resurrects=3601 reached_dop=17 cpu_threads=36 pool_threads=17
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 16): max_dop=400
Completed read files with Yield (run 16): t_ms=188 resurrects=3605 reached_dop=16 cpu_threads=36 pool_threads=16
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 17): max_dop=400
Completed read files with Yield (run 17): t_ms=192 resurrects=3605 reached_dop=16 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 18): max_dop=400
Completed read files with Yield (run 18): t_ms=219 resurrects=3605 reached_dop=10 cpu_threads=36 pool_threads=16
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 19): max_dop=400
Completed read files with Yield (run 19): t_ms=204 resurrects=3604 reached_dop=16 cpu_threads=36 pool_threads=16
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 20): max_dop=400
Completed read files with Yield (run 20): t_ms=189 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 21): max_dop=400
Completed read files with Yield (run 21): t_ms=189 resurrects=3605 reached_dop=16 cpu_threads=36 pool_threads=16
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 22): max_dop=400
Completed read files with Yield (run 22): t_ms=198 resurrects=3604 reached_dop=16 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 23): max_dop=400
Completed read files with Yield (run 23): t_ms=188 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=16
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 24): max_dop=400
Completed read files with Yield (run 24): t_ms=192 resurrects=3604 reached_dop=16 cpu_threads=36 pool_threads=15
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 25): max_dop=400
Completed read files with Yield (run 25): t_ms=202 resurrects=3605 reached_dop=15 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 26): max_dop=400
Completed read files with Yield (run 26): t_ms=206 resurrects=3604 reached_dop=15 cpu_threads=36 pool_threads=15
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 27): max_dop=400
Completed read files with Yield (run 27): t_ms=201 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=15
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 28): max_dop=400
Completed read files with Yield (run 28): t_ms=195 resurrects=3605 reached_dop=15 cpu_threads=36 pool_threads=15
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 29): max_dop=400
Completed read files with Yield (run 29): t_ms=191 resurrects=3605 reached_dop=15 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 30): max_dop=400
Completed read files with Yield (run 30): t_ms=186 resurrects=3605 reached_dop=15 cpu_threads=36 pool_threads=15
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 31): max_dop=400
Completed read files with Yield (run 31): t_ms=209 resurrects=3605 reached_dop=15 cpu_threads=36 pool_threads=15
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 32): max_dop=400
Completed read files with Yield (run 32): t_ms=212 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 33): max_dop=400
Completed read files with Yield (run 33): t_ms=194 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 34): max_dop=400
Completed read files with Yield (run 34): t_ms=195 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 35): max_dop=400
Completed read files with Yield (run 35): t_ms=189 resurrects=3604 reached_dop=15 cpu_threads=36 pool_threads=15
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 36): max_dop=400
Completed read files with Yield (run 36): t_ms=183 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=14
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 37): max_dop=400
Completed read files with Yield (run 37): t_ms=191 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 38): max_dop=400
Completed read files with Yield (run 38): t_ms=191 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 39): max_dop=400
Completed read files with Yield (run 39): t_ms=202 resurrects=3604 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 40): max_dop=400
Completed read files with Yield (run 40): t_ms=195 resurrects=3599 reached_dop=14 cpu_threads=36 pool_threads=14
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 41): max_dop=400
Completed read files with Yield (run 41): t_ms=193 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 42): max_dop=400
Completed read files with Yield (run 42): t_ms=194 resurrects=3601 reached_dop=14 cpu_threads=36 pool_threads=14
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 43): max_dop=400
Completed read files with Yield (run 43): t_ms=185 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=14
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 44): max_dop=400
Completed read files with Yield (run 44): t_ms=199 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 45): max_dop=400
Completed read files with Yield (run 45): t_ms=230 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 46): max_dop=400
Completed read files with Yield (run 46): t_ms=192 resurrects=3603 reached_dop=14 cpu_threads=36 pool_threads=14
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 47): max_dop=400
Completed read files with Yield (run 47): t_ms=183 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=14
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 48): max_dop=400
Completed read files with Yield (run 48): t_ms=185 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 49): max_dop=400
Completed read files with Yield (run 49): t_ms=192 resurrects=3603 reached_dop=14 cpu_threads=36 pool_threads=14
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 50): max_dop=400
Completed read files with Yield (run 50): t_ms=187 resurrects=3605 reached_dop=14 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0

Start read files with Yield (run 1): max_dop=1
Completed read files with Yield (run 1): t_ms=956 resurrects=3603 reached_dop=1 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 2): max_dop=1
Completed read files with Yield (run 2): t_ms=945 resurrects=3604 reached_dop=1 cpu_threads=36 pool_threads=10
files=3605 bytes=1864636800 errors=0

Start read files without Yield (run 1): max_dop=400
Completed read files without Yield (run 1): t_ms=213 resurrects=3605 reached_dop=13 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 2): max_dop=400
Completed read files without Yield (run 2): t_ms=191 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 3): max_dop=400
Completed read files without Yield (run 3): t_ms=192 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 4): max_dop=400
Completed read files without Yield (run 4): t_ms=194 resurrects=3603 reached_dop=13 cpu_threads=36 pool_threads=13
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 5): max_dop=400
Completed read files without Yield (run 5): t_ms=190 resurrects=3605 reached_dop=13 cpu_threads=36 pool_threads=13
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 6): max_dop=400
Completed read files without Yield (run 6): t_ms=210 resurrects=3603 reached_dop=13 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 7): max_dop=400
Completed read files without Yield (run 7): t_ms=210 resurrects=3605 reached_dop=13 cpu_threads=36 pool_threads=13
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 8): max_dop=400
Completed read files without Yield (run 8): t_ms=201 resurrects=3605 reached_dop=13 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 9): max_dop=400
Completed read files without Yield (run 9): t_ms=188 resurrects=3605 reached_dop=13 cpu_threads=36 pool_threads=13
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 10): max_dop=400
Completed read files without Yield (run 10): t_ms=190 resurrects=3605 reached_dop=13 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 11): max_dop=400
Completed read files without Yield (run 11): t_ms=190 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 12): max_dop=400
Completed read files without Yield (run 12): t_ms=217 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 13): max_dop=400
Completed read files without Yield (run 13): t_ms=225 resurrects=3603 reached_dop=13 cpu_threads=36 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 14): max_dop=400
Completed read files without Yield (run 14): t_ms=192 resurrects=3605 reached_dop=8 cpu_threads=36 pool_threads=12
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 15): max_dop=400
Completed read files without Yield (run 15): t_ms=219 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 16): max_dop=400
Completed read files without Yield (run 16): t_ms=235 resurrects=3605 reached_dop=8 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 17): max_dop=400
Completed read files without Yield (run 17): t_ms=244 resurrects=3605 reached_dop=8 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 18): max_dop=400
Completed read files without Yield (run 18): t_ms=254 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=12
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 19): max_dop=400
Completed read files without Yield (run 19): t_ms=257 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 20): max_dop=400
Completed read files without Yield (run 20): t_ms=268 resurrects=3605 reached_dop=8 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 21): max_dop=400
Completed read files without Yield (run 21): t_ms=262 resurrects=3605 reached_dop=8 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 22): max_dop=400
Completed read files without Yield (run 22): t_ms=239 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=12
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 23): max_dop=400
Completed read files without Yield (run 23): t_ms=238 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 24): max_dop=400
Completed read files without Yield (run 24): t_ms=259 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 25): max_dop=400
Completed read files without Yield (run 25): t_ms=279 resurrects=3605 reached_dop=8 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 26): max_dop=400
Completed read files without Yield (run 26): t_ms=239 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 27): max_dop=400
Completed read files without Yield (run 27): t_ms=235 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 28): max_dop=400
Completed read files without Yield (run 28): t_ms=235 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=12
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 29): max_dop=400
Completed read files without Yield (run 29): t_ms=286 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 30): max_dop=400
Completed read files without Yield (run 30): t_ms=254 resurrects=3605 reached_dop=8 cpu_threads=44 pool_threads=12
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 31): max_dop=400
Completed read files without Yield (run 31): t_ms=242 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 32): max_dop=400
Completed read files without Yield (run 32): t_ms=246 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=12
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 33): max_dop=400
Completed read files without Yield (run 33): t_ms=231 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=12
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 34): max_dop=400
Completed read files without Yield (run 34): t_ms=279 resurrects=3605 reached_dop=12 cpu_threads=44 pool_threads=11
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 35): max_dop=400
Completed read files without Yield (run 35): t_ms=268 resurrects=3605 reached_dop=11 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 36): max_dop=400
Completed read files without Yield (run 36): t_ms=236 resurrects=3605 reached_dop=11 cpu_threads=44 pool_threads=11
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 37): max_dop=400
Completed read files without Yield (run 37): t_ms=247 resurrects=3605 reached_dop=11 cpu_threads=44 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 38): max_dop=400
Completed read files without Yield (run 38): t_ms=233 resurrects=3605 reached_dop=11 cpu_threads=43 pool_threads=11
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 39): max_dop=400
Completed read files without Yield (run 39): t_ms=242 resurrects=3605 reached_dop=11 cpu_threads=43 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 40): max_dop=400
Completed read files without Yield (run 40): t_ms=283 resurrects=3605 reached_dop=11 cpu_threads=43 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 41): max_dop=400
Completed read files without Yield (run 41): t_ms=242 resurrects=3605 reached_dop=8 cpu_threads=42 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 42): max_dop=400
Completed read files without Yield (run 42): t_ms=237 resurrects=3605 reached_dop=11 cpu_threads=42 pool_threads=11
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 43): max_dop=400
Completed read files without Yield (run 43): t_ms=229 resurrects=3605 reached_dop=11 cpu_threads=42 pool_threads=11
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 44): max_dop=400
Completed read files without Yield (run 44): t_ms=235 resurrects=3605 reached_dop=11 cpu_threads=42 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 45): max_dop=400
Completed read files without Yield (run 45): t_ms=287 resurrects=3605 reached_dop=8 cpu_threads=42 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 46): max_dop=400
Completed read files without Yield (run 46): t_ms=253 resurrects=3605 reached_dop=11 cpu_threads=42 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 47): max_dop=400
Completed read files without Yield (run 47): t_ms=241 resurrects=3605 reached_dop=11 cpu_threads=41 pool_threads=11
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 48): max_dop=400
Completed read files without Yield (run 48): t_ms=235 resurrects=3605 reached_dop=11 cpu_threads=41 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 49): max_dop=400
Completed read files without Yield (run 49): t_ms=235 resurrects=3605 reached_dop=11 cpu_threads=41 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 50): max_dop=400
Completed read files without Yield (run 50): t_ms=257 resurrects=3605 reached_dop=11 cpu_threads=41 pool_threads=11
files=3605 bytes=1864636800 errors=0

Start read files without Yield (run 1): max_dop=1
Completed read files without Yield (run 1): t_ms=1191 resurrects=3605 reached_dop=1 cpu_threads=40 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 2): max_dop=1
Completed read files without Yield (run 2): t_ms=1159 resurrects=3605 reached_dop=1 cpu_threads=40 pool_threads=8
files=3605 bytes=1864636800 errors=0

*/
#endregion
#region 256K buffers
/*

Running on 210415-0224 at QUUX os='Microsoft Windows 10.0.19042 (64-bit)' fwk=.NETCoreApp,Version=v5.0 ncpu=8 build=Release buf_kb=256
Thread pool: min=(wk=8 cq=8) max=(wk=32767 cq=1000)

Start counting files (warm up run 0): max_dop=400
Completed counting files (warm up run 0): t_ms=89 resurrects=0 cpu_threads=25 pool_threads=8
File count: 3605
Start counting files (run 1): max_dop=400
Completed counting files (run 1): t_ms=24 resurrects=0 cpu_threads=25 pool_threads=8
File count: 3605

Start read files with Yield (run 1): max_dop=400
Completed read files with Yield (run 1): t_ms=190 resurrects=3596 reached_dop=9 cpu_threads=26 pool_threads=9
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 2): max_dop=400
Completed read files with Yield (run 2): t_ms=151 resurrects=3605 reached_dop=9 cpu_threads=26 pool_threads=9
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 3): max_dop=400
Completed read files with Yield (run 3): t_ms=148 resurrects=3605 reached_dop=9 cpu_threads=26 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 4): max_dop=400
Completed read files with Yield (run 4): t_ms=171 resurrects=3605 reached_dop=8 cpu_threads=26 pool_threads=9
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 5): max_dop=400
Completed read files with Yield (run 5): t_ms=177 resurrects=3605 reached_dop=9 cpu_threads=26 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 6): max_dop=400
Completed read files with Yield (run 6): t_ms=161 resurrects=3605 reached_dop=8 cpu_threads=26 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 7): max_dop=400
Completed read files with Yield (run 7): t_ms=149 resurrects=3604 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 8): max_dop=400
Completed read files with Yield (run 8): t_ms=134 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 9): max_dop=400
Completed read files with Yield (run 9): t_ms=136 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 10): max_dop=400
Completed read files with Yield (run 10): t_ms=138 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 11): max_dop=400
Completed read files with Yield (run 11): t_ms=150 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 12): max_dop=400
Completed read files with Yield (run 12): t_ms=142 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 13): max_dop=400
Completed read files with Yield (run 13): t_ms=151 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 14): max_dop=400
Completed read files with Yield (run 14): t_ms=179 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 15): max_dop=400
Completed read files with Yield (run 15): t_ms=143 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 16): max_dop=400
Completed read files with Yield (run 16): t_ms=141 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 17): max_dop=400
Completed read files with Yield (run 17): t_ms=146 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 18): max_dop=400
Completed read files with Yield (run 18): t_ms=142 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 19): max_dop=400
Completed read files with Yield (run 19): t_ms=138 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 20): max_dop=400
Completed read files with Yield (run 20): t_ms=140 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 21): max_dop=400
Completed read files with Yield (run 21): t_ms=147 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 22): max_dop=400
Completed read files with Yield (run 22): t_ms=188 resurrects=3605 reached_dop=9 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 23): max_dop=400
Completed read files with Yield (run 23): t_ms=145 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 24): max_dop=400
Completed read files with Yield (run 24): t_ms=145 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 25): max_dop=400
Completed read files with Yield (run 25): t_ms=143 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 26): max_dop=400
Completed read files with Yield (run 26): t_ms=143 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 27): max_dop=400
Completed read files with Yield (run 27): t_ms=142 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 28): max_dop=400
Completed read files with Yield (run 28): t_ms=144 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 29): max_dop=400
Completed read files with Yield (run 29): t_ms=139 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 30): max_dop=400
Completed read files with Yield (run 30): t_ms=143 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 31): max_dop=400
Completed read files with Yield (run 31): t_ms=183 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 32): max_dop=400
Completed read files with Yield (run 32): t_ms=155 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 33): max_dop=400
Completed read files with Yield (run 33): t_ms=147 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 34): max_dop=400
Completed read files with Yield (run 34): t_ms=143 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 35): max_dop=400
Completed read files with Yield (run 35): t_ms=139 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 36): max_dop=400
Completed read files with Yield (run 36): t_ms=139 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 37): max_dop=400
Completed read files with Yield (run 37): t_ms=142 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 38): max_dop=400
Completed read files with Yield (run 38): t_ms=143 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 39): max_dop=400
Completed read files with Yield (run 39): t_ms=141 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 40): max_dop=400
Completed read files with Yield (run 40): t_ms=150 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 41): max_dop=400
Completed read files with Yield (run 41): t_ms=161 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 42): max_dop=400
Completed read files with Yield (run 42): t_ms=157 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 43): max_dop=400
Completed read files with Yield (run 43): t_ms=141 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 44): max_dop=400
Completed read files with Yield (run 44): t_ms=144 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 45): max_dop=400
Completed read files with Yield (run 45): t_ms=142 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 46): max_dop=400
Completed read files with Yield (run 46): t_ms=146 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 47): max_dop=400
Completed read files with Yield (run 47): t_ms=145 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 48): max_dop=400
Completed read files with Yield (run 48): t_ms=165 resurrects=3605 reached_dop=9 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 49): max_dop=400
Completed read files with Yield (run 49): t_ms=164 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 50): max_dop=400
Completed read files with Yield (run 50): t_ms=142 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0

Start read files with Yield (run 1): max_dop=1
Completed read files with Yield (run 1): t_ms=601 resurrects=3605 reached_dop=1 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 2): max_dop=1
Completed read files with Yield (run 2): t_ms=655 resurrects=3605 reached_dop=1 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0

Start read files without Yield (run 1): max_dop=400
Completed read files without Yield (run 1): t_ms=165 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 2): max_dop=400
Completed read files without Yield (run 2): t_ms=153 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 3): max_dop=400
Completed read files without Yield (run 3): t_ms=149 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 4): max_dop=400
Completed read files without Yield (run 4): t_ms=152 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 5): max_dop=400
Completed read files without Yield (run 5): t_ms=151 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 6): max_dop=400
Completed read files without Yield (run 6): t_ms=150 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 7): max_dop=400
Completed read files without Yield (run 7): t_ms=154 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 8): max_dop=400
Completed read files without Yield (run 8): t_ms=189 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 9): max_dop=400
Completed read files without Yield (run 9): t_ms=158 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 10): max_dop=400
Completed read files without Yield (run 10): t_ms=150 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 11): max_dop=400
Completed read files without Yield (run 11): t_ms=155 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 12): max_dop=400
Completed read files without Yield (run 12): t_ms=150 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 13): max_dop=400
Completed read files without Yield (run 13): t_ms=150 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 14): max_dop=400
Completed read files without Yield (run 14): t_ms=157 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 15): max_dop=400
Completed read files without Yield (run 15): t_ms=149 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 16): max_dop=400
Completed read files without Yield (run 16): t_ms=175 resurrects=3605 reached_dop=8 cpu_threads=27 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 17): max_dop=400
Completed read files without Yield (run 17): t_ms=161 resurrects=3605 reached_dop=10 cpu_threads=27 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 18): max_dop=400
Completed read files without Yield (run 18): t_ms=166 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 19): max_dop=400
Completed read files without Yield (run 19): t_ms=152 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 20): max_dop=400
Completed read files without Yield (run 20): t_ms=159 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 21): max_dop=400
Completed read files without Yield (run 21): t_ms=153 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 22): max_dop=400
Completed read files without Yield (run 22): t_ms=149 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 23): max_dop=400
Completed read files without Yield (run 23): t_ms=148 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 24): max_dop=400
Completed read files without Yield (run 24): t_ms=149 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 25): max_dop=400
Completed read files without Yield (run 25): t_ms=171 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 26): max_dop=400
Completed read files without Yield (run 26): t_ms=184 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 27): max_dop=400
Completed read files without Yield (run 27): t_ms=151 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 28): max_dop=400
Completed read files without Yield (run 28): t_ms=152 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 29): max_dop=400
Completed read files without Yield (run 29): t_ms=150 resurrects=3605 reached_dop=9 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 30): max_dop=400
Completed read files without Yield (run 30): t_ms=149 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 31): max_dop=400
Completed read files without Yield (run 31): t_ms=155 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 32): max_dop=400
Completed read files without Yield (run 32): t_ms=154 resurrects=3605 reached_dop=9 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 33): max_dop=400
Completed read files without Yield (run 33): t_ms=184 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 34): max_dop=400
Completed read files without Yield (run 34): t_ms=164 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 35): max_dop=400
Completed read files without Yield (run 35): t_ms=149 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 36): max_dop=400
Completed read files without Yield (run 36): t_ms=153 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 37): max_dop=400
Completed read files without Yield (run 37): t_ms=150 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 38): max_dop=400
Completed read files without Yield (run 38): t_ms=148 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 39): max_dop=400
Completed read files without Yield (run 39): t_ms=171 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 40): max_dop=400
Completed read files without Yield (run 40): t_ms=182 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 41): max_dop=400
Completed read files without Yield (run 41): t_ms=209 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 42): max_dop=400
Completed read files without Yield (run 42): t_ms=230 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 43): max_dop=400
Completed read files without Yield (run 43): t_ms=207 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 44): max_dop=400
Completed read files without Yield (run 44): t_ms=194 resurrects=3605 reached_dop=9 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 45): max_dop=400
Completed read files without Yield (run 45): t_ms=194 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 46): max_dop=400
Completed read files without Yield (run 46): t_ms=194 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 47): max_dop=400
Completed read files without Yield (run 47): t_ms=190 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 48): max_dop=400
Completed read files without Yield (run 48): t_ms=254 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=10
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 49): max_dop=400
Completed read files without Yield (run 49): t_ms=198 resurrects=3605 reached_dop=10 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 50): max_dop=400
Completed read files without Yield (run 50): t_ms=194 resurrects=3605 reached_dop=8 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0

Start read files without Yield (run 1): max_dop=1
Completed read files without Yield (run 1): t_ms=856 resurrects=3605 reached_dop=1 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 2): max_dop=1
Completed read files without Yield (run 2): t_ms=839 resurrects=3605 reached_dop=1 cpu_threads=35 pool_threads=8
files=3605 bytes=1864636800 errors=0

*/
#endregion
#endregion

#region .NET Framework 4.6.1
#region 32K buffers
/*

Running on 210415-0221 at QUUX os='Windows (64-bit)' fwk=.NETFramework,Version=v4.6.1 ncpu=8 build=Release buf_kb=32
Thread pool: min=(wk=8 cq=8) max=(wk=32767 cq=1000)

Start counting files (warm up run 0): max_dop=400
Completed counting files (warm up run 0): t_ms=106 resurrects=0 cpu_threads=28
File count: 3605
Start counting files (run 1): max_dop=400
Completed counting files (run 1): t_ms=22 resurrects=0 cpu_threads=28
File count: 3605

Start read files with Yield (run 1): max_dop=400
Completed read files with Yield (run 1): t_ms=279 resurrects=3596 reached_dop=9 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 2): max_dop=400
Completed read files with Yield (run 2): t_ms=243 resurrects=3605 reached_dop=9 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 3): max_dop=400
Completed read files with Yield (run 3): t_ms=238 resurrects=3605 reached_dop=9 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 4): max_dop=400
Completed read files with Yield (run 4): t_ms=260 resurrects=3605 reached_dop=9 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 5): max_dop=400
Completed read files with Yield (run 5): t_ms=249 resurrects=3603 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 6): max_dop=400
Completed read files with Yield (run 6): t_ms=231 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 7): max_dop=400
Completed read files with Yield (run 7): t_ms=223 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 8): max_dop=400
Completed read files with Yield (run 8): t_ms=228 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 9): max_dop=400
Completed read files with Yield (run 9): t_ms=259 resurrects=3602 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 10): max_dop=400
Completed read files with Yield (run 10): t_ms=239 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 11): max_dop=400
Completed read files with Yield (run 11): t_ms=244 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 12): max_dop=400
Completed read files with Yield (run 12): t_ms=240 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 13): max_dop=400
Completed read files with Yield (run 13): t_ms=236 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 14): max_dop=400
Completed read files with Yield (run 14): t_ms=251 resurrects=3601 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 15): max_dop=400
Completed read files with Yield (run 15): t_ms=241 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 16): max_dop=400
Completed read files with Yield (run 16): t_ms=247 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 17): max_dop=400
Completed read files with Yield (run 17): t_ms=234 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 18): max_dop=400
Completed read files with Yield (run 18): t_ms=228 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 19): max_dop=400
Completed read files with Yield (run 19): t_ms=259 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 20): max_dop=400
Completed read files with Yield (run 20): t_ms=256 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 21): max_dop=400
Completed read files with Yield (run 21): t_ms=239 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 22): max_dop=400
Completed read files with Yield (run 22): t_ms=244 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 23): max_dop=400
Completed read files with Yield (run 23): t_ms=236 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 24): max_dop=400
Completed read files with Yield (run 24): t_ms=248 resurrects=3603 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 25): max_dop=400
Completed read files with Yield (run 25): t_ms=255 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 26): max_dop=400
Completed read files with Yield (run 26): t_ms=238 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 27): max_dop=400
Completed read files with Yield (run 27): t_ms=240 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 28): max_dop=400
Completed read files with Yield (run 28): t_ms=238 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 29): max_dop=400
Completed read files with Yield (run 29): t_ms=238 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 30): max_dop=400
Completed read files with Yield (run 30): t_ms=234 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 31): max_dop=400
Completed read files with Yield (run 31): t_ms=246 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 32): max_dop=400
Completed read files with Yield (run 32): t_ms=248 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 33): max_dop=400
Completed read files with Yield (run 33): t_ms=241 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 34): max_dop=400
Completed read files with Yield (run 34): t_ms=239 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 35): max_dop=400
Completed read files with Yield (run 35): t_ms=233 resurrects=3604 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 36): max_dop=400
Completed read files with Yield (run 36): t_ms=238 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 37): max_dop=400
Completed read files with Yield (run 37): t_ms=274 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 38): max_dop=400
Completed read files with Yield (run 38): t_ms=243 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 39): max_dop=400
Completed read files with Yield (run 39): t_ms=239 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 40): max_dop=400
Completed read files with Yield (run 40): t_ms=231 resurrects=3604 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 41): max_dop=400
Completed read files with Yield (run 41): t_ms=233 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 42): max_dop=400
Completed read files with Yield (run 42): t_ms=238 resurrects=3605 reached_dop=9 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 43): max_dop=400
Completed read files with Yield (run 43): t_ms=250 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 44): max_dop=400
Completed read files with Yield (run 44): t_ms=253 resurrects=3604 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 45): max_dop=400
Completed read files with Yield (run 45): t_ms=260 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 46): max_dop=400
Completed read files with Yield (run 46): t_ms=289 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 47): max_dop=400
Completed read files with Yield (run 47): t_ms=301 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 48): max_dop=400
Completed read files with Yield (run 48): t_ms=340 resurrects=3605 reached_dop=11 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 49): max_dop=400
Completed read files with Yield (run 49): t_ms=305 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 50): max_dop=400
Completed read files with Yield (run 50): t_ms=288 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0

Start read files with Yield (run 1): max_dop=1
Completed read files with Yield (run 1): t_ms=1541 resurrects=3605 reached_dop=1 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 2): max_dop=1
Completed read files with Yield (run 2): t_ms=1468 resurrects=3605 reached_dop=1 cpu_threads=31
files=3605 bytes=1864636800 errors=0

Start read files without Yield (run 1): max_dop=400
Completed read files without Yield (run 1): t_ms=292 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 2): max_dop=400
Completed read files without Yield (run 2): t_ms=312 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 3): max_dop=400
Completed read files without Yield (run 3): t_ms=322 resurrects=3604 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 4): max_dop=400
Completed read files without Yield (run 4): t_ms=309 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 5): max_dop=400
Completed read files without Yield (run 5): t_ms=313 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 6): max_dop=400
Completed read files without Yield (run 6): t_ms=294 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 7): max_dop=400
Completed read files without Yield (run 7): t_ms=311 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 8): max_dop=400
Completed read files without Yield (run 8): t_ms=326 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 9): max_dop=400
Completed read files without Yield (run 9): t_ms=341 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 10): max_dop=400
Completed read files without Yield (run 10): t_ms=301 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 11): max_dop=400
Completed read files without Yield (run 11): t_ms=302 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 12): max_dop=400
Completed read files without Yield (run 12): t_ms=292 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 13): max_dop=400
Completed read files without Yield (run 13): t_ms=310 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 14): max_dop=400
Completed read files without Yield (run 14): t_ms=334 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 15): max_dop=400
Completed read files without Yield (run 15): t_ms=300 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 16): max_dop=400
Completed read files without Yield (run 16): t_ms=323 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 17): max_dop=400
Completed read files without Yield (run 17): t_ms=306 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 18): max_dop=400
Completed read files without Yield (run 18): t_ms=347 resurrects=3605 reached_dop=10 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 19): max_dop=400
Completed read files without Yield (run 19): t_ms=307 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 20): max_dop=400
Completed read files without Yield (run 20): t_ms=309 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 21): max_dop=400
Completed read files without Yield (run 21): t_ms=298 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 22): max_dop=400
Completed read files without Yield (run 22): t_ms=361 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 23): max_dop=400
Completed read files without Yield (run 23): t_ms=299 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 24): max_dop=400
Completed read files without Yield (run 24): t_ms=292 resurrects=3605 reached_dop=8 cpu_threads=31
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 25): max_dop=400
Completed read files without Yield (run 25): t_ms=320 resurrects=3604 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 26): max_dop=400
Completed read files without Yield (run 26): t_ms=291 resurrects=3603 reached_dop=10 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 27): max_dop=400
Completed read files without Yield (run 27): t_ms=293 resurrects=3605 reached_dop=10 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 28): max_dop=400
Completed read files without Yield (run 28): t_ms=306 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 29): max_dop=400
Completed read files without Yield (run 29): t_ms=294 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 30): max_dop=400
Completed read files without Yield (run 30): t_ms=314 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 31): max_dop=400
Completed read files without Yield (run 31): t_ms=330 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 32): max_dop=400
Completed read files without Yield (run 32): t_ms=300 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 33): max_dop=400
Completed read files without Yield (run 33): t_ms=298 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 34): max_dop=400
Completed read files without Yield (run 34): t_ms=297 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 35): max_dop=400
Completed read files without Yield (run 35): t_ms=351 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 36): max_dop=400
Completed read files without Yield (run 36): t_ms=309 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 37): max_dop=400
Completed read files without Yield (run 37): t_ms=311 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 38): max_dop=400
Completed read files without Yield (run 38): t_ms=318 resurrects=3603 reached_dop=10 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 39): max_dop=400
Completed read files without Yield (run 39): t_ms=300 resurrects=3605 reached_dop=10 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 40): max_dop=400
Completed read files without Yield (run 40): t_ms=349 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 41): max_dop=400
Completed read files without Yield (run 41): t_ms=299 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 42): max_dop=400
Completed read files without Yield (run 42): t_ms=308 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 43): max_dop=400
Completed read files without Yield (run 43): t_ms=288 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 44): max_dop=400
Completed read files without Yield (run 44): t_ms=354 resurrects=3604 reached_dop=10 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 45): max_dop=400
Completed read files without Yield (run 45): t_ms=320 resurrects=3605 reached_dop=10 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 46): max_dop=400
Completed read files without Yield (run 46): t_ms=296 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 47): max_dop=400
Completed read files without Yield (run 47): t_ms=292 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 48): max_dop=400
Completed read files without Yield (run 48): t_ms=322 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 49): max_dop=400
Completed read files without Yield (run 49): t_ms=303 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 50): max_dop=400
Completed read files without Yield (run 50): t_ms=302 resurrects=3605 reached_dop=8 cpu_threads=39
files=3605 bytes=1864636800 errors=0

Start read files without Yield (run 1): max_dop=1
Completed read files without Yield (run 1): t_ms=1520 resurrects=3605 reached_dop=1 cpu_threads=39
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 2): max_dop=1
Completed read files without Yield (run 2): t_ms=1517 resurrects=3605 reached_dop=1 cpu_threads=39
files=3605 bytes=1864636800 errors=0

*/
#endregion
#region 256K buffers
/*

Running on 210415-0223 at QUUX os='Windows (64-bit)' fwk=.NETFramework,Version=v4.6.1 ncpu=8 build=Release buf_kb=256
Thread pool: min=(wk=8 cq=8) max=(wk=32767 cq=1000)

Start counting files (warm up run 0): max_dop=400
Completed counting files (warm up run 0): t_ms=114 resurrects=0 cpu_threads=28
File count: 3605
Start counting files (run 1): max_dop=400
Completed counting files (run 1): t_ms=23 resurrects=0 cpu_threads=28
File count: 3605

Start read files with Yield (run 1): max_dop=400
Completed read files with Yield (run 1): t_ms=215 resurrects=3596 reached_dop=9 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 2): max_dop=400
Completed read files with Yield (run 2): t_ms=159 resurrects=3605 reached_dop=9 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 3): max_dop=400
Completed read files with Yield (run 3): t_ms=154 resurrects=3605 reached_dop=8 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 4): max_dop=400
Completed read files with Yield (run 4): t_ms=155 resurrects=3605 reached_dop=8 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 5): max_dop=400
Completed read files with Yield (run 5): t_ms=161 resurrects=3605 reached_dop=9 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 6): max_dop=400
Completed read files with Yield (run 6): t_ms=195 resurrects=3605 reached_dop=9 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 7): max_dop=400
Completed read files with Yield (run 7): t_ms=184 resurrects=3605 reached_dop=8 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 8): max_dop=400
Completed read files with Yield (run 8): t_ms=151 resurrects=3605 reached_dop=8 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 9): max_dop=400
Completed read files with Yield (run 9): t_ms=157 resurrects=3605 reached_dop=8 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 10): max_dop=400
Completed read files with Yield (run 10): t_ms=155 resurrects=3605 reached_dop=8 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 11): max_dop=400
Completed read files with Yield (run 11): t_ms=155 resurrects=3605 reached_dop=8 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 12): max_dop=400
Completed read files with Yield (run 12): t_ms=158 resurrects=3605 reached_dop=8 cpu_threads=29
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 13): max_dop=400
Completed read files with Yield (run 13): t_ms=204 resurrects=3604 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 14): max_dop=400
Completed read files with Yield (run 14): t_ms=175 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 15): max_dop=400
Completed read files with Yield (run 15): t_ms=162 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 16): max_dop=400
Completed read files with Yield (run 16): t_ms=160 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 17): max_dop=400
Completed read files with Yield (run 17): t_ms=171 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 18): max_dop=400
Completed read files with Yield (run 18): t_ms=160 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 19): max_dop=400
Completed read files with Yield (run 19): t_ms=158 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 20): max_dop=400
Completed read files with Yield (run 20): t_ms=164 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 21): max_dop=400
Completed read files with Yield (run 21): t_ms=176 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 22): max_dop=400
Completed read files with Yield (run 22): t_ms=168 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 23): max_dop=400
Completed read files with Yield (run 23): t_ms=168 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 24): max_dop=400
Completed read files with Yield (run 24): t_ms=166 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 25): max_dop=400
Completed read files with Yield (run 25): t_ms=162 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 26): max_dop=400
Completed read files with Yield (run 26): t_ms=158 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 27): max_dop=400
Completed read files with Yield (run 27): t_ms=172 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 28): max_dop=400
Completed read files with Yield (run 28): t_ms=158 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 29): max_dop=400
Completed read files with Yield (run 29): t_ms=159 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 30): max_dop=400
Completed read files with Yield (run 30): t_ms=167 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 31): max_dop=400
Completed read files with Yield (run 31): t_ms=159 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 32): max_dop=400
Completed read files with Yield (run 32): t_ms=175 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 33): max_dop=400
Completed read files with Yield (run 33): t_ms=160 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 34): max_dop=400
Completed read files with Yield (run 34): t_ms=165 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 35): max_dop=400
Completed read files with Yield (run 35): t_ms=173 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 36): max_dop=400
Completed read files with Yield (run 36): t_ms=159 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 37): max_dop=400
Completed read files with Yield (run 37): t_ms=197 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 38): max_dop=400
Completed read files with Yield (run 38): t_ms=168 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 39): max_dop=400
Completed read files with Yield (run 39): t_ms=175 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 40): max_dop=400
Completed read files with Yield (run 40): t_ms=158 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 41): max_dop=400
Completed read files with Yield (run 41): t_ms=157 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 42): max_dop=400
Completed read files with Yield (run 42): t_ms=162 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 43): max_dop=400
Completed read files with Yield (run 43): t_ms=160 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 44): max_dop=400
Completed read files with Yield (run 44): t_ms=175 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 45): max_dop=400
Completed read files with Yield (run 45): t_ms=163 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 46): max_dop=400
Completed read files with Yield (run 46): t_ms=167 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 47): max_dop=400
Completed read files with Yield (run 47): t_ms=168 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 48): max_dop=400
Completed read files with Yield (run 48): t_ms=167 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 49): max_dop=400
Completed read files with Yield (run 49): t_ms=162 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 50): max_dop=400
Completed read files with Yield (run 50): t_ms=159 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0

Start read files with Yield (run 1): max_dop=1
Completed read files with Yield (run 1): t_ms=822 resurrects=3605 reached_dop=1 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files with Yield (run 2): max_dop=1
Completed read files with Yield (run 2): t_ms=789 resurrects=3605 reached_dop=1 cpu_threads=30
files=3605 bytes=1864636800 errors=0

Start read files without Yield (run 1): max_dop=400
Completed read files without Yield (run 1): t_ms=177 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 2): max_dop=400
Completed read files without Yield (run 2): t_ms=189 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 3): max_dop=400
Completed read files without Yield (run 3): t_ms=174 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 4): max_dop=400
Completed read files without Yield (run 4): t_ms=178 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 5): max_dop=400
Completed read files without Yield (run 5): t_ms=173 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 6): max_dop=400
Completed read files without Yield (run 6): t_ms=171 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 7): max_dop=400
Completed read files without Yield (run 7): t_ms=176 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 8): max_dop=400
Completed read files without Yield (run 8): t_ms=172 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 9): max_dop=400
Completed read files without Yield (run 9): t_ms=208 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 10): max_dop=400
Completed read files without Yield (run 10): t_ms=174 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 11): max_dop=400
Completed read files without Yield (run 11): t_ms=178 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 12): max_dop=400
Completed read files without Yield (run 12): t_ms=169 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 13): max_dop=400
Completed read files without Yield (run 13): t_ms=179 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 14): max_dop=400
Completed read files without Yield (run 14): t_ms=171 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 15): max_dop=400
Completed read files without Yield (run 15): t_ms=164 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 16): max_dop=400
Completed read files without Yield (run 16): t_ms=167 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 17): max_dop=400
Completed read files without Yield (run 17): t_ms=164 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 18): max_dop=400
Completed read files without Yield (run 18): t_ms=167 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 19): max_dop=400
Completed read files without Yield (run 19): t_ms=173 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 20): max_dop=400
Completed read files without Yield (run 20): t_ms=174 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 21): max_dop=400
Completed read files without Yield (run 21): t_ms=172 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 22): max_dop=400
Completed read files without Yield (run 22): t_ms=168 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 23): max_dop=400
Completed read files without Yield (run 23): t_ms=166 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 24): max_dop=400
Completed read files without Yield (run 24): t_ms=186 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 25): max_dop=400
Completed read files without Yield (run 25): t_ms=181 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 26): max_dop=400
Completed read files without Yield (run 26): t_ms=183 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 27): max_dop=400
Completed read files without Yield (run 27): t_ms=172 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 28): max_dop=400
Completed read files without Yield (run 28): t_ms=169 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 29): max_dop=400
Completed read files without Yield (run 29): t_ms=172 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 30): max_dop=400
Completed read files without Yield (run 30): t_ms=170 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 31): max_dop=400
Completed read files without Yield (run 31): t_ms=181 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 32): max_dop=400
Completed read files without Yield (run 32): t_ms=171 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 33): max_dop=400
Completed read files without Yield (run 33): t_ms=171 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 34): max_dop=400
Completed read files without Yield (run 34): t_ms=177 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 35): max_dop=400
Completed read files without Yield (run 35): t_ms=182 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 36): max_dop=400
Completed read files without Yield (run 36): t_ms=247 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 37): max_dop=400
Completed read files without Yield (run 37): t_ms=221 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 38): max_dop=400
Completed read files without Yield (run 38): t_ms=213 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 39): max_dop=400
Completed read files without Yield (run 39): t_ms=265 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 40): max_dop=400
Completed read files without Yield (run 40): t_ms=223 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 41): max_dop=400
Completed read files without Yield (run 41): t_ms=207 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 42): max_dop=400
Completed read files without Yield (run 42): t_ms=215 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 43): max_dop=400
Completed read files without Yield (run 43): t_ms=207 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 44): max_dop=400
Completed read files without Yield (run 44): t_ms=224 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 45): max_dop=400
Completed read files without Yield (run 45): t_ms=226 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 46): max_dop=400
Completed read files without Yield (run 46): t_ms=241 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 47): max_dop=400
Completed read files without Yield (run 47): t_ms=214 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 48): max_dop=400
Completed read files without Yield (run 48): t_ms=210 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 49): max_dop=400
Completed read files without Yield (run 49): t_ms=208 resurrects=3605 reached_dop=8 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 50): max_dop=400
Completed read files without Yield (run 50): t_ms=222 resurrects=3605 reached_dop=10 cpu_threads=30
files=3605 bytes=1864636800 errors=0

Start read files without Yield (run 1): max_dop=1
Completed read files without Yield (run 1): t_ms=1147 resurrects=3605 reached_dop=1 cpu_threads=30
files=3605 bytes=1864636800 errors=0
Start read files without Yield (run 2): max_dop=1
Completed read files without Yield (run 2): t_ms=1087 resurrects=3605 reached_dop=1 cpu_threads=30
files=3605 bytes=1864636800 errors=0

*/
#endregion
#endregion

#endregion
