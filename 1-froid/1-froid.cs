using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using static System.Console;

/*
Consider a set of 3500 files, about 2GB in total. The task at hand is to
read them asynchronously (just read into memory, chunking into 16K-sized reads,
and discard), while keeping the number of open files at a preset number (400
at this example).

This is a distilled toy problem for a more complex design problem. There is a
3rd party communication library based on async/await pattern. There is also a
large set of files that must be streamed to a remote service in parallel. With
a hefty margin, a 1000 parallel streams may be required, and the total data
rate before protocol overhead may be up to 60 MB/s, while 1 to 4MB/s is more
typical.

  NONE OF THESE TESTS WORK.

It appears to be amazingly unsimple to escape from the Task async comonad back
into the imperative world. The reason for this particular approach failure is
the waiting on task completion in a blocking manner inside Parallel.ForEach.
The Action invoked by it is itself running as a Task, on a scheduler.
A different scheduler may be specified via ParallelOptions, but I don't see
how it would help: a blocked pool thread is a huge waste.

Everything should be a single composite Task, but limiting the DOP in the
task composition is hard. The Parallel class seemed promising, but it controls
the parallelism outside of the monad, leading to the behaviors these tests
show: a huge overhead with even a thread per task, which is already a non-
solution. Perhaps a custom scheduler, aware of the number of channels, could
possibly do by counting the tasks in progress, but reality is much complex
than just keeping the overall number of tasks under a limit: there is not a
single species of Task in the real version of this problem.

  THE REAL MCCOY

The real problem is significantly more complex, and limiting the DOP is only
part of it. There are tasks to open a stream to the server (subject to the
DOP limit), and those which are concerned with streaming into an already open
streams, which aren't.

The server may start refusing new streams, and the client better react
efficiently to these backoff hints. Thus, one file does not equal one task
pumped through and this is it. Instead, is a task is rejected with a specific
code, the whole machinery may consider reduce the rate of stream origination
(NOT the rate of streaming on existing calls, we're talking about the rate of
creation of new streams!). Since the server does not send an _underload_ signal,
it is okay to just retry failed streams until they are accepted. The server
should not ever starve of streamed data, and a few large files, that take up
time to process, may cause the backoff condition. A more sophisticated approach
is to drop the DOP somewhat under repeating backoff signal from the server,
while letting the said drop decay over time up to the initial value.

In either case, the server optimal and maximum capacity is known, and it's
ideal to slightly oversubscribe the streams. The server processes a fixed
number of channels, but does accept and pre-buffers initial data of the
oversubscribed streams, to quickly take new work from this backup pile when
other streams end, to keep itself running at the stated fixed capacity.

However, the server imposes a high watermark on the oversubscription, where
maintaining the backup streams would be nothing but an unnecessary burden.
The optimal tuning is such that there is enough backup channels to fill in
new stream slots with a 95-97% probability for the next internal pump cycle.
The pump cycle processes a fixed chunk of every of the fixed number of streams
mentioned above in a "minibatch," employing parallelism of computation; not
keeping enough streams means proportional reduction in performance).

The TPL attempted to solve the problem of reducing the implementation
complexity, but did not hit the mark. In the C++ world, TBB is the example of
a successful solution of a graph data flow, and provides everything the TPL
could but did not.

The part 2 is an attempt to solve this conundrum by marrying the reactive
programming paradigm with the tasks (remember, the async/await and Tasks are
imposed by the communication library in the real setting, thus unavoidable).

And may the types always guide you!

*/

public static class Program {
  const int MAXDOP = 400;

  static ParallelOptions g_paropt = new ParallelOptions() {
    MaxDegreeOfParallelism = MAXDOP
  };

  static IEnumerable<string> Filenames =>
    Directory.EnumerateFiles(Environment.SystemDirectory, "*.dll");

