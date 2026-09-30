using System;
using System.Collections;
using System.Threading;
using SentisTests.Core;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Crashes the server on purpose: an exception nobody catches, on a thread of its own, the way a fatal one reaches
    /// Torch. Checks by hand that CrashDump leaves a full dump (CrashDumps\crash_*.dmp, "CrashDump: dump written" in
    /// the log) before Torch kills the process. Never part of a run of all scenarios.
    /// </summary>
    public sealed class FatalExceptionScenario : TestScenario
    {
        public const string ScenarioName = "fatal_exception";

        public override string Name => ScenarioName;

        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            Note("throwing an unhandled exception on a new thread: the server goes down after a full dump");
            new Thread(() => throw new InvalidOperationException("fatal_exception scenario: deliberate crash")) { IsBackground = true, Name = "fatal_exception" }.Start();
            yield return WaitForSeconds(60, "waiting for the crash");
            Check(false, "the server is still up: the exception did not reach Torch's handler");
        }
    }
}
