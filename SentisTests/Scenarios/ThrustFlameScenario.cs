using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A thruster's flame burns what stands in it while the thruster gives thrust, and burns nothing
    /// while it is idle (SentisOptimisations' IdleThrustDamage), over a flame as long as the thrust
    /// makes it; and the thruster's ten-frame update turns off on the server (ParallelUpdateTweaks
    /// still clears the flag the skipped flame render would).
    ///
    /// A static grid with an atmospheric thruster and a charged battery, a kilometre up with a player
    /// beside it; a block of light armour, on a grid of its own, in front of the nozzle - farther than
    /// the flame reaches with no thrust, nearer than it reaches with full thrust. A static grid is not
    /// pushed by its thrusters, so the scenario sets the thrust itself, every frame.
    ///
    ///  1. idle, for a few damage ticks: the armour keeps all of its integrity;
    ///  2. full thrust, for a few damage ticks: the armour loses integrity.
    /// </summary>
    public sealed class ThrustFlameScenario : TestScenario
    {
        public const string ScenarioName = "thrust_flame";
        private const string Prefix = "flame-";
        private const double AltitudeM = 1000;
        private const double SiteOffsetM = 700;     // away from the explosions' site
        private const double IdleSeconds = 6;       // a damage tick is every 100 frames
        private const double BurnSeconds = 8;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(Sandbox.Game.World.MySession.Static.ThrusterDamage, "thruster damage is off in this world");
            _optimisations.Set("FreezerEnabled", false);
            // The plugin cancels every bit of damage for the first seconds after a start.
            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);

            // ------------------------------------------------------------- where
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var centre = anchorM.Translation + up * AltitudeM + side * SiteOffsetM;

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (centre + up * 30 + side * 30, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            // ------------------------------------------------------------- the thruster
            var engineGrid = SpawnEngine(centre, up, side);
            var thrust = engineGrid.GetFatBlocks().OfType<MyThrust>().FirstOrDefault();
            Check(thrust != null, "the engine has no thruster");
            var settle = WaitForSeconds(2, "the engine powers up");
            while (settle.MoveNext()) yield return settle.Current;
            Check(thrust.IsWorking, "the thruster does not work (power: " + thrust.IsPowered + ")");
            var flame = thrust.Flames.FirstOrDefault(f => f.HasDamage);
            Check(thrust.Flames.Any(f => f.HasDamage), "the thruster has no flame that burns");

            // Where the flame ends: with no thrust it is its radius long, with full thrust at least
            // 2 * (10 * 0.6 * FlameLengthScale * radius / 2 * FlameDamageLengthScale) - radius.
            var def = thrust.BlockDefinition;
            var matrix = thrust.WorldMatrix;
            var start = Vector3D.Transform(flame.Position, matrix);
            var dir = Vector3D.Normalize(Vector3D.TransformNormal(flame.Direction, matrix));
            var shortest = 2 * (10 * 0.6 * def.FlameLengthScale * flame.Radius * 0.5 * def.FlameDamageLengthScale) - flame.Radius;
            Note("flame: radius " + flame.Radius.ToString("0.00") + " m, at full thrust at least " + shortest.ToString("0.00") + " m");
            Check(shortest > flame.Radius + 1, "the flame at full thrust is hardly longer than with none; nothing to tell apart");
            var nearFace = (flame.Radius + shortest) / 2;

            var target = SpawnArmor(start + dir * (nearFace + 1.25), up, side);
            var armor = target.CubeBlocks.First();
            var full = armor.Integrity;

            // ------------------------------------------------------------- 1. idle
            var frames = 0;
            var idleUntil = DateTime.UtcNow.AddSeconds(IdleSeconds);
            while (DateTime.UtcNow < idleUntil)
            {
                thrust.CurrentStrength = 0f;
                frames++;
                yield return null;
            }
            var afterIdle = armor.Integrity;
            var update10 = (thrust.NeedsUpdate & MyEntityUpdateEnum.EACH_10TH_FRAME) != 0;
            Note("idle " + frames + " frames: armour " + afterIdle.ToString("0.0") + " of " + full.ToString("0.0") +
                 "; thruster's ten-frame update " + (update10 ? "on" : "off"));
            Check(afterIdle >= full, "an idle thruster burnt the armour: " + afterIdle + " of " + full);
            Check(!update10, "the thruster's ten-frame update never turns off");

            // ------------------------------------------------------------- 2. full thrust
            frames = 0;
            var burnUntil = DateTime.UtcNow.AddSeconds(BurnSeconds);
            while (DateTime.UtcNow < burnUntil && !target.MarkedForClose && armor.Integrity >= full)
            {
                thrust.CurrentStrength = 1f;
                frames++;
                yield return null;
            }
            thrust.CurrentStrength = 0f;
            var afterBurn = target.MarkedForClose ? 0f : armor.Integrity;
            Note("full thrust " + frames + " frames: armour " + afterBurn.ToString("0.0") + " of " + full.ToString("0.0") +
                 ", its near face " + nearFace.ToString("0.00") + " m from the nozzle");
            Check(afterBurn < full, "the flame at full thrust did not burn the armour " + nearFace.ToString("0.00") + " m away");
        }

        private MyCubeGrid SpawnEngine(Vector3D at, Vector3D up, Vector3D side)
        {
            var owner = WorldApi.PlayerIdentityId();
            var thruster = WorldApi.MakeBlockOb("LargeBlockSmallAtmosphericThrust");
            thruster.Min = new SerializableVector3I(0, 0, 0);
            var battery = WorldApi.MakeBlockOb("LargeBlockBatteryBlock");
            battery.Min = new SerializableVector3I(0, 3, 0);
            if (battery is MyObjectBuilder_BatteryBlock charged)
            {
                charged.CurrentStoredPower = 3f;
                charged.ProducerEnabled = true;
            }
            var blocks = new List<MyObjectBuilder_CubeBlock> { thruster, battery };
            // armour between them, so the grid holds together
            for (var y = 1; y < 3; y++)
            {
                var link = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                link.Min = new SerializableVector3I(0, y, 0);
                blocks.Add(link);
            }
            foreach (var block in blocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
            }
            return Spawn("engine", at, up, side, blocks);
        }

        private MyCubeGrid SpawnArmor(Vector3D at, Vector3D up, Vector3D side)
        {
            var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
            var owner = WorldApi.PlayerIdentityId();
            block.Owner = owner;
            block.BuiltBy = owner;
            return Spawn("target", at, up, side, new List<MyObjectBuilder_CubeBlock> { block });
        }

        private MyCubeGrid Spawn(string name, Vector3D at, Vector3D up, Vector3D side, List<MyObjectBuilder_CubeBlock> blocks)
        {
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _gameplay.Restore();
                _optimisations.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
