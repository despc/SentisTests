using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Where a SentisAi bot puts its base's connector for the flying drill when nothing on top of the base has room for it
    /// (the user, 16.10.2026: the place must let the drill dock and fly in; if there is none, a line of conveyor blocks out
    /// to where there is, the connector on its end). A base of three conveyor blocks on the ground under a ceiling of
    /// armour (another grid) 10 m over it, 13 by 13 blocks: the drill would run into the ceiling over every top of the base.
    /// A bot of the stand is lent the base for its look, a call a frame (its own base given back after each), the drill weighed on the sites thread: the look must
    /// find a line out from a port to a place clear of the ceiling, no call of it longer than 100 ms. Then the base is made again with that line
    /// built, and the look must put the connector on the line's end.
    /// </summary>
    public sealed class DrillDockLineScenario : TestScenario
    {
        public const string ScenarioName = "drill_dock_line";
        private const string Prefix = "DrillDockLine_";
        private const int Ceiling = 6;
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private object _bot;
        private long _ownBase;
        /// <summary>
        /// The bot's identity: a battery of it on each test grid (the game's trash removal took the base of conveyor
        /// blocks, owned by nobody, a second after it was made).
        /// </summary>
        private long _owner;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var botType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisAi.Bots.Bot", false)).FirstOrDefault(t => t != null);
            Check(botType != null, "SentisAi not loaded");
            SkipUnless(botType.GetField("_drillFromConnector", Any)?.GetValue(null) != null, "the flying drill's shape is not known yet (no bot has had a chassis since the start)");
            // (a bot not looking for its own connector's place now - no chassis, or its connector up: its look and this one
            // would take turns and neither come back)
            bool Quiet(MyPlayer p)
            {
                var bot = BotPitScenario.SentisAiBot(p);
                if (bot == null || p.Character == null || p.Character.IsDead) return false;
                var chassis = bot.GetType().GetMethod("Chassis", Any)?.Invoke(bot, new object[] { p.Character });
                var dock = bot.GetType().GetMethod("BaseDock", Any)?.Invoke(bot, new object[] { p.Character });
                return chassis == null || dock != null;
            }
            var player = MySession.Static.Players.GetOnlinePlayers().FirstOrDefault(Quiet);
            SkipUnless(player != null, "no SentisAi bot with a body that is not looking for its own connector's place");
            Note("the bot lent the base: " + player.DisplayName);
            _bot = BotPitScenario.SentisAiBot(player);
            _owner = player.Identity.IdentityId;
            var character = player.Character;

            var anchor = WorldApi.PlanetAnchor("drill_dock_line");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = anchor.Translation + Vector3D.Normalize(anchor.Translation - centre) * 50 - Vector3D.CalculatePerpendicularVector(anchor.Translation - centre) * 300;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            WorldApi.RevertTerrain(planet, ground, 80);
            yield return null;

            // the ceiling: a grid of its own, 13 by 13 armour blocks, 10 m over the base
            var roofOb = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "ceiling", MyCubeSize.Large, true, ground + up * (1.5 + 2.5 * 4), Enumerable.Empty<BlockSpec>(), (Vector3)side, (Vector3)up);
            for (var x = -Ceiling; x <= Ceiling; x++)
            for (var z = -Ceiling; z <= Ceiling; z++)
            {
                var armour = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                armour.Min = new SerializableVector3I(x, 0, z);
                roofOb.CubeBlocks.Add(armour);
            }
            roofOb.CubeBlocks.Add(Owned("LargeBlockBatteryBlock", new Vector3I(0, 1, 0)));
            Track(WorldApi.SpawnGrid(roofOb));

            var home = SpawnBase(ground, side, up, new List<Vector3I>());
            var looking = Look(botType, character, home);
            while (looking.MoveNext()) yield return looking.Current;
            Note($"under the ceiling: connector site {_site ?? "none"}; line {(_line == null ? "none" : _line.Count + " blocks: " + string.Join(" ", _line))}, connector at {_top}; {_why}");
            Check(_site == null, "a connector place on top of the base under the ceiling: the drill would run into it");
            Check(_line != null && _line.Count > 0 && _top != null, "no line out to a place for the connector: " + _why);
            Check(Math.Abs(_top.Value.X) > Ceiling || Math.Abs(_top.Value.Z) > Ceiling, $"the connector at the line's end {_top} is under the ceiling");
            Check(_worstMs < 100, $"a call of the look took {_worstMs:0} ms");
            var line = _line;
            var top = _top;

            // the line built: the connector on its end now
            home.Close();
            yield return null;
            home = SpawnBase(ground, side, up, line);
            looking = Look(botType, character, home);
            while (looking.MoveNext()) yield return looking.Current;
            Note($"the line built: connector site {_site ?? "none"}, at {_top}; {_why}");
            Check(_site != null && _top == top, $"the connector does not go on the line's end {top} once the line is there ({_why})");
            Check(_worstMs < 100, $"a call of the look took {_worstMs:0} ms");
        }

        /// <summary>The base: three conveyor blocks in a row on the ground, and the line's; a battery by them.</summary>
        private MyCubeGrid SpawnBase(Vector3D ground, Vector3D side, Vector3D up, List<Vector3I> line)
        {
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "base", MyCubeSize.Large, true, ground + up * 1.5, Enumerable.Empty<BlockSpec>(), (Vector3)side, (Vector3)up);
            foreach (var cell in new[] { new Vector3I(-1, 0, 0), Vector3I.Zero, new Vector3I(1, 0, 0) }.Concat(line))
            {
                var conveyor = WorldApi.MakeBlockOb("LargeBlockConveyor");
                conveyor.Min = cell;
                ob.CubeBlocks.Add(conveyor);
            }
            ob.CubeBlocks.Add(Owned("LargeBlockBatteryBlock", new Vector3I(0, 0, -1)));
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        private VRage.Game.MyObjectBuilder_CubeBlock Owned(string subtype, Vector3I at)
        {
            var block = WorldApi.MakeBlockOb(subtype);
            block.Min = at;
            block.Owner = _owner;
            block.BuiltBy = _owner;
            block.ShareMode = VRage.Game.MyOwnershipShareModeEnum.All;
            return block;
        }

        // what the last look found
        private string _site, _why;
        private List<Vector3I> _line;
        private Vector3I? _top;
        private double _worstMs;

        /// <summary>
        /// The bot's look for the connector's place on <paramref name="home"/>, as if it were its base: a call a frame, as the
        /// bot makes it (it goes on over ticks, a few milliseconds each), the base lent only for the call. The longest call
        /// must stay short: the look must not hold the server.
        /// </summary>
        private IEnumerator Look(Type botType, MyCharacter character, MyCubeGrid home)
        {
            var t = _bot.GetType();
            if (_ownBase == 0) _ownBase = (long)t.GetField("_base", Any).GetValue(_bot);
            t.GetField("_dockSiteBlocks", Any).SetValue(_bot, -1);
            t.GetField("_dockAsk", Any).SetValue(_bot, null);
            var look = t.GetMethod("BaseConnectorSite", Any);
            var pending = botType.GetField("SitePending", Any);
            object site = null;
            _worstMs = 0;
            for (var i = 0; i < 600; i++)
            {
                var checkedAt = t.GetField("_baseCheckedAt", Any).GetValue(_bot);
                var own = t.GetField("_base", Any).GetValue(_bot);
                t.GetField("_base", Any).SetValue(_bot, home.EntityId);
                t.GetField("_baseCheckedAt", Any).SetValue(_bot, t.GetField("_now", Any).GetValue(_bot));
                pending.SetValue(null, false);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool more;
                try
                {
                    site = look.Invoke(_bot, new object[] { character });
                    more = (bool)pending.GetValue(null);
                }
                finally
                {
                    pending.SetValue(null, false);
                    t.GetField("_base", Any).SetValue(_bot, own);
                    t.GetField("_baseCheckedAt", Any).SetValue(_bot, checkedAt);
                }
                _worstMs = Math.Max(_worstMs, watch.Elapsed.TotalMilliseconds);
                if (!more) break;
                yield return null;
            }
            _line = t.GetField("_dockLineSite", Any).GetValue(_bot) == null ? null : (List<Vector3I>)t.GetField("_dockLine", Any).GetValue(_bot);
            _top = (Vector3I?)t.GetField("_dockTop", Any).GetValue(_bot);
            _why = (string)t.GetField("_dockSiteWhy", Any).GetValue(_bot);
            var kept = (HashSet<Vector3I>)botType.GetMethod("DockClear", Any).Invoke(null, new object[] { home });
            _why += $"; {kept.Count} cells kept free for the drill; the longest call {_worstMs:0} ms";
            if (_line != null && _line.Any(c => !kept.Contains(c))) _why += " (NOT all of the line among them)";
            var last = t.GetField("_dockLastAsk", Any).GetValue(_bot);
            if (last != null)
            {
                var places = (System.Collections.IList)last.GetType().GetField("Places").GetValue(last);
                var fits = (List<int>)last.GetType().GetField("Fits").GetValue(last);
                string Told(object place) => place.GetType().GetField("Cell").GetValue(place) + (place.GetType().GetField("Line").GetValue(place) is List<Vector3I> l ? " line " + l.Count : "");
                Note($"the look of grid {last.GetType().GetField("Grid").GetValue(last)} (the base {home.EntityId}, blocks at {string.Join(" ", home.GetBlocks().Select(b => b.Position))}): " +
                     $"{places.Count} places, the first {string.Join(", ", places.Cast<object>().Take(4).Select(Told))}; fits {string.Join(", ", fits.Take(6).Select(f => Told(places[f])))}");
            }
            _site = site == null ? null : ((ValueTuple<MyCubeGrid, MyCubeGrid.MyBlockLocation>)site).Item2.Min.ToString();
            t.GetField("_dockSiteBlocks", Any).SetValue(_bot, -1);
            t.GetField("_dockAsk", Any).SetValue(_bot, null);
        }

        /// <summary>The bot's own base back: looked for anew, the look for the connector's place too.</summary>
        private void GiveBack()
        {
            if (_bot == null) return;
            var t = _bot.GetType();
            t.GetField("_base", Any).SetValue(_bot, 0L);
            t.GetField("_baseCheckedAt", Any).SetValue(_bot, long.MinValue / 2);
            t.GetField("_dockSiteBlocks", Any).SetValue(_bot, -1);
            t.GetField("_dockAsk", Any).SetValue(_bot, null);
            t.GetField("_dockSite", Any).SetValue(_bot, null);
            t.GetField("_dockLineSite", Any).SetValue(_bot, null);
            t.GetField("_dockLine", Any).SetValue(_bot, null);
            t.GetField("_dockTop", Any).SetValue(_bot, null);
        }

        public override void Cleanup()
        {
            try { GiveBack(); }
            finally { base.Cleanup(); }
        }
    }
}
