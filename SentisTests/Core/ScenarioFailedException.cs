using System;

namespace SentisTests.Core
{
    /// <summary>
    /// Thrown by an assertion inside a scenario. Marks the scenario FAILED with the message;
    /// anything else thrown is reported as an ERROR (bug in the scenario itself).
    /// </summary>
    public class ScenarioFailedException : Exception
    {
        public ScenarioFailedException(string message) : base(message)
        {
        }
    }
}
