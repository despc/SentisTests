using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Blocks inside each other: a ship spawned into a station's blocks, 0.2 to 1 m deep, from every side. Havok pushes
    /// them apart and the impact deforms them; the server lives through it, the ship ends apart from the station or broken.
    /// </summary>
    public sealed class OverlapFreeCheck : RegressionCheck
    {
        public override string Name => Prefix + "overlap_free";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -12));
            var station = Build("overlap-station", Site.Origin, true,
                new Part("CargoContainer/LargeBlockSmallContainer", -1, 0, 0), new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0));
            var middle = station.GridIntegerToWorld(Vector3I.Zero);
            var runs = new List<string>();
            foreach (var side in new[] { Site.Up, -Site.Up, Site.Forward, -Site.Forward })
            foreach (var depth in new[] { 0.2, 0.6, 1.0 })
            {
                var ship = Build("overlap-ship", middle + side * (2.5 - depth), false,
                    new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
                var blocks = ship.BlocksCount;
                yield return WaitForTicks(60);
                runs.Add(F(depth) + " m: " + (ship.MarkedForClose ? "broken up" : ship.BlocksCount + " of " + blocks + " blocks, " +
                                                                    F(Vector3D.Distance(ship.PositionComp.WorldAABB.Center, middle)) + " m off"));
                yield return CloseAndWait(ship);
            }
            Check(!station.MarkedForClose, "the station is gone");
            Result("a ship spawned into a station: " + string.Join(", ", runs));
        }
    }

    /// <summary>
    /// Blocks inside each other across a mechanism: an advanced rotor with containers on every side of its stator, and a
    /// reactor added to the head on each side of the head block and razed again - the head sits in the stator's upper
    /// cell, so the reactors are in the containers beside it. The impacts deform the head, may break it off the rotor or
    /// up; the server lives through it. In a detached cluster Havok broke here within a minute (the tick's thread set up by
    /// HkBaseSystem.InitThread, and the tick beside the game's frame; SentisClusters, 27.10.2026).
    /// </summary>
    public sealed class OverlapRotorCheck : RegressionCheck
    {
        public override string Name => Prefix + "overlap_rotor";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            // six rotors one after another: in a detached cluster its world broke within the first four (see the summary)
            const int rounds = 6;
            var results = new List<string>();
            for (var round = 0; round < rounds; round++)
            {
                var grid = Build("overlap-rotor", Site.Origin + Site.Right * (12 * (round % 4) - 18) + Site.Up * (12 * (round / 4)), true,
                    new Part("MotorAdvancedStator/LargeAdvancedStator", 0, 0, 0));
                var rotor = Block<MyMechanicalConnectionBlockBase>(grid);
                yield return AddHead(rotor);
                ((IMyTerminalBlock)rotor).SetValueFloat("Velocity", 0f);
                ((IMyTerminalBlock)rotor).SetValueBool("RotorLock", true);
                var head = rotor.TopGrid;
                var top = rotor.TopBlock;
                foreach (var cell in Wave31.Around(rotor.SlimBlock))
                    Wave31.Add(grid, "CargoContainer/LargeBlockSmallContainer", cell, Base6Directions.Direction.Forward, Base6Directions.Direction.Up, Identity);
                var added = 0;
                foreach (var cell in Wave31.Around(top.SlimBlock).ToList())
                foreach (var face in new[] { Base6Directions.Direction.Forward, Base6Directions.Direction.Left, Base6Directions.Direction.Up })
                {
                    if (head.MarkedForClose) break;
                    if (Wave31.Add(head, "Reactor/LargeBlockSmallGenerator", cell, face, Base6Directions.GetPerpendicular(face), Identity) == null) continue;
                    added++;
                    yield return WaitForTicks(10);
                    if (head.MarkedForClose) break;
                    head.RazeBlock(cell);
                    yield return WaitForTicks(1);
                }
                yield return WaitForTicks(60);
                Check(!grid.MarkedForClose, "the stator's grid is gone");
                results.Add(added + " reactors, head " + (head.MarkedForClose ? "broken up" : head.BlocksCount + " blocks") + ", rotor " +
                            (((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)rotor).IsAttached ? "attached" : "detached"));
                Note("round " + round + ": " + results.Last());
            }
            Result("reactors added into the containers round a rotor's head: " + string.Join("; ", results));
        }
    }
}