  static void Main(string[] args) {
    PrintRunStats();

    S01_Count("run 1");
    S01_Count("run 2");
    WriteLine();

    for (int i = 0; i < 8; i++) {
      S02_SyncRead($"maxdop=MAXDOP, run {i + 1}");
    }
    g_paropt.MaxDegreeOfParallelism = 1;
    S02_SyncRead("maxdop=1, try 1");
    S02_SyncRead("maxdop=1, try 2");
    g_paropt.MaxDegreeOfParallelism = MAXDOP;
    WriteLine();

    RunS04andS03();

    WriteLine("\n*** Even outright condoning the pool blow-up does not help:\n");
    ThreadPool.SetMinThreads(MAXDOP * 2, MAXDOP);

    RunS04andS03();
  }

  static void RunS04andS03() {
    for (int i = 0; i < 4; i++) {
      S04_BlockingAsyncReadWithYield($"maxdop=MAXDOP, run {i + 1}");
    }
    WriteLine();
    g_paropt.MaxDegreeOfParallelism = 1;
    S04_BlockingAsyncReadWithYield("maxdop=1, run 1");
    S04_BlockingAsyncReadWithYield("maxdop=1, run 2");
    g_paropt.MaxDegreeOfParallelism = MAXDOP;
    WriteLine();

    for (int i = 0; i < 4; i++) {
      S03_BlockingAsyncRead($"maxdop=MAXDOP, run {i + 1}");
    }
    WriteLine();
    g_paropt.MaxDegreeOfParallelism = 1;
    S03_BlockingAsyncRead("maxdop=1, run 1");
    S03_BlockingAsyncRead("maxdop=1, run 2");
    g_paropt.MaxDegreeOfParallelism = MAXDOP;
    WriteLine();
  }

  static void Clock(string what, Action<string> action) {
    WriteLine("Start {0}", what);
    var clock = Stopwatch.StartNew();
    Parallel.ForEach(Filenames, g_paropt, action);
    WriteLine("Completed {0} in {1} ms, threads={2}",
              what, clock.ElapsedMilliseconds, NumThreadpoolThreads);
  }

  static void SyncWait(this Task t) =>
    t.ConfigureAwait(false).GetAwaiter().GetResult();

  static T SyncWait<T>(this Task<T> t) =>
    t.ConfigureAwait(false).GetAwaiter().GetResult();

  static long g_nfiles, g_nbytes, g_nerrors;

  static void S01_Count(string pass) {
    g_nfiles = 0;
    Clock($"counting files ({pass})",
      _ => { Interlocked.Increment(ref g_nfiles); });
    WriteLine($"File count: {g_nfiles}");
  }

  static void S02_SyncRead(string pass) {
    g_nfiles = g_nbytes = g_nerrors = 0;
    Clock($"read files synchronously ({pass})", file => {
      Interlocked.Increment(ref g_nfiles);
      var buf = new byte[16384];
      long total_bytes = 0;
      try {
        using (var stm = File.OpenRead(file)) {
          for (; ; ) {
            int cb = stm.Read(buf, 0, buf.Length);
            if (cb == 0) break;
            total_bytes += cb;
          }
        }
        Interlocked.Add(ref g_nbytes, total_bytes);
      }
      catch {
        Interlocked.Increment(ref g_nerrors);
      }
    });
    WriteLine($"files={g_nfiles} bytes={g_nbytes} errors={g_nerrors}");
  }

  static void S03_BlockingAsyncRead(string pass) {
    g_nfiles = g_nbytes = g_nerrors = 0;
    Clock($"blocking async read files without Yield ({pass})", file => {
      WorkerForBlockingAsyncRead(file, false).SyncWait();
    });
    WriteLine($"files={g_nfiles} bytes={g_nbytes} errors={g_nerrors}");
  }

  static void S04_BlockingAsyncReadWithYield(string pass) {
    g_nfiles = g_nbytes = g_nerrors = 0;
    Clock($"blocking async read files with Yield ({pass})", file => {
      WorkerForBlockingAsyncRead(file, true).SyncWait();
    });
    WriteLine($"files={g_nfiles} bytes={g_nbytes} errors={g_nerrors}");
  }

