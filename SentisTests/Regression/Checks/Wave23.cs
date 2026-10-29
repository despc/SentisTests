using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using SentisTests.Game;
using VRage.Game;
using VRage.Network;
using VRageMath;

namespace SentisTests.Regression.Checks
{

    /// <summary>
    /// A timer block with a block group's action on its toolbar (the group's "switch off": every light in the group): when
    /// its time is up, all three lights of the group go off, the light outside the group stays on.
    /// </summary>
    public sealed class TimerGroupCheck : RegressionCheck
    {
        public override string Name => Prefix + "timer_group";
        private const string Group = "Check lights";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var timerId = VRage.MyEntityIdentifier.AllocateId();
            var grid = Build("timer-group", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 1, 0, 0),
                new Part("InteriorLight/SmallLight", 2, 0, 0),
                new Part("InteriorLight/SmallLight", 3, 0, 0),
                new Part("InteriorLight/SmallLight", 4, 0, 0),
                new Part("TimerBlock/TimerBlockLarge", 0, 1, 0).With(b =>
                {
                    b.EntityId = timerId;
                    var timer = (MyObjectBuilder_TimerBlock)b;
                    timer.Delay = 1000;
                    timer.Toolbar = new MyObjectBuilder_Toolbar
                    {
                        ToolbarType = MyToolbarType.ButtonPanel,
                        Slots = new List<MyObjectBuilder_Toolbar.Slot>
                        {
                            new MyObjectBuilder_Toolbar.Slot
                            {
                                Index = 0,
                                Item = "",
                                Data = new MyObjectBuilder_ToolbarItemTerminalGroup { BlockEntityId = timerId, GroupName = Group, _Action = "OnOff_Off" },
                            },
                        },
                    };
                }));
            var lights = grid.GetFatBlocks().OfType<MyLightingBlock>().OrderBy(l => l.Position.X).ToList();
            var inGroup = lights.Take(3).ToList();
            var outside = lights[3];
            // the group, as the terminal makes it
            var builder = new MyObjectBuilder_BlockGroup { Name = Group, Blocks = inGroup.Select(l => l.Position).ToList() };
            var group = (MyBlockGroup)System.Activator.CreateInstance(typeof(MyBlockGroup), true);
            typeof(MyBlockGroup).GetMethod("Init", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(group, new object[] { grid, builder });
            typeof(MyCubeGrid).GetMethod("AddGroup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(MyBlockGroup), typeof(bool) }, null)
                .Invoke(grid, new object[] { group, true });
            var timer = Block<SpaceEngineers.Game.Entities.Blocks.MyTimerBlock>(grid);
            yield return Until(() => lights.All(l => l.IsWorking) ? 1 : 0, v => v > 0, "the lights working", 10);
            Check(grid.GridSystems.TerminalSystem.BlockGroups.Any(g => g.Name.ToString() == Group), "the group is not in the grid's terminal");
            Request(timer, "Start");
            yield return Until(() => inGroup.Count(l => !l.Enabled), v => v == 3, "the group's lights switched off", 10,
                () => "on: " + string.Join(", ", lights.Select(l => l.Position.X + "=" + l.Enabled)));
            Check(outside.Enabled, "the light outside the group switched off too");
            Result("the timer's group action switched off the 3 lights of the group, the one outside it stayed on");
        }
    }

