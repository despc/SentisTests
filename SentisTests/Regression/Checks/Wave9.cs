using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// An NPC cargo ship, as the global event "SpawnCargoShip" brings it: spawned near a player (the check's own: while it
    /// picks, the online players are only them), owned by an NPC, on its autopilot (a remote control or AI blocks), and
    /// flying once the player is by it (further than 10 km from any player the SO freezer holds it). The ship is removed after.
    /// </summary>
    public sealed class CargoShipCheck : RegressionCheck
    {
        public override string Name => Prefix + "cargo_ship";

        private static HarmonyLib.Harmony _harmony;
        private static MyPlayer _only;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.CargoShipsEnabled, "cargo ships are off in this world");
            SkipUnless(!MySandboxGame.Static.LimitNPCsOnLowMemory, "the game spawns no NPCs on low memory");
            SkipUnless(MySession.Static.NPCBlockLimits.HasRemainingPCU(false), "the NPCs' PCU is spent");
            yield return Arrive(Site.At(0, 0, -3));
            var grids = new List<MyCubeGrid>();
            var mine = false;
            // the game's own cargo timer can spawn a ship for another player while this one waits: take only those
            // that came within the box round this check's player the game spawns it in. The player's character can be
            // gone by the last tries under the suite's load, so the spot they were put at, not their live position
            var here = Site.At(0, 0, -3);
            var far = new List<string>();
            Action<List<MyCubeGrid>, MySpawnGroupDefinition, MySpawnGroupDefinition.SpawnGroupPrefab, long> created =
                (list, group, prefab, owner) =>
                {
                    if (!mine || !group.IsCargoShip) return;
                    grids.AddRange(list.Where(g => Vector3D.Distance(g.PositionComp.GetPosition(), here) < 40000));
                    far.AddRange(list.Where(g => Vector3D.Distance(g.PositionComp.GetPosition(), here) >= 40000)
                        .Select(g => g.DisplayName + " " + F(Vector3D.Distance(g.PositionComp.GetPosition(), here)) + " m off"));
                };
            Patch();
            MySpawnGroupDefinition.OnSpawnGroupGridsCreated += created;
            try
            {
                // the event as the game raises it; a try that finds no free place is tried again, as the game does
                for (var attempt = 0; attempt < 10 && grids.Count == 0; attempt++)
                {
                    mine = true;
                    _only = Player;
                    try { MyNeutralShipSpawner.OnGlobalSpawnEvent(null); }
                    finally { _only = null; }
                    yield return Seconds(5);
                }
            }
            finally
            {
                mine = false;
                MySpawnGroupDefinition.OnSpawnGroupGridsCreated -= created;
                foreach (var g in grids) Track(g);
            }
            Check(grids.Count > 0, "no cargo ship spawned by the player in 10 tries" + (far.Count > 0 ? " (further than 40 km: " + string.Join(", ", far) + ")" : ""));
            var ship = grids.OrderByDescending(g => g.BlocksCount).First();
            // within 40 km of the player by the take above
            var distance = Vector3D.Distance(ship.PositionComp.GetPosition(), here);
            var owner = ship.BigOwners.FirstOrDefault();
            Check(owner != 0 && MySession.Static.Players.IdentityIsNpc(owner), "the cargo ship is owned by " + owner + ", not an NPC");
            // flown by a remote control's autopilot, AI blocks, or (with neither) the game's simple autopilot on the grid
            var remote = grids.SelectMany(g => g.GetFatBlocks()).OfType<MyRemoteControl>().FirstOrDefault();
            var ai = grids.SelectMany(g => g.GetFatBlocks()).Any(b => b.GetType().Name.Contains("MyFlightMovementBlock"));
            // the game puts it on the edge of a box of 8 km round the player, out to 14 km: past the SO freezer's 10 km it
            // stands frozen until a player comes near. The player flies up to it, as one meeting it does
            var near = ship.PositionComp.GetPosition() + Vector3D.Normalize(here - ship.PositionComp.GetPosition()) * 400;
            StandAt(near, ship.PositionComp.GetPosition());
            yield return Until(() => ship.Physics?.LinearVelocity.Length() ?? 0, v => v > 1, "the cargo ship under way with the player by it", 60,
                () => "physics " + (ship.Physics == null ? "none" : "active " + ship.Physics.IsActive) + ", the player " + F(Vector3D.Distance(ship.PositionComp.GetPosition(), Body.PositionComp.GetPosition())) + " m off"
                      + (remote == null && !ai
                          ? "; a ship of the game's simple autopilot (MySimpleAutopilot) gives no thrust, it coasts on the speed it was spawned with: the SO freezer set that to 0 (FreezeLogic, SetSpeeds) and nothing starts it again"
                          : ""));
            var from = ship.PositionComp.GetPosition();
            var trace = new List<string>();
            for (var i = 0; i < 10; i++)
            {
                yield return Seconds(1);
                trace.Add(F(ship.Physics?.LinearVelocity.Length() ?? -1));
            }
            var rc = remote as Sandbox.ModAPI.Ingame.IMyRemoteControl;
            Check(!ship.MarkedForClose && !ship.Closed, "the cargo ship was removed in flight; its speed by the second: " + string.Join(", ", trace));
            Note("the ship's speed by the second: " + string.Join(", ", trace) + "; physics " + (ship.Physics == null ? "none" : "enabled " + ship.Physics.Enabled + ", active " + ship.Physics.IsActive)
                 + ", static " + ship.IsStatic + ", dampeners " + ship.EntityThrustComponent?.DampenersEnabled + ", remote " + (rc == null ? "none" : "autopilot " + rc.IsAutoPilotEnabled + ", working " + remote.IsWorking + ", speed limit " + rc.SpeedLimit)
                 + ", thrusters " + ship.GetFatBlocks().Count(f => f is Sandbox.Game.Entities.MyThrust) + ", powered " + ship.GridSystems?.ResourceDistributor?.ResourceStateByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId)
                 + ", the player " + F(Vector3D.Distance(ship.PositionComp.GetPosition(), Body.PositionComp.GetPosition())) + " m off");
            var moved = Vector3D.Distance(from, ship.PositionComp.GetPosition());
            Check(moved > 20, "the cargo ship moved " + F(moved) + " m in 10 s (speed " + F(ship.Physics?.LinearVelocity.Length() ?? 0) + " m/s)");
            Result("cargo ship " + ship.DisplayName + " (" + ship.BlocksCount + " blocks, " + grids.Count + " grids) spawned " + F(distance) + " m from the player, NPC-owned, on "
                   + (ai ? "AI blocks" : remote != null ? "its remote control's autopilot" : "the game's simple autopilot") + ", flew " + F(moved) + " m in 10 s");
        }

        /// <summary>The online players, while the check's event picks one, are only the check's player.</summary>
        private static void Patch()
        {
            if (_harmony != null) return;
            _harmony = new HarmonyLib.Harmony("SentisTests.CargoShip");
            _harmony.Patch(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection).GetMethod(nameof(Sandbox.Game.Multiplayer.MyPlayerCollection.GetOnlineHumanPlayers)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(CargoShipCheck).GetMethod(nameof(OnlyPlayer), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        private static void OnlyPlayer(List<MyPlayer> collect)
        {
            if (_only != null && MySandboxGame.Static.UpdateThread == System.Threading.Thread.CurrentThread) collect.RemoveAll(p => p != _only);
        }
    }
}
