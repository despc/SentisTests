using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A turret with nothing around to shoot at still takes a target the moment one comes near
    /// (SentisOptimisations' TurretIdleSearch skips the searches that can only find nothing).
    ///
    /// A static grid with an interior turret, a charged battery and ammunition, a kilometre up. A
    /// player stands 2 km away: the grid is sent to that player, so its turret keeps searching as on a
    /// live server, but nothing is within the turret's reach.
    ///
    ///  1. nobody near, for a few seconds: the turret has no target;
    ///  2. the player comes 60 m from the turret: the turret takes him as its target within
    ///     <see cref="AcquireSeconds"/> - a grid rescans what is around it every 100 frames, a turret
    ///     searches every 10.
    /// </summary>
    public sealed class TurretTargetScenario : TestScenario
    {
        public const string ScenarioName = "turret_target";
        private const string Prefix = "turret-";
        private const double AltitudeM = 1000;
        private const double SiteOffsetM = 1400;    // away from the explosions' and the flame's sites
        private const double QuietSeconds = 4;
        private const double AcquireSeconds = 5;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _optimisations.Set("FreezerEnabled", false);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var centre = anchorM.Translation + up * AltitudeM + side * SiteOffsetM;

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (centre + side * 2000 + up * 30, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var grid = SpawnTurret(centre, up, side);
            var turret = grid.GetFatBlocks().OfType<MyLargeTurretBase>().FirstOrDefault();
            Check(turret != null, "the grid has no turret");
            var magazine = turret.GunBase.CurrentAmmoMagazineId;
            var ammo = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine);
            turret.GetInventory(0).AddItems((MyFixedPoint)20, ammo);

            // ------------------------------------------------------------- 1. nobody near
            var quiet = WaitForSeconds(QuietSeconds, "the turret stands with nobody near");
            while (quiet.MoveNext()) yield return quiet.Current;
            Note("nobody near: turret " + (turret.IsWorking ? "working" : "not working") + ", grid seen by a player: " +
                 (grid.PlayerPresenceTier == VRage.Game.ModAPI.MyUpdateTiersPlayerPresence.Normal) + ", ammo " +
                 turret.GetInventory(0).GetItemAmount(magazine) + " " + magazine.SubtypeName +
                 ", target " + (turret.TargetingSystem.Target?.DisplayName ?? "none"));
            Check(turret.IsWorking, "the turret does not work");
            Check(turret.TargetingSystem.Target == null, "the turret has a target with nobody near: " + turret.TargetingSystem.Target);

            // ------------------------------------------------------------- 2. the player comes near
            var character = FakeClients.Character(0);
            Check(character != null, "the player has no character");
            FakeClients.MoveTo(0, turret.PositionComp.GetPosition() + side * 60 + up * 5);
            var watch = Stopwatch.StartNew();
            var frames = 0;
            while (watch.Elapsed.TotalSeconds < AcquireSeconds && turret.TargetingSystem.Target == null)
            {
                frames++;
                yield return null;
            }
            var target = turret.TargetingSystem.Target;
            ((Sandbox.ModAPI.IMyFunctionalBlock)turret).Enabled = false;    // before it shoots the player
            Note("the player 60 m away: target " + (target == null ? "none" : target == character ? "the player" : target.DisplayName) +
                 " after " + watch.Elapsed.TotalSeconds.ToString("0.00") + " s (" + frames + " frames)");
            if (target != character) Note("why: " + Diagnose(turret, character));
            Check(target == character, "the turret did not take the player 60 m away as its target in " + AcquireSeconds + " s");
        }

        /// <summary>What the turret knew about the player when it did not take him.</summary>
        private static string Diagnose(MyLargeTurretBase turret, Sandbox.Game.Entities.Character.MyCharacter character)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance |
                                                       System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var targeting = turret.CubeGrid.Components.Get<Sandbox.Game.EntityComponents.MyGridTargeting>();
            var roots = targeting == null ? null :
                typeof(Sandbox.Game.EntityComponents.MyGridTargeting).GetField("m_targetRoots", any)?.GetValue(targeting) as List<VRage.Game.Entity.MyEntity>;
            var lastScan = targeting == null ? -1 :
                (int)typeof(Sandbox.Game.EntityComponents.MyGridTargeting).GetField("m_lastScan", any).GetValue(targeting);
            var identity = character.GetPlayerIdentityId();
            var relation = Sandbox.Game.Entities.MyIDModule.GetRelationPlayerPlayer(turret.OwnerId, identity);
            var isTarget = typeof(MyLargeTurretBase).GetMethod("IsTarget", any, null, new[] { typeof(VRage.Game.Entity.MyEntity) }, null);
            return "relation " + relation + ", targets characters " + turret.TargetCharacters +
                   ", search range " + turret.SearchRange + " m, distance " +
                   Vector3D.Distance(turret.PositionComp.GetPosition(), character.PositionComp.GetPosition()).ToString("0") + " m" +
                   ", roots " + (roots == null ? "?" : roots.Count + (roots.Contains(character) ? " with the player" : " without the player")) +
                   ", scan " + (Sandbox.Game.World.MySession.Static.GameplayFrameCounter - lastScan) + " frames old" +
                   ", searching " + turret.TargetingSystem.ParallelTargetSelectionInProcess +
                   ", IsTarget " + (isTarget == null ? "?" : isTarget.Invoke(turret, new object[] { character })) +
                   ", character dead " + character.IsDead;
        }

        private MyCubeGrid SpawnTurret(Vector3D at, Vector3D up, Vector3D side)
        {
            // Not the operator's identity: that can be the fake player's own, and a turret does not
            // shoot at its owner.
            var owner = WorldApi.TestIdentityId();
            var turret = WorldApi.MakeBlockOb("LargeInteriorTurret");
            turret.Min = new SerializableVector3I(0, 0, 0);
            var battery = WorldApi.MakeBlockOb("LargeBlockBatteryBlock");
            battery.Min = new SerializableVector3I(0, -1, 0);
            if (battery is MyObjectBuilder_BatteryBlock charged)
            {
                charged.CurrentStoredPower = 3f;
                charged.ProducerEnabled = true;
            }
            var blocks = new List<MyObjectBuilder_CubeBlock> { turret, battery };
            foreach (var block in blocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
            }
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "grid",
                DisplayName = WorldApi.EntityPrefix + Prefix + "grid",
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
                _optimisations.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
