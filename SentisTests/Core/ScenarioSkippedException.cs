using System;

namespace SentisTests.Core
{
    /// <summary>
    /// Thrown when a scenario cannot run here at all - the plugin it tests, or the world it needs,
    /// is not there. Marks the scenario SKIPPED: nothing of the stand is broken, the thing under
    /// test is simply absent. Thrown from anywhere in the coroutine; the runner records the reason
    /// and moves to the next scenario.
    /// </summary>
    public class ScenarioSkippedException : Exception
    {
        public ScenarioSkippedException(string message) : base(message)
        {
        }
    }
}
