using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Which way a large landing gear locks: six small grids, each an armor plate with one
    /// LargeBlockLandingGear under it in one of the six "forward" directions, dropped onto the
    /// ground next to WHEEL_TEST with autolock on. Reports which orientations locked.
    /// </summary>
    public sealed class GearProbeScenario : TestScenario
    {
        public const string ScenarioName = "gear_probe";
        private static readonly Base6Directions.Direction[] Forwards =
        {
            Base6Directions.Direction.Down, Base6Directions.Direction.Up, Base6Directions.Direction.Forward,
            Base6Directions.Direction.Backward, Base6Directions.Direction.Left, Base6Directions.Direction.Right,
        };

        public override string Name => ScenarioName;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private ulong? _protected;

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                if (_protected.HasValue) Sandbox.Game.World.MySession.Static?.RemoteAdminSettings.Remove(_protected.Value);
                _protected = null;
            }
            finally { base.Cleanup(); }
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "gear-probe-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            var east = Vector3D.Normalize(Vector3D.Cross(up, Vector3D.Forward));
            var grids = new List<MyCubeGrid>();
            for (var i = 0; i < Forwards.Length; i++)
            {
                var guess = anchor + east * (300 + 25 * i);
                var surface = planet.GetClosestSurfacePointGlobal(ref guess);
                var forward = Forwards[i];
                var gearUp = Base6Directions.GetPerpendicular(forward);
                var owner = WorldApi.PlayerIdentityId();
                var blocks = new List<MyObjectBuilder_CubeBlock>();
                for (var x = 0; x < 5; x++)
                for (var z = 0; z < 5; z++)
                {
                    var armor = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                    armor.Min = new SerializableVector3I(x, 5, z);
                    armor.Owner = owner;
                    blocks.Add(armor);
                }

                var gearOb = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_LandingGear)WorldApi.MakeBlockOb("LargeBlockLandingGear");
                gearOb.Min = new SerializableVector3I(2, 0, 2);
                gearOb.BlockOrientation = new SerializableBlockOrientation(forward, gearUp);
                gearOb.AutoLock = true;
                gearOb.Owner = owner;
                blocks.Add(gearOb);
                var ob = new MyObjectBuilder_CubeGrid
                {
                    Name = WorldApi.EntityPrefix + "gear-probe-" + forward,
                    DisplayName = WorldApi.EntityPrefix + "gear-probe-" + forward,
                    GridSizeEnum = MyCubeSize.Large,
                    IsStatic = false,
                    PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(surface + up * 6, Vector3D.Normalize(Vector3D.Cross(east, up)), up)),
                    PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                    CubeBlocks = blocks,
                };
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                grids.Add(grid);
                yield return null;
            }
            // a player by the plates: with selective physics updates a cluster with nobody in it is not stepped, and the
            // plates hung where they appeared - every orientation "Unlocked" (stand, 10.10.2026, after the scenario
            // before had taken its player away)
            var watcherAt = planet.GetClosestSurfacePointGlobal(anchor + east * (300 + 25 * Forwards.Length / 2.0) + Vector3D.Normalize(Vector3D.Cross(east, up)) * 40);
            FakeClients.Add(1, Network, p => (watcherAt + up * 3, 0, 0), withCharacters: true);
            var steamId = FakeClients.PlayerOf(0).Id.SteamId;
            _protected = steamId;
            Sandbox.Game.World.MySession.Static.RemoteAdminSettings[steamId] =
                Sandbox.Game.World.AdminSettingsEnum.Invulnerable | Sandbox.Game.World.AdminSettingsEnum.Untargetable;
            var wait = WaitForSeconds(12, "gears fall and lock");
            while (wait.MoveNext()) yield return wait.Current;
            var report = new List<string>();
            for (var i = 0; i < grids.Count; i++)
            {
                var g = WorldApi.FindFunctionals<SpaceEngineers.Game.Entities.Blocks.MyLandingGear>(grids[i]).FirstOrDefault();
                report.Add("Forward=" + Forwards[i] + " Up=" + Base6Directions.GetPerpendicular(Forwards[i]) + ": " +
                           (g == null ? "no gear" : g.LockMode.ToString()));
            }
            Note("GEAR PROBE: " + string.Join("; ", report));
        }
    }
}
