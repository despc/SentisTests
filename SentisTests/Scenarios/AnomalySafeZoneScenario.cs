using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilders.Components;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisAnomalyZone's ban on safe zones near a zone (15 km here), on safe zone blocks with zone chips:
    /// - near the zone the block's turning on is stopped where the game does it: no zone chip is taken
    ///   (TryConsumeUpkeep says no), no activation countdown starts (as with paid time left);
    /// - far from it both go as in the game: a chip is taken, the countdown starts;
    /// - a block's safe zone on near the zone (as one turned on before the zone was there) is turned off within 10 s;
    /// - an admins' safe zone (no block) near the zone stays on.
    /// The block's own server steps are called by reflection: a player's request needs a real client.
    /// </summary>
    public class AnomalySafeZoneScenario : TestScenario
    {
        public const string ScenarioName = "anomaly_safezone";
        private const string ZoneId = "scenario-safezone";
        private const double Ban = 15000;

        private static readonly MethodInfo Pay = typeof(MySafeZoneComponent).GetMethod("TryConsumeUpkeep", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo Countdown = typeof(MySafeZoneComponent).GetMethod("StartActivationCountdown", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Activating = typeof(MySafeZoneComponent).GetField("m_activating", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Dictionary<string, object> _settingsBefore = new Dictionary<string, object>();
        private Type _api;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;
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
            Check(Pay != null && Countdown != null && Activating != null, "MySafeZoneComponent has no TryConsumeUpkeep / StartActivationCountdown / m_activating");
            WorldApi.EnsureUnpaused(Name);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "as-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var site = anchor + Vector3D.Normalize(new Vector3D(0.7, -0.3, -0.6)) * 400000;
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(site - anchor));

            Setting("SafeZoneBanRadius", Ban);
            Setting("CenterCube", false);
            Call("Forget", ZoneId);
            Call("PutZone", ZoneId, "Сценарий сейф-зон", site.X, site.Y, site.Z, 60.0, 600.0);

            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, i => (site + side * 30000, 0, 0), withCharacters: true);
            var body = Wait(() => FakeClients.HasLiveCharacter(0), "a player with a body", 30);
            while (body.MoveNext()) yield return body.Current;
            var owner = FakeClients.PlayerOf(0).Identity.IdentityId;

            var near = Station(site + side * 5000, owner, "as-near");
            var far = Station(site + side * 40000, owner, "as-far");
            yield return WaitForTicks(10);
            var nearZone = Component(near);
            var farZone = Component(far);
            Check(nearZone != null && farZone != null, "the safe zone blocks have no safe zone component");

            // near: no chip taken, no countdown
            var chipsBefore = Chips(near);
            var paidNear = (bool)Pay.Invoke(nearZone, null);
            Countdown.Invoke(nearZone, null);
            Note($"near (5 km): paid {paidNear}, chips {chipsBefore} -> {Chips(near)}, counting down {Activating.GetValue(nearZone)}");
            Check(!paidNear && Chips(near) == chipsBefore, "a zone chip was taken for a safe zone 5 km off the zone");
            Check(!(bool)Activating.GetValue(nearZone), "the activation countdown started 5 km off the zone");

            // far: as in the game
            var farBefore = Chips(far);
            var paidFar = (bool)Pay.Invoke(farZone, null);
            Countdown.Invoke(farZone, null);
            Note($"far (40 km): paid {paidFar}, chips {farBefore} -> {Chips(far)}, counting down {Activating.GetValue(farZone)}");
            Check(paidFar && Chips(far) < farBefore, "no zone chip was taken for a safe zone 40 km off the zone");
            Check((bool)Activating.GetValue(farZone), "the activation countdown did not start 40 km off the zone");

            // a block's safe zone already on near the zone: turned off; an admins' one stays on
            var blockZone = (MySafeZone)MySessionComponentSafeZones.CrateSafeZone(near.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "SafeZoneBlock").PositionComp.WorldMatrixRef,
                MySafeZoneShape.Sphere, MySafeZoneAccess.Whitelist, new[] { owner }, null, 100, true, true, Color.SkyBlue.ToVector3(), "", near.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "SafeZoneBlock").EntityId);
            var adminZone = (MySafeZone)MySessionComponentSafeZones.CrateSafeZone(MatrixD.CreateWorld(site + side * 3000), MySafeZoneShape.Sphere, MySafeZoneAccess.Blacklist, null, null, 100, true);
            Track(blockZone);
            Track(adminZone);
            Check(blockZone.Enabled && adminZone.Enabled, "the safe zones were not made on");
            var off = Wait(() => !blockZone.Enabled, "the block's safe zone near the zone turned off", 15);
            while (off.MoveNext()) yield return off.Current;
            Check(adminZone.Enabled, "the admins' safe zone near the zone was turned off");

            Note("PASS: near the zone no chip is taken and no countdown starts; far both go; a block's safe zone on near the zone is turned off, an admins' one is left");
        }

        /// <summary>A static station with a safe zone block (10 zone chips in it) and a battery.</summary>
        private MyCubeGrid Station(Vector3D position, long owner, string name)
        {
            var safe = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializer.CreateNewObject(MyObjectBuilderType.Parse("MyObjectBuilder_SafeZoneBlock"), "SafeZoneBlock");
            safe.Min = new SerializableVector3I(0, 0, 0);
            safe.Owner = owner;
            safe.BuiltBy = owner;
            safe.ShareMode = MyOwnershipShareModeEnum.Faction;
            var builder = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                DisplayName = WorldApi.EntityPrefix + name,
                CreatePhysics = true,
                PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled,
                PositionAndOrientation = new MyPositionAndOrientation(position, Vector3.Forward, Vector3.Up),
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>
                {
                    safe,
                    new MyObjectBuilder_BatteryBlock
                    {
                        SubtypeName = "LargeBlockBatteryBlock", Min = new SerializableVector3I(4, 0, 0), CurrentStoredPower = 3f, MaxStoredPower = 3f,
                        Enabled = true, ProducerEnabled = true, Owner = owner, BuiltBy = owner, ShareMode = MyOwnershipShareModeEnum.Faction
                    }
                }
            };
            MyAPIGateway.Entities.RemapObjectBuilder(builder);
            var grid = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder);
            Track(grid);
            var block = grid.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "SafeZoneBlock");
            block.GetInventory().AddItems(10, new MyObjectBuilder_Component { SubtypeName = "ZoneChip" });
            return grid;
        }

        private static MySafeZoneComponent Component(MyCubeGrid grid) =>
            grid.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "SafeZoneBlock").Components.Get<MySafeZoneComponent>();

        private static int Chips(MyCubeGrid grid) =>
            (int)grid.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "SafeZoneBlock").GetInventory()
                .GetItemAmount(new MyDefinitionId(typeof(MyObjectBuilder_Component), "ZoneChip"));

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
                Log.Warn(e, "anomaly_safezone: the zone's settings were not restored");
            }
            FakeClients.RemoveAll();
            base.Cleanup();
        }
    }
}
