using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A look at a bot's flying drill as it stands (13.10.2026, Cedar's would not rise off its base): its connector's state,
    /// its landing gear, its thrusters and batteries, what its body touches. Read only; the drill by name ("Атмосферный бур Cedar").
    /// </summary>
    public sealed class DrillStateScenario : TestScenario
    {
        public const string ScenarioName = "drill_state";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 30;

        public override IEnumerator Run()
        {
            var drill = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => g.DisplayName.StartsWith("Атмосферный бур ") && g.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyShipConnector>().Any() && g.DisplayName != "Атмосферный бур");
            Check(drill != null, "no bot's flying drill");
            foreach (var c in drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyShipConnector>())
                Note($"DRILL STATE | connector: {c.Status}, other {c.OtherConnector?.CubeGrid?.DisplayName}, strength {c.PullStrength:0.000}, throw out {c.ThrowOut}, on {c.Enabled}");
            foreach (var g in drill.GetFatBlocks().OfType<SpaceEngineers.Game.ModAPI.IMyLandingGear>())
                Note($"DRILL STATE | landing gear: locked {g.IsLocked}, auto lock {g.AutoLock}, mode {g.LockMode}");
            var thrusters = drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyThrust>().ToList();
            Note($"DRILL STATE | thrusters: working {thrusters.Count(t => t.IsWorking)} of {thrusters.Count}, overrides {string.Join(",", thrusters.Select(t => t.ThrustOverridePercentage.ToString("0.00")).Distinct())}, max effective {thrusters.Sum(t => t.MaxEffectiveThrust):0} N");
            // the thrust each way along its own axes: its nose (the drills), its up (the connector's opposite)
            var nose = drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyShipDrill>().First().WorldMatrix.Forward;
            var top = -drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyShipConnector>().First().WorldMatrix.Forward;
            var right = VRageMath.Vector3D.Cross(nose, top);
            string Way(VRageMath.Vector3D w) => (thrusters.Where(t => VRageMath.Vector3D.Dot(t.WorldMatrix.Backward, w) > 0.7).Sum(t => t.MaxEffectiveThrust) / 1000).ToString("0") + " kN";
            Note($"DRILL STATE | thrust: forward {Way(nose)}, back {Way(-nose)}, up {Way(top)}, down {Way(-top)}, right {Way(right)}, left {Way(-right)}; weight {drill.Physics.Mass * 9.81 / 1000:0} kN");
            foreach (var b in drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyBatteryBlock>())
                Note($"DRILL STATE | battery: {b.CurrentStoredPower:0.000}/{b.MaxStoredPower:0.000} MWh, {b.ChargeMode}, working {b.IsWorking}, output {b.CurrentOutput:0.000}");
            Note($"DRILL STATE | physics: mass {drill.Physics.Mass:0} kg, fixed {drill.Physics.RigidBody?.IsFixed}, static {drill.IsStatic}, velocity {drill.Physics.LinearVelocity.Length():0.00}, dampeners {drill.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>()?.DampenersEnabled}");
            var box = drill.PositionComp.WorldAABB;
            var touching = MyEntities.GetEntitiesInAABB(ref box).OfType<MyCubeGrid>().Where(g => g != drill).Select(g => g.DisplayName).Distinct();
            Note($"DRILL STATE | grids in its box: {string.Join(", ", touching)}");
            // where it is: over the ground, against the base's connector
            var planet = MyGamePruningStructure.GetClosestPlanet(drill.Physics.CenterOfMassWorld);
            var at = drill.Physics.CenterOfMassWorld;
            var surface = planet.GetClosestSurfacePointGlobal(ref at);
            var up = VRageMath.Vector3D.Normalize(at - planet.PositionComp.GetPosition());
            var hits = new System.Collections.Generic.List<Sandbox.Engine.Physics.MyPhysics.HitInfo>();
            Sandbox.Engine.Physics.MyPhysics.CastRay(at, at - up * 300, hits, Sandbox.Engine.Physics.MyPhysics.CollisionLayers.VoxelCollisionLayer);
            var ground = hits.Count > 0 ? VRageMath.Vector3D.Dot(at - hits[0].Position, up) : -1;
            Note($"DRILL STATE | {VRageMath.Vector3D.Dot(at - surface, up):0.0} m over the planet's surface as made, {ground:0.0} m over the ground under it");
            var home = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => g.DisplayName == "База Cedar");
            var dock = home?.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyShipConnector>().FirstOrDefault(c => c.BlockDefinition.Id.SubtypeName == "Connector");
            if (dock != null)
            {
                var d = at - dock.PositionComp.GetPosition();
                Note($"DRILL STATE | from the base's connector: {VRageMath.Vector3D.Dot(d, up):0.0} m up, {VRageMath.Vector3D.Reject(d, up).Length():0.0} m aside");
            }
            yield break;
        }
    }
}