  static async Task WorkerForBlockingAsyncRead(string file, bool yieldFirst) {
    if (yieldFirst) {
      await Task.Yield();
    }
    Interlocked.Increment(ref g_nfiles);
    var buf = new byte[16384];
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
  }

  #region Helpers, runtime-dependent.

  static int NumThreadpoolThreads =>
#if NETCOREAPP3_1_OR_GREATER
    ThreadPool.ThreadCount;
#else
    // We didn't launch any threads. Assume that all threads but the main are
    // pool threads.
    Process.GetCurrentProcess().Threads.Count - 1;
#endif

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
    string framework = Assembly.GetEntryAssembly()?
      .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?
      .FrameworkName ?? "unknown";

    string bitness = Environment.Is64BitProcess ? "64-bit" : "32-bit";

    WriteLine("Run at {0} os='{1} ({2})' fwk={3} ncpu={4} build={5}",
      DateTime.Now.ToString("yyMMdd'-'HHmm"),
      osname, bitness, framework, Environment.ProcessorCount, build);

    int min_wkt, max_wkt, min_cqp, max_cqp;
    ThreadPool.GetMinThreads(out min_wkt, out min_cqp);
    ThreadPool.GetMaxThreads(out max_wkt, out max_cqp);
    WriteLine("Thread pool: min=(wk={0} cq={1}) max=(wk={2} cq={3})\n",
              min_wkt, min_cqp, max_wkt, max_cqp);
  }

  #endregion
}

#region === RESULTS ===

// All results obtained on my very  same laptop. Other frameworks in the
// project's RID list. The optimum obtained in the synchronous test, where the
// framework optimizes parallelism, are not beaten even by a 800-thread pool
// monstrosity.
//
// Do not block on individual tasks. In theory, the Task type is a comonad[1]
// with the Extract and Cobind operators. In practice, its Extract, a.k.a.
// Task.Result, is very hard to get to work without blocking the system threads
// that shall never be blocked.
//
// Tasks are infectious bottom-up.
//
// ________________
// [1] See, e.g., https://www.davesexton.com/blog/post/monads-and-comonads-and-linq-oh-my.aspx

