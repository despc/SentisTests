using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What the PCU limit of SentisGameplayImprovements costs the game thread. <see cref="Copies"/> dynamic copies of
    /// Spitfire (2757 blocks) out in space; with the server's own limits (Spitfire is within them, nothing is
    /// switched off) it measures the check of one group, and a whole pass of the limiter over the world call by
    /// call, as the limiter makes it: at most 20000 blocks a call, a call every 100 ms.
    /// </summary>
    internal sealed class PcuLimitCostScenario : TestScenario
    {
        public const string ScenarioName = "pcu_limit_cost";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        private const string Prefix = "pcucost-";
        private const int Copies = 20;
        private const string Spitfire = "SentisTests.Resources.Spitfire.xml";
        private object _limiter;
        private FieldInfo _lastPass, _queue, _cursor;
        private MethodInfo _checkGroup, _checkSlice, _gridPcu;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var plugin = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisGameplayImprovements.SentisGameplayImprovementsPlugin", false)).FirstOrDefault(t => t != null);
            Check(plugin != null, "SentisGameplayImprovements is not loaded");
            _limiter = plugin.GetField("_limiter", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            var type = _limiter.GetType();
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            _lastPass = type.GetField("_lastPass", Private);
            _queue = type.GetField("_queue", Private);
            _cursor = type.GetField("_cursor", Private);
            _checkGroup = type.GetMethod("CheckGroup", Private);
            _checkSlice = type.GetMethod("CheckSlice", BindingFlags.Instance | BindingFlags.Public);
            _gridPcu = type.GetMethod("GridPcu", BindingFlags.Static | BindingFlags.NonPublic);
            Check(_lastPass != null && _queue != null && _cursor != null && _checkGroup != null && _checkSlice != null && _gridPcu != null,
                "the limiter has changed");

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var forward = Vector3D.Cross(side, up);
            Vector3D centre = default;
            for (var d = 100000.0; d <= 1000000; d += 50000)
            {
                centre = anchor + up * d + forward * 20000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f) break;
            }

            var ships = new List<MyCubeGrid>();
            for (var i = 0; i < Copies; i++)
            {
                var ob = WorldApi.LoadGridTemplate(Spitfire, WorldApi.EntityPrefix + Prefix + i, centre + side * (300 * (i % 5)) + forward * (300 * (i / 5)), forward, up);
                MyAPIGateway.Entities.RemapObjectBuilder(ob);
                var ship = WorldApi.SpawnGrid(ob);
                Track(ship);
                ships.Add(ship);
                yield return null;
            }
            var settle = WaitForSeconds(3, "the ships settle");
            while (settle.MoveNext()) yield return settle.Current;

            // ------------------------------------------------------------- one group
            var groupMs = new List<double>();
            for (var round = 0; round < 3; round++)
            {
                foreach (var ship in ships)
                {
                    var group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(ship);
                    var at = Stopwatch.GetTimestamp();
                    _checkGroup.Invoke(_limiter, new object[] { group });
                    groupMs.Add(Ms(at));
                }
                yield return null;
            }
            var blocksPerShip = ships[0].BlocksCount;

            // the exact count, which a group over the game's count gets; and the game's count is never below it
            var exactMs = new List<double>();
            var below = new List<string>();
            foreach (var ship in ships)
            {
                var at = Stopwatch.GetTimestamp();
                var exact = (int)_gridPcu.Invoke(null, new object[] { ship });
                exactMs.Add(Ms(at));
                if (ship.BlocksPCU < exact) below.Add(ship.DisplayName + " " + ship.BlocksPCU + " < " + exact);
            }
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
            {
                if (grid.MarkedForClose || grid.Physics == null || grid.IsPreview) continue;
                var exact = (int)_gridPcu.Invoke(null, new object[] { grid });
                if (grid.BlocksPCU < exact) below.Add(grid.DisplayName + " " + grid.BlocksPCU + " < " + exact);
            }
            var perBlockNs = exactMs.Average() * 1e6 / blocksPerShip;

            // ------------------------------------------------------------- a whole pass over the world
            var worldBlocks = 0;
            var worldGroups = 0;
            foreach (var group in MyCubeGridGroups.Static.Mechanical.Groups)
            {
                if (group.Nodes.All(n => n.NodeData.IsStatic)) continue;
                worldGroups++;
                worldBlocks += group.Nodes.Sum(n => n.NodeData.BlocksCount);
            }
            // the limiter's own calls hold off: its next pass is due at the end of time, its queue empty
            ((IList)_queue.GetValue(_limiter)).Clear();
            _lastPass.SetValue(_limiter, DateTime.MinValue);
            var sliceMs = new List<double>();
            do
            {
                var at = Stopwatch.GetTimestamp();
                _checkSlice.Invoke(_limiter, null);
                sliceMs.Add(Ms(at));
                yield return null;
            } while ((int)_cursor.GetValue(_limiter) < ((IList)_queue.GetValue(_limiter)).Count);

            var result = "one Spitfire group within the limit (" + blocksPerShip + " blocks, " + ships[0].BlocksPCU + " PCU by the game's count): avg " +
                         groupMs.Average().ToString("F4") + " ms, max " + groupMs.Max().ToString("F4") + " ms || its exact count (a group over the limit): avg " +
                         exactMs.Average().ToString("F3") + " ms, " + perBlockNs.ToString("F1") + " ns a block || a pass over the world (" + worldGroups + " groups with a dynamic grid, " +
                         worldBlocks + " blocks): " + sliceMs.Count + " calls, " + sliceMs.Sum().ToString("F2") + " ms in all, the longest call " +
                         sliceMs.Max().ToString("F2") + " ms";
            Note("PCU LIMIT COST: " + result);
            Check(below.Count == 0, "the game's PCU count is below the exact one: " + string.Join("; ", below.Take(10)));
        }

        private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

        public override void Cleanup()
        {
            try
            {
                if (_limiter != null && _lastPass != null) _lastPass.SetValue(_limiter, DateTime.UtcNow);
            }
            finally { base.Cleanup(); }
        }
    }
}
