using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using SentisTests.Regression;
using VRage.Game;
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Three independent MGP projection welds running AT THE SAME TIME in three different physics
    /// clusters (regression sites 8 km apart along the world's axes - one cluster of their own each,
    /// the same layout SentisClusters' 5 km clusters give the regression checks).
    ///
    /// Measures the load MGP adds, in three windows of frame time:
    ///   baseline - MGP loaded, nothing projected, no welding;
    ///   idle projection - all three repair projections active and stocked, welders off;
    ///   welding - all three welders armed on the same tick until every rig is fully built.
    /// The verdict reports each window's sim-work (busy ms/frame p50/p95/p99/max) and the welder
    /// build counts, so the projection's steady cost and the concurrent-weld peak are separate numbers.
    /// </summary>
    public sealed class MultigridProjectorClustersScenario : TestScenario
    {
        public const string ScenarioName = "multigrid_projector_clusters";
        private const string Prefix = "ST-47351-mgp3-";
        private const int Rigs = 3;
        private readonly Rig[] _rigs = new Rig[Rigs];
        private object _api;
        private Type _apiType;
        private bool _spawned;

        public override string Name => ScenarioName;
        public override string[] Requires => new[] { Integrations.MultigridProjector };
        public override int TimeoutSeconds => 900;

        private sealed class Rig
        {
            public Vector3D Origin;
            public MyCubeGrid Seed;
            public MyCubeGrid Ship;
            public MyProjectorBase Projector;
            public SpaceWelder Welder;
            public readonly MyCubeGrid[] Built = new MyCubeGrid[3];
            public readonly MyCubeGrid[] Previews = new MyCubeGrid[3];
            public int[] Expected;
            public List<MyObjectBuilder_CubeGrid> Blueprint;
            public Dictionary<string, int> Costs;
            public Dictionary<string, int> Stock0;
            public double BestIntegrity;
            public DateTime LastProgress;
            public int Finished;
        }

        private object Api(string method, params object[] args)
        {
            var target = _apiType.GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
            Check(target != null, "incompatible MGP API: " + method + " missing");
            return target.Invoke(_api, args);
        }

        public override IEnumerator Run()
        {
            RequirePlugin(Integrations.MultigridProjector);
            WorldApi.EnsureUnpaused(Name);
            Check(!MySession.Static.CreativeMode, "this scenario requires Survival: component payment must be real");
            var provider = RequireType(Integrations.TypeOf(Integrations.MultigridProjector,
                "MultigridProjector.Logic.MultigridProjectorApiProvider"), "MGP API provider");
            _api = provider.GetProperty("Api", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            Check(_api != null, "MGP API provider returned null");
            _apiType = _api.GetType();

            // Baseline: MGP loaded, no projector exists yet.
            yield return WaitForTicks(120);
            TickMetrics.Take(); FrameProbe.Take();
            yield return WaitForTicks(600);
            var baseline = TickMetrics.Take(); var baselineWork = FrameProbe.Take();
            Note("baseline (MGP loaded, nothing projected): " + baselineWork);

            var owner = WorldApi.TestIdentityId();
            var projectorType = WorldApi.FindSubtype(MyCubeSize.Large, "projector");
            var battery = WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock");
            var welderType = WorldApi.FindSubtype(MyCubeSize.Large, "shipwelder");
            for (var i = 0; i < Rigs; i++)
            {
                var site = Sites.Space(i);
                var origin = site.Origin + new Vector3D(0, 0, i * 5013); // off any regression grid columns
                Check(MyGravityProviderSystem(site.Origin), "site " + i + " must be outside natural gravity");
                _rigs[i] = new Rig { Origin = origin, LastProgress = DateTime.UtcNow };
                var blueprint = WorldApi.LoadAuthoredGroup(MultigridProjectorWeldScenario.ResourceName, Prefix + "bp" + i);
                Check(blueprint.Count == 3, "fixture must contain a main grid and two chained subgrids");
                _rigs[i].Expected = blueprint.Select(g => g.CubeBlocks.Count).ToArray();
                _rigs[i].Blueprint = blueprint;
                var seed = WorldApi.GridOb(Prefix + "seed" + i, MyCubeSize.Large, true, origin, new[]
                {
                    new BlockSpec(battery, Vector3I.Zero),
                    new BlockSpec(projectorType, new Vector3I(1, 0, 0)),
                });
                var projectorOb = seed.CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>().Single();
                projectorOb.CustomName = Prefix + "repair" + i;
                projectorOb.ProjectedGrids = blueprint;
                projectorOb.KeepProjection = true;
                projectorOb.Scale = 1;
                _rigs[i].Seed = WorldApi.SpawnGrid(WithOwner(seed, owner));
                _spawned = true;
                Track(_rigs[i].Seed);
            }
            yield return WaitForTicks(120);
            // Stand rule: a grid that must run needs a real player nearby, not a disabled
            // freezer - one fake character per rig keeps its cluster awake.
            FakeClients.RemoveAll();
            FakeClients.Add(Rigs, new FakeClients.NetworkProfile { RttMs = 30 },
                i => (_rigs[i].Origin + new Vector3D(0, 30, -30), 0, 0), withCharacters: true);
            yield return Wait(() => Enumerable.Range(0, Rigs).All(i => FakeClients.HasLiveCharacter(i)), "a live fake player at every rig", 60);
            for (var i = 0; i < Rigs; i++)
            {
                var rig = _rigs[i];
                Check(rig.Seed.BlocksCount == 2, "seed must not already contain projected payload blocks");
                rig.Projector = WorldApi.FindFunctional<MyProjectorBase>(rig.Seed);
                Check(rig.Projector != null, "no projector on seed " + i);
                WorldApi.EnsureDistributor(rig.Seed);
            }
            yield return Wait(() =>
            {
                var all = true;
                for (var i = 0; i < Rigs; i++)
                {
                    WorldApi.ChargeBatteries(_rigs[i].Seed);
                    if ((int)Api("GetSubgridCount", _rigs[i].Projector.EntityId) != 3) all = false;
                }
                return all;
            }, "all three MGP projections scanned", 120);
            for (var i = 0; i < Rigs; i++)
            {
                var rig = _rigs[i];
                for (var g = 0; g < 3; g++)
                {
                    rig.Previews[g] = Api("GetPreviewGrid", rig.Projector.EntityId, g) as MyCubeGrid;
                    Check(rig.Previews[g] != null && rig.Previews[g].IsPreview, "missing preview " + g + " on rig " + i);
                }
                var ship = WorldApi.SpawnGrid(WithOwner(WorldApi.GridOb(Prefix + "welder" + i, MyCubeSize.Large, false,
                    rig.Origin + new Vector3D(0, 10, -35), new[]
                    {
                        new BlockSpec(welderType, Vector3I.Zero),
                        new BlockSpec(battery, new Vector3I(1, 0, 1)),
                        new BlockSpec(battery, new Vector3I(-1, 0, 1)),
                    }, forward: Vector3.Backward, up: Vector3.Up), owner));
                Track(ship);
                rig.Ship = ship;
            }
            yield return WaitForTicks(120);
            for (var i = 0; i < Rigs; i++)
            {
                var rig = _rigs[i];
                WorldApi.EnsureDistributor(rig.Ship);
                WorldApi.ChargeBatteries(rig.Ship);
                rig.Welder = WorldApi.FindFunctional<SpaceWelder>(rig.Ship);
                Check(rig.Welder != null && rig.Welder.OwnerId == owner && rig.Welder.BuiltBy == owner, "welder " + i + " not prepared");
                rig.Welder.Enabled = false;
            }
            // Stock every rig before arming any; same exact scaffold-accounted payment as the single-weld scenario.
            for (var i = 0; i < Rigs; i++)
            {
                var rig = _rigs[i];
                var missingBuilders = rig.Blueprint.SelectMany((g, gi) => g.CubeBlocks.Where(b =>
                    gi != 0 || (!(b is MyObjectBuilder_ProjectorBase) && b.SubtypeName != battery))).ToList();
                rig.Costs = WorldApi.ComponentsNeeded(missingBuilders, 1);
                foreach (var head in rig.Previews.Skip(1).SelectMany(g => g.CubeBlocks).Where(b => b.FatBlock is MyAttachableTopBlockBase))
                {
                    var remaining = new Dictionary<string, int>();
                    new MyComponentStack(head.BlockDefinition, 0.01f, 0.01f).GetMissingComponents(remaining);
                    foreach (var component in head.BlockDefinition.Components.GroupBy(c => c.Definition.Id.SubtypeName))
                    {
                        var missing = remaining.TryGetValue(component.Key, out var count) ? count : 0;
                        rig.Costs[component.Key] -= component.Sum(c => c.Count) - missing;
                    }
                }
                var needs = new Dictionary<string, int>(rig.Costs);
                foreach (var key in needs.Keys.ToList()) needs[key] += Math.Max(4, needs[key] / 5);
                WorldApi.StockComponents(rig.Welder.GetInventory(), needs);
                rig.Stock0 = needs.Keys.ToDictionary(k => k, k => WorldApi.CountComponent(rig.Welder.GetInventory(), k));
            }
            // Idle projection window: three MGP repair projections live, welders still off.
            TickMetrics.Take(); FrameProbe.Take();
            yield return WaitForTicks(600);
            var idle = TickMetrics.Take(); var idleWork = FrameProbe.Take();
            Note("idle projection (3 active MGP repair projections, no welding): " + idleWork);

            // Arm all three welders on the same tick.
            for (var i = 0; i < Rigs; i++)
            {
                _rigs[i].Welder.Enabled = true;
                WorldApi.ChargeBatteries(_rigs[i].Ship);
                if (!WorldApi.ToolIsActivated(_rigs[i].Welder)) WorldApi.ToolStartShooting(_rigs[i].Welder);
            }
            TickMetrics.Take(); FrameProbe.Take();
            var doneCount = 0; var lastLog = DateTime.UtcNow;
            while (doneCount < Rigs)
            {
                doneCount = 0;
                for (var i = 0; i < Rigs; i++)
                {
                    var rig = _rigs[i];
                    if (rig.Finished >= rig.Expected.Sum()) { doneCount++; continue; }
                    for (var g = 0; g < 3; g++) { rig.Built[g] = Api("GetBuiltGrid", rig.Projector.EntityId, g) as MyCubeGrid; Track(rig.Built[g]); }
                    var finished = rig.Built.Sum(g => g == null ? 0 : g.CubeBlocks.Count(b => b.IsFullIntegrity));
                    rig.Finished = finished;
                    var integrity = rig.Built.Sum(g => g == null ? 0 : g.CubeBlocks.Sum(b => (double)b.Integrity / b.MaxIntegrity));
                    if (integrity > rig.BestIntegrity + 0.0001) { rig.BestIntegrity = integrity; rig.LastProgress = DateTime.UtcNow; }
                    WorldApi.ChargeBatteries(rig.Seed);
                    WorldApi.ChargeBatteries(rig.Ship);
                    if (TicksRun % 60 == 0) WorldApi.EnsureDistributor(rig.Seed);
                    var sensor = WorldApi.SensorSphere(rig.Welder);
                    var gap = Math.Min(1.5, sensor.Radius * 0.65);
                    var sensorOffset = sensor.Center - rig.Ship.PositionComp.GetPosition();
                    var target = rig.Built.Where(g => g != null).SelectMany(g => g.CubeBlocks)
                        .Where(b => !b.IsFullIntegrity).OrderBy(b => Vector3D.DistanceSquared(b.WorldAABB.Center, sensor.Center)).FirstOrDefault()
                        ?? rig.Previews.SelectMany(g => g.CubeBlocks)
                            .Where(b => rig.Projector.CanBuild(b, true) == BuildCheckResult.OK)
                            .OrderBy(b => Vector3D.DistanceSquared(b.WorldAABB.Center, rig.Ship.PositionComp.GetPosition())).FirstOrDefault();
                    if (target != null)
                    {
                        var face = target.WorldAABB.Center;
                        face.Z = target.WorldAABB.Min.Z - gap;
                        var desired = (face - sensorOffset - rig.Ship.PositionComp.GetPosition()) * 1.5;
                        if (desired.Length() > 12) desired = Vector3D.Normalize(desired) * 12;
                        rig.Ship.Physics.LinearVelocity = Vector3.Lerp(rig.Ship.Physics.LinearVelocity, (Vector3)desired, 0.2f);
                        rig.Ship.Physics.AngularVelocity = Vector3.Zero;
                        rig.Ship.Physics.Activate();
                    }
                    else rig.Ship.Physics.LinearVelocity = Vector3.Zero;
                    Check((DateTime.UtcNow - rig.LastProgress).TotalSeconds < 150,
                        "rig " + i + " stalled at " + finished + "/" + rig.Expected.Sum());
                }
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 8)
                {
                    lastLog = DateTime.UtcNow;
                    Note("welding: " + string.Join(" | ", _rigs.Select((r, i) => "rig" + i + " " + r.Finished + "/" + r.Expected.Sum())));
                }
                yield return null;
            }
            var welding = TickMetrics.Take(); var weldingWork = FrameProbe.Take();
            for (var i = 0; i < Rigs; i++) { _rigs[i].Welder.Enabled = false; _rigs[i].Ship.Physics.LinearVelocity = Vector3.Zero; }

            for (var i = 0; i < Rigs; i++)
            {
                var rig = _rigs[i];
                var total = rig.Expected.Sum();
                Check(rig.Built.All(g => g != null && !g.IsPreview && g.Physics != null && !g.MarkedForClose), "rig " + i + " lost a grid");
                Check(rig.Built.Select(g => g.EntityId).Distinct().Count() == 3, "rig " + i + " subgrids not three distinct physical grids");
                for (var g = 0; g < 3; g++)
                    Check(rig.Built[g].BlocksCount == rig.Expected[g] && rig.Built[g].CubeBlocks.All(b => b.IsFullIntegrity),
                        "rig " + i + " grid " + g + " wrong count/incomplete");
                for (var c = 0; c < 2; c++)
                {
                    var piston = rig.Built[c].GetFatBlocks().OfType<MyPistonBase>().SingleOrDefault();
                    Check(piston != null && piston.TopGrid == rig.Built[c + 1], "rig " + i + " mechanical link " + c + " broken");
                }
                foreach (var component in rig.Costs)
                {
                    var used = rig.Stock0[component.Key] - WorldApi.CountComponent(rig.Welder.GetInventory(), component.Key);
                    Check(used == component.Value, "rig " + i + " payment " + component.Key + ": actual=" + used + " expected=" + component.Value);
                }
            }
            var delta = weldingWork.Contains("avg=") && baselineWork.Contains("avg=") ? ComputeDelta(baselineWork, weldingWork) : "n/a";
            Note("CONCURRENT 3-CLUSTER RESULT | baseline: " + baselineWork);
            Note("idle projection: " + idleWork);
            Note("welding x3: " + weldingWork);
            Note("PASS: 3 independent MGP welds (15 blocks / 3 grids each) completed in separate clusters simultaneously; all components paid exactly; busy-frame delta welding-vs-baseline " + delta);
        }

        private static bool MyGravityProviderSystem(Vector3D point) =>
            Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(point).Length() < 0.05;

        private static string ComputeDelta(string baseline, string welding)
        {
            double Busy(string s)
            {
                var part = s.Split(' ').First(p => p.StartsWith("avg="));
                return double.Parse(part.Substring(4).TrimEnd('m', 's'), System.Globalization.CultureInfo.InvariantCulture);
            }
            return (Busy(welding) - Busy(baseline)).ToString("F2") + " ms/frame";
        }

        private static MyObjectBuilder_CubeGrid WithOwner(MyObjectBuilder_CubeGrid grid, long owner)
        {
            foreach (var block in grid.CubeBlocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
            }
            return grid;
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            if (!_spawned) return;
            var owned = new HashSet<MyCubeGrid>(Tracked.OfType<MyCubeGrid>().Concat(_rigs.Where(r => r != null)
                .SelectMany(r => r.Built).Where(g => g != null)));
            var queue = owned.ToList();
            for (var i = 0; i < queue.Count; i++)
                foreach (var connection in queue[i].GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>())
                    if (connection.TopGrid != null && owned.Add(connection.TopGrid)) queue.Add(connection.TopGrid);
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                if (!grid.MarkedForClose && (owned.Contains(grid) ||
                    (grid.DisplayName ?? "").StartsWith(Prefix, StringComparison.Ordinal)))
                    grid.Close();
        }
    }
}
