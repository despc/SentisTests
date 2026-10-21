using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The hand welders and grinders of every tier: each welds a frame to the end and grinds a block away, and a tier
    /// is not slower than the one below it.
    /// </summary>
    public sealed class HandToolTiersCheck : HandCheck
    {
        public override string Name => Prefix + "hand_tool_tiers";
        public override int TimeoutSeconds => 400;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            var times = new List<string>();
            foreach (var kind in new[] { "Welder", "AngleGrinder" })
            {
                var last = double.MaxValue;
                for (var tier = 1; tier <= 4; tier++)
                {
                    var item = kind + (tier == 1 ? "" : tier.ToString()) + "Item";
                    var hand = MyDefinitionManager.Static.TryGetHandItemForPhysicalItem(Item("PhysicalGunObject/" + item));
                    if (hand == null) { times.Add(item + " not in this game"); continue; }
                    var weld = kind == "Welder";
                    // each tool's block a place of its own along the line: the last one's grid may not be gone yet
                    var grid = Build("tier", Site.Origin + Site.Right * (5 * times.Count), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0).At(weld ? 0.05f : 1f));
                    var block = grid.GetCubeBlock(Vector3I.Zero);
                    if (weld) Put(Inventory(Body), "Component/SteelPlate", 30);
                    yield return Equip(item);
                    yield return AimAt(block);
                    var started = MySandboxGameTime();
                    yield return Pull();
                    if (weld)
                        yield return Until(() => block.Integrity / block.MaxIntegrity, v => v >= 0.999, item + " welding the frame", 120, () => GunState());
                    else
                        yield return Until(() => grid.MarkedForClose || grid.GetCubeBlock(Vector3I.Zero) == null ? 1 : 0, v => v > 0, item + " grinding the block away", 120, () => GunState());
                    var took = MySandboxGameTime() - started;
                    Release();
                    Check(took <= last * 1.15 + 0.5, item + " took " + F(took) + " s, slower than the tier below (" + F(last) + " s)");
                    last = took;
                    times.Add(item + " " + F(took) + " s");
                    grid.Close();
                    // what grinding gave back out of the way: the next tool starts on an empty pocket
                    Inventory(Body).Clear();
                    yield return Seconds(0.5);
                }
            }
            Result("every tier worked, none slower than the one below: " + string.Join(", ", times));
        }

        /// <summary>Game time in seconds.</summary>
        private static double MySandboxGameTime() => Sandbox.MySandboxGame.TotalGamePlayTimeInMilliseconds / 1000.0;
    }

    /// <summary>
    /// Every hand weapon of the game - pistols, rifles, rocket launchers - with its own magazine: aimed at a wall 15 m
    /// off and fired, the wall takes damage.
    /// </summary>
    public sealed class HandWeaponsCheck : HandCheck
    {
        public override string Name => Prefix + "hand_weapons";
        public override int TimeoutSeconds => 300;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            // at the game's low quality (its own under load) bullets check what they hit a third as often and fly through walls
            HoldHighQuality();
            yield return Arrive(Site.At(0, 0, -15));
            var guns = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyWeaponItemDefinition>()
                .Where(d => d.Public && d.Id.TypeId == typeof(VRage.Game.MyObjectBuilder_PhysicalGunObject))
                .Select(d => new { Item = d, Hand = MyDefinitionManager.Static.TryGetHandItemForPhysicalItem(d.Id), Weapon = MyDefinitionManager.Static.GetWeaponDefinition(d.WeaponDefinitionId) })
                .Where(g => g.Hand != null && g.Weapon?.AmmoMagazinesId != null && g.Weapon.AmmoMagazinesId.Length > 0)
                .OrderBy(g => g.Item.Id.SubtypeName).ToList();
            Check(guns.Count > 0, "no hand weapons in this game");
            var fired = new List<string>();
            foreach (var gun in guns)
            {
                var wall = Build("hand-gun-wall", Site.Origin, true, Bodies.Wall("CubeBlock/LargeHeavyBlockArmorBlock", 2, 1));
                Func<double> integrity = () => wall.MarkedForClose ? 0 : wall.GetBlocks().Sum(b => (double)b.Integrity);
                var magazine = gun.Weapon.AmmoMagazinesId[0];
                Inventory(Body).Clear();
                Inventory(Body).AddItems(3, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(magazine));
                yield return Equip(gun.Item.Id.SubtypeName);
                var spot = Site.Origin - Site.Forward * 15;
                // the astronaut left where they are put, not steered on their client's circle
                FakeClients.SetFree(Me, true);
                StandAt(spot, spot + Site.Forward);
                yield return null;
                var head = Body.GetHeadMatrix(false).Translation - Body.PositionComp.GetPosition();
                StandAt(spot - head, spot - head + Site.Forward);
                Body.SetHeadLocalXAngle(0);
                yield return WaitForTicks(3);
                var whole = integrity();
                yield return Pull();
                // a burst ends on its own (the gun waits at its burst limit for the trigger to be let go): while the wall
                // stands whole the trigger is let go and pressed again every 1.5 s of game time, as a player fires bursts
                var bursts = 1;
                var frames = 0;
                yield return Until(() =>
                {
                    if (++frames % Frames(1.5) == 0 && whole - integrity() <= 0)
                    {
                        Release();
                        Trigger();
                        bursts++;
                    }
                    return whole - integrity();
                }, v => v > 0, gun.Item.Id.SubtypeName + " hitting the wall", 40,
                    () => GunState() + ", magazines " + F((double)Inventory(Body).GetItemAmount(magazine)) + ", " + bursts + " bursts, " +
                          F(Vector3D.Distance(Body.PositionComp.GetPosition(), spot - head)) + " m from where the astronaut was put, looking " +
                          F(Motion.Angle(Body.GetHeadMatrix(false).Forward, Site.Forward)) + " degrees off the wall; " + Sight(wall));
                Release();
                fired.Add(gun.Item.Id.SubtypeName);
                yield return CloseAndWait(wall);
            }
            Result(guns.Count + " hand weapons fired and hit: " + string.Join(", ", fired));
        }

        /// <summary>The grid's own update state: the freezer's mark, its update needs, its queued callbacks, its pending damage.</summary>
        private static string Updates(MyCubeGrid grid)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            object Get(string name) => typeof(MyCubeGrid).GetField(name, flags)?.GetValue(grid);
            var queues = Get("m_updateQueues") as System.Collections.IEnumerable;
            var queued = new List<string>();
            if (queues != null)
                foreach (var queue in queues)
                    if (queue is System.Collections.ICollection list) queued.Add(list.Count.ToString());
            return "frozen mark " + (((int)grid.Flags & 4) != 0) + ", needs update " + grid.NeedsUpdate + ", queued after " + Get("m_totalQueuedSynchronousUpdatesAfter") +
                   " before " + Get("m_totalQueuedSynchronousUpdatesBefore") + ", queues [" + string.Join(" ", queued) + "], update in progress " + Get("m_updateInProgress") +
                   ", delayed " + Get("m_hasDelayedUpdate") + ", damage dirty " + Get("m_blocksForDamageApplicationDirty") + ", in scene " + grid.InScene + "; " + Orchestrator(grid);
        }

        /// <summary>What the game's update orchestrator knows of the grid: its lists, its record of the grid's flags, its pending changes.</summary>
        private static string Orchestrator(MyCubeGrid grid)
        {
            var orchestrator = MyEntities.Orchestrator;
            if (orchestrator == null) return "no orchestrator";
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            object Get(string name) => orchestrator.GetType().GetField(name, flags)?.GetValue(orchestrator);
            bool In(string name) => Get(name) is HashSet<VRage.Game.Entity.MyEntity> set && set.Contains(grid);
            var record = Get("m_lastUpdateRecord") is System.Collections.IDictionary records && records.Contains(grid) ? records[grid].ToString() : "none";
            return "orchestrator " + orchestrator.GetType().Name + ": in after " + In("m_entitiesForUpdateAfter") + ", in each frame " + In("m_entitiesForUpdate") +
                   ", to add " + In("m_entitiesToAdd") + ", to remove " + In("m_entitiesToRemove") + ", record " + record + ", grid's flags " + grid.UpdateFlags;
        }

        /// <summary>What the player's eye meets ahead, the wall's state and the game's simulation quality.</summary>
        private string Sight(MyCubeGrid wall)
        {
            var eye = Body.GetHeadMatrix(false);
            var hit = Sandbox.Engine.Physics.MyPhysics.CastRay(eye.Translation, eye.Translation + eye.Forward * 40, 15);
            var hitEntity = hit.HasValue ? ((VRage.Game.ModAPI.IHitInfo)hit.Value).HitEntity as VRage.Game.Entity.MyEntity : null;
            string Count(string field)
            {
                var set = typeof(MyEntities).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
                return set?.GetType().GetProperty("Count")?.GetValue(set)?.ToString() ?? "?";
            }
            var met = hit.HasValue ? hitEntity?.DisplayName + " " + hitEntity?.EntityId + (hitEntity == wall ? " (this wall)" : " (not this wall " + wall.EntityId + ")") +
                                     ", closed " + hitEntity?.Closed + ", marked for close " + hitEntity?.MarkedForClose + ", in scene " + hitEntity?.InScene +
                                     " at " + F(Vector3D.Distance(eye.Translation, hit.Value.Position)) + " m" : "nothing";
            met += "; it is pinned " + hitEntity?.Pinned + "; entities waiting to be deleted " + Count("m_entitiesToDelete") + ", for the next frame " + Count("m_entitiesToDeleteNextFrame");
            var updates = Updates(wall);
            return "the eye meets " + met + "; the wall " + wall.BlocksCount + " blocks, physics " + (wall.Physics?.Enabled == true) + ", closed " + wall.MarkedForClose +
                   "; " + updates + "; high simulation quality " + Sandbox.Game.World.MySession.Static.HighSimulationQuality;
        }
    }
}
