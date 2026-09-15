using System.Collections.Generic;

namespace SentisTests
{
    public class MainConfig
    {
        /// <summary>Run the configured scenarios automatically once the world is loaded.</summary>
        public bool AutoRun { get; set; } = false;

        /// <summary>
        /// Scenario names queued when AutoRun is enabled. Empty = all registered.
        /// NOTE: must stay empty-initialized; XmlSerializer APPENDS file items to the default list.
        /// </summary>
        public List<string> AutoScenarios { get; set; } = new List<string>();

        /// <summary>Grace period after session load before the first scenario starts (seconds).</summary>
        public int AutoRunDelaySeconds { get; set; } = 20;

        /// <summary>Per-scenario timeout used when the scenario does not request its own.</summary>
        public int DefaultTimeoutSeconds { get; set; } = 300;

        /// <summary>Remove every entity the scenario spawned once it finishes (pass or fail).</summary>
        public bool CleanupAfterTests { get; set; } = true;

        /// <summary>
        /// How long spawned test entities stay in the world after a test finishes, so an admin
        /// can join and inspect them (the queue is processed on the game thread). 0 = delete now.
        /// </summary>
        public int CleanupDelaySeconds { get; set; } = 60;

        /// <summary>Directory for markdown/json reports. Relative paths are resolved against the instance.</summary>
        public string ReportDirectory { get; set; } = @"SentisTests";

        /// <summary>
        /// Enables the loopback-only administrative DebugBridge. Disabled by default because its
        /// endpoints can move/delete grids and read logs.
        /// </summary>
        public bool EnableDebugBridge { get; set; } = false;

        /// <summary>Bearer token required by every DebugBridge REST/MCP request (minimum 32 characters).</summary>
        public string DebugBridgeToken { get; set; } = "";

        /// <summary>
        /// Display name of the player that owns and built the spawned test ships.
        /// welder passes its OWN OwnerId to the projector as the owner of the block it starts, so
        /// the tools have to belong to a real player - the same one who hand-welds the projection
        /// in the client. Empty, or a name that is not in the save, falls back to any saved player.
        /// </summary>
        public string OwnerPlayerName { get; set; } = "";
    }
}
