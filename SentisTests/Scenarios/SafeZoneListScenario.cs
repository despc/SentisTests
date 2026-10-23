using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;

namespace SentisTests.Scenarios
{
    /// <summary>Every safe zone of the world: where, how big, on, its block, what it allows - leftovers of runs show here.</summary>
    public sealed class SafeZoneListScenario : TestScenario
    {
        public const string ScenarioName = "safe_zone_list";
        public override string Name => ScenarioName;

        public override IEnumerator Run()
        {
            foreach (var zone in MyEntities.GetEntities().OfType<MySafeZone>())
                Note("zone " + zone.EntityId + " '" + zone.DisplayName + "' at " + zone.PositionComp.GetPosition() + " r " + zone.Radius + " " + zone.Shape + ", on " + zone.Enabled +
                     ", block " + zone.SafeZoneBlockId + ", allowed " + zone.AllowedActions + ", players " + zone.AccessTypePlayers);
            yield return null;
        }
    }
}
