using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A holder for a measurement, not a check: a fake player with a character beside each of the world's heaviest grids,
    /// kept there for <see cref="HoldSeconds"/> with the freezer on - so those grids run as they do with their owners
    /// at home, and the rest of the world stays frozen. The load is measured from outside meanwhile (SentisWatcher's
    /// burst: tools/burst.py).
    ///
    /// Which grids: the names in <c>Instance\SentisTests\top_grids.txt</c>, one a line (written from SentisWatcher's
    /// load table of a run with the freezer off), the biggest grid of each name; what the file does not give - no file,
    /// a grid that is gone - is made up to <see cref="Grids"/> with the grids of the most blocks.
    /// </summary>
    public class TopGridsPlayersScenario : TestScenario
    {
        public const string ScenarioName = "top_grids_players";

        /// <summary>The freezer on, as the players' grids run at home (false: the world as it is, the freezer on or off).</summary>
        protected virtual bool NeedsFreezer => true;

        protected virtual int Hold => HoldSeconds;
        private const string Prefix = "topgrids-";
        private const int Grids = 20;
        private const int HoldSeconds = 420;
        private const double ThawTimeoutSeconds = 60;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly List<ulong> _protected = new List<ulong>();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => Hold + 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);
            if (NeedsFreezer) Check(RuntimePluginControls.FreezerEnabled, "the freezer is off: this measures the world with it on");

            var all = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g.Physics != null && !g.IsPreview).ToList();
            var chosen = new List<MyCubeGrid>();
            var missing = new List<string>();
            var file = Path.Combine(SentisTestsPlugin.TorchInstance.Config.InstancePath, "SentisTests", "top_grids.txt");
            if (File.Exists(file))
                foreach (var name in File.ReadAllLines(file).Where(l => l.Length > 0))
                {
                    var grid = all.Where(g => g.DisplayName == name && !chosen.Contains(g)).OrderByDescending(g => g.BlocksCount).FirstOrDefault();
                    if (grid != null) chosen.Add(grid);
                    else missing.Add(name);
                }
            var named = chosen.Count;
            // (a base's subgrids go with it: one player a physical group)
            foreach (var grid in all.OrderByDescending(g => g.BlocksCount))
            {
                if (chosen.Count >= Grids) break;
                if (chosen.Contains(grid) || chosen.Any(c => Vector3D.DistanceSquared(c.PositionComp.GetPosition(), grid.PositionComp.GetPosition()) < 300 * 300)) continue;
                chosen.Add(grid);
            }
            chosen = chosen.Take(Grids).ToList();
            Check(chosen.Count > 0, "no grids in the world");

            var places = chosen.Select(Beside).ToList();
            var frozenBefore = chosen.Count(g => RuntimePluginControls.IsGridFrozen(g.EntityId));
            FakeClients.Add(chosen.Count, Network, i => (places[i], 0, 0), withCharacters: true);
            // the bases' turrets shot them and they fell: in a minute 8 of 20 were dead and their grids frozen again.
            // An admin's invulnerable and untargetable for each, the way the admin menu sets them
            for (var i = 0; i < chosen.Count; i++)
            {
                var steamId = FakeClients.PlayerOf(i).Id.SteamId;
                _protected.Add(steamId);
                Sandbox.Game.World.MySession.Static.RemoteAdminSettings[steamId] = Sandbox.Game.World.AdminSettingsEnum.Invulnerable | Sandbox.Game.World.AdminSettingsEnum.Untargetable;
            }
            var thaw = Stopwatch.StartNew();
            while (chosen.Any(g => RuntimePluginControls.IsGridFrozen(g.EntityId)) && thaw.Elapsed.TotalSeconds < ThawTimeoutSeconds) yield return null;
            Note("TOP GRIDS PLAYERS | " + chosen.Count + " players set (" + named + " grids by the list" + (missing.Count > 0 ? ", not in the world: " + string.Join(", ", missing) : "") +
                 "), frozen before " + frozenBefore + ", still frozen after " + thaw.Elapsed.TotalSeconds.ToString("F0") + " s: " +
                 Frozen(chosen) + " | " + string.Join("; ", chosen.Select(g => g.DisplayName + " " + g.BlocksCount + " blocks" + (g.IsStatic ? " static" : ""))));

            var hold = Stopwatch.StartNew();
            var nextNote = 60.0;
            while (hold.Elapsed.TotalSeconds < Hold)
            {
                if (hold.Elapsed.TotalSeconds >= nextNote)
                {
                    nextNote += 60;
                    Note("TOP GRIDS PLAYERS | " + hold.Elapsed.TotalSeconds.ToString("F0") + " s: characters alive " +
                         Enumerable.Range(0, chosen.Count).Count(FakeClients.HasLiveCharacter) + " of " + chosen.Count + ", grids frozen: " + Frozen(chosen) +
                         ", frozen grids in the world " + RuntimePluginControls.FrozenGridCount + " of " + all.Count);
                }
                yield return null;
            }
            Note("TOP GRIDS PLAYERS RESULT | held " + Hold + " s, characters alive at the end " + Enumerable.Range(0, chosen.Count).Count(FakeClients.HasLiveCharacter) +
                 " of " + chosen.Count + ", grids frozen at the end: " + Frozen(chosen));
        }

        private static string Frozen(List<MyCubeGrid> grids)
        {
            var frozen = grids.Where(g => !g.MarkedForClose && RuntimePluginControls.IsGridFrozen(g.EntityId)).Select(g => g.DisplayName).ToList();
            var gone = grids.Count(g => g.MarkedForClose);
            return (frozen.Count == 0 ? "none" : frozen.Count + " (" + string.Join(", ", frozen) + ")") + (gone > 0 ? ", gone " + gone : "");
        }

        /// <summary>A place for a character by the grid: over its top, out of its blocks.</summary>
        private static Vector3D Beside(MyCubeGrid grid)
        {
            var box = grid.PositionComp.WorldAABB;
            var planet = MyGamePruningStructure.GetClosestPlanet(box.Center);
            var up = planet != null && Vector3D.Distance(planet.PositionComp.GetPosition(), box.Center) < planet.MaximumRadius * 1.5
                ? Vector3D.Normalize(box.Center - planet.PositionComp.GetPosition())
                : Vector3D.Up;
            return box.Center + up * (box.HalfExtents.Length() + 5);
        }

        public override void Cleanup()
        {
            try
            {
                foreach (var steamId in _protected) Sandbox.Game.World.MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// The same players by the heaviest grids, kept 25 minutes and with the world as it is - the freezer may be off:
    /// a holder for comparing measurements taken from outside (SentisClusters: the clusters in a row and side by side).
    /// </summary>
    public sealed class TopGridsPlayersOpenScenario : TopGridsPlayersScenario
    {
        public new const string ScenarioName = "top_grids_players_open";
        public override string Name => ScenarioName;
        protected override bool NeedsFreezer => false;
        protected override int Hold => 1500;
    }

}