#region .NET 5.0
/*

Run at 210413-1622 os='Microsoft Windows 10.0.19042 (64-bit)' fwk=.NETCoreApp,Version=v5.0 ncpu=8 build=Release
Thread pool: min=(wk=8 cq=8) max=(wk=32767 cq=1000)

Start counting files (run 1)
Completed counting files (run 1) in 65 ms, threads=8
File count: 3609
Start counting files (run 2)
Completed counting files (run 2) in 7 ms, threads=8
File count: 3609

Start read files synchronously (maxdop=MAXDOP, run 1)
Completed read files synchronously (maxdop=MAXDOP, run 1) in 632 ms, threads=9
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 2)
Completed read files synchronously (maxdop=MAXDOP, run 2) in 638 ms, threads=9
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 3)
Completed read files synchronously (maxdop=MAXDOP, run 3) in 749 ms, threads=9
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 4)
Completed read files synchronously (maxdop=MAXDOP, run 4) in 723 ms, threads=8
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 5)
Completed read files synchronously (maxdop=MAXDOP, run 5) in 697 ms, threads=8
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 6)
Completed read files synchronously (maxdop=MAXDOP, run 6) in 516 ms, threads=8
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 7)
Completed read files synchronously (maxdop=MAXDOP, run 7) in 528 ms, threads=8
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 8)
Completed read files synchronously (maxdop=MAXDOP, run 8) in 581 ms, threads=9
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=1, try 1)
Completed read files synchronously (maxdop=1, try 1) in 1654 ms, threads=9
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=1, try 2)
Completed read files synchronously (maxdop=1, try 2) in 1299 ms, threads=9
files=3609 bytes=1865039432 errors=0

Start blocking async read files with Yield (maxdop=MAXDOP, run 1)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 1) in 3127 ms, threads=25
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 2)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 2) in 1256 ms, threads=62
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 3)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 3) in 1689 ms, threads=77
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 4)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 4) in 1216 ms, threads=87
files=3609 bytes=1865039432 errors=0

Start blocking async read files with Yield (maxdop=1, run 1)
Completed blocking async read files with Yield (maxdop=1, run 1) in 2409 ms, threads=67
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=1, run 2)
Completed blocking async read files with Yield (maxdop=1, run 2) in 2451 ms, threads=67
files=3609 bytes=1865039432 errors=0

Start blocking async read files without Yield (maxdop=MAXDOP, run 1)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 1) in 3438 ms, threads=128
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 2)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 2) in 2025 ms, threads=147
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 3)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 3) in 4967 ms, threads=186
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 4)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 4) in 2109 ms, threads=187
files=3609 bytes=1865039432 errors=0

Start blocking async read files without Yield (maxdop=1, run 1)
Completed blocking async read files without Yield (maxdop=1, run 1) in 2303 ms, threads=187
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=1, run 2)
Completed blocking async read files without Yield (maxdop=1, run 2) in 2902 ms, threads=187
files=3609 bytes=1865039432 errors=0


*** Even outright condoning the pool blow-up does not help:

[NOTE: This is the only result in the both tests that _came close_ to the simple
sequential reads, within a factor of 1.5, and also has a low dispersion. But
the price paid is 800 active threads, which is nonsensical in real client -kkm]

Start blocking async read files with Yield (maxdop=MAXDOP, run 1)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 1) in 877 ms, threads=800
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 2)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 2) in 858 ms, threads=800
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 3)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 3) in 950 ms, threads=800
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 4)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 4) in 876 ms, threads=800
files=3609 bytes=1865039432 errors=0

Start blocking async read files with Yield (maxdop=1, run 1)
Completed blocking async read files with Yield (maxdop=1, run 1) in 2647 ms, threads=800
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=1, run 2)
Completed blocking async read files with Yield (maxdop=1, run 2) in 2733 ms, threads=800
files=3609 bytes=1865039432 errors=0

Start blocking async read files without Yield (maxdop=MAXDOP, run 1)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 1) in 745 ms, threads=800
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 2)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 2) in 842 ms, threads=800
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 3)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 3) in 824 ms, threads=800
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 4)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 4) in 937 ms, threads=800
files=3609 bytes=1865039432 errors=0

Start blocking async read files without Yield (maxdop=1, run 1)
Completed blocking async read files without Yield (maxdop=1, run 1) in 2840 ms, threads=800
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=1, run 2)
Completed blocking async read files without Yield (maxdop=1, run 2) in 2748 ms, threads=800
files=3609 bytes=1865039432 errors=0

*/
#endregion

