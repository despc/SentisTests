using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a grid on the border of a safe zone costs.
    ///
    /// A. Building on a grid in and around a zone - what welding from a projection does: one block
    ///    a frame is added to a large grid lying across the border of its owner's zone (a static base,
    ///    then a dynamic ship), to a dynamic ship wholly inside the zone, and to the same base far
    ///    from any zone. Every new block
    ///    reshapes the grid's body, the zone hears that the body has left it, and the game tests the
    ///    whole grid's shape against the zone's to find out whether it really did.
    /// B. A ship with a rotor head that the zone does not admit flying into it and out of it, again
    ///    and again: the zone locks the motors of the group that is inside and unlocks them when a
    ///    grid of it leaves - this looks for motors switched back and forth and for stalls.
    ///
    /// Reported per phase: simulation frames, the shape tests (count, time, the longest), the enters
    /// and leaves the zone was told about, its update time and the motor locks and unlocks.
    ///
    /// Checked: every grid across the border or inside is held by its zone after it settles and after
    /// it has been built on, and the unwelcome ship is held each time it comes in and let go each time
    /// it is out (how many frames that took is reported). <see cref="GameScenarioName"/> runs the same
    /// with SentisOptimisations' safe zone grid tracking off - the zones as the game has them.
    /// </summary>
    internal sealed class SafeZoneBorderScenario : TestScenario
    {
        public const string ScenarioName = "safezone_border";
        public const string GameScenarioName = "safezone_border_vanilla";
        private readonly string _name;
        private readonly bool _gameTracking;
        private readonly ConfigOverride _config = new ConfigOverride();

        public SafeZoneBorderScenario() : this(ScenarioName, false)
        {
        }

        public SafeZoneBorderScenario(string name, bool gameTracking)
        {
            _name = name;
            _gameTracking = gameTracking;
        }

        public override string Name => _name;
        public override int TimeoutSeconds => 300;

        private const string Prefix = "sz-";
        private const float ZoneRadius = 60;
        private const int BaseX = 20, BaseY = 8, BaseZ = 20;   // 3200 blocks
        private const int BuildFrames = 600;
        private const int FlyCycles = 20;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private Vector3D _up, _side, _forward;
        private MyEntity _zone;
        private readonly List<string> _results = new List<string>();

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _config.Set("SafeZoneGridTracking", !_gameTracking);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            _up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            _forward = Vector3D.Cross(_side, _up);
            Vector3D centre = default;
            for (var d = 100000.0; d <= 1000000; d += 50000)
            {
                centre = anchor + _up * d + _forward * 5000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f) break;
            }

            FakeClients.RemoveAll();
            FakeClients.Add(2, Network, p => (centre + _up * 150 + _side * (20 * p), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            var owner = FakeClients.Character(0)?.GetPlayerIdentityId() ?? 0;
            var stranger = FakeClients.Character(1)?.GetPlayerIdentityId() ?? 0;
            Check(owner != 0 && stranger != 0 && owner != stranger, "the fake players have no identities");

            // the owner's zone: only the owner is welcome
            _zone = (MyEntity)MySessionComponentSafeZones.CrateSafeZone(MatrixD.CreateWorld(centre, _forward, _up), MySafeZoneShape.Sphere,
                MySafeZoneAccess.Whitelist, new[] { owner }, null, ZoneRadius, enable: true, isVisible: true);
            Check(_zone != null, "no safe zone");
            // grids are welcome unless blacklisted (a grid's owner is not checked against the players' list)
            ((MySafeZone)_zone).AccessTypeGrids = MySafeZoneAccess.Blacklist;
            ((MySafeZone)_zone).AllowedActions = MySafeZoneAction.All & ~MySafeZoneAction.Damage;
            Note("zone of radius " + ZoneRadius + " m, grids welcome unless blacklisted, damage off; its phantom on layer " +
                 (_zone.Physics?.RigidBody?.Layer.ToString() ?? "?") + (_gameTracking ? " (the game's tracking)" : " (the plugin's tracking)"));
            Check(SafeZoneProbe.Start() == "safe zone probe on", "the safe zone probe did not start");

            // ------------------------------------------------------------- A. building across the border
            // the middle of the grid on the zone's surface: half of it in, half out
            var border = centre + _side * ZoneRadius;
            var far = centre + _side * 2000 - _forward * 2000;

            var a1 = Build("base across the border", border, owner, isStatic: true);
            while (a1.MoveNext()) yield return a1.Current;
            var a2 = Build("base far from the zone", far, owner, isStatic: true);
            while (a2.MoveNext()) yield return a2.Current;
            var a3 = Build("ship across the border", border + _forward * 200, owner, isStatic: false, zoneCentre: centre + _forward * 200);
            while (a3.MoveNext()) yield return a3.Current;
            // a ship welded wholly inside its owner's zone
            var a4 = Build("ship inside the zone", centre + _forward * 400, owner, isStatic: false, zoneCentre: centre + _forward * 400);
            while (a4.MoveNext()) yield return a4.Current;

            // ------------------------------------------------------------- B. an unwelcome ship with a rotor, in and out
            var ship = Ship(centre + _side * (ZoneRadius + 80), stranger);
            var settle = WaitForSeconds(2, "the ship settles");
            while (settle.MoveNext()) yield return settle.Current;
            var stator = WorldApi.FindFunctional<MyMotorStator>(ship);
            var recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", BindingFlags.Instance | BindingFlags.NonPublic);
            recreate.Invoke(stator, new object[] { stranger, Enum.Parse(recreate.GetParameters()[1].ParameterType, "Normal"), true });
            var topped = Wait(() => stator.TopGrid != null, "the rotor head is built", 10);
            while (topped.MoveNext()) yield return topped.Current;
            Track(stator.TopGrid);
            // this ship is not welcome in the zone
            ((MySafeZone)_zone).Entities.Add(ship.EntityId);
            ((MySafeZone)_zone).Entities.Add(stator.TopGrid.EntityId);

            FrameProbe.Take();
            SafeZoneProbe.Reset();
            var lockChanges = 0;
            var wasLocked = ship.ForceLockMotors.Value;
            var worstFrame = 0.0;
            var inside = ship.WorldMatrix;
            inside.Translation = centre + _side * (ZoneRadius - 10);
            var outside = ship.WorldMatrix;
            outside.Translation = centre + _side * (ZoneRadius + 80);
            var zoneGrids = ((MySafeZone)_zone).GetGridsInside();
            int missedEntries = 0, missedExits = 0, slowestEntry = 0, slowestExit = 0;
            var fastest = 0.0;
            for (var cycle = 0; cycle < FlyCycles * 2; cycle++)
            {
                var goingIn = cycle % 2 == 0;
                fastest = Math.Max(fastest, ship.Physics?.LinearVelocity.Length() ?? 0);
                ship.Teleport(goingIn ? inside : outside);
                // a ship that came in and stays: the push out of the last visit does not carry over
                ship.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
                var seenAt = -1;
                for (var f = 0; f < 60; f++)
                {
                    var at = DateTime.UtcNow;
                    yield return null;
                    worstFrame = Math.Max(worstFrame, (DateTime.UtcNow - at).TotalMilliseconds);
                    if (ship.ForceLockMotors.Value != wasLocked)
                    {
                        lockChanges++;
                        wasLocked = ship.ForceLockMotors.Value;
                    }
                    if (seenAt < 0 && zoneGrids.Contains(ship) == goingIn) seenAt = f + 1;
                }
                if (goingIn)
                {
                    if (seenAt < 0) missedEntries++;
                    else slowestEntry = Math.Max(slowestEntry, seenAt);
                }
                else
                {
                    // the zone pushes the ship out; after 60 frames far outside it must be let go
                    if (zoneGrids.Contains(ship)) missedExits++;
                    else if (seenAt > 0) slowestExit = Math.Max(slowestExit, seenAt);
                }
            }
            var flyResult = "unwelcome ship with a rotor, " + FlyCycles + " times in and out: " + SafeZoneProbe.Format() +
                            ", motor lock switched " + lockChanges + " times, worst frame " + worstFrame.ToString("F0") + " ms" +
                            "; held on entry within " + slowestEntry + " frames, let go within " + slowestExit + " frames, missed entries " +
                            missedEntries + ", missed exits " + missedExits + "; the ship left each place at up to " + fastest.ToString("F0") + " m/s";
            Note(flyResult);
            Note("frames: " + Short(FrameProbe.Take()));
            _results.Add(flyResult);

            Note("SAFEZONE BORDER RESULT: " + string.Join(" || ", _results));
            Check(missedEntries == 0 && missedExits == 0,
                "the zone missed the unwelcome ship: " + missedEntries + " entries, " + missedExits + " exits of " + FlyCycles);
        }

        /// <summary>A large armour grid, then one block a frame added to it for BuildFrames frames.</summary>
        private IEnumerator Build(string what, Vector3D middle, long owner, bool isStatic, Vector3D? zoneCentre = null)
        {
            MyEntity zone = null;
            if (zoneCentre.HasValue)
            {
                // the ship gets a zone of its own so the phases do not share one
                zone = (MyEntity)MySessionComponentSafeZones.CrateSafeZone(MatrixD.CreateWorld(zoneCentre.Value, _forward, _up), MySafeZoneShape.Sphere,
                    MySafeZoneAccess.Whitelist, new[] { owner }, null, ZoneRadius, enable: true, isVisible: true);
                ((MySafeZone)zone).AccessTypeGrids = MySafeZoneAccess.Blacklist;
                ((MySafeZone)zone).AllowedActions = MySafeZoneAction.All & ~MySafeZoneAction.Damage;
            }

            var grid = Slab(middle, owner, isStatic, what);
            yield return null;
            yield return null;
            // put the middle of the grid's box where it belongs (the grid's origin is a corner of it)
            var matrix = grid.WorldMatrix;
            matrix.Translation += middle - grid.PositionComp.WorldAABB.Center;
            grid.Teleport(matrix);
            if (grid.Physics != null) grid.Physics.SetSpeeds(Vector3.Zero, Vector3.Zero);
            var settle = WaitForSeconds(3, what + ": settles");
            while (settle.MoveNext()) yield return settle.Current;
            var zoneAt = zoneCentre ?? (Vector3D)((MyEntity)_zone).PositionComp.GetPosition();
            var box = grid.PositionComp.WorldAABB;
            var corners = box.GetCorners();
            var cornersInside = corners.Count(c => Vector3D.Distance(c, zoneAt) < ZoneRadius);
            Note(what + ": " + cornersInside + " of 8 corners of the grid's box inside the zone, its middle " +
                 Vector3D.Distance(box.Center, zoneAt).ToString("F1") + " m from the zone's centre");
            var holder = (MySafeZone)(zone ?? _zone);
            var nearZone = Vector3D.Distance(box.Center, zoneAt) < ZoneRadius + 100;
            if (nearZone) Check(holder.GetGridsInside().Contains(grid), what + ": the zone does not hold the grid after it settled");

            FrameProbe.Take();
            SafeZoneProbe.Reset();
            var added = 0;
            var worstFrame = 0.0;
            for (var i = 0; i < BuildFrames; i++)
            {
                var x = i % BaseX;
                var z = (i / BaseX) % BaseZ;
                var y = BaseY + i / (BaseX * BaseZ);
                var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                block.Min = new SerializableVector3I(x, y, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                if (((IMyCubeGrid)grid).AddBlock(block, false) != null) added++;
                var at = DateTime.UtcNow;
                yield return null;
                worstFrame = Math.Max(worstFrame, (DateTime.UtcNow - at).TotalMilliseconds);
            }

            var settleAfter = WaitForTicks(30);
            while (settleAfter.MoveNext()) yield return settleAfter.Current;
            if (nearZone) Check(holder.GetGridsInside().Contains(grid), what + ": the zone lost the grid while it was built on");
            else Check(!holder.GetGridsInside().Contains(grid), what + ": the zone holds a grid far from it");
            var result = what + " (" + grid.BlocksCount + " blocks, " + added + " added one a frame): " + SafeZoneProbe.Format() +
                         ", worst frame " + worstFrame.ToString("F0") + " ms";
            Note(result);
            Note("frames: " + Short(FrameProbe.Take()));
            _results.Add(result);
            var id = grid.EntityId;
            grid.Close();
            var closing = WaitForTicks(3);
            while (closing.MoveNext()) yield return closing.Current;
            // a grid that left the world must not stay among the zone's contents
            var contained = (VRage.Collections.MyConcurrentHashSet<long>)typeof(MySafeZone)
                .GetField("m_containedEntities", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(holder);
            Check(!contained.Contains(id), what + ": the zone still holds the grid after it was closed");
            zone?.Close();
        }

        /// <summary>A slab of armour whose box is centred on <paramref name="middle"/>.</summary>
        private MyCubeGrid Slab(Vector3D middle, long owner, bool isStatic, string name)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < BaseX; x++)
            for (var y = 0; y < BaseY; y++)
            for (var z = 0; z < BaseZ; z++)
            {
                var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                block.Min = new SerializableVector3I(x, y, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                blocks.Add(block);
            }
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-'),
                DisplayName = WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-'),
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(middle, _forward, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            return grid;
        }

        /// <summary>A small ship: armour, a battery and a rotor.</summary>
        private MyCubeGrid Ship(Vector3D at, long owner)
        {
            MyObjectBuilder_CubeBlock Block(string subtype, int x, int y, int z)
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(x, y, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                return block;
            }

            var battery = (MyObjectBuilder_BatteryBlock)Block("LargeBlockBatteryBlock", -1, 1, 0);
            battery.CurrentStoredPower = 3f;
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "ship",
                DisplayName = WorldApi.EntityPrefix + Prefix + "ship",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _forward, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>
                {
                    Block("LargeBlockArmorBlock", -1, 0, 0), Block("LargeBlockArmorBlock", 0, 0, 0), Block("LargeBlockArmorBlock", 1, 0, 0),
                    Block("LargeBlockArmorBlock", -1, 0, 1), Block("LargeBlockArmorBlock", 0, 0, 1), Block("LargeBlockArmorBlock", 1, 0, 1),
                    battery, Block("LargeStator", 1, 1, 0),
                },
            });
            Track(grid);
            return grid;
        }

        private static string Short(string text) => text.Length <= 400 ? text : text.Substring(0, 400) + "...";

        public override void Cleanup()
        {
            try
            {
                SafeZoneProbe.Stop();
                _config.Restore();
                FakeClients.RemoveAll();
                if (_zone != null && !_zone.Closed) _zone.Close();
                foreach (var zone in MyEntities.GetEntities().OfType<MySafeZone>().ToList())
                    if (zone.SafeZoneBlockId == 0 && zone.Radius == ZoneRadius) zone.Close();
            }
            catch (Exception e)
            {
                Log.Warn("cleaning up after the test failed: " + e.Message);
            }
            finally { base.Cleanup(); }
        }
    }
}
