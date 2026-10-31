using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using SentisTests.Regression;
using VRage.Game;
using VRage.Network;
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Acceptance of the installed BuildInternal patch chain, not a call to a guard helper.
    /// AsClient installs a real non-local MyEventContext but replays the handler in-process:
    /// no packet serialization, transport, replicable-rights dispatch or client UI is tested.
    /// The mechanical assembly is deliberately PRECONSTRUCTED for this trust fixture only.
    /// multigrid_projector_weld separately proves Survival welding without copying subgrids.
    /// </summary>
    public sealed class MultigridProjectorTrustScenario : TestScenario
    {
        public const string ScenarioName = "multigrid_projector_trust";
        private const string Prefix = "ST-47351-mgp-trust-";
        private readonly MyCubeGrid[] _built = new MyCubeGrid[3];
        private object _api;
        private MethodInfo _handler;
        private FieldInfo _refused;
        private MyProjectorBase _projector;
        private long _owner;
        private double _reach;
        private bool _clientsCreated;
        private readonly Vector3I[] _targets = { Vector3I.Zero, new Vector3I(-1, 0, 0), new Vector3I(1, 2, 0) };

        public override string Name => ScenarioName;
        public override string[] Requires => new[] { Integrations.MultigridProjector, Integrations.Anticheat };
        public override int TimeoutSeconds => 180;

        private object Api(string name, params object[] args)
        {
            var method = _api.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Check(method != null, "incompatible MGP API: " + name);
            return method.Invoke(_api, args);
        }

        private long Refused() => (long)_refused.GetValue(null);

        public override IEnumerator Run()
        {
            RequirePlugin(Requires);
            WorldApi.EnsureUnpaused(Name);
            Check(!MySession.Static.CreativeMode, "trust acceptance requires real Survival");
            var trust = RequireType(Integrations.TypeOf(Integrations.Anticheat, "SentisAnticheat.BuildTrustCheck"), "BuildTrustCheck");
            _refused = trust.GetField("Refused", BindingFlags.Public | BindingFlags.Static);
            Check(_refused != null && _refused.FieldType == typeof(long), "missing BuildTrustCheck.Refused counter");
            var reach = trust.GetField("Reach", BindingFlags.Public | BindingFlags.Static);
            Check(reach != null, "missing BuildTrustCheck.Reach");
            _reach = Convert.ToDouble(reach.GetValue(null));
            Check(_reach > 5 && _reach < 200, "unsupported trust reach " + _reach);
            _handler = typeof(MyProjectorBase).GetMethod("BuildInternal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Vector3I), typeof(long), typeof(long), typeof(bool), typeof(long) }, null);
            Check(_handler != null, "unsupported MyProjectorBase.BuildInternal signature");
            var provider = RequireType(Integrations.TypeOf(Integrations.MultigridProjector,
                "MultigridProjector.Logic.MultigridProjectorApiProvider"), "MGP API provider");
            _api = provider.GetProperty("Api", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            Check(_api != null, "MGP API missing");
            var origin = (TestRunner.RunOrigin ?? Sites.Space(0).Origin) + Shift;
            FakeClients.RemoveAll();
            _clientsCreated = true;
            FakeClients.Add(2, new FakeClients.NetworkProfile { RttMs = 30 },
                i => (origin + new Vector3D(i * 5, 30, -30), 0, 0), withCharacters: true);
            yield return Wait(() => FakeClients.HasLiveCharacter(0) && FakeClients.HasLiveCharacter(1), "two live fake players", 30);
            _owner = FakeClients.PlayerOf(0).Identity.IdentityId;
            WorldApi.PrepareTestIdentity(_owner);
            Check(!MySession.Static.HasPlayerCreativeRights(FakeClients.PlayerOf(0).Id.SteamId), "sender has creative rights; guard would be bypassed");
            var body = FakeClients.Character(0);
            FakeClients.PlayerOf(0).Controller.TakeControl(body);
            Check(MySession.Static.Players.GetControllingPlayer(body)?.Identity?.IdentityId == _owner,
                "the sender must control its legitimate builder character");

            var blueprint = WorldApi.LoadAuthoredGroup(MultigridProjectorWeldScenario.ResourceName, Prefix + "preview");
            Check(blueprint.Count == 3, "expected chained three-grid blueprint");
            // A new armor cell attached to head 1 avoids requesting its already-existing piston.
            blueprint[1].CubeBlocks.Add(new MyObjectBuilder_CubeBlock
            {
                SubtypeName = "LargeBlockArmorBlock", Min = _targets[1],
                BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up),
            });
            var physical = WorldApi.CopyGroup(blueprint.Select(g => (MyObjectBuilder_CubeGrid)g.Clone()).ToList(), Prefix + "physical", true);
            var translation = origin - physical[0].PositionAndOrientation.Value.Position;
            for (var index = 0; index < 3; index++)
            {
                foreach (var block in blueprint[index].CubeBlocks) { block.Owner = _owner; block.BuiltBy = _owner; }
                var grid = physical[index];
                grid.Name = grid.DisplayName = Prefix + index;
                var transform = grid.PositionAndOrientation.Value;
                transform.Position += translation;
                grid.PositionAndOrientation = transform;
                foreach (var block in grid.CubeBlocks)
                {
                    block.Owner = _owner;
                    block.BuiltBy = _owner;
                    block.ShareMode = MyOwnershipShareModeEnum.None;
                }
                if (index > 0)
                    Check(grid.CubeBlocks.RemoveAll(b => (Vector3I)b.Min == _targets[index]) == 1, "fixture target is not unique on grid " + index);
            }
            var projectorOb = physical[0].CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>().Single();
            projectorOb.ProjectedGrids = blueprint;
            projectorOb.CustomName = "MGP trust Repair";
            projectorOb.KeepProjection = true;
            projectorOb.Enabled = true;
            // Fixture setup, NOT evidence of projected construction. Only the two removed cells
            // below can count as builds, and only via the installed runtime request handler.
            for (var index = 0; index < 3; index++) { _built[index] = WorldApi.SpawnGrid(physical[index]); Track(_built[index]); }
            yield return WaitForTicks(120);
            _projector = WorldApi.FindFunctional<MyProjectorBase>(_built[0]);
            Check(_projector != null && _projector.OwnerId == _owner, "projector ownership not prepared");
            WorldApi.EnsureDistributor(_built[0]);
            yield return Wait(() =>
            {
                WorldApi.ChargeBatteries(_built[0]);
                return _projector.IsWorking && (int)Api("GetSubgridCount", _projector.EntityId) == 3 &&
                       Enumerable.Range(0, 3).All(i => ReferenceEquals(Api("GetBuiltGrid", _projector.EntityId, i), _built[i]));
            }, "MGP repair projection bound to all three preconstructed fixture grids", 60);
            Check(_built.Select(g => g.EntityId).Distinct().Count() == 3, "fixture grids must be distinct");
            for (var i = 0; i < 2; i++)
                Check(_built[i].GetFatBlocks().OfType<MyPistonBase>().Single().TopGrid == _built[i + 1], "preconstructed fixture mechanical link missing");
            var needs = new Dictionary<string, int> { ["SteelPlate"] = 100 };
            WorldApi.StockComponents(body.GetInventory(), needs);
            Check(WorldApi.CountComponent(body.GetInventory(), "SteelPlate") >= 100, "character has insufficient component stock");
            Note("PRECONSTRUCTED mechanical trust fixture; context replay; NOT network transport; runtime BuildInternal patch chain armed");

            for (var index = 1; index <= 2; index++)
            {
                var preview = Api("GetPreviewGrid", _projector.EntityId, index) as MyCubeGrid;
                Check(preview != null && preview.IsPreview, "missing preview " + index);
                var target = preview.GetCubeBlock(_targets[index]);
                Check(target != null, "missing requested projected cell " + index);
                Check(!MySession.Static.ResearchEnabled || MySessionComponentResearch.Static.CanUse(_owner, target.BlockDefinition.Id), "target research not prepared");
                Check(target.BlockDefinition.DLCs == null || target.BlockDefinition.DLCs.Length == 0, "trust fixture must not require DLC");
                var at = preview.GridIntegerToWorld(target.Position);
                var cell = _built[index].WorldToGridInteger(at);
                Check(_built[index].GetCubeBlock(cell) == null, "target was already physically constructed");
                yield return Wait(() => _projector.CanBuild(target, true) == BuildCheckResult.OK, "subgrid " + index + " target buildable", 30);
                FakeClients.MoveTo(0, at + Vector3D.Backward * 3);
                Check(Vector3D.Distance(body.PositionComp.GetPosition(), at) < 4, "builder is not near actual subgrid target");
                yield return Denied("negative-index", target.Position, -1, _owner, body.EntityId);
                yield return Denied("out-of-range-index", target.Position, 3, _owner, body.EntityId);
                yield return Denied("nonexistent-cell", new Vector3I(100000, 100000, 100000), index, _owner, body.EntityId);
                yield return Denied("foreign-owner", target.Position, index, FakeClients.PlayerOf(1).Identity.IdentityId, body.EntityId);
                yield return Denied("foreign-builder", target.Position, index, _owner, FakeClients.Character(1).EntityId);
                FakeClients.MoveTo(0, at + Vector3D.Backward * 300);
                yield return Denied("remote-distance", target.Position, index, _owner, body.EntityId);
                // Strong coordinate-space discriminator: close to root's same numeric cell,
                // but beyond Reach from the requested preview. The wrong-grid check permits this.
                var rootPreview = (MyCubeGrid)Api("GetPreviewGrid", _projector.EntityId, 0);
                var wrongAt = rootPreview.GridIntegerToWorld(target.Position);
                Check(Vector3D.Distance(wrongAt, at) > 5, "fixture does not distinguish root/subgrid coordinate spaces");
                var rootNear = wrongAt + Vector3D.Normalize(wrongAt - at) * (_reach - 1);
                FakeClients.MoveTo(0, rootNear);
                Check(Vector3D.Distance(rootNear, wrongAt) < _reach && Vector3D.Distance(rootNear, at) > _reach,
                    "root-near/subgrid-far distance discriminator invalid");
                yield return Denied("root-near-subgrid-far", target.Position, index, _owner, body.EntityId);
                FakeClients.MoveTo(0, at + Vector3D.Backward * 3);
                var stock = WorldApi.CountComponent(body.GetInventory(), "SteelPlate");
                var before = Refused();
                var counts = _built.Select(g => g.BlocksCount).ToArray();
                Request(target.Position, index, _owner, body.EntityId);
                yield return Wait(() => _built[index].GetCubeBlock(cell) != null, "legitimate hand request materialized on subgrid " + index, 10);
                var actual = _built[index].GetCubeBlock(cell);
                Check(actual.BlockDefinition.Id == target.BlockDefinition.Id && !actual.CubeGrid.IsPreview && actual.CubeGrid == _built[index], "wrong physical destination or definition");
                Check(Vector3D.Distance(actual.WorldAABB.Center, at) < 0.5, "materialized block is not at the actual preview target");
                Check(Refused() == before, "legitimate indexed request was refused");
                Check(WorldApi.CountComponent(body.GetInventory(), "SteelPlate") < stock, "legitimate request paid no components");
                for (var i = 0; i < 3; i++) Check(_built[i].BlocksCount == counts[i] + (i == index ? 1 : 0), "request built on wrong/multiple grids");
                Note("index=" + index + ": 7 forged requests refused with unchanged physical census/inventories/stockpiles; near-subgrid request built correct paid scaffold");
            }
            Note("PASS: indices 1/2 via installed runtime handler, Survival payment and 14 refusals verified; context replay only, NOT transport acceptance");
        }

        private void Request(Vector3I position, long index, long owner, long builder)
        {
            FakeClients.AsClient(0, () =>
            {
                Check(!MyEventContext.Current.IsLocallyInvoked && MyEventContext.Current.Sender.Value == FakeClients.PlayerOf(0).Id.SteamId,
                    "request did not enter genuine non-local fake-player event context");
                try { _handler.Invoke(_projector, new object[] { position, owner, builder, false, index }); }
                catch (TargetInvocationException error) { throw new ScenarioFailedException("runtime BuildInternal threw: " + error.InnerException); }
            });
        }

        private IEnumerator Denied(string label, Vector3I position, long index, long owner, long builder)
        {
            var state = Snapshot();
            var census = PhysicalCensus();
            var before = Refused();
            Request(position, index, owner, builder);
            Check(Refused() > before, label + ": anticheat did not count a refusal");
            Check(PhysicalCensus() == census, label + ": request physically constructed/removed a block or grid");
            Check(Snapshot() == state, label + ": request changed integrity, inventory or construction stock");
            yield return WaitForTicks(3);
            Check(Snapshot() == state, label + ": deferred construction/material mutation");
        }

        private static string PhysicalCensus() => string.Join("|", MyEntities.GetEntities().OfType<MyCubeGrid>()
            .Where(g => !g.IsPreview && !g.MarkedForClose).OrderBy(g => g.EntityId)
            .Select(g => g.EntityId + ":" + string.Join(",", g.CubeBlocks.OrderBy(b => b.Position.X).ThenBy(b => b.Position.Y).ThenBy(b => b.Position.Z)
                .Select(b => b.Position + "/" + b.BlockDefinition.Id))));

        private string Snapshot()
        {
            var rows = new List<string>();
            foreach (var grid in _built)
                foreach (var block in grid.CubeBlocks)
                {
                    rows.Add(grid.EntityId + ":" + block.Position + ":" + block.Integrity + ":" + block.BuildIntegrity);
                    foreach (var component in block.BlockDefinition.Components.Select(c => c.Definition.Id).Distinct())
                        rows.Add(grid.EntityId + ":" + block.Position + ":stock:" + component + ":" + block.GetConstructionStockpileItemAmount(component));
                    if (block.FatBlock == null) continue;
                    for (var i = 0; i < block.FatBlock.InventoryCount; i++)
                        rows.Add("inv:" + block.FatBlock.EntityId + ":" + i + ":" + Inventory(block.FatBlock.GetInventory(i)));
                }
            for (var i = 0; i < 2; i++) rows.Add("character:" + i + ":" + Inventory(FakeClients.Character(i).GetInventory()));
            rows.Sort(StringComparer.Ordinal);
            return string.Join("|", rows);
        }

        private static string Inventory(VRage.Game.Entity.MyInventoryBase inventory) => string.Join(",", inventory.GetItems()
            .Where(i => i.Content is MyObjectBuilder_Component).OrderBy(i => i.Content.SubtypeName)
            .Select(i => i.Content.SubtypeName + ":" + i.Amount));

        public override void Cleanup()
        {
            try { if (_clientsCreated) FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                if (!grid.MarkedForClose && ((_built.Contains(grid)) || (grid.DisplayName ?? "").StartsWith(Prefix, StringComparison.Ordinal))) grid.Close();
        }
    }
}
