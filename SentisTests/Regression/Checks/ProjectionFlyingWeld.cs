using System;
using System.Collections;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A projection welded in flight at the game's top speed: a ship with a projector (a blueprint of two blocks of
    /// armor right next to it) and a cockpit the check's player sits in, and beside it a ship with a ship welder of the
    /// standard radius over the hologram, both sent at the large ships' top speed the same way; the welder switched on
    /// only then, with the components in its inventory. It has to find in flight that there is something to build
    /// (Multigrid Projector's projection follows the projector every frame while a welder reaches its buildable blocks:
    /// SentisOptimisations' ProjectionSlowdown) and build both blocks to the end. Both ships keep the top speed (no
    /// thrusters, no dampers) and their places to each other the whole time. The welder is switched on 150 frames into
    /// the flight: by then the projection, with no welder on, is moved once a second and left up to 100 m behind, and the
    /// welder has to be seen at where the blocks belong, not where the projection was left (the whole regression,
    /// 12.11.2026: 0 of 2 built).
    ///
    /// The "_slowdowns" one holds SentisOptimisations' slowdowns on (always) for itself - the projection of a projector
    /// moving faster than 0.5 m/s with a welder near follows it once in 4 updates - and runs only when named: it would
    /// slow down the checks beside it.
    /// </summary>
    public sealed class ProjectionFlyingWeldCheck : ProjectorRigCheck
    {
        private readonly bool _slowdowns;

        public ProjectionFlyingWeldCheck(bool slowdowns = false) { _slowdowns = slowdowns; }

        public override string Name => Prefix + "projection_flying_weld" + (_slowdowns ? "_slowdowns" : "");

        public override bool OnlyWhenAsked => _slowdowns;

        private Type _peak;
        private System.Reflection.FieldInfo _config;
        private object _saved;

        private void HoldSlowdowns(bool on)
        {
            var settingsType = _peak.GetNestedType("Settings");
            var settings = Activator.CreateInstance(settingsType);
            settingsType.GetField("Enabled").SetValue(settings, on);
            settingsType.GetField("Always").SetValue(settings, on);
            _config.SetValue(null, System.Linq.Expressions.Expression.Lambda(_config.FieldType, System.Linq.Expressions.Expression.Constant(settings, settingsType)).Compile());
            _peak.GetMethod("Second").Invoke(null, new object[] { 0.0, 1.0, settings });
        }

        public override void Cleanup()
        {
            try
            {
                if (_config != null && _saved != null)
                {
                    _config.SetValue(null, _saved);
                    var settings = _peak.GetNestedType("Settings").GetMethod("FromPlugin").Invoke(null, null);
                    _peak.GetMethod("Second").Invoke(null, new[] { (object)0.0, 1.0, settings });
                }
            }
            catch (Exception)
            {
            }
            base.Cleanup();
        }

        protected override IEnumerator Check()
        {
            if (_slowdowns)
            {
                _peak = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisations.Slowdowns.PeakLoad", false)).FirstOrDefault(t => t != null);
                if (_peak == null) throw new Core.ScenarioSkippedException("SentisOptimisations' slowdowns are not loaded");
                _config = _peak.GetField("Config", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                _saved = _config.GetValue(null);
                HoldSlowdowns(true);
                Check((bool)_peak.GetProperty("Active").GetValue(null), "the slowdowns did not come on");
            }
            yield return Arrive(Site.At(0, 10, 10));
            yield return Project("projection-flying", Site.Origin, null, false, new Part("Cockpit/LargeBlockCockpitSeat", 0, 1, 0));
            var grid = Grid;
            var projector = Projector;
            var cells = Cells;
            var hologram = projector.ProjectedGrid;
            Check(hologram != null && cells.Count > 0, "no hologram");
            Check(hologram.CubeBlocks.Count(b => projector.CanBuild(b, true) == BuildCheckResult.OK) > 0, "nothing of the projection can be built");

            // the welder on a ship of its own over the middle of the hologram, facing down at it; switched off for now
            var middle = (grid.GridIntegerToWorld(cells[0]) + grid.GridIntegerToWorld(cells[cells.Count - 1])) / 2;
            var rig = ToolRig("projection-flying-welder", "ShipWelder/LargeShipWelder", middle + Site.Up * 1.5, -Site.Up, isStatic: false);
            var welder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(rig);
            foreach (var need in hologram.CubeBlocks.SelectMany(b => b.BlockDefinition.Components).GroupBy(c => c.Definition.Id))
                Inventory(welder).AddItems(need.Sum(c => c.Count) + 10, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(need.Key));
            Switch(welder, false);

            // the player rides along: the freezer stops a ship nobody is near
            yield return Sit(Block<MyCockpit>(grid));

            // both ships at the top speed, the same way
            var top = MyGridPhysics.LargeShipMaxLinearVelocity();
            var way = Vector3D.Normalize(Site.Up + Site.Forward);
            var velocity = (Vector3)(way * top);
            var apart = rig.PositionComp.GetPosition() - grid.PositionComp.GetPosition();
            grid.Physics.LinearVelocity = velocity;
            rig.Physics.LinearVelocity = velocity;
            // long enough for SentisOptimisations to see no welder on (it looks once in 60 frames) and leave the projection
            // behind - up to a second, 100 m at this speed: the welder switched on must still be seen and the
            // projection brought up
            yield return WaitForTicks(150);
            double Speed(MyCubeGrid g) => g.Physics?.LinearVelocity.Length() ?? 0;
            double Drift() => Vector3D.Distance(rig.PositionComp.GetPosition() - grid.PositionComp.GetPosition(), apart);
            Note($"flying: the projector's ship {Speed(grid):F1} m/s, the welder's {Speed(rig):F1} m/s of {top:F0}; apart as built within {Drift():F3} m");
            Check(Speed(grid) > top * 0.95 && Speed(rig) > top * 0.95, $"the ships are not at the top speed: {Speed(grid):F1} and {Speed(rig):F1} m/s of {top:F0}");

            // the welder on in flight: it finds the hologram's blocks and builds them
            Switch(welder, true);
            var from = grid.PositionComp.GetPosition();
            double slowest = double.MaxValue, drift = 0;
            var started = DateTime.UtcNow;
            int Built() => cells.Count(c => grid.GetCubeBlock(c)?.IsFullIntegrity == true);
            int Placed() => cells.Count(c => grid.GetCubeBlock(c) != null);
            var firstPlacedAt = -1.0;
            while (Built() < cells.Count && (DateTime.UtcNow - started).TotalSeconds < 60)
            {
                slowest = Math.Min(slowest, Math.Min(Speed(grid), Speed(rig)));
                drift = Math.Max(drift, Drift());
                if (firstPlacedAt < 0 && Placed() > 0) firstPlacedAt = Vector3D.Distance(grid.PositionComp.GetPosition(), from);
                yield return null;
            }
            var flown = Vector3D.Distance(grid.PositionComp.GetPosition(), from);
            var line = (_slowdowns ? "with the slowdowns on: " : "") + $"welded {Built()} of {cells.Count} projected blocks at {top:F0} m/s (the slowest {slowest:F1} m/s), the first block placed after {firstPlacedAt:F0} m, " +
                       $"all built after {flown:F0} m; the ships kept apart as built within {drift:F3} m";
            Note(line);
            Check(Built() == cells.Count, $"in flight the welder built {Built()} of {cells.Count} projected blocks in 60 s ({Placed()} placed): " + State(welder));
            // switched on, the welder is seen at once and the projection follows every frame: the first block within a
            // second of flight (a welder seen only by the once-a-second look, or not at all, is 100-300 m)
            Check(firstPlacedAt >= 0 && firstPlacedAt < top, $"the first block was placed only after {firstPlacedAt:F0} m of flight (a second is {top:F0} m)");
            Check(slowest > top * 0.9, $"the ships slowed down while welding: {slowest:F1} m/s of {top:F0}");
            Check(drift < 0.5, $"the ships moved {drift:F2} m apart while welding");
            Result(line);
        }
    }
}
