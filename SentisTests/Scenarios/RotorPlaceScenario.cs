using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using Sandbox.Common.ObjectBuilders;
using VRage.Game.ObjectBuilders.Components;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A player places rotors, as from the build menu (<c>MyCubeGrid.BuildBlocksRequest</c> from a client): the game makes
    /// the head on a grid of its own and joins it to the stator with a hinge (<c>MyMechanicalConnectionBlockBase
    /// .CreateTopPartAndAttach</c>). Reported from a server without SentisClusters: the head of an advanced rotor placed as a
    /// frame (not welded up) falls over on its side and jerks; so does a head added from the terminal to such a stator,
    /// and a frame with its head after a fixship; it stays so after a reconnect. A welded-up stator after a fixship holds
    /// its head as it should.
    ///
    /// On a static base and on a ship (a dynamic plate standing on the base), advanced rotors and a plain one to compare:
    ///  - "placed": the stator as the player places it, a frame with the head the game makes;
    ///  - "terminal head": the head removed and added again from the terminal (<c>RecreateTop</c> from the client);
    ///  - "fixship frame": the grids cut and pasted by SGI's fixship, the stator still a frame;
    ///  - "fixship built": the same with the stator welded up.
    ///
    /// In a safe zone ("rotor_place_zone", "rotor_place_zone_game"): the zone lets in only the player (a white list, as a
    /// player's safe zone block makes it), the base and the ship belong to nobody - as a rotor placed on its own is a grid
    /// of a frame that nobody owns until it is welded up. A dynamic grid a zone does not let in is pushed out of it with
    /// its mass times a thousand every frame (MySafeZone.UpdateBeforeSimulation). "_zone" with SentisOptimisations'
    /// own tracking of the grids in zones (SafeZoneGridTracking), "_game" with the game's phantom.
    ///
    /// SentisOptimisations' check of a placed head (MechanicalTopCheck): "rotor_fix" knocks each head out right after it
    /// is placed - detached, a metre aside, tilted 30° - and the check must put it back and attach it again; "rotor_fix_off"
    /// does the same with the check switched off, and the heads must stay knocked out (the scenario sees a wrong head).
    ///
    /// "rotor_place_freezer": after the placing, the player goes a kilometre and a half away, the freezer freezes the base,
    /// the ship and the heads, they sleep 20 s, the player comes back, they wake - and the heads are watched again.
    /// Pistons stand beside the rotors in every variant.
    /// After each the heads are watched for <see cref="WatchSeconds"/>: there and attached, the hinge in the world, the
    /// head where it was put relative to the stator, not moving against it, and upright on its axis (not over
    /// <see cref="MaxTiltDegrees"/>).
    /// </summary>
    public sealed class RotorPlaceScenario : TestScenario
    {
        public const string ScenarioName = "rotor_place";
        public const string ZoneName = "rotor_place_zone";
        public const string ZoneGameName = "rotor_place_zone_game";
        public const string FixName = "rotor_fix";
        public const string FixOffName = "rotor_fix_off";
        public const string FreezerName = "rotor_place_freezer";

        /// <summary>The heads watched again after the grids were frozen by SentisOptimisations' freezer and woken.</summary>
        private readonly bool _freezer;

        /// <summary>Null: no knocking out; true: knocked out with SO's check on; false: with it off.</summary>
        private readonly bool? _fixCheck;

        /// <summary>Null: no zone; true: SO's tracking of the grids in zones; false: the game's.</summary>
        private readonly bool? _zoneTracking;
        private readonly ConfigOverride _config = new ConfigOverride();
        private MyEntity _zone;

        public RotorPlaceScenario(bool? zoneTracking = null, bool? fixCheck = null, bool freezer = false)
        {
            _freezer = freezer;
            _zoneTracking = zoneTracking;
            _fixCheck = fixCheck;
        }
        private const string Prefix = "rotor-place-";
        private const double OffsetM = 60;
        private const double WatchSeconds = 10;
        private const double SettleSeconds = 1;
        private const double MaxDriftM = 0.3, MaxSpeedMps = 0.5, MaxTiltDegrees = 3;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] OnBase = { "LargeAdvancedStator", "LargeAdvancedStator", "LargeStator", "LargePistonBase" };
        private static readonly string[] OnShip = { "LargeAdvancedStator", "LargeStator", "LargePistonBase" };

        private long _identity;
        private MyCubeGrid _station, _ship;
        private readonly List<Vector3I> _baseCells = new List<Vector3I>(), _shipCells = new List<Vector3I>();
        private readonly List<string> _failures = new List<string>();

        public override string Name => _freezer ? FreezerName : _fixCheck != null ? (_fixCheck.Value ? FixName : FixOffName) :
            _zoneTracking == null ? ScenarioName : _zoneTracking.Value ? ZoneName : ZoneGameName;
        public override int TimeoutSeconds => 300;

        private sealed class Watched
        {
            public string Label;
            public MyMechanicalConnectionBlockBase Stator;
            public Vector3D? Offset;            // the head grid's place in the stator's frame, after the settling
            public double Drift, Speed, Tilt, FirstTilt = -1;
            public int Detached, NoConstraint;
            public readonly List<string> Seen = new List<string>();
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            // over any planet of the world (a copy of a server's world has none of the stand's test places): its
            // biggest one, above the surface in a fixed direction
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().OrderByDescending(x => x.MaximumRadius).FirstOrDefault();
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var direction = Vector3D.Normalize(new Vector3D(0.31, 0.83, -0.47));
            var probe = centre + direction * planet.MaximumRadius;
            var surface = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(surface - centre);
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var origin = surface + up * OffsetM;

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (origin + up * 6, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var player = FakeClients.PlayerOf(0);
            Check(player?.Character != null, "the player has no character");
            _identity = player.Identity.IdentityId;
            // a world with research (the server's): the player has what a player who builds these has researched
            Sandbox.Game.SessionComponents.MySessionComponentResearch.Static?.DebugUnlockAllResearch(_identity);

            if (_zoneTracking != null)
            {
                // before the zone is made: a zone takes the layer of its phantom when it makes its physics
                _config.Set("SafeZoneGridTracking", _zoneTracking.Value);
                _zone = (MyEntity)MySessionComponentSafeZones.CrateSafeZone(MatrixD.CreateWorld(origin, side, up),
                    MySafeZoneShape.Sphere, MySafeZoneAccess.Whitelist,
                    new[] { _identity }, null, 100, enable: true, isVisible: true);
                // building and welding allowed, as in a player's zone
                ((MySafeZone)_zone).AllowedActions = MySafeZoneAction.All;
                Track(_zone);
                Note($"a safe zone letting in only the player; its phantom on layer {_zone.Physics?.RigidBody?.Layer}, the grids belong to nobody");
            }
            if (_fixCheck != null) _config.Set("MechanicalTopCheck", _fixCheck.Value);
            _station = Plate("base", origin, up, side, 10, true);
            // over the middle of the base, one block up: it lands on it at once
            _ship = Plate("ship", _station.GridIntegerToWorld(new Vector3I(3, 0, 3)) + up * 2.6, up, side, 5, false);
            var settle = WaitForSeconds(4, "the ship settles on the base");
            while (settle.MoveNext()) yield return settle.Current;
            Note($"the ship: {(_ship.Physics?.LinearVelocity.Length() ?? -1):0.00} m/s, static {_ship.IsStatic}");

            // SO's index of the clients of each state group, checked against the game's own walk of every client
            RuntimePluginControls.SetStateGroupIndexVerify(true);

            // ------------------------------------------------------------- placed as a frame, as the player does
            foreach (var (grid, subtypes, cells) in new[] { (_station, OnBase, _baseCells), (_ship, OnShip, _shipCells) })
            {
                var x = grid == _ship ? 0 : 1;
                foreach (var subtype in subtypes)
                {
                    var cell = new Vector3I(x, 1, 1);
                    x += 2;
                    var place = Place(grid, player, subtype, cell);
                    while (place.MoveNext()) yield return place.Current;
                    Check(grid.GetCubeBlock(cell)?.FatBlock is MyMechanicalConnectionBlockBase, $"the {subtype} was not placed at {cell}");
                    cells.Add(cell);
                    if (_fixCheck != null) KnockOut((MyMechanicalConnectionBlockBase)grid.GetCubeBlock(cell).FatBlock);
                }
            }
            var watch = Watch("placed");
            while (watch.MoveNext()) yield return watch.Current;
            IndexCheck("placed");
            if (_fixCheck != null)
            {
                var fixedCount = Convert.ToInt64(AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                    ?.GetType("SentisOptimisationsPlugin.CrashFix.MechanicalTopCheck")?.GetField("PutRight")?.GetValue(null) ?? -1L);
                Note($"ROTOR FIX | check {(_fixCheck.Value ? "on" : "off")}: heads put right since the start {fixedCount}; heads still wrong: {_failures.Count}");
                if (_fixCheck.Value) Check(_failures.Count == 0, "the check did not put the heads right: " + string.Join("; ", _failures));
                else Check(_failures.Count == Stators().Count, $"with the check off {_failures.Count} of {Stators().Count} knocked out heads were seen wrong");
                yield break;
            }

            if (_freezer)
            {
                var sleep = FreezeAndWake(up);
                while (sleep.MoveNext()) yield return sleep.Current;
                Check(_failures.Count == 0, "heads not held: " + string.Join("; ", _failures));
                yield break;
            }

            // ------------------------------------------------------------- the head added from the terminal
            foreach (var stator in Stators())
            {
                var head = stator.TopBlock?.CubeGrid;
                if (head == null) continue;
                Detach(stator);
                head.Close();
            }
            yield return WaitForTicks(30);
            Check(Stators().All(s => s.TopBlock == null), "a head is still there after it was removed");
            var recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod("RecreateTop", Any);
            Check(recreate != null, "MyMechanicalConnectionBlockBase.RecreateTop not found");
            foreach (var stator in Stators())
            {
                var s = stator;
                var normal = Enum.ToObject(recreate.GetParameters()[1].ParameterType, 0);     // MyTopBlockSize.Normal
                FakeClients.AsClient(0, () => recreate.Invoke(s, new object[] { (long?)_identity, normal, false }));
            }
            yield return WaitForTicks(30);
            watch = Watch("terminal head");
            while (watch.MoveNext()) yield return watch.Current;
            IndexCheck("terminal head");

            // ------------------------------------------------------------- fixship, the stators still frames
            var fix = FixShip("fixship frame");
            while (fix.MoveNext()) yield return fix.Current;

            // ------------------------------------------------------------- fixship, the stators welded up
            foreach (var stator in Stators())
            {
                var slim = stator.SlimBlock;
                slim.SpawnConstructionStockpile();
                slim.IncreaseMountLevel(slim.MaxIntegrity * 10, _identity, null, 1f, false, MyOwnershipShareModeEnum.Faction, true);
                Note($"{stator.BlockDefinition.Id.SubtypeName} at {stator.Position}: built {slim.BuildLevelRatio:0.00}, functional {stator.IsFunctional}");
            }
            fix = FixShip("fixship built");
            while (fix.MoveNext()) yield return fix.Current;

            Check(_failures.Count == 0, "heads not held: " + string.Join("; ", _failures));
        }

        /// <summary>The player away, the grids frozen by the freezer, 20 s asleep, the player back, the grids awake; the heads watched.</summary>
        private IEnumerator FreezeAndWake(Vector3D up)
        {
            _config.Set("FreezerEnabled", true);
            _config.Set("DelayBeforeFreezeSec", 5);
            _config.Set("FreezeDistanceStatic", 300);
            _config.Set("FreezeDistanceDynamic", 300);
            var home = _station.PositionComp.GetPosition() + up * 6;
            FakeClients.MoveTo(0, home + up * 1500);
            var heads = Stators().Select(s => s.TopBlock?.CubeGrid).Where(g => g != null).ToList();
            bool AllFrozen() => RuntimePluginControls.IsGridFrozen(_station.EntityId) && RuntimePluginControls.IsGridFrozen(_ship.EntityId) &&
                                heads.All(h => RuntimePluginControls.IsGridFrozen(h.EntityId));
            var freezing = Wait(AllFrozen, "the base, the ship and the heads freeze", 180);
            while (freezing.MoveNext()) yield return freezing.Current;
            Note($"frozen: the base, the ship and {heads.Count} heads");
            var asleep = WaitForSeconds(20, "asleep");
            while (asleep.MoveNext()) yield return asleep.Current;
            FakeClients.MoveTo(0, home);
            var waking = Wait(() => !RuntimePluginControls.IsGridFrozen(_station.EntityId) && !RuntimePluginControls.IsGridFrozen(_ship.EntityId) &&
                                    heads.All(h => !RuntimePluginControls.IsGridFrozen(h.EntityId)), "they wake", 120);
            while (waking.MoveNext()) yield return waking.Current;
            Note("awake again");
            var watch = Watch("after the freezer");
            while (watch.MoveNext()) yield return watch.Current;
        }

        /// <summary>The head detached, a metre aside and tilted 30°: what the check must put right.</summary>
        private void KnockOut(MyMechanicalConnectionBlockBase stator)
        {
            var top = stator.TopBlock;
            Check(top != null, "the placed stator has no head");
            // detached the game's way (the links broken: a half detach left them for the grids' deletion, which then
            // threw in MyGridPhysicalHierarchy.BreakLink and took the server down, 11.10.2026)
            Detach(stator);
            var m = top.CubeGrid.WorldMatrix;
            var turned = MatrixD.CreateFromAxisAngle(m.Right, MathHelper.ToRadians(30));
            var moved = m * MatrixD.CreateTranslation(-m.Translation) * turned * MatrixD.CreateTranslation(m.Translation + m.Right * 1.0);
            ((VRage.ModAPI.IMyEntity)top.CubeGrid).SetWorldMatrix(moved);
            var check = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "SentisOptimisations")
                ?.GetType("SentisOptimisationsPlugin.CrashFix.MechanicalTopCheck");
            var problem = check?.GetMethod("Problem");
            if (problem != null)
            {
                var args = new object[] { stator, top.EntityId, null };
                Note($"knocked out {stator.BlockDefinition.Id.SubtypeName}: the check sees {problem.Invoke(null, args) ?? "nothing wrong"}; " +
                     $"watched {((System.Collections.IDictionary)check.GetField("Watched", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null))?.Contains(stator.EntityId)}");
            }
        }

        private void IndexCheck(string phase)
        {
            var (mismatches, first) = RuntimePluginControls.StateGroupIndexVerifyResult();
            Note($"STATE GROUP INDEX | {phase}: {mismatches} groups the index sends to other clients than the game would{(first == null ? "" : " - " + first)}");
            if (mismatches > 0) _failures.Add($"{phase}: state group index differs from the game's: {first}");
        }

        /// <summary>The stators on the base and the ship, as the grids are now (a fixship makes them anew).</summary>
        private List<MyMechanicalConnectionBlockBase> Stators() =>
            _baseCells.Select(c => _station.GetCubeBlock(c)?.FatBlock).Concat(_shipCells.Select(c => _ship.GetCubeBlock(c)?.FatBlock))
                .OfType<MyMechanicalConnectionBlockBase>().ToList();

        private static void Detach(MyMechanicalConnectionBlockBase stator)
        {
            var detach = typeof(MyMechanicalConnectionBlockBase).GetMethods(Any).FirstOrDefault(m => m.Name == "Detach" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));
            if (detach != null) detach.Invoke(stator, new object[] { true });
            else ((Sandbox.ModAPI.IMyMechanicalConnectionBlock)stator).Detach();
        }

        /// <summary>SGI's fixship: the base, the ship and the heads cut and pasted; the grids found again by name.</summary>
        private IEnumerator FixShip(string label)
        {
            var sgi = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisGameplayImprovements")
                ?.GetType("SentisGameplayImprovements.FixShipLogic");
            Check(sgi != null, "SGI's FixShipLogic not found");
            var groups = sgi.GetMethod("FixGroups", BindingFlags.Static | BindingFlags.Public);
            foreach (var name in new[] { "base", "ship" })
            {
                var grid = name == "base" ? _station : _ship;
                var group = MyCubeGridGroups.Static.Logical.GetGroup(grid)?.Nodes.Select(n => n.NodeData).ToList() ?? new List<MyCubeGrid> { grid };
                groups.Invoke(null, new object[] { group });
                yield return WaitForTicks(60);
                var again = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !g.MarkedForClose && g.DisplayName == WorldApi.EntityPrefix + Prefix + name);
                Check(again != null, $"{label}: the {name} is not there after the fixship");
                Track(again);
                if (name == "base") _station = again; else _ship = again;
            }
            yield return WaitForTicks(30);
            var watch = Watch(label);
            while (watch.MoveNext()) yield return watch.Current;
        }

        /// <summary>
        /// The player leaves and comes back, as a reconnect: the base, the ship and the heads are streamed to it again,
        /// from the builders SentisOptimisations keeps (GridStreamBuilders) - compared here with fresh ones.
        /// </summary>
        private IEnumerator Reconnect(string phase)
        {
            RuntimePluginControls.SetGridStreamBuilderVerify(true);
            var at = _station.PositionComp.GetPosition() + _station.WorldMatrix.Up * 6;
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (at, 0, 0), withCharacters: true);
            var heads = Stators().Select(s => s.TopBlock?.CubeGrid).Where(g => g != null).Cast<MyEntity>().ToList();
            var entities = new List<MyEntity> { _station, _ship }.Concat(heads).ToList();
            for (var i = 0; i < 600 && FakeClients.ArrivedCount(entities) > 0; i++) yield return null;
            var (mismatches, first) = RuntimePluginControls.GridStreamBuilderVerifyResult();
            Note($"{phase}, reconnected: {FakeClients.Arrivals(entities)}; kept builders differing from fresh ones: {mismatches}{(first == null ? "" : " - " + first)}");
            if (mismatches > 0 && first != null && first.Contains(Prefix)) _failures.Add($"{phase}: the builders streamed on a reconnect differ from fresh ones: {first}");
            RuntimePluginControls.SetGridStreamBuilderVerify(false);
        }

        private IEnumerator Watch(string phase)
        {
            var constraint = typeof(MyMechanicalConnectionBlockBase).GetProperty("Constraint", Any);
            Check(constraint != null, "MyMechanicalConnectionBlockBase.Constraint not found");
            var watched = Stators().Select((s, i) => new Watched
            {
                Label = $"{phase}: {(s.CubeGrid == _station ? "base" : "ship")} {s.BlockDefinition.Id.SubtypeName} #{i + 1} (built {s.SlimBlock.BuildLevelRatio:0.00})",
                Stator = s,
            }).ToList();
            Check(watched.Count == _baseCells.Count + _shipCells.Count, $"{phase}: {watched.Count} stators of {_baseCells.Count + _shipCells.Count}");
            var started = DateTime.UtcNow;
            var nextNote = 0.0;
            while ((DateTime.UtcNow - started).TotalSeconds < WatchSeconds)
            {
                var seconds = (DateTime.UtcNow - started).TotalSeconds;
                foreach (var w in watched)
                {
                    var top = w.Stator.TopBlock;
                    var attached = ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)w.Stator).IsAttached;
                    var hk = constraint.GetValue(w.Stator) as HkConstraint;
                    var state = top == null ? "no head" :
                        $"{(attached ? "attached" : "DETACHED")}, hinge {(hk == null ? "NONE" : hk.InWorld ? "in world" : "OUT of world")}";
                    if (top != null)
                    {
                        var offset = Vector3D.Transform(top.CubeGrid.PositionComp.GetPosition(), w.Stator.PositionComp.WorldMatrixNormalizedInv);
                        var speed = ((top.CubeGrid.Physics?.LinearVelocity ?? Vector3.Zero) - (w.Stator.CubeGrid.Physics?.LinearVelocity ?? Vector3.Zero)).Length();
                        // the head's own axis against the stator's: a head on its hinge stands on the stator's up
                        var tilt = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(top.WorldMatrix.Up, w.Stator.WorldMatrix.Up), -1, 1)));
                        if (w.FirstTilt < 0) w.FirstTilt = tilt;
                        if (seconds >= SettleSeconds)
                        {
                            if (w.Offset == null) w.Offset = offset;
                            w.Drift = Math.Max(w.Drift, (offset - w.Offset.Value).Length());
                            w.Speed = Math.Max(w.Speed, speed);
                            w.Tilt = Math.Max(w.Tilt, tilt);
                        }
                        state += $", head at {offset.Length():0.00} m, tilted {tilt:0.0}°, {speed:0.00} m/s, head physics {(top.CubeGrid.Physics?.Enabled == true ? "on" : "OFF")}" +
                                 $" (built {top.SlimBlock.BuildLevelRatio:0.00})";
                    }
                    if (seconds >= SettleSeconds)
                    {
                        if (top == null || !attached) w.Detached++;
                        if (hk == null || !hk.InWorld) w.NoConstraint++;
                    }
                    var head = state.Split(',')[0];
                    if (w.Seen.Count == 0 || w.Seen[w.Seen.Count - 1] != head) w.Seen.Add(head);
                    if (_zone is MySafeZone zone && top != null && seconds >= nextNote)
                    {
                        var held = (typeof(MySafeZone).GetField("m_containedEntities", Any)?.GetValue(zone) as System.Collections.IEnumerable)?.Cast<long>().ToList();
                        state += $", in the zone: head {held?.Contains(top.CubeGrid.EntityId)}, its grid {held?.Contains(w.Stator.CubeGrid.EntityId)}";
                    }
                    if (seconds >= nextNote) Note($"{seconds:0} s: {w.Label}: {state}");
                }
                if (seconds >= nextNote) nextNote += 5;
                yield return null;
            }

            foreach (var w in watched)
            {
                Note($"ROTOR RESULT | {w.Label}: frames detached {w.Detached}, without the hinge {w.NoConstraint}, the head moved {w.Drift:0.000} m " +
                     $"against the stator at up to {w.Speed:0.00} m/s, tilted {w.FirstTilt:0.0}° at first and up to {w.Tilt:0.0}°; states {string.Join(" > ", w.Seen)}");
                if (w.Detached > 0 || w.NoConstraint > 0 || w.Drift > MaxDriftM || w.Speed > MaxSpeedMps || w.Tilt > MaxTiltDegrees)
                    _failures.Add($"{w.Label}: detached {w.Detached}, no hinge {w.NoConstraint}, moved {w.Drift:0.00} m, {w.Speed:0.00} m/s, tilted {w.Tilt:0.0}°");
            }
            foreach (var w in watched)
                if (w.Stator.TopBlock?.CubeGrid is MyCubeGrid headGrid) Track(headGrid);
            var reconnect = Reconnect(phase);
            while (reconnect.MoveNext()) yield return reconnect.Current;
        }

        /// <summary>A rotor placed by the player from the build menu, with its components in the pocket, as in survival.</summary>
        private IEnumerator Place(MyCubeGrid grid, MyPlayer player, string subtype, Vector3I cell)
        {
            var definition = MyDefinitionManager.Static.GetDefinitionsOfType<MyCubeBlockDefinition>().First(d => d.Id.SubtypeName == subtype && d.CubeSize == grid.GridSizeEnum);
            var character = player.Character;
            foreach (var component in definition.Components)
                character.GetInventory().AddItems(component.Count, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(component.Definition.Id));
            FakeClients.MoveTo(0, grid.GridIntegerToWorld(cell) + grid.WorldMatrix.Up * 5 + grid.WorldMatrix.Backward * 4);
            yield return WaitForTicks(2);
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", Any);
            Check(request != null, "MyCubeGrid.BuildBlocksRequest not found");
            var max = cell + definition.Size - Vector3I.One;
            var location = new MyCubeGrid.MyBlockLocation(definition.Id, cell, max, cell + definition.Center, Quaternion.Identity, 0, _zoneTracking == null ? _identity : 0);
            var visuals = new MyCubeGrid.MyBlockVisuals(new Vector3(0, -1, 0).PackHSVToUint(), MyStringHash.NullOrEmpty);
            FakeClients.AsClient(0, () =>
                request.Invoke(grid, new object[] { visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, character.EntityId, false, _identity }));
            yield return WaitForTicks(10);
        }

        private MyCubeGrid Plate(string name, Vector3D at, Vector3D up, Vector3D side, int size, bool isStatic)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < size; x++)
            for (var z = 0; z < size; z++)
            {
                var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                block.Min = new SerializableVector3I(x, 0, z);
                block.Owner = _zoneTracking == null ? _identity : 0;
                block.BuiltBy = _zoneTracking == null ? _identity : 0;
                blocks.Add(block);
            }
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = isStatic,
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
                foreach (var head in MyEntities.GetEntities().OfType<MyCubeGrid>()
                             .Where(g => !g.MarkedForClose && g.BlocksCount == 1 && g.CubeBlocks.First().FatBlock is MyAttachableTopBlockBase &&
                                         _station != null && Vector3D.Distance(g.PositionComp.GetPosition(), _station.PositionComp.GetPosition()) < 60).ToList())
                    head.Close();
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
