using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The common rig of the warhead scenarios: a place a kilometre up in the atmosphere with a
    /// player beside it (Havok steps only a cluster somebody is in), plates of light armor with
    /// warheads on them, and the plugin's explosions switched on.
    /// </summary>
    internal abstract class WarheadScenarioBase : TestScenario
    {
        protected const double AltitudeM = 1000;
        protected static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private static readonly FieldInfo IsExplodedField =
            typeof(MyWarhead).GetField("m_isExploded", BindingFlags.Instance | BindingFlags.NonPublic);

        protected readonly ConfigOverride Optimisations = new ConfigOverride(ConfigOverride.Optimisations);
        protected readonly ConfigOverride Gameplay = new ConfigOverride(ConfigOverride.Gameplay);

        protected abstract string Prefix { get; }
        protected abstract double SideOffsetM { get; }
        protected virtual int Players => 1;

        protected Vector3D Centre, Up, Side;

        /// <summary>Settings, the place and the player. Enumerate it first.</summary>
        protected IEnumerator Prepare()
        {
            WorldApi.EnsureUnpaused(Name);
            Optimisations.Set("FreezerEnabled", false);
            Optimisations.Set("EnablePhysicsGuard", false);
            Gameplay.Set("ExplosionTweaks", true);
            // no damage at all for the first seconds after a start otherwise
            Gameplay.Set("DisableAnyDamageAfterStartTime", 0);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            Up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            Side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(Up));
            Centre = anchorM.Translation + Up * AltitudeM + Side * SideOffsetM;

            FakeClients.RemoveAll();
            var player = Centre + Up * 60 + Side * 40;
            FakeClients.Add(Players, Network, p => (player + Side * (3 * p), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
        }

        /// <summary>
        /// A static grid: <paramref name="layers"/> layers of light armor, sizeX x sizeZ, the top
        /// layer at <paramref name="at"/>; and warheads on top of it where <paramref name="warheadAt"/> says.
        /// </summary>
        protected MyCubeGrid BuildPlate(Vector3D at, int sizeX, int sizeZ, int layers, string name, Func<int, int, bool> warheadAt = null,
            Func<int, int, string> onTop = null)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            var owner = WorldApi.PlayerIdentityId();
            void Add(string subtype, int x, int y, int z)
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(x - sizeX / 2, y, z - sizeZ / 2);
                block.Owner = owner;
                block.BuiltBy = owner;
                blocks.Add(block);
            }
            for (var y = 0; y < layers; y++)
                for (var x = 0; x < sizeX; x++)
                    for (var z = 0; z < sizeZ; z++)
                        Add("LargeBlockArmorBlock", x, -y, z);
            if (warheadAt != null)
                for (var x = 0; x < sizeX; x++)
                    for (var z = 0; z < sizeZ; z++)
                        if (warheadAt(x, z)) Add("LargeWarhead", x, 1, z);
            if (onTop != null)
                for (var x = 0; x < sizeX; x++)
                    for (var z = 0; z < sizeZ; z++)
                    {
                        var subtype = onTop(x, z);
                        if (subtype != null) Add(subtype, x, 1, z);
                    }

            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, Side, Up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        protected static bool Exploded(MyWarhead warhead) => (bool)IsExplodedField.GetValue(warhead);

        protected static void Arm(MyWarhead warhead) => ((Sandbox.ModAPI.IMyWarhead)warhead).IsArmed = true;

        protected static void Detonate(MyWarhead warhead) => ((Sandbox.ModAPI.IMyWarhead)warhead).Detonate();

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                Gameplay.Restore();
                Optimisations.Restore();
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// Friendly fire in the plugin's explosions, as the game has it: with EnableTurretsFriendlyFire
    /// off an explosion does not touch the grid it came from (OriginEntity - for a missile, the
    /// launcher) nor any grid joined to it; with it on it does. Two pairs of plates, a missile-sized
    /// explosion between the plates of each pair, fired "from" the first plate.
    /// </summary>
    internal sealed class ExplosionFriendlyFireScenario : WarheadScenarioBase
    {
        public const string ScenarioName = "explosion_friendly_fire";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;
        protected override string Prefix => "ff-";
        protected override double SideOffsetM => 700;

        private const int PlateSize = 6;
        private const double GapM = 5;
        private const double RadiusM = 10;
        private const float Damage = 20000;

        private bool? _friendlyFireWas;

        public override IEnumerator Run()
        {
            var prepare = Prepare();
            while (prepare.MoveNext()) yield return prepare.Current;
            _friendlyFireWas = MySession.Static.Settings.EnableTurretsFriendlyFire;

            foreach (var friendlyFire in new[] { false, true })
            {
                var pairCentre = Centre + Side * (friendlyFire ? 80 : 0);
                var half = PlateSize * 2.5 / 2 + GapM / 2;
                var own = BuildPlate(pairCentre - Side * half, PlateSize, PlateSize, 1, "own-" + friendlyFire);
                var other = BuildPlate(pairCentre + Side * half, PlateSize, PlateSize, 1, "other-" + friendlyFire);
                var settle = WaitForSeconds(3, "the plates settle");
                while (settle.MoveNext()) yield return settle.Current;

                MySession.Static.Settings.EnableTurretsFriendlyFire = friendlyFire;
                var ownBefore = own.CubeBlocks.Sum(b => b.Integrity);
                var otherBefore = other.CubeBlocks.Sum(b => b.Integrity);
                var sphere = new BoundingSphereD(pairCentre + Up * 2.5, RadiusM);
                var info = new MyExplosionInfo
                {
                    PlayerDamage = 0,
                    Damage = Damage,
                    ExplosionType = Sandbox.ModAPI.MyExplosionTypeEnum.MISSILE_EXPLOSION,
                    ExplosionSphere = sphere,
                    LifespanMiliseconds = 700,
                    ParticleScale = 1,
                    OriginEntity = own.EntityId,
                    Direction = (Vector3)(-Up),
                    VoxelExplosionCenter = sphere.Center,
                    ExplosionFlags = MyExplosionFlags.CREATE_DEBRIS | MyExplosionFlags.APPLY_FORCE_AND_DAMAGE |
                                     MyExplosionFlags.CREATE_DECALS | MyExplosionFlags.CREATE_PARTICLE_EFFECT |
                                     MyExplosionFlags.APPLY_DEFORMATION,
                    VoxelCutoutScale = 1,
                    PlaySound = true,
                    ApplyForceAndDamage = true,
                    ObjectsRemoveDelayInMiliseconds = 40,
                };
                MyExplosions.AddExplosion(ref info);
                var blast = WaitForSeconds(3, "the explosion, friendly fire " + (friendlyFire ? "on" : "off"));
                while (blast.MoveNext()) yield return blast.Current;

                var ownLost = ownBefore - own.CubeBlocks.Sum(b => b.Integrity);
                var otherLost = otherBefore - other.CubeBlocks.Sum(b => b.Integrity);
                Note("friendly fire " + (friendlyFire ? "on" : "off") + ": own plate lost " + ownLost.ToString("F0") +
                     " integrity (" + own.BlocksCount + "/" + PlateSize * PlateSize + " blocks left), the other " +
                     otherLost.ToString("F0") + " (" + other.BlocksCount + " left)");
                Check(otherLost > 0, "the explosion did nothing to the other grid with friendly fire " + (friendlyFire ? "on" : "off"));
                if (friendlyFire)
                    Check(ownLost > 0, "with friendly fire on the explosion spared the grid it came from");
                else
                    Check(ownLost <= 0.01f && own.BlocksCount == PlateSize * PlateSize,
                        "with friendly fire off the explosion damaged the grid it came from: " + ownLost.ToString("F0") + " integrity");
            }
            Note("FRIENDLY FIRE RESULT: as the game has it");
        }

        public override void Cleanup()
        {
            if (_friendlyFireWas.HasValue) MySession.Static.Settings.EnableTurretsFriendlyFire = _friendlyFireWas.Value;
            base.Cleanup();
        }
    }

    /// <summary>
    /// A chain of armed warheads, each 20 m from the next - within the reach of the one before
    /// (23 m) and out of the reach of the one before that. The first is set off; each of the others
    /// must go off by itself, a couple of frames after the one that reached it, not all in one
    /// explosion. An unarmed warhead beside the chain is destroyed but does not go off.
    /// </summary>
    internal sealed class WarheadChainScenario : WarheadScenarioBase
    {
        public const string ScenarioName = "warhead_chain";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;
        protected override string Prefix => "chain-";
        protected override double SideOffsetM => -200;

        private const int Count = 6;
        private const int Spacing = 8;          // cells: 20 m
        private const int MaxStepFrames = 10;   // a couple of frames, with room for a slow one

        public override IEnumerator Run()
        {
            var prepare = Prepare();
            while (prepare.MoveNext()) yield return prepare.Current;

            var length = (Count - 1) * Spacing + 1;
            var plate = BuildPlate(Centre, length, 5, 1, "plate",
                (x, z) => z == 2 && x % Spacing == 0 || x == Spacing * 2 && z == 0);
            var settle = WaitForSeconds(3, "the plate settles");
            while (settle.MoveNext()) yield return settle.Current;

            var all = plate.GetFatBlocks().OfType<MyWarhead>().ToList();
            var chain = all.Where(w => w.Position.Z == 2 - 5 / 2).OrderBy(w => w.Position.X).ToList();
            var unarmed = all.Except(chain).Single();
            Check(chain.Count == Count, "expected " + Count + " warheads in the chain, found " + chain.Count);
            foreach (var warhead in chain) Arm(warhead);
            var armed = WaitForTicks(10);
            while (armed.MoveNext()) yield return armed.Current;

            var explodedAt = new long?[Count];
            var start = MySession.Static.GameplayFrameCounter;
            Detonate(chain[0]);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (explodedAt.Any(f => f == null) && DateTime.UtcNow < deadline)
            {
                for (var i = 0; i < Count; i++)
                    if (explodedAt[i] == null && Exploded(chain[i])) explodedAt[i] = MySession.Static.GameplayFrameCounter - start;
                yield return null;
            }
            var settleAfter = WaitForSeconds(2, "the dust settles");
            while (settleAfter.MoveNext()) yield return settleAfter.Current;

            Note("chain: went off at frames " + string.Join(", ", explodedAt.Select(f => f?.ToString() ?? "never")) +
                 "; the unarmed one " + (Exploded(unarmed) ? "went off" : "did not go off") + ", " +
                 (unarmed.Closed || unarmed.MarkedForClose || unarmed.SlimBlock.IsDestroyed ? "destroyed" : "still there"));
            Check(explodedAt.All(f => f != null), "not every warhead of the chain went off: " + explodedAt.Count(f => f != null) + " of " + Count);
            for (var i = 1; i < Count; i++)
            {
                var step = explodedAt[i].Value - explodedAt[i - 1].Value;
                Check(step >= 1, "warhead " + i + " went off in the same frame as the one before it - one explosion for both");
                Check(step <= MaxStepFrames, "warhead " + i + " went off " + step + " frames after the one before it");
            }
            Check(!Exploded(unarmed), "an unarmed warhead went off");
            Note("CHAIN RESULT: " + Count + " warheads, frames " + string.Join(", ", explodedAt));
        }
    }

    /// <summary>
    /// A pilot in a cockpit an explosion destroys is thrown out alive, with 3% of their health;
    /// a pilot in a cockpit it does not reach sits on untouched. Two cockpits with a pilot each,
    /// one five metres from a warhead, the other a hundred.
    /// </summary>
    internal sealed class CockpitEjectScenario : WarheadScenarioBase
    {
        public const string ScenarioName = "cockpit_eject";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;
        protected override string Prefix => "eject-";
        protected override double SideOffsetM => 1000;

        protected override int Players => 2;

        public override IEnumerator Run()
        {
            var prepare = Prepare();
            while (prepare.MoveNext()) yield return prepare.Current;

            var near = BuildPlate(Centre, 7, 3, 1, "near", onTop: (x, z) => z != 1 ? null : x == 1 ? "LargeBlockCockpit" : x == 3 ? "LargeWarhead" : null);
            var far = BuildPlate(Centre + Side * 100, 3, 3, 1, "far", onTop: (x, z) => x == 1 && z == 1 ? "LargeBlockCockpit" : null);
            var settle = WaitForSeconds(3, "the grids settle");
            while (settle.MoveNext()) yield return settle.Current;

            var nearCockpit = WorldApi.FindFunctional<MyCockpit>(near);
            var farCockpit = WorldApi.FindFunctional<MyCockpit>(far);
            var warhead = WorldApi.FindFunctional<MyWarhead>(near);
            Check(nearCockpit != null && farCockpit != null && warhead != null, "the rig is missing a block");
            var nearPilot = FakeClients.Character(0);
            var farPilot = FakeClients.Character(1);
            Check(nearPilot != null && farPilot != null, "the fake players have no characters");
            // straight into the seat: the mod API's AttachPilot asks the pilot to walk up and use it
            nearCockpit.AttachPilot(nearPilot, -1);
            farCockpit.AttachPilot(farPilot, -1);
            FakeClients.TakeControl(0, nearCockpit);
            FakeClients.TakeControl(1, farCockpit);
            var seated = Wait(() => nearCockpit.Pilot == nearPilot && farCockpit.Pilot == farPilot, "the pilots sit down", 10);
            while (seated.MoveNext()) yield return seated.Current;

            Arm(warhead);
            Detonate(warhead);
            // Health is read the frame the pilot is out: it grows back by itself afterwards.
            var thrownOut = Wait(() => nearCockpit.Pilot != nearPilot, "the near pilot is thrown out", 10);
            while (thrownOut.MoveNext()) yield return thrownOut.Current;
            var leftWith = nearPilot.StatComp.Health.Value / nearPilot.StatComp.Health.MaxValue;
            var blast = WaitForSeconds(3, "the dust settles");
            while (blast.MoveNext()) yield return blast.Current;

            string State(MyCharacter pilot, MyCockpit cockpit)
            {
                var health = pilot.StatComp?.Health;
                var share = health == null ? -1 : health.Value / health.MaxValue;
                return (pilot.IsDead ? "dead" : "alive") + ", " + (cockpit.Pilot == pilot ? "seated" : "out") +
                       ", health " + (share * 100).ToString("F1") + "%, cockpit " +
                       (cockpit.Closed || cockpit.MarkedForClose || cockpit.SlimBlock.IsDestroyed ? "destroyed" : "standing");
            }
            float Share(MyCharacter pilot) => pilot.StatComp.Health.Value / pilot.StatComp.Health.MaxValue;

            Note("near pilot: thrown out with " + (leftWith * 100).ToString("F1") + "% health, now " + State(nearPilot, nearCockpit) +
                 "; far pilot: " + State(farPilot, farCockpit));
            Check(Exploded(warhead), "the warhead did not go off");
            Check(nearCockpit.Closed || nearCockpit.MarkedForClose || nearCockpit.SlimBlock.IsDestroyed, "the near cockpit was not destroyed");
            Check(!nearPilot.IsDead && !nearPilot.MarkedForClose, "the pilot of the destroyed cockpit died");
            Check(nearCockpit.Pilot != nearPilot, "the pilot of the destroyed cockpit is still in it");
            Check(Math.Abs(leftWith - 0.03f) < 0.002f, "the pilot of the destroyed cockpit was left with " + (leftWith * 100).ToString("F1") + "% health, not 3%");
            Check(farCockpit.Pilot == farPilot && !farPilot.IsDead && Share(farPilot) > 0.999f, "the far pilot was touched: " + State(farPilot, farCockpit));
            Note("COCKPIT EJECT RESULT: thrown out with " + (leftWith * 100).ToString("F1") + "% health");
        }
    }

    /// <summary>
    /// The loot of a destroyed block: a warhead ten metres from a jump drive destroys it, and its
    /// components fall out as floating objects where it was - each with its drop chance, once (the
    /// damage handlers are asked twice about an explosion's hit, and the loot used to come out
    /// double), and near the block, not 75 m away.
    /// </summary>
    internal sealed class LootJumpDriveScenario : WarheadScenarioBase
    {
        public const string ScenarioName = "loot_jumpdrive";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;
        protected override string Prefix => "loot-";
        protected override double SideOffsetM => 1300;

        private const double LootSeconds = 15;
        private readonly Dictionary<long, (string Subtype, int Amount, double DistanceM)> _spawned =
            new Dictionary<long, (string, int, double)>();

        // what only the jump drive has, and what of it falls out: count x drop chance
        private static readonly (string Subtype, int Expected)[] DriveOnly =
            { ("Superconductor", 300), ("PowerCell", 36), ("Computer", 30) };
        private const double NearM = 30;
        private Action<VRage.Game.Entity.MyEntity> _onEntityAdd;

        public override IEnumerator Run()
        {
            var prepare = Prepare();
            while (prepare.MoveNext()) yield return prepare.Current;
            Gameplay.Set("LootSystemEnabled", true);

            var grid = BuildPlate(Centre, 8, 3, 1, "grid", onTop: (x, z) => z != 1 ? null : x == 1 ? "LargeJumpDrive" : x == 5 ? "LargeWarhead" : null);
            var settle = WaitForSeconds(3, "the grid settles");
            while (settle.MoveNext()) yield return settle.Current;
            var drive = WorldApi.FindFunctional<Sandbox.Game.Entities.MyJumpDrive>(grid);
            var warhead = WorldApi.FindFunctional<MyWarhead>(grid);
            Check(drive != null && warhead != null, "the rig is missing a block");

            var drivePosition = drive.PositionComp.GetPosition();
            _onEntityAdd = entity =>
            {
                if (!(entity is MyFloatingObject floating)) return;
                var distance = Vector3D.Distance(floating.PositionComp.GetPosition(), drivePosition);
                if (distance < 200)
                    _spawned[floating.EntityId] = (floating.Item.Content?.SubtypeName ?? "?", (int)floating.Item.Amount, distance);
            };
            MyEntities.OnEntityAdd += _onEntityAdd;

            Arm(warhead);
            Detonate(warhead);
            var loot = WaitForSeconds(LootSeconds, "the loot falls out");
            while (loot.MoveNext()) yield return loot.Current;

            var destroyed = drive.Closed || drive.MarkedForClose || drive.SlimBlock.IsDestroyed;
            var kinds = string.Join(", ", _spawned.Values.Select(v => v.Subtype + " " + v.Amount + " at " + v.DistanceM.ToString("F0") + " m"));
            Note("jump drive " + (destroyed ? "destroyed" : "standing, integrity " + drive.SlimBlock.Integrity.ToString("F0")) +
                 "; " + _spawned.Count + " floating objects: " + kinds);
            Check(destroyed, "the jump drive survived the warhead");
            Check(_spawned.Count > 0, "the destroyed jump drive dropped nothing");
            foreach (var (subtype, expected) in DriveOnly)
            {
                var got = _spawned.Values.Where(v => v.Subtype == subtype).Sum(v => v.Amount);
                Check(got == expected, subtype + ": " + got + " fell out, expected " + expected);
            }
            var farthest = _spawned.Values.Max(v => v.DistanceM);
            Check(farthest < NearM, "loot fell " + farthest.ToString("F0") + " m from the jump drive");
            Note("LOOT RESULT: " + _spawned.Count + " floating objects: " + kinds);
        }

        public override void Cleanup()
        {
            if (_onEntityAdd != null) MyEntities.OnEntityAdd -= _onEntityAdd;
            base.Cleanup();
        }
    }

    /// <summary>
    /// A thousand armed warheads on a big grid - 50 x 20 of them on three layers of armor. Either
    /// one in a corner is set off and the chain runs through the rest, or all are set off in the
    /// same frame. Every warhead must go off; the frames are measured from the first explosion to
    /// the last. The phase is announced for the profiler a few seconds ahead.
    /// </summary>
    internal sealed class WarheadMassScenario : WarheadScenarioBase
    {
        public const string ChainScenarioName = "warhead_mass_chain";
        public const string AllScenarioName = "warhead_mass_all";

        private const int SizeX = 50, SizeZ = 20, Layers = 3;
        private const int MaxSeconds = 120;

        private readonly bool _all;

        public WarheadMassScenario(bool all) { _all = all; }

        public override string Name => _all ? AllScenarioName : ChainScenarioName;
        public override int TimeoutSeconds => 300;
        protected override string Prefix => _all ? "massall-" : "masschain-";
        protected override double SideOffsetM => -450;

        public override IEnumerator Run()
        {
            var prepare = Prepare();
            while (prepare.MoveNext()) yield return prepare.Current;

            var grid = BuildPlate(Centre, SizeX, SizeZ, Layers, "grid", (x, z) => true);
            var settle = WaitForSeconds(5, "the grid settles");
            while (settle.MoveNext()) yield return settle.Current;
            var warheads = grid.GetFatBlocks().OfType<MyWarhead>().ToList();
            Note("grid: " + grid.BlocksCount + " blocks, " + warheads.Count + " warheads");
            Check(warheads.Count == SizeX * SizeZ, "expected " + SizeX * SizeZ + " warheads, found " + warheads.Count);
            foreach (var warhead in warheads) Arm(warhead);

            Note("PROFILE WINDOW START " + Name);
            var attach = WaitForSeconds(8, "the profiler attaches");
            while (attach.MoveNext()) yield return attach.Current;

            TickMetrics.Take();
            FrameProbe.Take();
            var start = MySession.Static.GameplayFrameCounter;
            var started = DateTime.UtcNow;
            if (_all)
                foreach (var warhead in warheads) Detonate(warhead);
            else
                Detonate(warheads.OrderBy(w => w.Position.X + w.Position.Z).First());

            var exploded = 0;
            var lastFrame = 0L;
            var lastProgress = DateTime.UtcNow;
            var done = new HashSet<MyWarhead>();
            while ((DateTime.UtcNow - started).TotalSeconds < MaxSeconds && (DateTime.UtcNow - lastProgress).TotalSeconds < 15)
            {
                foreach (var warhead in warheads)
                    if (!done.Contains(warhead) && Exploded(warhead))
                    {
                        done.Add(warhead);
                        lastFrame = MySession.Static.GameplayFrameCounter - start;
                        lastProgress = DateTime.UtcNow;
                    }
                exploded = done.Count;
                if (exploded == warheads.Count) break;
                if (DateTime.UtcNow.Second % 5 == 0 && DateTime.UtcNow.Millisecond < 20)
                    Note(exploded + " of " + warheads.Count + " went off");
                yield return null;
            }
            // The damage of the explosions may come after them: wait until the grid stops changing.
            var blocks = grid.BlocksCount;
            var lastChangeFrame = MySession.Static.GameplayFrameCounter - start;
            var stillSince = DateTime.UtcNow;
            while ((DateTime.UtcNow - stillSince).TotalSeconds < 3 && (DateTime.UtcNow - started).TotalSeconds < MaxSeconds + 60)
            {
                if (grid.BlocksCount != blocks)
                {
                    blocks = grid.BlocksCount;
                    lastChangeFrame = MySession.Static.GameplayFrameCounter - start;
                    stillSince = DateTime.UtcNow;
                }
                yield return null;
            }
            var frames = TickMetrics.Take();
            var work = FrameProbe.Take();

            var result = exploded + " of " + warheads.Count + " went off over " + lastFrame + " frames (" +
                         (lastFrame / 60.0).ToString("F1") + " s), the damage done by frame " + lastChangeFrame + " (" +
                         (lastChangeFrame / 60.0).ToString("F1") + " s), " + grid.BlocksCount + " blocks left | " + frames.Format() + " | " + work;
            Note("WARHEAD MASS RESULT (" + (_all ? "all at once" : "chain") + "): " + result);
            Check(exploded == warheads.Count, "not every warhead went off: " + exploded + " of " + warheads.Count);
        }
    }
}
