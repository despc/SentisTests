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
    /// - near the zone a press of "on" (the block's own steps after it) takes no zone chip, starts no activation
    ///   countdown, and the safe zone is taken away: the block's "on" button shows it off;
    /// - far from it it goes as in the game: a chip is taken, the countdown starts, the safe zone stays;
    /// - a block's safe zone on near the zone (as one turned on before the zone was there) is taken away within 10 s;
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
        private static readonly MethodInfo Created = typeof(MySafeZoneComponent).GetMethod("OnSafezoneCreated", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo ZoneId_ = typeof(MySafeZoneComponent).GetProperty("SafeZoneEntityId", BindingFlags.Instance | BindingFlags.Public);

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
            Check(Pay != null && Countdown != null && Activating != null && Created != null && ZoneId_?.SetMethod != null,
                "MySafeZoneComponent has no TryConsumeUpkeep / StartActivationCountdown / m_activating / OnSafezoneCreated / SafeZoneEntityId setter");
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

            // near: the block's own steps after a press of "on" (its safe zone made, then turned on): no chip taken, no
            // countdown, the safe zone taken away so the button shows it off
            var chipsBefore = Chips(near);
            var pressedNear = Attach(near, owner, false);
            Created.Invoke(nearZone, new object[] { pressedNear.EntityId });
            Note($"near (5 km): chips {chipsBefore} -> {Chips(near)}, counting down {Activating.GetValue(nearZone)}");
            Check(Chips(near) == chipsBefore, "a zone chip was taken for a safe zone 5 km off the zone");
            Check(!(bool)Activating.GetValue(nearZone), "the activation countdown started 5 km off the zone");
            var gone = Wait(() => nearZone.SafeZoneEntityId == 0 && Gone(pressedNear), "the refused safe zone taken away (the button off)", 5);
            while (gone.MoveNext()) yield return gone.Current;

            // far: as in the game
            var farBefore = Chips(far);
            var pressedFar = Attach(far, owner, false);
            Created.Invoke(farZone, new object[] { pressedFar.EntityId });
            yield return WaitForTicks(10);
            Note($"far (40 km): chips {farBefore} -> {Chips(far)}, counting down {Activating.GetValue(farZone)}, safe zone {farZone.SafeZoneEntityId}");
            Check(Chips(far) < farBefore, "no zone chip was taken for a safe zone 40 km off the zone");
            Check((bool)Activating.GetValue(farZone), "the activation countdown did not start 40 km off the zone");
            Check(farZone.SafeZoneEntityId == pressedFar.EntityId && !Gone(pressedFar), "the safe zone 40 km off the zone was taken away");

            // a block's safe zone already on near the zone: taken away (the button off); an admins' one stays on
            var onNear = Attach(near, owner, true);
            var adminZone = (MySafeZone)MySessionComponentSafeZones.CrateSafeZone(MatrixD.CreateWorld(site + side * 3000), MySafeZoneShape.Sphere, MySafeZoneAccess.Blacklist, null, null, 100, true);
            Track(adminZone);
            Check(onNear.Enabled && adminZone.Enabled, "the safe zones were not made on");
            var off = Wait(() => nearZone.SafeZoneEntityId == 0 && Gone(onNear), "the block's safe zone on near the zone taken away", 15);
            while (off.MoveNext()) yield return off.Current;
            Check(adminZone.Enabled && !Gone(adminZone), "the admins' safe zone near the zone was turned off");

            Note("PASS: near the zone no chip is taken, no countdown starts and the safe zone is taken away (the button off); far it goes as in the game; " +
                 "a block's safe zone on near the zone is taken away, an admins' one is left");
        }

        /// <summary>The block's safe zone made as the block does (on or off), the block's safe zone id set to it.</summary>
        private MySafeZone Attach(MyCubeGrid grid, long owner, bool on)
        {
            var block = grid.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "SafeZoneBlock");
            var zone = (MySafeZone)MySessionComponentSafeZones.CrateSafeZone(block.PositionComp.WorldMatrixRef, MySafeZoneShape.Sphere, MySafeZoneAccess.Whitelist,
                new[] { owner }, null, 100, on, true, Color.SkyBlue.ToVector3(), "", block.EntityId);
            Track(zone);
            ZoneId_.SetValue(block.Components.Get<MySafeZoneComponent>(), zone.EntityId);
            return zone;
        }

        private static bool Gone(MySafeZone zone) => zone.Closed || zone.MarkedForClose;

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
