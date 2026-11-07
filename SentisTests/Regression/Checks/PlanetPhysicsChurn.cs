using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The stand's crash of 07.11.2026 made to come often: an access violation in Havok's AddGravityAction as a planet
    /// put a new piece of its voxel physics into a world (MyPlanet.UpdateFloraAndPhysics -> CreateVoxelMap ->
    /// MyVoxelPhysicsBody.CreateRigidBodies -> HkWorld.AddRigidBody), and the game's handler of a crash killed the
    /// server without a word in Torch's log - twice in an hour of the regression, each time while
    /// reg_flight_cluster_border flew a ship out of its cluster and back. Here ships are thrown every 20-40 frames to
    /// places all over the surfaces of every planet, 30 m over the ground: each throw has a planet make physics where
    /// there was none, and with SentisClusters' 5 km clusters each ship lands in a cluster of its own - clusters and
    /// their Havok worlds made and dropped all the time beside the planets' new bodies. A ship hops 7 km up and back
    /// over the site meanwhile, as reg_flight_cluster_border's does. The server living to the end is the check.
    /// Focus: <c>ships=24 seconds=300</c>. Not part of the regression: it is a load of its own.
    /// </summary>
    public sealed class PlanetPhysicsChurnCheck : RegressionCheck
    {
        public const string ScenarioName = "planet_physics_churn";
        public override string Name => ScenarioName;
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => Arg("seconds", 300) + 180;

        private static int Arg(string name, int fallback)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Core.TestRunner.Focus ?? "", name + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : fallback;
        }

        protected override IEnumerator Check()
        {
            var count = Arg("ships", 24);
            var seconds = Arg("seconds", 300);
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().Where(p => p != null && !p.MarkedForClose).ToList();
            Check(planets.Count > 0, "no planet in the world");
            yield return Arrive(Site.Ground(Site.Origin) + Site.Up * 3);
            var ships = new List<MyCubeGrid>();
            for (var i = 0; i < count; i++)
                ships.Add(Build("churn-" + i, Site.Origin + Site.Up * (300 + 12 * i), false, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0)));
            var hopper = Build("churn-hop", Site.Origin + Site.Up * 150 + Site.Right * 60, false, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var hopHome = hopper.WorldMatrix;
            yield return WaitForTicks(5);
            Note(count + " ships over " + planets.Count + " planets (" + string.Join(", ", planets.Select(p => p.StorageName)) + ")");

            var random = new Random(47351);
            var started = DateTime.UtcNow;
            var told = started;
            var hopAt = started;
            var hopUp = false;
            int throws = 0, hops = 0, rebuilt = 0, minClusters = int.MaxValue, maxClusters = 0;
            while ((DateTime.UtcNow - started).TotalSeconds < seconds)
            {
                for (var k = 0; k < ships.Count; k++)
                {
                    // the game's trash collector takes a ship of one block far from everyone: another one in its place
                    if (ships[k].MarkedForClose || ships[k].Closed)
                    {
                        ships[k] = Build("churn-" + k + "-" + rebuilt++, Site.Origin + Site.Up * (300 + 12 * k), false, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
                        continue;
                    }
                    var ship = ships[k];
                    var planet = planets[random.Next(planets.Count)];
                    var centre = planet.PositionComp.GetPosition();
                    var direction = Vector3D.Normalize(new Vector3D(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
                    var far = centre + direction * planet.MaximumRadius * 1.5;
                    var surface = planet.GetClosestSurfacePointGlobal(ref far);
                    var up = Vector3D.Normalize(surface - centre);
                    var at = surface + up * 30;
                    ship.Teleport(MatrixD.CreateWorld(at, Vector3D.CalculatePerpendicularVector(up), up));
                    if (ship.Physics != null) ship.Physics.LinearVelocity = Vector3.Zero;
                    throws++;
                }
                // the hopper: 7 km up over the site and back every 5 s - its cluster parted from the site's and joined again
                if ((DateTime.UtcNow - hopAt).TotalSeconds >= 5 && !hopper.MarkedForClose)
                {
                    hopAt = DateTime.UtcNow;
                    hopUp = !hopUp;
                    var m = hopHome;
                    if (hopUp) m.Translation += Site.Up * 7000;
                    hopper.Teleport(m);
                    if (hopper.Physics != null) hopper.Physics.LinearVelocity = Vector3.Zero;
                    hops++;
                }
                var clusters = MyPhysics.Clusters.GetClusters().Count;
                minClusters = Math.Min(minClusters, clusters);
                maxClusters = Math.Max(maxClusters, clusters);
                if ((DateTime.UtcNow - told).TotalSeconds >= 30)
                {
                    told = DateTime.UtcNow;
                    Note($"{(DateTime.UtcNow - started).TotalSeconds:F0} s: {throws} throws, {hops} hops, {rebuilt} ships rebuilt; clusters {clusters} (from {minClusters} to {maxClusters}); voxel maps {MyEntities.GetEntities().OfType<MyVoxelBase>().Count()}");
                }
                yield return WaitForTicks(20 + random.Next(21));
            }
            Result($"{throws} throws of {count} ships over {planets.Count} planets ({rebuilt} rebuilt), {hops} hops of 7 km in {seconds} s, clusters from {minClusters} to {maxClusters}: the server lived");
        }
    }
}
