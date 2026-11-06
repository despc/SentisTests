using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI.Ingame;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A ship that welds its own hydrogen torpedoes on racks (prod 06.11.2026, Greediness_LG_543: the torpedoes took
    /// 750 L/s each, stayed empty, and the ship's tank drained). The player's blueprint is spawned 20 km up with an
    /// observer by it, the ship's big tank filled, the torpedoes' emptied and set to stockpile (as the torpedoes' script
    /// does to refill them). Each torpedo tank is accounted every 10 frames: what it was given (its input over the time)
    /// against what it holds. Phase 1: as spawned. Phase 2: one torpedo taken off its rack whole (as after its launch)
    /// and built again from the rack's projection the way the welders build it (the projector's Build, welded up), its
    /// fresh tank set to stockpile: the same accounting. focus "bp=path" for another blueprint, "gasbatch=0" without
    /// SentisOptimisations' gas transfer batching, "torp=trpN" for another torpedo.
    /// </summary>
    public sealed class TorpedoRefillScenario : TestScenario
    {
        public const string ScenarioName = "torpedo_refill";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 360;

        private const string DefaultBlueprint = @"C:\Users\user\AppData\Roaming\SpaceEngineers\Blueprints\local\Greediness_LG_543\bp.sbc";
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private const string Prefix = "torprefill-";
        private readonly ConfigOverride _config = new ConfigOverride();

        private sealed class Account
        {
            public MyGasTank Tank;
            public double Given, Start;
        }

        public override IEnumerator Run()
        {
            var focus = TestRunner.Focus ?? "";
            var path = System.Text.RegularExpressions.Regex.Match(focus, @"bp=(\S+)") is var m && m.Success ? m.Groups[1].Value : DefaultBlueprint;
            var torp = System.Text.RegularExpressions.Regex.Match(focus, @"torp=(\S+)") is var t && t.Success ? t.Groups[1].Value : "trp1";
            WorldApi.EnsureUnpaused(Name);
            _config.Set("FreezerEnabled", false);
            if (focus.Contains("gasbatch=0")) _config.Set("GasTankOptimisation", false);
            Note("switches: " + focus);
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchor.Translation - planet.PositionComp.GetPosition());
            var site = anchor.Translation + up * 20000;
            foreach (var g in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && Vector3D.Distance(g.PositionComp.GetPosition(), site) < 3000).ToList()) g.Close();
            yield return WaitForTicks(2);
            FakeClients.Add(1, Network, i => (site + up * 150, 0, 0), withCharacters: true);
            yield return WaitForSeconds(5, "the observer arrives");

            var obs = WorldApi.LoadBlueprintFile(path, WorldApi.EntityPrefix + Prefix + "ship");
            var shift = site - (Vector3D)obs[0].PositionAndOrientation.Value.Position;
            foreach (var ob in obs)
            {
                var po = ob.PositionAndOrientation.Value;
                po.Position = (Vector3D)po.Position + shift;
                ob.PositionAndOrientation = po;
                ob.LinearVelocity = default;
                ob.AngularVelocity = default;
                ob.CreatePhysics = true;
            }
            var grids = obs.Select(WorldApi.SpawnGrid).ToList();
            foreach (var g in grids) Track(g);
            Note("spawned: " + string.Join(", ", grids.Select(g => g.DisplayName + " " + g.BlocksCount)));
            yield return WaitForSeconds(5, "the ship settles");

            List<MyCubeGrid> Group() => MyCubeGridGroups.Static.Logical.GetGroup(grids.First(g => !g.MarkedForClose))?.Nodes.Select(n => n.NodeData).Where(g => !g.MarkedForClose).ToList() ?? new List<MyCubeGrid>();
            List<MyGasTank> Tanks() => Group().SelectMany(g => g.GetFatBlocks().OfType<MyGasTank>()).Where(x => x.BlockDefinition.StoredGasId.SubtypeName == "Hydrogen").ToList();
            var big = Tanks().Where(x => x.CubeGrid.GridSizeEnum == MyCubeSize.Large).ToList();
            foreach (var x in big) ((Sandbox.ModAPI.IMyGasTank)x).ChangeFilledRatio(1, true);
            Note($"the ship's tanks: {big.Count}, torpedo tanks: {Tanks().Count(x => x.CubeGrid.GridSizeEnum == MyCubeSize.Small)}");

            string Describe(MyGasTank x)
            {
                var input = x.ResourceSink.CurrentInputByType(x.BlockDefinition.StoredGasId);
                var perFrame = (x.NeedsUpdate & MyEntityUpdateEnum.EACH_FRAME_AFTER) != 0;
                return $"{x.CustomName} ({x.EntityId}) on {x.CubeGrid.DisplayName}: {x.FilledRatio:F3}, in {input:F0} L/s, stockpile {((IMyGasTank)x).Stockpile}, working {x.IsWorking}, per-frame {perFrame}";
            }

            IEnumerator Watch(string phase, List<MyGasTank> tanks, double seconds)
            {
                var accounts = tanks.Select(x => new Account { Tank = x, Start = x.FilledRatio * x.Capacity }).ToList();
                var bigStart = big.Sum(x => x.FilledRatio * x.Capacity);
                var frames = (int)(seconds * 60);
                for (var f = 0; f < frames; f += 10)
                {
                    foreach (var a in accounts.Where(a => !a.Tank.MarkedForClose))
                        a.Given += a.Tank.ResourceSink.CurrentInputByType(a.Tank.BlockDefinition.StoredGasId) * 10 / 60.0;
                    yield return WaitForTicks(10);
                }
                var given = accounts.Sum(a => a.Given);
                var got = accounts.Sum(a => a.Tank.FilledRatio * a.Tank.Capacity - a.Start);
                Note($"{phase}: {accounts.Count} torpedo tanks given {given:F0} L, they hold {got:F0} L more; the ship's tanks gave {bigStart - big.Sum(x => x.FilledRatio * x.Capacity):F0} L (thrusters too)");
                foreach (var a in accounts) Note($"   given {a.Given:F0} L, holds {a.Tank.FilledRatio * a.Tank.Capacity - a.Start:F0} L more: {Describe(a.Tank)}");
                Check(given <= got + Math.Max(500, given * 0.05), $"{phase}: gas lost - the torpedo tanks were given {given:F0} L and hold {got:F0} L more");
            }

            // phase 1: as spawned, the torpedoes emptied and stockpiling
            var torps = Tanks().Where(x => x.CubeGrid.GridSizeEnum == MyCubeSize.Small).ToList();
            foreach (var x in torps)
            {
                ((Sandbox.ModAPI.IMyGasTank)x).ChangeFilledRatio(0, true);
                ((IMyGasTank)x).Stockpile = true;
            }
            yield return WaitForTicks(5);
            var w1 = Watch("as spawned", torps, 15);
            while (w1.MoveNext()) yield return w1.Current;

            // phase 2: one torpedo off its rack whole, built again from the projection
            var tag = "[Mouse(" + torp + ")]";
            var parts = Group().SelectMany(g => g.GetBlocks()).Where(b => b.FatBlock is Sandbox.ModAPI.Ingame.IMyTerminalBlock tb && tb.CustomName.StartsWith(tag)).ToList();
            Check(parts.Count > 0, "no blocks named " + tag);
            var rack = parts[0].CubeGrid;
            Note($"{tag}: {parts.Count} named blocks on {rack.DisplayName}");
            var projectors = Group().SelectMany(g => g.GetFatBlocks().OfType<MyProjectorBase>()).ToList();
            Note("projectors: " + string.Join("; ", projectors.Select(p => $"{p.CustomName} on {p.CubeGrid.DisplayName}, enabled {p.Enabled}, projecting {p.ProjectedGrid?.DisplayName ?? "nothing"} {p.ProjectedGrid?.CubeBlocks.Count ?? 0}")));
            // the whole torpedo: the blocks within the box of its named ones (armor, conveyors between them too)
            var box = BoundingBoxI.CreateInvalid();
            foreach (var b in parts) { box.Include(b.Min); box.Include(b.Max); }
            var cells = rack.GetBlocks().Where(b => box.Contains(b.Min) == ContainmentType.Contains && box.Contains(b.Max) == ContainmentType.Contains).Select(b => b.Min).Distinct().ToList();
            Note($"the torpedo's box {box.Min}..{box.Max}: {cells.Count} blocks taken off");
            rack.RazeBlocks(cells);
            yield return WaitForSeconds(3, "the torpedo taken off");
            // the projector whose projection now misses blocks: the one that rebuilds this rack
            MyProjectorBase projector = null;
            var most = 0;
            foreach (var p in projectors.Where(p => p.ProjectedGrid != null))
            {
                var buildable = p.ProjectedGrid.CubeBlocks.Count(b => p.CanBuild(b, true) == Sandbox.ModAPI.BuildCheckResult.OK);
                Note($"   {p.CustomName} on {p.CubeGrid.DisplayName}: {buildable} buildable");
                if (buildable > most) { most = buildable; projector = p; }
            }
            Check(projector != null, "no projector can build anything after the torpedo was taken off");
            Note($"building with {projector.CustomName} on {projector.CubeGrid.DisplayName}");
            var owner = projector.OwnerId;
            var built = 0;
            for (var round = 0; round < 40; round++)
            {
                var preview = projector.ProjectedGrid;
                if (preview == null) break;
                var ready = preview.CubeBlocks.Where(b => projector.CanBuild(b, true) == Sandbox.ModAPI.BuildCheckResult.OK).ToList();
                if (ready.Count == 0) break;
                foreach (var block in ready)
                {
                    projector.Build(block, owner, projector.EntityId, true, owner);
                    built++;
                }
                yield return WaitForTicks(10);
            }
            // what was built unfinished is welded up as a welder does
            var inventory = new Sandbox.Game.MyInventory(100000f, 10000000f, Vector3.One, 0);
            var unfinished = 0;
            foreach (var slim in Group().SelectMany(g => g.GetBlocks()).Where(b => b.FatBlock is Sandbox.ModAPI.Ingame.IMyTerminalBlock tb && tb.CustomName.StartsWith(tag) && !b.IsFullIntegrity).ToList()
                         .Concat(rack.GetBlocks().Where(b => box.Contains(b.Min) == ContainmentType.Contains && !b.IsFullIntegrity)).Distinct().ToList())
            {
                unfinished++;
                for (var i = 0; i < 100 && !slim.IsFullIntegrity; i++)
                {
                    inventory.Clear();
                    foreach (var component in slim.BlockDefinition.Components)
                        inventory.AddItems(component.Count, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(component.Definition.Id));
                    slim.MoveItemsToConstructionStockpile(inventory);
                    slim.IncreaseMountLevel(slim.MaxIntegrity, owner, inventory, 1f, false);
                }
            }
            yield return WaitForSeconds(2, "the torpedo built");
            var fresh = Tanks().Where(x => ((Sandbox.ModAPI.Ingame.IMyTerminalBlock)x).CustomName.StartsWith(tag)).ToList();
            Note($"built {built} blocks from the projection, {unfinished} welded up; the torpedo's tanks now: " + string.Join("; ", fresh.Select(Describe)));
            Check(fresh.Count > 0, "the torpedo's tank was not built again");
            foreach (var x in fresh) ((IMyGasTank)x).Stockpile = true;
            yield return WaitForTicks(5);
            foreach (var x in fresh)
            {
                var gas = x.BlockDefinition.StoredGasId;
                var linked = big.Select(b => ((VRage.Game.ModAPI.Ingame.IMyInventory)x.GetInventory()).IsConnectedTo(b.GetInventory())).ToList();
                Note($"the fresh tank: required {x.ResourceSink.RequiredInputByType(gas):F0} L/s, input {x.ResourceSink.CurrentInputByType(gas):F0}, conveyors to the ship's tanks {string.Join(",", linked)}, " +
                     $"its grid's distributor: {x.CubeGrid.GridSystems.ResourceDistributor?.ResourceStateByType(gas)}, the ship tank's grid's: {big[0].CubeGrid.GridSystems.ResourceDistributor?.ResourceStateByType(gas)}");
                var conn = x.CubeGrid.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyShipConnector>().Where(c => ((Sandbox.ModAPI.Ingame.IMyTerminalBlock)c).CustomName.StartsWith(tag)).ToList();
                foreach (var c in conn) Note($"   its connector {((Sandbox.ModAPI.Ingame.IMyTerminalBlock)c).CustomName}: {((Sandbox.ModAPI.Ingame.IMyShipConnector)c).Status}");
            }
            // the torpedo's connector locked to the rack's, as its script does to refuel it
            foreach (var c in fresh.SelectMany(x => x.CubeGrid.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyShipConnector>()).Where(c => ((Sandbox.ModAPI.Ingame.IMyTerminalBlock)c).CustomName.StartsWith(tag)).Distinct())
            {
                ((Sandbox.ModAPI.Ingame.IMyShipConnector)c).Connect();
                Note($"   locked {((Sandbox.ModAPI.Ingame.IMyTerminalBlock)c).CustomName}: {((Sandbox.ModAPI.Ingame.IMyShipConnector)c).Status}, to {(c.Other == null ? "nothing" : ((Sandbox.ModAPI.Ingame.IMyTerminalBlock)c.Other).CustomName + " on " + c.Other.CubeGrid.DisplayName + (c.Other.CubeGrid == c.CubeGrid ? " (the same grid)" : ""))}");
            }
            yield return WaitForTicks(30);
            foreach (var x in fresh)
            {
                var gas = x.BlockDefinition.StoredGasId;
                Note($"the fresh tank locked: required {x.ResourceSink.RequiredInputByType(gas):F0} L/s, input {x.ResourceSink.CurrentInputByType(gas):F0}, conveyors to the ship's tanks {string.Join(",", big.Select(b => ((VRage.Game.ModAPI.Ingame.IMyInventory)x.GetInventory()).IsConnectedTo(b.GetInventory())))}");
            }
            var any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var batches = Integrations.TypeOf("SentisOptimisations", "SentisOptimisationsPlugin.Perfomance.GasTankOptimisations")?.GetField("Batches", any)?.GetValue(null) as System.Collections.IDictionary;
            foreach (var x in fresh)
            {
                var twins = MyEntities.GetEntities().OfType<MyCubeGrid>().SelectMany(g => g.GetFatBlocks()).Where(b => b.EntityId == x.EntityId && b != x)
                    .Select(b => b.GetType().Name + " on " + b.CubeGrid.DisplayName + (b.CubeGrid.Physics == null ? " (no physics: a projection)" : "")).ToList();
                var ghosts = projector.ProjectedGrid?.GetFatBlocks().Where(b => b.EntityId == x.EntityId).Count() ?? -1;
                Note($"   the fresh tank's id {x.EntityId}: other blocks with it: {(twins.Count == 0 ? "none" : string.Join("; ", twins))}; in the projection {ghosts}; batches kept {batches?.Count}");
            }
            for (var k = 0; k < 4; k++)
            {
                foreach (var x in fresh)
                {
                    var gas = x.BlockDefinition.StoredGasId;
                    object batch = batches != null && batches.Contains(x.EntityId) ? batches[x.EntityId] : null;
                    string Field(object o, string f) => o?.GetType().GetField(f, any)?.GetValue(o)?.ToString() ?? "-";
                    Note($"   fresh {k * 2} s: {x.FilledRatio:F4}, in {x.ResourceSink.CurrentInputByType(gas):F1}, out {x.Components.Get<Sandbox.Game.EntityComponents.MyResourceSourceComponent>()?.CurrentOutputByType(gas):F1}, " +
                         $"prevSum {Field(x, "m_prevSumTotalTransfer")}, oldInLitres {Field(x, "m_oldInLitres")}, batch {(batch == null ? "none" : Field(batch, "Count") + " x " + Field(batch, "Amount") + " since " + Field(batch, "Since"))}, update {x.NeedsUpdate}");
                }
                yield return WaitForSeconds(2, "watching the fresh tank");
            }
            var w2 = Watch("a fresh torpedo", fresh, 20);
            while (w2.MoveNext()) yield return w2.Current;
            Note("done");
        }

        public override void Cleanup()
        {
            try { _config.Restore(); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