    /// <summary>
    /// Relative dampeners (<c>MyPlayerCollection.SetDampeningEntity</c>, the key that sets them to what the player looks
    /// at): a character on its jetpack with dampeners on, beside a ship drifting at 10 m/s, its dampeners set to the ship,
    /// keeps the ship's speed instead of stopping. Cleared, it stops.
    /// </summary>
    public sealed class RelativeDampenersCheck : RegressionCheck
    {
        public override string Name => Prefix + "relative_dampeners";
        public override int TimeoutSeconds => 120; // under the suite's load the character alone can take most of a minute to stand up

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -8));
            var ship = Build("relative-dampeners", Site.Origin, false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 1, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 1, 0));
            HoldHighQuality();
            yield return Seconds(1);
            var velocity = Site.Right * 10;
            FakeClients.SetFree(Me, true);
            var body = Body;
            StandAt(Site.At(0.5, 0.5, -8), ship.PositionComp.GetPosition());
            FakeClients.SetFree(Me, true);
            ship.Physics.LinearVelocity = velocity;
            body.Physics.LinearVelocity = velocity;
            for (var i = 0; i < 10; i++)
            {
                Keys(Vector3.Zero, jetpack: true, dampeners: true, face: ship.PositionComp.GetPosition() - body.PositionComp.GetPosition());
                yield return null;
            }
            Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "SetDampeningEntity", body.EntityId);
            yield return null;
            var relative = ((Sandbox.Game.Entities.IMyControllableEntity)body).RelativeDampeningEntity;
            Check(relative == ship, "the player's dampeners set to " + (relative?.DisplayName ?? "nothing") + ", not the ship they look at");
            for (var f = Frames(3); f > 0; f--)
            {
                Keys(Vector3.Zero, jetpack: true, dampeners: true);
                yield return null;
            }
            var kept = (body.Physics.LinearVelocity - ship.Physics.LinearVelocity).Length();
            Check(kept < 2, "with the dampeners set to the ship the player is " + F(kept) + " m/s off its speed (" + F(body.Physics.LinearVelocity.Length()) + " m/s)");
            Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "ClearDampeningEntity", body.EntityId);
            for (var f = Frames(4); f > 0; f--)
            {
                Keys(Vector3.Zero, jetpack: true, dampeners: true);
                yield return null;
            }
            var speed = body.Physics.LinearVelocity.Length();
            Check(speed < 2, "with the dampeners cleared the player still moves at " + F(speed) + " m/s");
            Result("dampeners set to the drifting ship: the player kept its 10 m/s (" + F(kept) + " off); cleared: stopped (" + F(speed) + " m/s)");
        }
    }

    /// <summary>
    /// What a ship drill digs out of each ore: a cube of voxels of one material for every ore the game's voxel materials
    /// give (one material of each), a large drill boring into each. Every drill fills with the ore of its material
    /// (the material's <c>MinedOre</c>) and nothing else.
    /// </summary>
    public sealed class DrillOresCheck : RegressionCheck
    {
        public override string Name => Prefix + "drill_ores";
        public override int TimeoutSeconds => 120;
        // a cube of 8 m of rock in a 16 m storage: eleven drills at once in more of it slowed the whole suite
        private const int Size = 16;

        protected override IEnumerator Check()
        {
            var materials = Sandbox.Definitions.MyDefinitionManager.Static.GetVoxelMaterialDefinitions()
                .Where(m => m.CanBeHarvested && !string.IsNullOrEmpty(m.MinedOre) && m.Enabled)
                .GroupBy(m => m.MinedOre).Select(g => g.OrderBy(m => m.Id.SubtypeName).First()).OrderBy(m => m.MinedOre).ToList();
            SkipUnless(materials.Count > 0, "no harvestable voxel materials");
            yield return Arrive(Site.At(75, 20, 0));
            var drills = new List<(VRage.Game.MyVoxelMaterialDefinition Material, Sandbox.Game.Weapons.MyShipDrill Drill)>();
            for (var i = 0; i < materials.Count; i++)
            {
                var material = materials[i];
                var corner = Site.At((i % 6) * 30, 0, (i / 6) * 40);
                var storage = Sandbox.ModAPI.MyAPIGateway.Session.VoxelMaps.CreateStorage(new Vector3I(Size));
                var data = new VRage.Voxels.MyStorageData();
                var min = new Vector3I(4);
                var max = new Vector3I(Size - 5);
                data.Resize(min, max);
                data.ClearContent(VRage.Voxels.MyVoxelConstants.VOXEL_CONTENT_FULL);
                data.ClearMaterials(material.Index);
                storage.WriteRange(data, VRage.Voxels.MyStorageDataTypeFlags.ContentAndMaterial, min, max);
                var voxel = Sandbox.ModAPI.MyAPIGateway.Session.VoxelMaps.CreateVoxelMap(Game.WorldApi.EntityPrefix + "drill-ore-" + material.MinedOre, storage, corner, 0);
                Track((VRage.Game.Entity.MyEntity)voxel);
                var middle = corner + new Vector3D(Size / 2.0);
                // the face toward the site's back, a metre into the rock
                var face = middle - Site.Forward * (Size / 2.0 - 4);
                var rig = ToolRig("drill-ore-" + material.MinedOre, "Drill/LargeBlockDrill", face + Site.Forward * 1, Site.Forward);
                var drill = Block<Sandbox.Game.Weapons.MyShipDrill>(rig);
                Switch(drill, true);
                drills.Add((material, drill));
            }
            double Ore(Sandbox.Game.Weapons.MyShipDrill drill, string ore) =>
                Inventory(drill).GetItems().Where(i => i.Content is MyObjectBuilder_Ore && i.Content.SubtypeName == ore).Sum(i => (double)i.Amount);
            yield return Until(() =>
                {
                    // a drill with its ore stops: the others' rock is what the check waits for
                    foreach (var d in drills)
                        if (d.Drill.Enabled && Ore(d.Drill, d.Material.MinedOre) > 1) Switch(d.Drill, false);
                    return drills.Count(d => Ore(d.Drill, d.Material.MinedOre) > 1);
                }, v => v == drills.Count, "every drill with its ore", 60,
                () => string.Join("; ", drills.Where(d => Ore(d.Drill, d.Material.MinedOre) <= 1).Select(d => d.Material.MinedOre + ": " + State(d.Drill))));
            var wrong = new List<string>();
            foreach (var (material, drill) in drills)
            {
                var others = Inventory(drill).GetItems().Where(i => i.Content is MyObjectBuilder_Ore && i.Content.SubtypeName != material.MinedOre)
                    .Select(i => i.Content.SubtypeName + " " + F((double)i.Amount)).ToList();
                if (others.Count > 0) wrong.Add(material.Id.SubtypeName + " (" + material.MinedOre + ") gave also " + string.Join(", ", others));
            }
            Check(wrong.Count == 0, string.Join("; ", wrong));
            Result(drills.Count + " ores, each drill only its own: " + string.Join(", ", drills.Select(d => d.Material.MinedOre + " " + F(Ore(d.Drill, d.Material.MinedOre)))));
        }
    }

    /// <summary>
    /// A defensive combat block on a drone with an AI flight block, set to flee to its own coordinates always (flee mode
    /// "to GPS or beacon", custom coordinates 300 m off, trigger "always" - when it finds an enemy): an enemy station 150 m
    /// off (another player's) found, it turns to fleeing and the drone flies to the coordinates.
    /// </summary>
    public sealed class DefensiveCombatCheck : RegressionCheck
    {
        public override string Name => Prefix + "defensive_combat";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            // an enemy to flee from ("always": whenever it finds one), 150 m off
            var enemy = -1;
            var target = Site.At(150, 0, 0);
            yield return Join(target + Site.Up * 10 + Site.Forward * 10, i => enemy = i);
            BuildFor(FakeClients.PlayerOf(enemy).Identity.IdentityId, "defensive-target", target,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var flee = Site.At(-300, 0, 0);
            var drone = Build("defensive-drone", Site.Origin, false, Drones.Parts(new Part("DefensiveCombatBlock/LargeDefensiveCombat", 1, 1, 0).With(b =>
            {
                b.GetType().GetField("FleeMode").SetValue(b, VRage.Game.ModAPI.Ingame.FleeMode.ToGpsOrBeacon);
                b.GetType().GetField("FleeTrigger").SetValue(b, VRage.Game.ModAPI.Ingame.FleeTrigger.Always);
                b.GetType().GetField("CustomFleeCoordinates").SetValue(b, flee);
                b.GetType().GetField("UseCustomFleeCoordinates").SetValue(b, true);
            })));
            yield return null;
            var defensive = drone.GetFatBlocks().OfType<Sandbox.ModAPI.IMyDefensiveCombatBlock>().FirstOrDefault();
            Check(defensive != null, "no defensive combat block on the drone");
            var block = (MyCubeBlock)defensive;
            yield return Drones.Activate((MyCubeBlock)drone.GetCubeBlock(Vector3I.Zero).FatBlock);
            yield return Drones.Activate(block);
            var fleeing = block.GetType().GetProperty("IsFleeing");
            bool Fleeing() => fleeing != null && (bool)fleeing.GetValue(block);
            yield return Until(() => Fleeing() ? 1 : 0, v => v > 0, "the defensive block fleeing", 30, () => Drones.Info(block));
            double Off() => Vector3D.Distance(drone.PositionComp.WorldAABB.Center, flee);
            var start = Off();
            yield return Until(Off, v => v < start - 30, "the drone flying to its flee coordinates, m off them", 60, () => F(Motion.Speed(drone)) + " m/s; " + Drones.Info(block));
            Result("the defensive combat block fled to its coordinates, the drone from " + F(start) + " m to " + F(Off()) + " m off them");
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' cleanup exception (<c>IgnoreCleanupSubtypes</c>): a grid with a block of such a subtype
    /// is not given to the game's trash removal (<c>MySessionComponentTrash</c>'s grids to check), any other grid is. With
    /// "Container" in the setting (in memory; the stand's "Cargo" is in no vanilla subtype): a grid with a small container
    /// is not among the trash removal's grids, a grid of armor alone is.
    /// </summary>
    public sealed class CleanupExceptionCheck : RegressionCheck
    {
        public override string Name => Prefix + "cleanup_exception";
        private Scenarios.ConfigOverride _gameplay;

        protected override IEnumerator Check()
        {
            SkipUnless(SentisTests.Core.Integrations.TypeOf(Scenarios.ConfigOverride.Gameplay, "SentisGameplayImprovements.CleanupPatch") != null, "SentisGameplayImprovements has no CleanupPatch");
            yield return Arrive(Site.At(0, 0, -6));
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("IgnoreCleanupSubtypes", "Container");
            var kept = Build("cleanup-kept", Site.Origin, false, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0));
            var trash = Build("cleanup-trash", Site.At(6, 0, 0), false, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            yield return Seconds(1);
            var component = typeof(Sandbox.Game.SessionComponents.MySessionComponentTrash);
            var updater = component.GetField("m_gridsToCheck", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                          ?? throw new Core.ScenarioFailedException("MySessionComponentTrash has no m_gridsToCheck");
            var list = (IEnumerable)updater.GetType().GetProperty("List").GetValue(updater);
            var checkedGrids = new HashSet<MyCubeGrid>(list.Cast<MyCubeGrid>());
            Check(!checkedGrids.Contains(kept), "the grid with a container (\"Container\" in IgnoreCleanupSubtypes) is among the trash removal's grids");
            Check(checkedGrids.Contains(trash), "the grid of armor alone is not among the trash removal's grids");
            Result("with \"Container\" ignored: the grid with a container is not given to the trash removal, the armor alone is");
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' renaming of grids with the game's default names (<c>AutoRenameGrids</c>, a pass every
    /// 30 s): a grid of the player's named "Large Grid 1234" becomes "&lt;owner&gt;_LG_..."; a grid with a name of its own
    /// keeps it.
    /// </summary>
    public sealed class AutoRenameCheck : RegressionCheck
    {
        public override string Name => Prefix + "auto_rename";
        public override int TimeoutSeconds => 120;
        private Scenarios.ConfigOverride _gameplay;

        protected override IEnumerator Check()
        {
            SkipUnless(SentisTests.Core.Integrations.TypeOf(Scenarios.ConfigOverride.Gameplay, "SentisGameplayImprovements.BackgroundActions.GridAutoRenamer") != null, "SentisGameplayImprovements has no GridAutoRenamer");
            yield return Arrive(Site.At(0, 0, -6));
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("AutoRenameGrids", true);
            var plain = Build("auto-rename", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0));
            var named = Build("auto-rename-kept", Site.At(6, 0, 0), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0));
            plain.DisplayName = "Large Grid 1234";
            named.DisplayName = "My Base";
            var owner = Sandbox.Game.World.MySession.Static.Players.TryGetIdentity(Identity).DisplayName;
            yield return Until(() => plain.DisplayName.StartsWith(owner + "_LG_") ? 1 : 0, v => v > 0, "the default-named grid renamed for its owner", 90,
                () => "named " + plain.DisplayName);
            Check(named.DisplayName == "My Base", "a grid with a name of its own was renamed to " + named.DisplayName);
            Result("\"Large Grid 1234\" became \"" + plain.DisplayName + "\", \"My Base\" kept");
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' reward for time online (<c>OnlineRewardEnabled</c>, looked at every 30 s): a player online
    /// longer than <c>OnlineRewardEachMinutes</c> since they logged in gets <c>OnlineReward</c> in their inventory, once
    /// in that time. With 1 minute (in memory) and the player's login 2 minutes back: the reward once. In the suite it waits
    /// for every other check to finish first: the reward goes to every player online, and keeping the others' logins fresh
    /// missed the players who came in meanwhile - 7 plates more in 14 checks' suits and containers (29.10.2026).
    /// </summary>
    public sealed class OnlineRewardCheck : RegressionCheck
    {
        public override string Name => Prefix + "online_reward";
        public override int TimeoutSeconds => 900;
        private Scenarios.ConfigOverride _gameplay;

        protected override IEnumerator Check()
        {
            SkipUnless(SentisTests.Core.Integrations.TypeOf(Scenarios.ConfigOverride.Gameplay, "SentisGameplayImprovements.BackgroundActions.OnlineReward") != null, "SentisGameplayImprovements has no OnlineReward");
            if (InSuite) yield return Until(() => RegressionSuite.Unfinished <= 1 ? 1 : 0, v => v > 0, "every other check done", 780, () => RegressionSuite.Unfinished + " checks running");
            yield return Arrive(Site.At(0, 0, -6));
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("OnlineRewardEnabled", true);
            _gameplay.Set("OnlineReward", "Component_SteelPlate=7");
            _gameplay.Set("OnlineRewardEachMinutes", 1);
            Sandbox.Game.World.MySession.Static.Players.TryGetIdentity(Identity).LastLoginTime = System.DateTime.Now.AddMinutes(-2);
            var suit = Inventory(Body);
            suit.Clear();
            // the reward goes to every player online that long: the other checks' players are kept just logged in while it is
            // on, or 7 plates came into their suits and containers (hand_grind, remote_transfer, weld_without_components)
            void OthersJustIn()
            {
                foreach (var player in Sandbox.Game.World.MySession.Static.Players.GetOnlinePlayers())
                    if (player.Identity != null && player.Identity.IdentityId != Identity) player.Identity.LastLoginTime = System.DateTime.Now;
            }
            OthersJustIn();
            yield return Until(() => { OthersJustIn(); return Amount(suit, "Component/SteelPlate"); }, v => v >= 7, "the reward in the player's suit", 45);
            var hold = System.DateTime.UtcNow;
            while ((System.DateTime.UtcNow - hold).TotalSeconds < 2) { OthersJustIn(); yield return null; }
            _gameplay.Restore();
            _gameplay = null;
            Check(Amount(suit, "Component/SteelPlate") == 7, "the reward came as " + F(Amount(suit, "Component/SteelPlate")) + " plates, 7 once");
            Result("2 minutes online, a reward each minute: 7 plates once");
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }

    /// <summary>
    /// SentisGameplayImprovements' own cleanup of loose items (<c>CustomFloatingObjectsCleanup</c>, a look every 5 s): a
    /// loose item older than <c>FloatingObjectsLifetime</c> minutes is removed, a fresh one stays. Two loose stacks of
    /// plates, the time one of them came into the world (as SGI noted it) put 10 minutes back: that one is gone, the other
    /// is there.
    /// </summary>
    public sealed class FloatingLifetimeCheck : RegressionCheck
    {
        public override string Name => Prefix + "floating_lifetime";
        public override int TimeoutSeconds => 60;
        protected override bool NeedsLooseItems => true;
        private Scenarios.ConfigOverride _gameplay;

        protected override IEnumerator Check()
        {
            var observer = SentisTests.Core.Integrations.TypeOf(Scenarios.ConfigOverride.Gameplay, "SentisGameplayImprovements.AllGridsActions.EntitiesObserver");
            SkipUnless(observer != null, "SentisGameplayImprovements has no EntitiesObserver");
            yield return Arrive(Site.At(0, 0, -6));
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("CustomFloatingObjectsCleanup", true);
            var lifetime = (int)Scenarios.ConfigOverride.Get(Scenarios.ConfigOverride.Gameplay, "FloatingObjectsLifetime");
            SkipUnless(lifetime > 0 && lifetime < 10, "the loose items' lifetime is " + lifetime + " minutes");
            var oldAt = Site.At(0, 0, 0);
            var newAt = Site.At(10, 0, 0);
            foreach (var at in new[] { oldAt, newAt })
                Sandbox.Game.Entities.MyFloatingObjects.Spawn(new VRage.Game.Entity.MyPhysicalInventoryItem((VRage.MyFixedPoint)5,
                    (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(Item("Component/SteelPlate"))), at, Site.Forward, Site.Up);
            Sandbox.Game.Entities.MyFloatingObject At(Vector3D at) =>
                MyEntities.GetEntities().OfType<Sandbox.Game.Entities.MyFloatingObject>().FirstOrDefault(f => !f.MarkedForClose && Vector3D.Distance(f.PositionComp.GetPosition(), at) < 2);
            yield return Until(() => At(oldAt) != null && At(newAt) != null ? 1 : 0, v => v > 0, "the two loose stacks in the world", 20);
            var old = At(oldAt);
            var fresh = At(newAt);
            Track(old);
            Track(fresh);
            var noted = (System.Collections.Concurrent.ConcurrentDictionary<Sandbox.Game.Entities.MyFloatingObject, System.DateTime>)observer.GetField("MyFloatingObjects").GetValue(null);
            yield return Until(() => noted.ContainsKey(old) && noted.ContainsKey(fresh) ? 1 : 0, v => v > 0, "SGI noting the loose stacks", 10);
            noted[old] = System.DateTime.Now.AddMinutes(-10);
            yield return Until(() => old.MarkedForClose || old.Closed ? 1 : 0, v => v > 0, "the stack older than " + lifetime + " minutes removed", 20);
            Check(!fresh.MarkedForClose && !fresh.Closed, "the fresh stack was removed too (loose in the world: " + LooseItems + ")");
            Result("a stack 10 minutes old removed (lifetime " + lifetime + " min), the fresh one kept");
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }
}
