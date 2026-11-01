using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>Read-only paired checks of the optimized and original ship collision queries.</summary>
    public sealed class AiVoxelClashScenario : TestScenario
    {
        public const string ScenarioName = "ai_voxel_clash";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;
        public override bool KeepsNewGrids => true;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisAi.Bots.GridRoute", false)).FirstOrDefault(t => t != null);
            SkipUnless(type != null, "SentisAi is not loaded");
            var clash = type.GetMethod("Clash", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(Vector3D), typeof(double), typeof(bool) }, null);
            Check(clash != null, "loaded GridRoute has no differential collision seam");
            yield return WaitForTicks(30 * 60);
            var owners = new HashSet<long>(MySession.Static.Players.GetOnlinePlayers()
                .Where(p => p.DisplayName.StartsWith("[BOT] ")).Select(p => p.Identity.IdentityId));
            var ship = MyEntities.GetEntities().OfType<MyCubeGrid>()
                .Where(g => !g.MarkedForClose && g.Physics != null && g.GridSizeEnum == MyCubeSize.Small)
                .Where(g => g.BigOwners.Any(owners.Contains) && g.GetFatBlocks().OfType<Sandbox.ModAPI.IMyShipDrill>().Any())
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            SkipUnless(ship != null, "no existing bot-owned drill ship; this probe creates no fixtures");
            var centre = ship.Physics.CenterOfMassWorld;
            var planet = MyGamePruningStructure.GetClosestPlanet(centre);
            SkipUnless(planet != null, "the existing drill ship has no planet nearby");
            var up = Vector3D.Normalize(centre - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var across = Vector3D.Cross(up, side);
            var route = Activator.CreateInstance(type, new object[] { ship, centre, centre + up * 10, up });
            string Query(Vector3D p, bool filtered) => (string)clash.Invoke(route, new object[] { p, 0.5, filtered });
            // Warm both paths before paired timing. Nothing is moved, built, cut or stocked.
            Query(centre, false);
            Query(centre, true);
            var exactMs = 0.0;
            var filteredMs = 0.0;
            var rocks = 0;
            var free = 0;
            var grids = 0;
            var samples = 0;
            foreach (var x in new[] { -12.0, 0.0, 12.0 })
            foreach (var y in new[] { -12.0, 0.0, 12.0 })
            {
                var probe = centre + side * x + across * y;
                var surface = planet.GetClosestSurfacePointGlobal(ref probe);
                foreach (var height in new[] { -24.0, -8.0, 0.0, 2.0, 8.0, 24.0, 48.0 })
                {
                    var point = surface + up * height;
                    string expected = null;
                    for (var repeat = 0; repeat < 3; repeat++)
                    {
                        // Alternate order to avoid crediting the second query's warmed voxel cache.
                        string exact = null, filtered = null;
                        for (var order = 0; order < 2; order++)
                        {
                            var useFilter = (order + repeat + samples) % 2 == 0;
                            var timer = Stopwatch.StartNew();
                            var result = Query(point, useFilter);
                            var elapsed = timer.Elapsed.TotalMilliseconds;
                            if (useFilter) { filtered = result; filteredMs += elapsed; }
                            else { exact = result; exactMs += elapsed; }
                        }
                        Check(exact == filtered, $"collision mismatch at sample {samples}, height {height}; original/optimized disagree");
                        expected = exact;
                    }
                    if (expected == "rock") rocks++;
                    else if (expected == null) free++;
                    else grids++;
                    samples++;
                    yield return null;
                }
            }
            Note($"paired collision checks: {samples} positions x3, rock={rocks}, free={free}, grid={grids}; original={exactMs:0.00} ms, broadphase={filteredMs:0.00} ms");
            Check(rocks > 0 && free > 0, "comparison did not cover both terrain collision and clear air");
            Note(filteredMs < exactMs
                ? $"the storage prefilter reduced paired query time by {exactMs - filteredMs:0.00} ms; production still retains the exact path pending an active-load comparison"
                : $"the storage prefilter added {filteredMs - exactMs:0.00} ms; production retains the exact collision path");
        }

        public override void CleanupLeftovers() { } // observation only; owns no world entities
    }
}
