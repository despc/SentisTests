using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.Game.SessionComponents;
using Sandbox.ModAPI.Interfaces;
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
    /// Survival construction through an activated ship welder: main grid -> piston -> subgrid
    /// -> piston -> payload. Only the seed and tool are spawned; neither subgrid is pasted.
    /// After welding, remove the hologram and operate both real, newly built pistons both ways.
    /// </summary>
    public sealed class MultigridProjectorWeldScenario : TestScenario
    {
        public const string ScenarioName = "multigrid_projector_weld";
        public const string ResourceName = "SentisTests.Resources.MultigridProjection.xml";
        private const string Prefix = "ST-47351-mgp-";
        private readonly MyCubeGrid[] _built = new MyCubeGrid[3];
        private readonly MyCubeGrid[] _previews = new MyCubeGrid[3];
        private Vector3D _origin;
        private bool _spawned;
        private object _api;
        private Type _apiType;

        public override string Name => ScenarioName;
        public override string[] Requires => new[] { Integrations.MultigridProjector };
        public override int TimeoutSeconds => 600;

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
            _origin = (TestRunner.RunOrigin ?? Sites.Space(0).Origin) + Shift;
            Check(Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(_origin).Length() < 0.05,
                "the construction site must be outside natural gravity");

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 },
                i => (_origin + new Vector3D(0, 30, -30), 0, 0), withCharacters: true);
            yield return WaitForTicks(120);
            Check(FakeClients.Character(0) != null, "the nearby player has no character");
            var blueprint = WorldApi.LoadAuthoredGroup(ResourceName, Prefix + "blueprint");
            Check(blueprint.Count == 3, "fixture must contain a main grid and two chained subgrids");
            var expected = blueprint.Select(g => g.CubeBlocks.Count).ToArray();
            var totalExpected = expected.Sum();
            var armor = WorldApi.FindSubtype(MyCubeSize.Large, "blockarmorblock");
            var battery = WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock");
            var owner = WorldApi.TestIdentityId();
            var projectorType = WorldApi.FindSubtype(MyCubeSize.Large, "projector");
            var welderType = WorldApi.FindSubtype(MyCubeSize.Large, "shipwelder");
            // Only two real seed blocks. Blueprint grids are NEVER passed to SpawnGrid.
            var seed = WorldApi.GridOb(Prefix + "seed", MyCubeSize.Large, true, _origin, new[]
            {
                new BlockSpec(battery, Vector3I.Zero),
                new BlockSpec(projectorType, new Vector3I(1, 0, 0)),
            });
            var projectorOb = seed.CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>().Single();
            projectorOb.CustomName = "MGP test Repair";
            projectorOb.ProjectedGrids = blueprint;
            projectorOb.KeepProjection = true;
            projectorOb.Scale = 1;
            _built[0] = WorldApi.SpawnGrid(WithOwner(seed, owner));
            _spawned = true;
            Track(_built[0]);
            yield return WaitForTicks(60);
            var root = _built[0];
            Check(root.BlocksCount == 2, "seed must not already contain any projected payload blocks");
            var projector = WorldApi.FindFunctional<MyProjectorBase>(root);
            Check(projector != null, "no projector on seed");
            var distributor = WorldApi.EnsureDistributor(root);
            yield return Wait(() =>
            {
                WorldApi.ChargeBatteries(root);
                distributor.MarkForUpdate();
                distributor.UpdateBeforeSimulation();
                return projector.IsWorking && (int)Api("GetSubgridCount", projector.EntityId) == 3;
            }, "MGP has loaded and scanned all three projected grids", 90);
            for (var i = 0; i < 3; i++)
            {
                _previews[i] = Api("GetPreviewGrid", projector.EntityId, i) as MyCubeGrid;
                Check(_previews[i] != null && _previews[i].IsPreview, "missing preview " + i);
                Check(_previews[i].BlocksCount == expected[i], "preview " + i + " differs from fixture");
            }
            Check(Api("GetBuiltGrid", projector.EntityId, 1) == null && Api("GetBuiltGrid", projector.EntityId, 2) == null,
                "subgrids already physically exist before welding");
            Note("projection ready: " + string.Join(" + ", expected) + " blocks on 3 grids; only 2 seed blocks exist");

            var ship = WorldApi.SpawnGrid(WithOwner(WorldApi.GridOb(Prefix + "welder", MyCubeSize.Large, false,
                _origin + new Vector3D(0, 10, -35), new[]
                {
                    new BlockSpec(welderType, Vector3I.Zero),
                    new BlockSpec(battery, new Vector3I(1, 0, 1)),
                    new BlockSpec(battery, new Vector3I(-1, 0, 1)),
                }, forward: Vector3.Backward, up: Vector3.Up), owner));
            Track(ship);
            yield return WaitForTicks(90);
            WorldApi.EnsureDistributor(ship);
            WorldApi.ChargeBatteries(ship);
            var welder = WorldApi.FindFunctional<SpaceWelder>(ship);
            Check(welder != null, "no ship welder");
            Check(welder.OwnerId == owner && welder.BuiltBy == owner, "welder must belong to the prepared Survival builder");
            foreach (var block in _previews.SelectMany(g => g.CubeBlocks))
                Check(!MySession.Static.ResearchEnabled || MySessionComponentResearch.Static.CanUse(owner, block.BlockDefinition.Id),
                    "builder research does not allow " + block.BlockDefinition.Id);
            welder.Enabled = false;
            // Setup stocks exactly the missing workload + 20% reserve, once, before arming.
            var missingBuilders = blueprint.SelectMany((g, i) => g.CubeBlocks.Where(b =>
                i != 0 || (!(b is MyObjectBuilder_ProjectorBase) && b.SubtypeName != battery))).ToList();
            var expectedCosts = WorldApi.ComponentsNeeded(missingBuilders, 1);
            // MGP BuildMissingHead creates a 1% scaffold without charging the welder. Use
            // the real game stack to account for exactly those already mounted components.
            foreach (var head in _previews.Skip(1).SelectMany(g => g.CubeBlocks).Where(b => b.FatBlock is MyAttachableTopBlockBase))
            {
                var remaining = new Dictionary<string, int>();
                new MyComponentStack(head.BlockDefinition, 0.01f, 0.01f).GetMissingComponents(remaining);
                foreach (var component in head.BlockDefinition.Components.GroupBy(c => c.Definition.Id.SubtypeName))
                {
                    var missing = remaining.TryGetValue(component.Key, out var count) ? count : 0;
                    expectedCosts[component.Key] -= component.Sum(c => c.Count) - missing;
                }
            }
            var needs = new Dictionary<string, int>(expectedCosts);
            foreach (var key in needs.Keys.ToList()) needs[key] += Math.Max(4, needs[key] / 5);
            WorldApi.StockComponents(welder.GetInventory(), needs);
            foreach (var item in needs)
                Check(WorldApi.CountComponent(welder.GetInventory(), item.Key) >= item.Value,
                    "welder inventory cannot hold the planned " + item.Key + " x" + item.Value);
            var stock0 = needs.Keys.ToDictionary(k => k, k => WorldApi.CountComponent(welder.GetInventory(), k));
            var steel0 = WorldApi.CountComponent(welder.GetInventory(), "SteelPlate");
            var motors0 = WorldApi.CountComponent(welder.GetInventory(), "Motor");
            var armorSteel = WorldApi.ComponentsNeeded(missingBuilders.Where(b => b.SubtypeName == armor), 1)["SteelPlate"];
            var sensor = WorldApi.SensorSphere(welder);
            Check(sensor.Radius > 0 && sensor.Radius < 5, "scenario requires normal, unscaled welder reach");
            var sensorOffset = sensor.Center - ship.PositionComp.GetPosition();
            var gap = Math.Min(1.5, sensor.Radius * 0.65);
            Note("welder stocked once; sensor radius=" + sensor.Radius.ToString("F2") + " m; starting real construction");
            welder.Enabled = true;
            yield return Wait(() => { WorldApi.ChargeBatteries(ship); return welder.IsWorking; }, "welder working", 30);
            if (!WorldApi.ToolIsActivated(welder)) WorldApi.ToolStartShooting(welder);

            var lastProgress = DateTime.UtcNow;
            var lastLog = DateTime.UtcNow;
            var bestIntegrity = 0.0;
            while (true)
            {
                for (var i = 0; i < 3; i++)
                {
                    _built[i] = Api("GetBuiltGrid", projector.EntityId, i) as MyCubeGrid;
                    Track(_built[i]);
                }
                Check(_built[0] == root, "MGP lost the main built grid");
                var finished = _built.Sum(g => g == null ? 0 : g.CubeBlocks.Count(b => b.IsFullIntegrity));
                var integrity = _built.Sum(g => g == null ? 0 : g.CubeBlocks.Sum(b => (double)b.Integrity / b.MaxIntegrity));
                if (integrity > bestIntegrity + 0.0001)
                {
                    bestIntegrity = integrity;
                    lastProgress = DateTime.UtcNow;
                }
                if (finished == totalExpected) break;
                WorldApi.ChargeBatteries(root);
                WorldApi.ChargeBatteries(ship);
                if (TicksRun % 60 == 0) WorldApi.EnsureDistributor(root);
                MySlimBlock target = _built.Where(g => g != null).SelectMany(g => g.CubeBlocks)
                    .Where(b => !b.IsFullIntegrity).OrderBy(b => Vector3D.DistanceSquared(b.WorldAABB.Center, sensor.Center)).FirstOrDefault();
                if (target == null)
                    target = _previews.SelectMany(g => g.CubeBlocks)
                        .Where(b => projector.CanBuild(b, true) == BuildCheckResult.OK)
                        .OrderBy(b => Vector3D.DistanceSquared(b.WorldAABB.Center, ship.PositionComp.GetPosition())).FirstOrDefault();
                if (target != null)
                {
                    var face = target.WorldAABB.Center;
                    face.Z = target.WorldAABB.Min.Z - gap;
                    var delta = face - sensorOffset - ship.PositionComp.GetPosition();
                    var desired = delta * 1.5;
                    if (desired.Length() > 12) desired = Vector3D.Normalize(desired) * 12;
                    ship.Physics.LinearVelocity = Vector3.Lerp(ship.Physics.LinearVelocity, (Vector3)desired, 0.2f);
                    ship.Physics.AngularVelocity = Vector3.Zero;
                    ship.Physics.Activate();
                }
                else ship.Physics.LinearVelocity = Vector3.Zero;
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 8)
                {
                    lastLog = DateTime.UtcNow;
                    Note("welding: " + finished + "/" + totalExpected + " finished; grids=" +
                         _built.Count(g => g != null) + "/3; steel=" + WorldApi.CountComponent(welder.GetInventory(), "SteelPlate"));
                }
                if ((DateTime.UtcNow - lastProgress).TotalSeconds >= 90)
                    Check(false, "welder stalled; finished=" + finished + "/" + totalExpected + "; " + WeldFailureDetails(welder, projector));
                yield return null;
            }
            welder.Enabled = false;
            ship.Physics.LinearVelocity = Vector3.Zero;
            VerifyStructure(expected);
            // Verify each blueprint cell, not merely an aggregate count or completion flag.
            for (var i = 0; i < 3; i++)
                foreach (var projected in _previews[i].CubeBlocks)
                {
                    var at = _built[i].WorldToGridInteger(_previews[i].GridIntegerToWorld(projected.Position));
                    var actual = _built[i].GetCubeBlock(at);
                    Check(actual != null && actual.BlockDefinition.Id == projected.BlockDefinition.Id && actual.IsFullIntegrity,
                        "grid " + i + " missing/wrong/unfinished blueprint cell " + projected.Position);
                }
            var steelUsed = steel0 - WorldApi.CountComponent(welder.GetInventory(), "SteelPlate");
            var motorsUsed = motors0 - WorldApi.CountComponent(welder.GetInventory(), "Motor");
            Check(steelUsed >= armorSteel, "welding did not pay even the armor cost: steel=" + steelUsed + "/" + armorSteel);
            Check(motorsUsed > 0, "both projected pistons were built without paying any motors");
            foreach (var component in expectedCosts)
            {
                var used = stock0[component.Key] - WorldApi.CountComponent(welder.GetInventory(), component.Key);
                Check(used == component.Value, "component payment " + component.Key + ": actual=" + used +
                    ", expected=" + component.Value + " (MGP 1% head scaffolds accounted for)");
            }
            Note("physical result verified: 3 connected grids, " + totalExpected + " full blocks; all components paid exactly; steel=" + steelUsed + ", motors=" + motorsUsed);

            projector.Enabled = false;
            yield return Wait(() => projector.ProjectedGrid == null, "hologram removed; only real grids remain", 45);
            var pistons = _built.SelectMany(g => g.GetFatBlocks()).OfType<MyPistonBase>().ToArray();
            Check(pistons.Length == 2, "expected two real pistons");
            foreach (var grid in _built) WorldApi.EnsureDistributor(grid);
            var starts = pistons.Select(p => ((Sandbox.ModAPI.Ingame.IMyPistonBase)p).CurrentPosition).ToArray();
            var directions = starts.Select(p => p > 2 ? -1f : 1f).ToArray();
            for (var i = 0; i < pistons.Length; i++) SetVelocity(pistons[i], directions[i]);
            yield return Wait(() =>
            {
                WorldApi.ChargeBatteries(root);
                VerifyConnections();
                return pistons.Select((p, i) => (((Sandbox.ModAPI.Ingame.IMyPistonBase)p).CurrentPosition - starts[i]) * directions[i] > 0.75f).All(x => x);
            }, "both welded pistons move at least 0.75 m without the projector", 30);
            for (var i = 0; i < pistons.Length; i++) SetVelocity(pistons[i], -directions[i]);
            yield return Wait(() =>
            {
                WorldApi.ChargeBatteries(root);
                VerifyConnections();
                var all = true;
                for (var i = 0; i < pistons.Length; i++)
                {
                    var back = (((Sandbox.ModAPI.Ingame.IMyPistonBase)pistons[i]).CurrentPosition - starts[i]) * directions[i] <= 0.05f;
                    if (back) SetVelocity(pistons[i], 0); else all = false;
                }
                return all;
            }, "both pistons return to their original extension", 30);
            VerifyStructure(expected);
            Note("PASS: " + totalExpected + " full blocks on 3 grids welded from projection; steel=" + steelUsed +
                 ", motors=" + motorsUsed + "; both pistons moved and returned with hologram OFF");
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

        private string WeldFailureDetails(SpaceWelder welder, MyProjectorBase projector)
        {
            var sphere = WorldApi.SensorSphere(welder);
            var candidates = _previews.SelectMany(g => g.CubeBlocks)
                .Where(b => projector.CanBuild(b, true) == BuildCheckResult.OK).ToArray();
            var text = "power=" + projector.IsWorking + "; activated=" + WorldApi.ToolIsActivated(welder) +
                "; nativeCandidates=" + WorldApi.ProbeProjectedBlocks(welder) + "; sensor=" + sphere.Center + "/" + sphere.Radius +
                "; candidates=" + string.Join(" | ", candidates.Select(b => b.BlockDefinition.Id + " pos=" + b.Position +
                    " min=" + b.Min + " max=" + b.Max + " box=" + b.WorldAABB + " intersects=" + b.WorldAABB.Intersects(sphere)));
            var optimisation = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Optimizer.Optimizations.WelderOptimization", false)).FirstOrDefault(t => t != null);
            if (optimisation != null)
            {
                var find = optimisation.GetMethod("FindProjectedBlocks", BindingFlags.Static | BindingFlags.NonPublic);
                text += "; optimizedCandidates=" + ((ICollection)find.Invoke(null, new object[] { welder })).Count;
                var diagnostics = optimisation.Assembly.GetType("Optimizer.Optimizations.WelderDiagnostics");
                text += "; " + diagnostics?.GetMethod("Snapshot")?.Invoke(null, null);
            }
            return text;
        }

        private static void SetVelocity(MyPistonBase piston, float value) =>
            ((Sandbox.ModAPI.IMyTerminalBlock)piston).SetValueFloat("Velocity", value);

        private void VerifyConnections()
        {
            for (var i = 0; i < 2; i++)
            {
                var piston = _built[i]?.GetFatBlocks().OfType<MyPistonBase>().SingleOrDefault();
                Check(piston != null && piston.TopGrid == _built[i + 1] && piston.TopBlock != null,
                    "mechanical link " + i + " -> " + (i + 1) + " is broken");
            }
        }

        private void VerifyStructure(int[] expected)
        {
            Check(_built.All(g => g != null && !g.IsPreview && g.Physics != null && !g.MarkedForClose), "a real grid is missing");
            Check(_built.Select(g => g.EntityId).Distinct().Count() == 3, "subgrids are not three distinct physical grids");
            for (var i = 0; i < 3; i++)
                Check(_built[i].BlocksCount == expected[i] && _built[i].CubeBlocks.All(b => b.IsFullIntegrity),
                    "grid " + i + " has wrong block count or incomplete integrity");
            VerifyConnections();
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        private HashSet<MyCubeGrid> DiscoverOwnedGrids()
        {
            var owned = new HashSet<MyCubeGrid>(Tracked.OfType<MyCubeGrid>().Concat(_built.Where(g => g != null)));
            var queue = owned.ToList();
            for (var i = 0; i < queue.Count; i++)
                foreach (var connection in queue[i].GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>())
                    if (connection.TopGrid != null && owned.Add(connection.TopGrid)) queue.Add(connection.TopGrid);
            return owned;
        }

        public override void CleanupLeftovers()
        {
            if (!_spawned) return;
            var owned = DiscoverOwnedGrids();
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                if (!grid.MarkedForClose && (owned.Contains(grid) ||
                    (grid.DisplayName ?? "").StartsWith(Prefix, StringComparison.Ordinal)))
                    grid.Close();
        }
    }
}
