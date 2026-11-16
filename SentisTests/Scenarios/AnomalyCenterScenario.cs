using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisAnomalyZone's center from a blueprint, with the capture's rewards in its container:
    /// - a zone given a blueprint of its own (a folder with bp.sbc, made here: two containers, a beacon, a battery, armor
    ///   and a safe zone block, saved upside down far away, owned by somebody) gets its center made from it: the grid's
    ///   middle at the zone's center, turned as saved, a station that takes no damage, every block nobody's and shared
    ///   with all, the safe zone block left out;
    /// - "Rewards in the center's container" on: a capture by a player on foot puts the zone's rewards into the larger of
    ///   the two containers, no reward structure is made, a player of no faction may take from it; what did not fit is
    ///   not kept for later (no queue: the center's container is meant to be big);
    /// - the blueprint taken away: the center is the armor cube again.
    /// The plugin is driven through SentisAnomalyZone.AnomalyZoneApi by reflection; its settings come back as they were.
    /// </summary>
    public class AnomalyCenterScenario : TestScenario
    {
        public const string ScenarioName = "anomaly_center";
        private const string ZoneId = "scenario-center";
        private const double Radius = 60;
        private const double CaptureSeconds = 8;
        private const double Iron = 50, Stone = 50000000; // stone: 18500 m3, more than the large container holds (421 m3 times the inventory multiplier)
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly Dictionary<string, object> _settingsBefore = new Dictionary<string, object>();
        private readonly List<long> _factions = new List<long>();
        private string _folder;
        private Type _api;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;
        public override string[] Requires => new[] { Integrations.AnomalyZone };

        private object Call(string method, params object[] args)
        {
            var m = _api.GetMethod(method, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException("AnomalyZoneApi", method);
            return m.Invoke(null, args);
        }

        private void Setting(string name, object value)
        {
            var old = Call("Set", name, value);
            if (!_settingsBefore.ContainsKey(name)) _settingsBefore[name] = old;
        }

        public override IEnumerator Run()
        {
            _api = RequireType(Integrations.TypeOf(Integrations.AnomalyZone, "SentisAnomalyZone.AnomalyZoneApi"), "SentisAnomalyZone.AnomalyZoneApi");
            Check((bool)_api.GetProperty("Running").GetValue(null), "SentisAnomalyZone is not running");
            Check(_api.GetMethod("SetZoneBlueprint") != null, "SentisAnomalyZone has no center blueprints (an older build)");
            WorldApi.EnsureUnpaused(Name);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "ac-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var site = anchor + Vector3D.Normalize(new Vector3D(-0.6, 0.5, 0.6)) * 400000;
            var away = site + new Vector3D(400, 0, 0);
            var saved = MatrixD.CreateWorld(new Vector3D(983650, 224494, 1708199), Vector3D.Left, Vector3D.Down);
            var blocks = WriteBlueprint(saved);

            Setting("CenterCube", true);
            Setting("CountOnFoot", true);
            Setting("SpawnRewards", true);
            Setting("RewardsInCenter", true);
            Setting("K1", 0.0);
            Setting("PointsPerCapture", 0);
            Setting("CaptureRewardOpenMinutes", 30.0); // a structure would be the faction's: the center's container is everyone's anyway
            Call("Forget", ZoneId);
            Call("PutZone", ZoneId, "Сценарий центра", site.X, site.Y, site.Z, Radius, CaptureSeconds);
            Call("AddReward", ZoneId, 0, "Ingot", "Iron", Iron);
            Call("AddReward", ZoneId, 0, "Ore", "Stone", Stone);
            Call("SetZoneBlueprint", ZoneId, _folder);

            // the center from the blueprint
            MyCubeGrid center = null;
            var made = Wait(() => (center = Grid(CenterGrid)) != null && center.GetFatBlocks<MyCargoContainer>().Count() == 2, "the center made from the blueprint", 30);
            while (made.MoveNext()) yield return made.Current;
            var middle = center.PositionComp.WorldAABB.Center;
            Note($"center {center.EntityId}: {center.BlocksCount} blocks, its middle {Vector3D.Distance(middle, site):0.00} m from the zone's center, " +
                 $"forward {center.WorldMatrix.Forward:0.00}, up {center.WorldMatrix.Up:0.00}");
            Check(center.BlocksCount == blocks - 1, $"{center.BlocksCount} blocks, {blocks - 1} expected (the safe zone block left out)");
            Check(!center.GetFatBlocks().Any(b => b.BlockDefinition.Id.TypeId.ToString().Contains("SafeZone")), "the blueprint's safe zone block is there");
            Check(Vector3D.Distance(middle, site) < 0.5, $"the center's middle is {Vector3D.Distance(middle, site):0.00} m from the zone's center");
            Check(Vector3D.Distance(center.WorldMatrix.Up, saved.Up) < 1e-3 && Vector3D.Distance(center.WorldMatrix.Forward, saved.Forward) < 1e-3,
                "the center is not turned as the blueprint was saved");
            Check(center.IsStatic && !center.DestructibleBlocks && !center.Editable, "the center is not an indestructible, uneditable station");
            Check(center.GetFatBlocks().All(b => b.OwnerId == 0 && (b.IDModule == null || b.IDModule.ShareMode == MyOwnershipShareModeEnum.All)),
                "a block of the center has an owner or is not shared with all");

            // three players: one captures (a faction), one of no faction looks
            FakeClients.Add(2, Network, i => (away + new Vector3D(0, i * 10, 0), 0, 0), withCharacters: true);
            var bodies = Wait(() => Enumerable.Range(0, 2).All(FakeClients.HasLiveCharacter), "two players with bodies", 30);
            while (bodies.MoveNext()) yield return bodies.Current;
            var capturer = FakeClients.PlayerOf(0).Identity.IdentityId;
            var stranger = FakeClients.PlayerOf(1).Identity.IdentityId;
            var factions = MySession.Static.Factions;
            var suffix = (DateTime.UtcNow.Ticks % 900 + 100).ToString();
            ((IMyFactionCollection)factions).CreateFaction(capturer, "AC" + suffix, "Anomaly center " + suffix, "", "");
            var joined = Wait(() => factions.GetPlayerFaction(capturer) != null, "the capturer's faction", 15);
            while (joined.MoveNext()) yield return joined.Current;
            _factions.Add(factions.GetPlayerFaction(capturer).FactionId);
            Check(factions.GetPlayerFaction(stranger) == null, "the second player has a faction");

            FakeClients.MoveTo(0, site + Vector3D.Normalize(center.WorldMatrix.Right + center.WorldMatrix.Backward) * 30);
            var captured = Wait(() => (int)Call("Captures", ZoneId) >= 1, "a capture", 40);
            while (captured.MoveNext()) yield return captured.Current;
            FakeClients.MoveTo(0, away);
            yield return null;

            var large = center.GetFatBlocks<MyCargoContainer>().OrderByDescending(c => (double)c.GetInventory().MaxVolume).First();
            var small = center.GetFatBlocks<MyCargoContainer>().OrderBy(c => (double)c.GetInventory().MaxVolume).First();
            var iron = Amount(large, "MyObjectBuilder_Ingot/Iron");
            var stone = Amount(large, "MyObjectBuilder_Ore/Stone");
            var pending = (string[])Call("Pending", ZoneId);
            Note($"after the capture: the large container iron {iron:0.##}, stone {stone:0}; the small one {small.GetInventory().ItemCount} items; " +
                 $"waiting: {string.Join(", ", pending)}; reward structure {(long)Call("RewardGrid", ZoneId)}");
            Check((long)Call("RewardGrid", ZoneId) == 0, "a reward structure was made");
            Check(Math.Abs(iron - Iron) < 0.01, $"iron {iron} in the center's large container, {Iron} given");
            Check(stone > 0 && stone < Stone, $"stone {stone}: the large container is not full of it");
            Check(small.GetInventory().ItemCount == 0, "the rewards went into the small container");
            Check(pending.Length == 0, "what did not fit waits: " + string.Join(", ", pending));
            Check(large.HasPlayerAccess(stranger) && large.HasPlayerAccess(capturer), "a player may not use the center's container");

            // no blueprint: the cube again, the blueprint's grid gone
            var blueprintGrid = center.EntityId;
            Call("SetZoneBlueprint", ZoneId, "");
            MyCubeGrid cube = null;
            var back = Wait(() => (cube = Grid(CenterGrid)) != null && cube.EntityId != blueprintGrid && Grid(blueprintGrid) == null, "the armor cube again", 30);
            while (back.MoveNext()) yield return back.Current;
            Check(cube.GetFatBlocks<MyCargoContainer>().Count() == 0 && Vector3D.Distance(cube.PositionComp.GetPosition(), site) < 0.5, "the center is not the cube");

            Note($"PASS: the center from the blueprint ({center.BlocksCount} blocks, {Vector3D.Distance(middle, site):0.00} m off, turned as saved, nobody's, " +
                 "no safe zone); the capture's rewards in its larger container, open to a player of no faction, no queue of what did not fit; the cube again without the blueprint");
        }

        /// <summary>The blueprint in a folder of its own (bp.sbc); the number of its blocks.</summary>
        private int WriteBlueprint(MatrixD saved)
        {
            const long somebody = 144115188075855897;
            MyObjectBuilder_CubeBlock Block(string type, string subtype, int x, int y = 0, int z = 0)
            {
                var block = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializer.CreateNewObject(MyObjectBuilderType.Parse(type), subtype);
                block.Min = new Vector3I(x, y, z);
                block.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
                block.Owner = somebody;
                block.BuiltBy = somebody;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                block.IntegrityPercent = 1;
                block.BuildPercent = 1;
                return block;
            }
            var blocks = new List<MyObjectBuilder_CubeBlock>
            {
                Block("MyObjectBuilder_CargoContainer", "LargeBlockLargeContainer", 0),
                Block("MyObjectBuilder_CargoContainer", "LargeBlockSmallContainer", 3),
                Block("MyObjectBuilder_Beacon", "LargeBlockBeacon", 4),
                Block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", 5),
                Block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", 6),
                Block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", 6, 1),
                Block("MyObjectBuilder_SafeZoneBlock", "SafeZoneBlock", 0, 4),
            };
            var grid = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                DisplayName = "Scenario center",
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                PositionAndOrientation = new MyPositionAndOrientation(saved.Translation, saved.Forward, saved.Up),
                CubeBlocks = blocks
            };
            var definitions = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Definitions>();
            var blueprint = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
            blueprint.Id = new MyDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)), "Scenario center");
            blueprint.CubeGrids = new[] { grid };
            definitions.ShipBlueprints = new[] { blueprint };
            _folder = Path.Combine(Path.GetTempPath(), "sentis-anomaly-center-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            Check(MyObjectBuilderSerializerKeen.SerializeXML(Path.Combine(_folder, "bp.sbc"), false, definitions), "the blueprint was not written");
            return blocks.Count;
        }

        private long CenterGrid => (long)Call("CenterGrid", ZoneId);

        private static double Amount(MyCargoContainer container, string id) =>
            container.GetInventory().GetItems().Where(i => i.Content.GetObjectId().ToString() == id).Sum(i => (double)i.Amount);

        private static MyCubeGrid Grid(long id) =>
            id != 0 && MyEntities.TryGetEntityById(id, out MyCubeGrid grid) && !grid.MarkedForClose ? grid : null;

        public override void Cleanup()
        {
            try
            {
                if (_api != null)
                {
                    Call("Forget", ZoneId);
                    Call("RemoveZone", ZoneId);
                    foreach (var setting in _settingsBefore) Call("Set", setting.Key, setting.Value);
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "anomaly_center: the zone's settings were not restored");
            }
            try
            {
                if (_folder != null && Directory.Exists(_folder)) Directory.Delete(_folder, true);
            }
            catch (Exception)
            {
            }
            foreach (var faction in _factions) MyFactionCollection.RemoveFaction(faction);
            FakeClients.RemoveAll();
            base.Cleanup();
        }
    }
}