#region .NET Framework 4.8
/*

Run at 210413-1628 os='Windows (64-bit)' fwk=.NETFramework,Version=v4.8 ncpu=8 build=Release
Thread pool: min=(wk=8 cq=8) max=(wk=32767 cq=1000)

Start counting files (run 1)
Completed counting files (run 1) in 34 ms, threads=17
File count: 3609
Start counting files (run 2)
Completed counting files (run 2) in 16 ms, threads=17
File count: 3609

Start read files synchronously (maxdop=MAXDOP, run 1)
Completed read files synchronously (maxdop=MAXDOP, run 1) in 736 ms, threads=17
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 2)
Completed read files synchronously (maxdop=MAXDOP, run 2) in 845 ms, threads=18
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 3)
Completed read files synchronously (maxdop=MAXDOP, run 3) in 619 ms, threads=18
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 4)
Completed read files synchronously (maxdop=MAXDOP, run 4) in 630 ms, threads=18
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 5)
Completed read files synchronously (maxdop=MAXDOP, run 5) in 419 ms, threads=18
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 6)
Completed read files synchronously (maxdop=MAXDOP, run 6) in 443 ms, threads=18
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 7)
Completed read files synchronously (maxdop=MAXDOP, run 7) in 555 ms, threads=18
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=MAXDOP, run 8)
Completed read files synchronously (maxdop=MAXDOP, run 8) in 564 ms, threads=18
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=1, try 1)
Completed read files synchronously (maxdop=1, try 1) in 1536 ms, threads=17
files=3609 bytes=1865039432 errors=0
Start read files synchronously (maxdop=1, try 2)
Completed read files synchronously (maxdop=1, try 2) in 1511 ms, threads=17
files=3609 bytes=1865039432 errors=0

[NOTE the inconsistent timing below. The time is wasted in spawning threads,
not in the tasks! -kkm]

Start blocking async read files with Yield (maxdop=MAXDOP, run 1)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 1) in 2732 ms, threads=52
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 2)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 2) in 2943 ms, threads=87
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 3)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 3) in 2175 ms, threads=120
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 4)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 4) in 1781 ms, threads=135
files=3609 bytes=1865039432 errors=0

Start blocking async read files with Yield (maxdop=1, run 1)
Completed blocking async read files with Yield (maxdop=1, run 1) in 2804 ms, threads=135
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=1, run 2)
Completed blocking async read files with Yield (maxdop=1, run 2) in 2868 ms, threads=135
files=3609 bytes=1865039432 errors=0

[NOTE the same time dispersion. -kkm]

Start blocking async read files without Yield (maxdop=MAXDOP, run 1)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 1) in 15378 ms, threads=175
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 2)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 2) in 3041 ms, threads=212
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 3)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 3) in 4147 ms, threads=232
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 4)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 4) in 7262 ms, threads=269
files=3609 bytes=1865039432 errors=0

Start blocking async read files without Yield (maxdop=1, run 1)
Completed blocking async read files without Yield (maxdop=1, run 1) in 2964 ms, threads=269
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=1, run 2)
Completed blocking async read files without Yield (maxdop=1, run 2) in 3381 ms, threads=269
files=3609 bytes=1865039432 errors=0


*** Even outright condoning the pool blow-up does not help:

Start blocking async read files with Yield (maxdop=MAXDOP, run 1)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 1) in 2496 ms, threads=807
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 2)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 2) in 2254 ms, threads=807
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 3)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 3) in 2538 ms, threads=812
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=MAXDOP, run 4)
Completed blocking async read files with Yield (maxdop=MAXDOP, run 4) in 2522 ms, threads=811
files=3609 bytes=1865039432 errors=0

Start blocking async read files with Yield (maxdop=1, run 1)
Completed blocking async read files with Yield (maxdop=1, run 1) in 4444 ms, threads=811
files=3609 bytes=1865039432 errors=0
Start blocking async read files with Yield (maxdop=1, run 2)
Completed blocking async read files with Yield (maxdop=1, run 2) in 4712 ms, threads=811
files=3609 bytes=1865039432 errors=0

Start blocking async read files without Yield (maxdop=MAXDOP, run 1)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 1) in 2023 ms, threads=811
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 2)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 2) in 2027 ms, threads=825
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 3)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 3) in 2084 ms, threads=825
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=MAXDOP, run 4)
Completed blocking async read files without Yield (maxdop=MAXDOP, run 4) in 2315 ms, threads=825
files=3609 bytes=1865039432 errors=0

Start blocking async read files without Yield (maxdop=1, run 1)
Completed blocking async read files without Yield (maxdop=1, run 1) in 4844 ms, threads=825
files=3609 bytes=1865039432 errors=0
Start blocking async read files without Yield (maxdop=1, run 2)
Completed blocking async read files without Yield (maxdop=1, run 2) in 4501 ms, threads=825
files=3609 bytes=1865039432 errors=0

*/

#endregion

#endregion
