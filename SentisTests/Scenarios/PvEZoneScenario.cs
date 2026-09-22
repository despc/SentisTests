using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The PvE zone of SentisGameplayImprovements: a sphere in which grids take no damage from other players.
    ///
    /// Two fake players, Alice and Bob, and an NPC identity. Each case hits a block of a grid and checks
    /// whether it lost integrity:
    /// <list type="bullet">
    /// <item>Alice's grid in the zone: Bob's grid, Bob's character and an explosion nobody owns (a missile of
    /// a player who is offline) do nothing; Alice's own grid does; an NPC only with EnableDamageFromNPC;</item>
    /// <item>a cargo drop container ("Container MK-") in the zone is not protected;</item>
    /// <item>the same hits on Alice's grid outside the zone go through (the control);</item>
    /// <item>a grid moved into the zone is protected in the same frame, and nothing is with the zone off;</item>
    /// <item>real drills: Bob's does not cut Alice's grid in the zone, Alice's own does, Bob's does outside;</item>
    /// <item>a ram at 100 m/s: no damage to a grid in the zone, damage to the same grid outside;</item>
    /// <item>Alice's character in the zone: Bob's grid, character, ship and an explosion nobody owns do
    /// nothing; an NPC (NPC damage off or on), her own explosion, the ground and a hit with no one behind it
    /// hurt; outside the zone Bob's grid hurts.</item>
    /// </list>
    /// </summary>
    internal sealed class PvEZoneScenario : TestScenario
    {
        public const string ScenarioName = "pve_zone";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;

        private const string Prefix = "pve-";
        private const int ZoneRadius = 400;
        private const float Hit = 200f;
        private const float RamSpeed = 100;
        private const int DrillSeconds = 4;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private readonly List<string> _failures = new List<string>();
        private readonly List<string> _results = new List<string>();
        private Vector3D _up, _side, _forward;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            _up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            _forward = Vector3D.Cross(_side, _up);
            Vector3D centre = default;
            for (var d = 100000.0; d <= 1000000; d += 50000)
            {
                centre = anchor + _up * d + _side * 20000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f) break;
            }
            var outside = centre + _side * 5000;

            _gameplay.Set("PvEZoneEnabled", true);
            _gameplay.Set("PveZonePos", centre.X.ToString("R", CultureInfo.InvariantCulture) + ":" +
                                        centre.Y.ToString("R", CultureInfo.InvariantCulture) + ":" +
                                        centre.Z.ToString("R", CultureInfo.InvariantCulture));
            _gameplay.Set("PveZoneRadius", ZoneRadius);
            _gameplay.Set("EnableDamageFromNPC", false);
            // SentisGameplayImprovements lets no damage through for a while after the server starts
            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);

            FakeClients.RemoveAll();
            FakeClients.Add(2, Network, p => (centre + _up * 300 + _side * (20 * p), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            var aliceCharacter = FakeClients.Character(0);
            var bobCharacter = FakeClients.Character(1);
            Check(aliceCharacter != null && bobCharacter != null, "the fake players have no characters");
            var alice = aliceCharacter.GetPlayerIdentityId();
            var bob = bobCharacter.GetPlayerIdentityId();
            Check(alice != 0 && bob != 0 && alice != bob, "the fake players have no identities of their own");
            Check(MySession.Static.Factions.TryGetPlayerFaction(alice) == null && MySession.Static.Factions.TryGetPlayerFaction(bob) == null,
                "a fake player is in a faction");
            var npc = NpcIdentity();
            Note("zone of radius " + ZoneRadius + " m; Alice " + alice + ", Bob " + bob + ", NPC " + npc);

            // ------------------------------------------------------------- hits
            var aliceIn = Target("alice-in", centre, alice);
            var containerIn = Target("Container MK-1", centre + _side * 60, alice);
            var aliceOut = Target("alice-out", outside, alice);
            var mover = Target("alice-mover", outside + _side * 60, alice);
            var bobGun = Target("bob-gun", centre + _up * 150 + _side * 100, bob);
            var aliceGun = Target("alice-gun", centre + _up * 150 + _side * 130, alice);
            var npcGun = Target("npc-gun", centre + _up * 150 + _side * 160, npc);
            var settle = WaitForSeconds(2, "the grids settle");
            while (settle.MoveNext()) yield return settle.Current;
            foreach (var (grid, owner) in new[] { (aliceIn, alice), (containerIn, alice), (aliceOut, alice), (mover, alice), (bobGun, bob), (aliceGun, alice), (npcGun, npc) })
                Check(grid.BigOwners.Contains(owner), grid.DisplayName + " is not owned by " + owner);

            { var e = Expect("in the zone: Bob's grid", aliceIn, bobGun.EntityId, MyDamageType.Bullet, false); while (e.MoveNext()) yield return e.Current; }
            { var e = Expect("in the zone: Bob's character", aliceIn, bobCharacter.EntityId, MyDamageType.Bullet, false); while (e.MoveNext()) yield return e.Current; }
            { var e = Expect("in the zone: an explosion nobody owns", aliceIn, 0, MyDamageType.Explosion, false); while (e.MoveNext()) yield return e.Current; }
            { var e = Expect("in the zone: Alice's own grid", aliceIn, aliceGun.EntityId, MyDamageType.Bullet, true); while (e.MoveNext()) yield return e.Current; }
            { var e = Expect("in the zone: an NPC, NPC damage off", aliceIn, npcGun.EntityId, MyDamageType.Bullet, false); while (e.MoveNext()) yield return e.Current; }
            _gameplay.Set("EnableDamageFromNPC", true);
            { var e = Expect("in the zone: an NPC, NPC damage on", aliceIn, npcGun.EntityId, MyDamageType.Bullet, true); while (e.MoveNext()) yield return e.Current; }
            _gameplay.Set("EnableDamageFromNPC", false);
            { var e = Expect("in the zone: a cargo drop container", containerIn, bobGun.EntityId, MyDamageType.Bullet, true); while (e.MoveNext()) yield return e.Current; }
            { var e = Expect("outside: Bob's grid", aliceOut, bobGun.EntityId, MyDamageType.Bullet, true); while (e.MoveNext()) yield return e.Current; }
            { var e = Expect("outside: an explosion nobody owns", aliceOut, 0, MyDamageType.Explosion, true); while (e.MoveNext()) yield return e.Current; }

            var into = mover.WorldMatrix;
            into.Translation = centre - _side * 60;
            mover.Teleport(into);
            { var e = Expect("moved into the zone this frame: Bob's grid", mover, bobGun.EntityId, MyDamageType.Bullet, false); while (e.MoveNext()) yield return e.Current; }
            _gameplay.Set("PvEZoneEnabled", false);
            { var e = Expect("the zone off: Bob's grid", aliceIn, bobGun.EntityId, MyDamageType.Bullet, true); while (e.MoveNext()) yield return e.Current; }
            _gameplay.Set("PvEZoneEnabled", true);

            // ------------------------------------------------------------- characters
            // Alice stands in the zone (the players were placed 300 m over its centre)
            var steps = new (string What, long Attacker, MyStringHash Type, bool Damaged)[]
            {
                ("Alice in the zone: Bob's grid", bobGun.EntityId, MyDamageType.Bullet, false),
                ("Alice in the zone: Bob's character", bobCharacter.EntityId, MyDamageType.Bullet, false),
                ("Alice in the zone: an explosion nobody owns", 0, MyDamageType.Explosion, false),
                ("Alice in the zone: Bob's ship runs into her", bobGun.EntityId, MyDamageType.Environment, false),
                ("Alice in the zone: an NPC, NPC damage off", npcGun.EntityId, MyDamageType.Bullet, true),
                // the game itself ignores damage whose attacker is the character hurt, so her own explosion
                // comes from her own grid (a warhead, a launcher of hers)
                ("Alice in the zone: an explosion of her own grid", aliceGun.EntityId, MyDamageType.Explosion, true),
                ("Alice in the zone: she hits the ground", planet.EntityId, MyDamageType.Environment, true),
                ("Alice in the zone: a hit with no one behind it", 0, MyDamageType.Environment, true),
            };
            Check(PveSphere(centre).Contains(aliceCharacter.PositionComp.GetPosition()) == ContainmentType.Contains, "Alice is not in the zone");
            foreach (var step in steps)
            {
                var e = ExpectCharacter(step.What, aliceCharacter, step.Attacker, step.Type, step.Damaged);
                while (e.MoveNext()) yield return e.Current;
            }
            FakeClients.MoveTo(0, outside + _up * 300);
            yield return null;
            yield return null;
            Check(PveSphere(centre).Contains(aliceCharacter.PositionComp.GetPosition()) != ContainmentType.Contains, "Alice did not leave the zone");
            { var e = ExpectCharacter("Alice outside: Bob's grid", aliceCharacter, bobGun.EntityId, MyDamageType.Bullet, true); while (e.MoveNext()) yield return e.Current; }

            // ------------------------------------------------------------- drills
            var drills = new List<(string What, MyCubeGrid Target, MyCubeGrid Rig, bool Damaged)>
            {
                ("Bob's drill on Alice's grid in the zone", Slab("drill-in-bob", centre + _side * 200, alice), null, false),
                ("Alice's drill on her own grid in the zone", Slab("drill-in-alice", centre + _side * 240, alice), null, true),
                ("Bob's drill on Alice's grid outside", Slab("drill-out-bob", outside + _side * 200, alice), null, true),
            };
            var rigs = new[] { bob, alice, bob };
            for (var i = 0; i < drills.Count; i++)
                drills[i] = (drills[i].What, drills[i].Target, DrillRig("rig-" + i, drills[i].Target.PositionComp.WorldAABB.Center, rigs[i]), drills[i].Damaged);
            yield return null;
            yield return null;
            var before = new List<Hurt>();
            foreach (var d in drills)
            {
                AimDrill(d.Rig, d.Target);
                before.Add(Health(d.Target));
            }
            var placed = WaitForSeconds(1, "the drill rigs are placed");
            while (placed.MoveNext()) yield return placed.Current;
            foreach (var d in drills)
            {
                WorldApi.EnsureDistributor(d.Rig);
                WorldApi.ChargeBatteries(d.Rig);
                foreach (var drill in WorldApi.FindFunctionals<MyShipDrill>(d.Rig))
                    ((Sandbox.ModAPI.IMyFunctionalBlock)drill).Enabled = true;
            }
            yield return null;
            foreach (var d in drills)
            {
                var drill = WorldApi.FindFunctional<MyShipDrill>(d.Rig);
                var box = d.Target.PositionComp.WorldAABB;
                var battery = WorldApi.FindFunctional<Sandbox.Game.Entities.MyBatteryBlock>(d.Rig);
                var distributor = d.Rig.Components.Get<Sandbox.Game.EntityComponents.MyResourceDistributorComponent>();
                Note(d.What + ": battery working " + battery?.IsWorking + ", enabled " + battery?.Enabled + ", stored " + battery?.CurrentStoredPower +
                     ", mode " + battery?.ChargeMode + "; distributor " + (distributor == null ? "none" :
                         distributor.ResourceStateByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId).ToString()) +
                     "; drill needs " + drill.ResourceSink?.RequiredInputByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId));
                Note(d.What + ": drill working " + drill.IsWorking + ", functional " + drill.IsFunctional + ", enabled " + drill.Enabled +
                     ", powered " + (drill.ResourceSink?.IsPoweredByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId) ?? false) +
                     ", drill centre " + box.Distance(drill.PositionComp.GetPosition()).ToString("F2") + " m from the target's box");
            }
            var drilling = WaitForSeconds(DrillSeconds, "drilling");
            while (drilling.MoveNext()) yield return drilling.Current;
            for (var i = 0; i < drills.Count; i++)
            {
                var after = Health(drills[i].Target);
                Report(drills[i].What, !Same(before[i], after), drills[i].Damaged, Describe(before[i], after));
                foreach (var drill in WorldApi.FindFunctionals<MyShipDrill>(drills[i].Rig))
                    ((Sandbox.ModAPI.IMyFunctionalBlock)drill).Enabled = false;
            }

            // ------------------------------------------------------------- rams
            foreach (var (what, at, damaged) in new[] { ("Bob's ship rams Alice's grid in the zone", centre - _up * 150, false),
                                                        ("Bob's ship rams Alice's grid outside", outside - _up * 150, true) })
            {
                var wall = Slab(what.Replace(' ', '-').Replace('\'', '-'), at, alice);
                var ship = Ship(at - _forward * 160, bob);
                var place = Place(wall, at, ship, at - _forward * 160);
                while (place.MoveNext()) yield return place.Current;
                var wallBefore = Health(wall);
                ship.Physics.SetSpeeds(_forward * RamSpeed, Vector3.Zero);
                var hitAt = -1;
                for (var f = 0; f < 300; f++)
                {
                    yield return null;
                    if (hitAt < 0 && ship.Physics != null && ship.Physics.LinearVelocity.Length() < RamSpeed * 0.5f) hitAt = f;
                }
                // untouched must not mean missed
                if (!damaged && hitAt < 0) _failures.Add(what + ": the ship never hit the wall");
                var wallAfter = Health(wall);
                Report(what, !Same(wallBefore, wallAfter), damaged, Describe(wallBefore, wallAfter));
            }

            Note("PVE ZONE RESULT: " + string.Join(" || ", _results));
            Check(_failures.Count == 0, string.Join("; ", _failures));
        }

        /// <summary>
        /// One hit on an armour block of <paramref name="target"/>; whether it lost integrity is checked against
        /// <paramref name="damaged"/>. The grid takes the damage off the block's integrity on its own update, so
        /// the check waits a few frames.
        /// </summary>
        private IEnumerator Expect(string what, MyCubeGrid target, long attackerEntityId, MyStringHash type, bool damaged)
        {
            var block = target.CubeBlocks.First(b => b.FatBlock == null && b.Integrity > Hit * 2);
            var before = block.Integrity;
            block.DoDamage(Hit, type, true, null, attackerEntityId);
            for (var f = 0; f < 3; f++) yield return null;
            var lost = before - block.Integrity;
            Report(what, lost > 0.001f, damaged, "lost " + lost.ToString("F0"));
        }

        private const float CharacterHit = 5f;

        private static BoundingSphereD PveSphere(Vector3D centre) => new BoundingSphereD(centre, ZoneRadius);

        /// <summary>One hit on a character; whether it lost health is checked against <paramref name="damaged"/>.</summary>
        private IEnumerator ExpectCharacter(string what, Sandbox.Game.Entities.Character.MyCharacter character, long attackerEntityId,
            MyStringHash type, bool damaged)
        {
            var before = character.StatComp.Health.Value;
            ((VRage.Game.ModAPI.Interfaces.IMyDestroyableObject)character).DoDamage(CharacterHit, type, true, null, attackerEntityId);
            for (var f = 0; f < 3; f++) yield return null;
            var lost = before - character.StatComp.Health.Value;
            // the air and the cold may take a little on their own meanwhile
            Report(what, lost > CharacterHit / 2, damaged, "lost " + lost.ToString("F1") + " health");
        }

        private void Report(string what, bool wasDamaged, bool expected, string detail)
        {
            var line = what + ": " + (wasDamaged ? "damaged" : "untouched") + " (" + detail + ")";
            _results.Add(line);
            if (wasDamaged != expected)
            {
                _failures.Add(what + ": expected " + (expected ? "damage" : "none") + ", got " + detail);
                Note("FAIL " + line);
            }
            else Note(line);
        }

        private static long NpcIdentity()
        {
            var players = MySession.Static.Players;
            var identity = players.GetAllIdentities().FirstOrDefault(i => i.DisplayName == "SentisTests PvE NPC")
                           ?? players.CreateNewIdentity("SentisTests PvE NPC");
            players.MarkIdentityAsNPC(identity.IdentityId);
            return identity.IdentityId;
        }

        /// <summary>A static plate of light armour 3 x 1 x 3 and a battery owned by <paramref name="owner"/>, so the grid is theirs.</summary>
        private MyCubeGrid Target(string name, Vector3D at, long owner)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
            for (var z = 0; z < 3; z++)
                blocks.Add(Block("LargeBlockArmorBlock", x, 0, z, owner));
            blocks.Add(Battery(1, 1, 1, owner));
            return Spawn(name, at, true, blocks, owner);
        }

        /// <summary>
        /// A static slab of light armour 5 x 5 x 2 owned by <paramref name="owner"/>, its face at z = 1 towards
        /// whatever comes along <see cref="_forward"/>, and a battery on the far side (z = -1).
        /// </summary>
        private MyCubeGrid Slab(string name, Vector3D at, long owner)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 5; x++)
            for (var y = 0; y < 5; y++)
            for (var z = 0; z < 2; z++)
                blocks.Add(Block("LargeBlockArmorBlock", x, y, z, owner));
            blocks.Add(Battery(2, 2, -1, owner));
            var grid = Spawn(name, at, true, blocks, owner);
            return grid;
        }

        /// <summary>A static rig of a large drill (3 x 3 x 3 cells) facing forward and a battery behind it.</summary>
        private MyCubeGrid DrillRig(string name, Vector3D near, long owner)
        {
            var drill = Block("LargeBlockDrill", 0, 0, 0, owner);
            ((MyObjectBuilder_FunctionalBlock)drill).Enabled = false;
            var blocks = new List<MyObjectBuilder_CubeBlock> { drill, Battery(1, 1, 3, owner) };
            return Spawn(name, near - _forward * 30, true, blocks, owner);
        }

        /// <summary>Puts the rig's drill head against the middle of the slab's near face, pointing into it.</summary>
        private void AimDrill(MyCubeGrid rig, MyCubeGrid target)
        {
            var drill = WorldApi.FindFunctional<MyShipDrill>(rig);
            // the slab's +Z faces the rig (the grid's forward is _forward, its +Z is backwards)
            var face = target.GridIntegerToWorld(new Vector3I(2, 2, 1)) - _forward * (target.GridSize / 2);
            // the drill's centre half its length (along its forward) back from the face, and a little more
            var want = face - _forward * (drill.BlockDefinition.Size.Z * rig.GridSize / 2 + 0.3);
            var matrix = rig.WorldMatrix;
            matrix.Translation += want - drill.PositionComp.GetPosition();
            rig.Teleport(matrix);
        }

        /// <summary>A ship of light armour 3 x 3 x 6 with a battery.</summary>
        private MyCubeGrid Ship(Vector3D at, long owner)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
            for (var y = 0; y < 3; y++)
            for (var z = 0; z < 6; z++)
                blocks.Add(Block("LargeBlockArmorBlock", x, y, z, owner));
            blocks.Add(Battery(1, 3, 3, owner));
            return Spawn("ram-ship", at, false, blocks, owner);
        }

        private IEnumerator Place(MyCubeGrid wall, Vector3D wallAt, MyCubeGrid ship, Vector3D shipAt)
        {
            yield return null;
            yield return null;
            foreach (var (grid, at) in new[] { (wall, wallAt), (ship, shipAt) })
            {
                var matrix = grid.WorldMatrix;
                matrix.Translation += at - grid.PositionComp.WorldAABB.Center;
                grid.Teleport(matrix);
                grid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
            }
            var settle = WaitForSeconds(2, "the grids settle");
            while (settle.MoveNext()) yield return settle.Current;
        }

        private static MyObjectBuilder_CubeBlock Block(string subtype, int x, int y, int z, long owner)
        {
            var block = WorldApi.MakeBlockOb(subtype);
            block.Min = new SerializableVector3I(x, y, z);
            block.Owner = owner;
            block.BuiltBy = owner;
            block.ShareMode = MyOwnershipShareModeEnum.None;
            return block;
        }

        private static MyObjectBuilder_CubeBlock Battery(int x, int y, int z, long owner)
        {
            var battery = (MyObjectBuilder_BatteryBlock)Block("LargeBlockBatteryBlock", x, y, z, owner);
            battery.CurrentStoredPower = 3f;
            return battery;
        }

        private MyCubeGrid Spawn(string name, Vector3D at, bool isStatic, List<MyObjectBuilder_CubeBlock> blocks, long owner)
        {
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = name.StartsWith("Container MK-") ? name : WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _forward, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            return grid;
        }

        private struct Hurt
        {
            public int Blocks;
            public double Integrity;
            public int Deformed;
        }

        private static Hurt Health(MyCubeGrid grid)
        {
            var hurt = new Hurt();
            if (grid == null || grid.MarkedForClose) return hurt;
            foreach (var block in grid.CubeBlocks)
            {
                hurt.Blocks++;
                hurt.Integrity += block.Integrity;
                if (block.HasDeformation) hurt.Deformed++;
            }
            return hurt;
        }

        private static bool Same(Hurt a, Hurt b) =>
            a.Blocks == b.Blocks && a.Deformed == b.Deformed && Math.Abs(a.Integrity - b.Integrity) < 0.001;

        private static string Describe(Hurt a, Hurt b) =>
            Same(a, b) ? a.Blocks + " blocks untouched" :
            "blocks " + a.Blocks + "->" + b.Blocks + ", integrity " + a.Integrity.ToString("F0") + "->" + b.Integrity.ToString("F0") +
            ", deformed " + a.Deformed + "->" + b.Deformed;

        public override void Cleanup()
        {
            try
            {
                _gameplay.Restore();
                FakeClients.RemoveAll();
            }
            catch (Exception e)
            {
                Log.Warn("cleaning up after the test failed: " + e.Message);
            }
            finally { base.Cleanup(); }
        }
    }
}
