using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What WeaponCore (the mod, if the world has it) keeps an AI for: its AIs (<c>CoreSystems.Session.I.EntityAIs</c>,
    /// one for a top grid with its parts), how many are concealed (entity flag 4: the SentisOptimisations freezer's mark,
    /// which WeaponCore's AiLoop skips), their weapons, and the live ones by owner (a player's, an NPC's, nobody's), by the
    /// nearest player and by the clusters of the physics they are in. A census: it fails only when WeaponCore is not
    /// loaded.
    /// </summary>
    internal sealed class WeaponCoreCensusScenario : TestScenario
    {
        public const string ScenarioName = "weaponcore_census";

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            var session = AppDomain.CurrentDomain.GetAssemblies().Select(a => SafeType(a, "CoreSystems.Session")).FirstOrDefault(t => t != null);
            Check(session != null, "no WeaponCore (CoreSystems.Session) in this game");
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var instance = session.GetField("I", any)?.GetValue(null);
            Check(instance != null, "WeaponCore's session not made yet");
            var ais = (IDictionary)session.GetField("EntityAIs", any).GetValue(instance);
            var players = MySession.Static.Players.GetOnlinePlayers().Where(p => p.Character != null).Select(p => p.Character.PositionComp.GetPosition()).ToList();

            int total = 0, concealed = 0, weapons = 0, liveWeapons = 0;
            var byOwner = new Dictionary<string, (int Ais, int Weapons)>();
            var byDistance = new SortedDictionary<int, (int Ais, int Weapons)>();
            var byCluster = new Dictionary<int, int>();
            var biggest = new List<(string Name, int Weapons, string Owner, double Near)>();
            foreach (DictionaryEntry pair in ais)
            {
                total++;
                var ai = pair.Value;
                var top = pair.Key as MyEntity;
                var comps = ai.GetType().GetField("WeaponComps", any)?.GetValue(ai) as IList;
                var count = comps?.Count ?? 0;
                weapons += count;
                if (top == null || ((uint)top.Flags & 4) > 0)
                {
                    concealed++;
                    continue;
                }
                liveWeapons += count;
                var grid = top as MyCubeGrid;
                var ownerId = grid != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
                var owner = ownerId == 0 ? "nobody" : MySession.Static.Players.IdentityIsNpc(ownerId) ? "npc" : "player";
                byOwner.TryGetValue(owner, out var o);
                byOwner[owner] = (o.Ais + 1, o.Weapons + count);
                var at = top.PositionComp.GetPosition();
                var near = players.Count == 0 ? double.PositiveInfinity : players.Min(p => Vector3D.Distance(p, at));
                var band = double.IsInfinity(near) ? int.MaxValue : near < 1000 ? 1 : near < 5000 ? 5 : near < 20000 ? 20 : near < 100000 ? 100 : 1000;
                byDistance.TryGetValue(band, out var d);
                byDistance[band] = (d.Ais + 1, d.Weapons + count);
                var world = (top.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.HavokWorld;
                var cluster = world != null ? world.GetHashCode() : 0;
                byCluster.TryGetValue(cluster, out var c);
                byCluster[cluster] = c + 1;
                biggest.Add((top.DisplayName, count, owner, near));
            }
            string Band(int b) => b == int.MaxValue ? "no player online" : b == 1000 ? "over 100 km" : "under " + b + " km";
            Note("WEAPONCORE CENSUS | " + total + " AIs, " + concealed + " concealed (frozen); " + weapons + " weapons, " + liveWeapons + " on live AIs | live by owner: " +
                 string.Join(", ", byOwner.Select(p => p.Key + " " + p.Value.Ais + " AIs/" + p.Value.Weapons + " weapons")) + " | by the nearest player: " +
                 string.Join(", ", byDistance.Select(p => Band(p.Key) + " " + p.Value.Ais + "/" + p.Value.Weapons)) + " | in " + byCluster.Count + " physics worlds (" +
                 byCluster.Count(p => p.Key == 0) + " groups with no body) | most weapons: " +
                 string.Join("; ", biggest.OrderByDescending(b => b.Weapons).Take(10).Select(b => b.Name + " " + b.Weapons + " " + b.Owner + (double.IsInfinity(b.Near) ? "" : " " + (b.Near / 1000).ToString("F1") + " km"))));
            yield break;
        }

        private static Type SafeType(Assembly a, string name)
        {
            try { return a.GetType(name, false); }
            catch { return null; }
        }
    }
}
