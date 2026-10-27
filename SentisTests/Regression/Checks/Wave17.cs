using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A refinery and an assembler on the conveyors of a container send their output into it once it is over three
    /// quarters full (as the game does).
    /// </summary>
    public sealed class ConveyorPushCheck : RegressionCheck
    {
        public override string Name => Prefix + "conveyor_push";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 12, -10));
            var grid = Build("conveyor-push", Site.Origin, true,
                new Part("Refinery/LargeRefinery", 0, 0, 0),
                new Part("Assembler/LargeAssembler", -3, 1, 1),
                new Part("CargoContainer/LargeBlockLargeContainer", -7, -1, 0),
                new Part("Reactor/LargeBlockSmallGenerator", -2, -1, 1),
                new Part("Conveyor/LargeBlockConveyor", -1, 0, 1),
                new Part("Conveyor/LargeBlockConveyor", -2, 0, 1),
                new Part("Conveyor/LargeBlockConveyor", -3, 0, 1),
                new Part("Conveyor/LargeBlockConveyor", -4, 0, 1));
            var cargo = Inventory(Block<MyCargoContainer>(grid));
            var refinery = Block<MyRefinery>(grid);
            var assembler = Block<MyAssembler>(grid);
            var reactor = Block<MyReactor>(grid);
            Put(Inventory(reactor), "Ingot/Uranium", 5);
            ((Sandbox.ModAPI.IMyTerminalBlock)reactor).SetValueBool("UseConveyor", false);
            // the game sends a machine's output down the conveyors once its output inventory is over three quarters full:
            // each filled to 80 %
            Func<Sandbox.Game.MyInventory, string, double> eighty = (inventory, item) =>
                (double)inventory.MaxVolume * 0.8 / Sandbox.Definitions.MyDefinitionManager.Static.GetPhysicalItemDefinition(Item(item)).Volume;
            var ingots = Math.Floor(eighty(refinery.OutputInventory, "Ingot/Iron"));
            var plates = Math.Floor(eighty(assembler.OutputInventory, "Component/SteelPlate"));
            Put(refinery.OutputInventory, "Ingot/Iron", ingots);
            Put(assembler.OutputInventory, "Component/SteelPlate", plates);
            yield return Until(() => Amount(cargo, "Ingot/Iron"), v => v > 0, "the refinery's ingots sent into the container", 30, () => State(refinery) + ", ingots kept " + F(Amount(refinery.OutputInventory, "Ingot/Iron")));
            yield return Until(() => Amount(cargo, "Component/SteelPlate"), v => v > 0, "the assembler's plates sent into the container", 30, () => State(assembler) + ", plates kept " + F(Amount(assembler.OutputInventory, "Component/SteelPlate")));
            Result("outputs at 80 %: into the container " + F(Amount(cargo, "Ingot/Iron")) + " kg of iron from the refinery, " + Amount(cargo, "Component/SteelPlate") + " plates from the assembler");
        }
    }

    /// <summary>A hinge locked while still holds at the speed set; unlocked, it moves.</summary>
    public sealed class HingeLockCheck : RegressionCheck
    {
        public override string Name => Prefix + "hinge_lock";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -8));
            var grid = Build("hinge-lock", Site.Origin, true,
                new Part("MotorAdvancedStator/LargeHinge", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var hinge = Block<MyMotorAdvancedStator>(grid);
            yield return AddHead(hinge);
            var ingame = (Sandbox.ModAPI.Ingame.IMyMotorStator)hinge;
            ingame.TargetVelocityRPM = 0;
            yield return Seconds(0.5);
            ingame.RotorLock = true;
            yield return Seconds(1);
            ingame.TargetVelocityRPM = 3;
            yield return Seconds(0.5);
            var angle = ingame.Angle;
            yield return Seconds(2);
            var locked = Math.Abs(ingame.Angle - angle);
            Check(locked < 0.01, "the locked hinge moved " + F(MathHelper.ToDegrees(locked)) + "°");
            ingame.RotorLock = false;
            yield return Until(() => Math.Abs(ingame.Angle - angle), v => v > 0.1, "the unlocked hinge moving", 10, () => State(hinge));
            Result("locked at 3 rpm the hinge held (" + F(MathHelper.ToDegrees(locked)) + "° in 2 s); unlocked it moved");
        }
    }

    /// <summary>
    /// A planetary encounter, as the game sends one to a player on a planet: an NPC installation set down on the ground
    /// some kilometres from where the player is (here a spot on the other side of the planet: the game keeps 7 km off structures).
    /// Removed after, and with it the generator's record of it.
    /// </summary>
    public sealed class PlanetaryEncounterCheck : RegressionCheck
    {
        public override string Name => Prefix + "planetary_encounter";
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => 120;

        /// <summary>The generator's installations: what each was spawned of, its grids, how far from the site.</summary>
        private string Installations(object generator, Type type)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var registry = type.GetField("m_installationsRegistry", flags)?.GetValue(generator);
            var data = registry?.GetType().GetMethod("GetInstallationsData")?.Invoke(registry, null) as IEnumerable;
            if (data == null) return "?";
            var lines = data.Cast<object>().Select(d =>
            {
                var position = (Vector3D)d.GetType().GetField("Position").GetValue(d);
                var associated = (IEnumerable)d.GetType().GetField("AssociatedGrids").GetValue(d);
                return d.GetType().GetField("SpawnGroupSubtype").GetValue(d) + " " + F(Vector3D.Distance(position, Site.Origin) / 1000) + " km, grids " + associated.Cast<object>().Count();
            }).ToList();
            return lines.Count + (lines.Count > 0 ? ": " + string.Join("; ", lines) : "");
        }

        /// <summary>The generator's installations within the distance of the point marked for removal; how many.</summary>
        private static int StaleAt(object generator, Type type, Vector3D point, double distance)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var registry = type.GetField("m_installationsRegistry", flags)?.GetValue(generator);
            var list = registry?.GetType().GetField("m_installations", flags)?.GetValue(registry) as IEnumerable;
            var remove = registry?.GetType().GetMethod("MarkForRemoval", flags);
            if (list == null || remove == null) return 0;
            var count = 0;
            foreach (var installation in list.Cast<object>().ToList())
            {
                var data = installation.GetType().GetField("Data", flags)?.GetValue(installation) ?? installation.GetType().GetProperty("Data", flags)?.GetValue(installation);
                if (data == null) continue;
                var position = (Vector3D)data.GetType().GetField("Position").GetValue(data);
                if (Vector3D.Distance(position, point) > distance) continue;
                remove.Invoke(registry, new[] { installation });
                count++;
            }
            return count;
        }

        protected override IEnumerator Check()
        {
            SkipUnless(Sandbox.Game.World.MySession.Static.Settings.EnablePlanetaryEncounters, "planetary encounters are off in this world");
            var type = typeof(SpaceEngineers.Game.Entities.Blocks.MyGravityGenerator).Assembly.GetType("SpaceEngineers.Game.SessionComponents.MyPlanetaryEncountersGenerator");
            var get = typeof(Sandbox.Game.World.MySession).GetMethods().FirstOrDefault(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var generator = type == null || get == null ? null : get.MakeGenericMethod(type).Invoke(Sandbox.Game.World.MySession.Static, null);
            SkipUnless(generator != null, "no planetary encounters in this game");
            yield return Arrive(Site.Origin + Site.Up * 3);
            Note("installations the generator knows: " + Installations(generator, type));
            var grids = new System.Collections.Generic.List<MyCubeGrid>();
            Action<System.Collections.Generic.List<MyCubeGrid>, Sandbox.Definitions.MySpawnGroupDefinition, Sandbox.Definitions.MySpawnGroupDefinition.SpawnGroupPrefab, long> created =
                (list, group, prefab, owner) => { if (group.IsPlanetaryEncounter) grids.AddRange(list); };
            // the other side of the planet: the sites spread tens of km over this one
            var far = 2 * Site.Planet.PositionComp.GetPosition() - Site.Origin;
            var spot = Site.Planet.GetClosestSurfacePointGlobal(ref far);
            spot += Vector3D.Normalize(spot - Site.Planet.PositionComp.GetPosition()) * 3;
            // the installations an earlier run left at the spot (one that did not end; their grids gone, the records kept for
            // the game to set them down again when a player comes): the game keeps a new one off them, and none came in
            // six tries (27.10.2026). Taken out of the generator's registry, the next frame does it
            var stale = StaleAt(generator, type, spot, 20000);
            if (stale > 0)
            {
                yield return WaitForTicks(2);
                Note(stale + " installations of earlier runs taken away from the spot");
            }
            Sandbox.Definitions.MySpawnGroupDefinition.OnSpawnGroupGridsCreated += created;
            try
            {
                // the game looks for a free place at random, 7 km off any structure: the sites are full of them, so the
                // encounter is sent round a spot on the ground on the other side of the planet (as for a player there); tried again until it finds one
                Func<MyCubeGrid, double> off = g => Vector3D.Distance(g.PositionComp.GetPosition(), spot);
                for (var attempt = 0; attempt < 6 && grids.Count == 0; attempt++)
                {
                    type.GetMethod("SpawnAroundPosition").Invoke(generator, new object[] { spot });
                    for (var f = Frames(10); f > 0 && grids.Count == 0; f--) yield return null;
                }
                Check(grids.Count > 0, "no planetary encounter came in 6 tries");
                // the installation is made and then set down where it goes: its place by the second
                var track = new System.Collections.Generic.List<string>();
                for (var i = 0; i < 15 && !grids.Any(g => off(g) < 100000 && g.Physics != null); i++)
                {
                    track.Add(string.Join("/", grids.Select(g => F(off(g) / 1000))));
                    yield return Seconds(1);
                }
                Note("the encounter's grids, km from the player by the second: " + string.Join(" ", track));
            }
            finally
            {
                Sandbox.Definitions.MySpawnGroupDefinition.OnSpawnGroupGridsCreated -= created;
                foreach (var g in grids) Track(g);
            }
            // the game keeps it 7 km off any structure (PlanetaryEncounterExistingStructuresRange): with the sites about, further
            var main = grids.Where(g => Vector3D.Distance(g.PositionComp.GetPosition(), spot) < 20000).OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            Check(main != null, "the encounter is not near the spot it was sent to: " + string.Join(", ", grids.Select(g => g.DisplayName + " " + F(Vector3D.Distance(g.PositionComp.GetPosition(), spot) / 1000) + " km")));
            var distance = Vector3D.Distance(main.PositionComp.GetPosition(), spot);
            var owner = main.BigOwners.FirstOrDefault();
            Check(owner != 0 && Sandbox.Game.World.MySession.Static.Players.IdentityIsNpc(owner), "the encounter is owned by " + owner + ", not an NPC");
            var centre = main.PositionComp.WorldAABB.Center;
            var ground = Site.Planet.GetClosestSurfacePointGlobal(ref centre);
            var height = Vector3D.Distance(centre, ground);
            Check(height < main.PositionComp.WorldAABB.HalfExtents.Length() + 30, "the encounter is " + F(height) + " m over the ground");
            Result("planetary encounter " + main.DisplayName + " (" + grids.Count + " grids, " + main.BlocksCount + " blocks), NPC-owned, set down " + F(distance) + " m from the spot it was sent to, " + F(height) + " m from the ground to its middle");
        }
    }

    /// <summary>
    /// A broadcast controller sends its message to the player seated on its grid: one of its messages transmitted, the
    /// chat message of its channel goes to that player (MyMultiplayer.SendChatMessage, seen by a patch of the check's).
    /// </summary>
    public sealed class BroadcastControllerCheck : RegressionCheck
    {
        public override string Name => Prefix + "broadcast_controller";

        private static HarmonyLib.Harmony _harmony;
        private static readonly List<string> Sent = new List<string>();

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 2, 4));
            var grid = Build("broadcast", Site.Origin, true,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BroadcastController/LargeBlockBroadcastController", 0, 1, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var seat = Block<MyCockpit>(grid);
            var controller = grid.GetFatBlocks().First(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("BroadcastController"));
            var chat = controller.Components.Get<SpaceEngineers.Game.EntityComponents.Blocks.MyChatBroadcastEntityComponent>();
            Check(chat != null, "the broadcast controller has no chat component");
            yield return Sit(seat);
            var marker = "regression " + Identity;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            ((string[])typeof(SpaceEngineers.Game.EntityComponents.Blocks.MyChatBroadcastEntityComponent).GetField("m_messages", flags).GetValue(chat))[0] = marker;
            Patch();
            lock (Sent) Sent.RemoveAll(m => m.Contains(marker));
            Request(chat, "OnTransmitMessage", 0);
            var expected = marker + " -> " + Identity;
            yield return Until(() => { lock (Sent) return Sent.Count(m => m.StartsWith(marker) && m.EndsWith(" -> " + Identity)); }, v => v > 0, "the controller's message to the seated player", 10,
                () => { lock (Sent) return "sent: " + string.Join("; ", Sent.Where(m => m.Contains(marker))); });
            Result("the broadcast controller's message went to the player seated on its grid, on its own chat channel");
        }

        private static void Patch()
        {
            if (_harmony != null) return;
            // the override the server's multiplayer runs, declared where it is (a patch takes the declaring type's method)
            System.Reflection.MethodInfo method = null;
            for (var type = Sandbox.Engine.Multiplayer.MyMultiplayer.Static.GetType(); type != null && method == null; type = type.BaseType)
                method = type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "SendChatMessage" && m.GetParameters().Length == 4 && !m.IsAbstract);
            if (method == null) throw new Core.ScenarioFailedException("no SendChatMessage of the server's multiplayer");
            _harmony = new HarmonyLib.Harmony("SentisTests.BroadcastController");
            _harmony.Patch(method, prefix: new HarmonyLib.HarmonyMethod(typeof(BroadcastControllerCheck).GetMethod(nameof(Seen), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        private static void Seen(string text, Sandbox.Game.Gui.ChatChannel channel, long targetId)
        {
            if (channel != Sandbox.Game.Gui.ChatChannel.BroadcastController || text == null || !text.StartsWith("regression ")) return;
            lock (Sent) Sent.Add(text + " -> " + targetId);
        }
    }
}
