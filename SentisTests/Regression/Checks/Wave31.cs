using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>What the checks of this file share: a block added to a grid that is already there, a session setting changed for a while.</summary>
    internal static class Wave31
    {
        /// <summary>
        /// A block put on an existing grid as the game puts one (<c>IMyCubeGrid.AddBlock</c>, built whole) - a rotor's
        /// head grid has no builder to spawn from. Null when the cells are taken.
        /// </summary>
        public static MySlimBlock Add(MyCubeGrid grid, string id, Vector3I min, Base6Directions.Direction forward, Base6Directions.Direction up, long owner,
            Action<MyObjectBuilder_CubeBlock> setup = null)
        {
            var definition = RegressionCheck.Definition(id);
            var block = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializer.CreateNewObject(definition.Id);
            block.Min = min;
            block.BlockOrientation = new SerializableBlockOrientation(forward, up);
            block.Owner = owner;
            block.BuiltBy = owner;
            block.ShareMode = MyOwnershipShareModeEnum.Faction;
            setup?.Invoke(block);
            try
            {
                return ((VRage.Game.ModAPI.IMyCubeGrid)grid).AddBlock(block, false) as MySlimBlock;
            }
            catch (Exception e)
            {
                // where in the game's method it fell (no line numbers there): the IL offsets of the frames
                var frames = new System.Diagnostics.StackTrace(e).GetFrames() ?? new System.Diagnostics.StackFrame[0];
                SentisTestsPlugin.Log.Error("Wave31.Add " + id + " into " + grid.DisplayName + " threw " + e.GetType().Name + ": " +
                                            string.Join(" <- ", frames.Take(4).Select(f => (f.GetMethod()?.Name ?? "?") + " IL_" + f.GetILOffset().ToString("x4"))) +
                                            "; grid systems " + (grid.GridSystems != null) + ", physics " + (grid.Physics != null) + ", closed " + grid.Closed +
                                            ", in scene " + grid.InScene);
                throw;
            }
        }

        /// <summary>The direction of the grid's own axes closest to a direction in the world.</summary>
        public static Base6Directions.Direction Local(MyCubeGrid grid, Vector3D world) => grid.WorldMatrix.GetClosestDirection(world);

        /// <summary>The cells next to a block (its six sides), in the block's grid.</summary>
        public static IEnumerable<Vector3I> Around(MySlimBlock block)
        {
            var taken = new HashSet<Vector3I>();
            for (var x = block.Min.X; x <= block.Max.X; x++)
            for (var y = block.Min.Y; y <= block.Max.Y; y++)
            for (var z = block.Min.Z; z <= block.Max.Z; z++)
                taken.Add(new Vector3I(x, y, z));
            return taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).Where(c => !taken.Contains(c)).Distinct().ToList();
        }

        /// <summary>The rock in a cube about a point of a planet: the content of all its voxels summed.</summary>
        public static long Rock(MyPlanet planet, Vector3D point, double half)
        {
            var low = point - new Vector3D(half);
            var high = point + new Vector3D(half);
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref low, out Vector3I min);
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref high, out Vector3I max);
            min += planet.StorageMin;
            max += planet.StorageMin;
            var cache = new VRage.Voxels.MyStorageData();
            cache.Resize(min, max);
            planet.Storage.ReadRange(cache, VRage.Voxels.MyStorageDataTypeFlags.Content, 0, min, max);
            long sum = 0;
            var size = max - min + Vector3I.One;
            for (var i = 0; i < size.X * size.Y * size.Z; i++) sum += cache.Content(i);
            return sum;
        }
    }

    /// <summary>Session settings changed for a check and put back as they were (<see cref="Restore"/>, from the check's cleanup).</summary>
    internal sealed class SessionSettingsOverride
    {
        private readonly List<(MemberInfo Member, object Value)> _old = new List<(MemberInfo, object)>();

        public void Set(string name, object value)
        {
            var settings = MySession.Static.Settings;
            var type = settings.GetType();
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            var property = field == null ? type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) : null;
            if (field == null && property == null) throw new Core.ScenarioFailedException("the session settings have no " + name);
            var old = field != null ? field.GetValue(settings) : property.GetValue(settings);
            if (_old.All(o => o.Member.Name != name)) _old.Add(((MemberInfo)field ?? property, old));
            var converted = Convert.ChangeType(value, field?.FieldType ?? property.PropertyType);
            if (field != null) field.SetValue(settings, converted);
            else property.SetValue(settings, converted);
        }

        public void Restore()
        {
            var settings = MySession.Static?.Settings;
            if (settings == null) return;
            foreach (var (member, value) in Enumerable.Reverse(_old))
            {
                if (member is FieldInfo field) field.SetValue(settings, value);
                else ((PropertyInfo)member).SetValue(settings, value);
            }
            _old.Clear();
        }
    }

    /// <summary>
    /// Conveyors through mechanisms (the conveyor endpoints of <c>MyMotorStator</c> and <c>MyMotorAdvancedRotor</c>,
    /// <c>MyPistonBase</c> and <c>MyPistonTop</c>): an advanced rotor, a hinge and a piston on a station, uranium in
    /// containers on the base, empty reactors on the head grid - a head reactor on the conveyor port of the head pulls
    /// uranium from the base through the mechanism. A plain rotor's head (<c>MyMotorRotor</c>) has no conveyor: nothing
    /// goes through it.
    /// </summary>
    public sealed class ConveyorMechanismsCheck : RegressionCheck
    {
        public override string Name => Prefix + "conveyor_mechanisms";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            var done = new List<string>();
            var mechanisms = new[] { "MotorAdvancedStator/LargeAdvancedStator", "MotorAdvancedStator/LargeHinge", "ExtendedPistonBase/LargePistonBase", "MotorStator/LargeStator" };
            for (var i = 0; i < mechanisms.Length; i++)
            {
                var id = mechanisms[i];
                var grid = Build("conveyor-mechanism", Site.At(i * 15 - 15, 0, 0), true, new Part(id, 0, 0, 0));
                var mechanism = Block<MyMechanicalConnectionBlockBase>(grid);
                yield return AddHead(mechanism);
                ((IMyTerminalBlock)mechanism).SetValueFloat("Velocity", 0f);
                if (mechanism is MyMotorStator) ((IMyTerminalBlock)mechanism).SetValueBool("RotorLock", true);
                var head = mechanism.TopGrid;
                var top = mechanism.TopBlock;
                Check(top != null, id + " has no head block");

                // the base: a battery and containers with uranium on every side of the mechanism away from its head
                bool NearHead(Vector3D at) => head.GetBlocks().Any(b => Vector3D.Distance(head.GridIntegerToWorld(b.Position), at) < 2);
                var free = Wave31.Around(mechanism.SlimBlock).Where(c => !NearHead(grid.GridIntegerToWorld(c))).ToList();
                Check(free.Count >= 2, id + ": " + free.Count + " free sides on the base");
                Check(Wave31.Add(grid, "BatteryBlock/LargeBlockBatteryBlock", free[0], Base6Directions.Direction.Forward, Base6Directions.Direction.Up, Identity,
                    b => Motion.Charged(b)) != null, id + ": the battery was not added");
                var cargos = new List<MyCargoContainer>();
                foreach (var cell in free.Skip(1))
                {
                    var slim = Wave31.Add(grid, "CargoContainer/LargeBlockSmallContainer", cell, Base6Directions.Direction.Forward, Base6Directions.Direction.Up, Identity);
                    if (slim?.FatBlock is MyCargoContainer cargo)
                    {
                        Put(Inventory(cargo), "Ingot/Uranium", 5);
                        cargos.Add(cargo);
                    }
                }
                Check(cargos.Count > 0, id + ": no container on the base");

                // the head: an empty reactor where its conveyor port meets the head's - the ports are in the models, so each
                // side of the head block away from the base and each way the reactor faces is tried, the first on the head's
                // conveyors kept; on a head with no conveyor, a reactor on every side
                bool NearBase(Vector3D at) => Vector3D.Distance(mechanism.PositionComp.GetPosition(), at) < 2;
                var baseEnd = (mechanism as Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock)?.ConveyorEndpoint;
                var topEnd = (top as Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock)?.ConveyorEndpoint;
                var reactors = new List<MyReactor>();
                var faces = new[]
                {
                    Base6Directions.Direction.Forward, Base6Directions.Direction.Backward, Base6Directions.Direction.Left,
                    Base6Directions.Direction.Right, Base6Directions.Direction.Up, Base6Directions.Direction.Down,
                };
                foreach (var cell in Wave31.Around(top.SlimBlock).Where(c => !NearBase(head.GridIntegerToWorld(c))).ToList())
                {
                    if (topEnd != null && reactors.Count > 0) break;
                    foreach (var face in topEnd == null ? faces.Take(1) : faces)
                    {
                        if (!(Wave31.Add(head, "Reactor/LargeBlockSmallGenerator", cell, face, Base6Directions.GetPerpendicular(face), Identity)?.FatBlock is MyReactor reactor)) continue;
                        if (topEnd == null)
                        {
                            reactors.Add(reactor);
                            break;
                        }
                        yield return WaitForTicks(10);
                        if (MyGridConveyorSystem.Reachable(topEnd, reactor.ConveyorEndpoint))
                        {
                            reactors.Add(reactor);
                            break;
                        }
                        head.RazeBlock(cell);
                        yield return WaitForTicks(1);
                    }
                }
                Check(reactors.Count > 0, id + ": no reactor on the head's conveyors");

                double Pulled() => reactors.Sum(r => r.GetInventory(0) is Sandbox.Game.MyInventory inventory ? Amount(inventory, "Ingot/Uranium") : 0);
                string Reactors() => string.Join(", ", reactors.Select(r => "reactor " + r.EntityId + (r.Closed ? " closed" : r.MarkedForClose ? " closing" : "") +
                                                                            (r.GetInventory(0) == null ? " no inventory" : "") + (r.CubeGrid == head ? "" : " on another grid " + r.CubeGrid?.EntityId))) +
                                     "; head grid " + head.EntityId + (head.Closed ? " closed" : "") + ", the mechanism's head now " + (mechanism.TopGrid?.EntityId.ToString() ?? "none") +
                                     (mechanism.Closed ? ", the mechanism closed" : "");
                string Why() => id + ": reachable " + reactors.Count(r => cargos.Any(c => MyGridConveyorSystem.Reachable(c.ConveyorEndpoint, r.ConveyorEndpoint))) + " of " +
                                reactors.Count + " head reactors from the base, attached " + ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)mechanism).IsAttached +
                                "; containers to the base " + (baseEnd == null ? "no endpoint" : cargos.Count(c => MyGridConveyorSystem.Reachable(c.ConveyorEndpoint, baseEnd)) + " of " + cargos.Count) +
                                ", base to head " + (baseEnd == null || topEnd == null ? "no endpoint" : MyGridConveyorSystem.Reachable(baseEnd, topEnd).ToString()) +
                                ", head to reactors " + (topEnd == null ? "no endpoint" : reactors.Count(r => MyGridConveyorSystem.Reachable(topEnd, r.ConveyorEndpoint)) + " of " + reactors.Count) +
                                "; head " + top.BlockDefinition.Id.SubtypeName + ", base power " + grid.GridSystems.ResourceDistributor.ResourceState + "; " + Reactors();
                if (id == "MotorStator/LargeStator")
                {
                    yield return Seconds(10);
                    Check(Pulled() == 0, "uranium went through a plain rotor, which has no conveyor: " + F(Pulled()) + " kg");
                    Check(reactors.All(r => !r.Closed && r.GetInventory(0) != null), id + ": " + Reactors());
                    break;
                }
                yield return Until(Pulled, v => v > 0, "uranium pulled from the base to the head through " + id, 30, Why);
                Check(reactors.All(r => !r.Closed && r.GetInventory(0) != null), id + ": " + Reactors());
                done.Add(mechanism.BlockDefinition.Id.SubtypeName + " " + F(Pulled()) + " kg");
            }
            Result("uranium went from the base to a reactor on the head through: " + string.Join(", ", done) + "; not through a plain rotor");
        }
    }

    /// <summary>
    /// Who drives a subgrid's thrusters (<c>MyShipController.MoveAndRotate</c>: a cockpit sets the thrust of its own
    /// grid's thrust component only): a ship with a cockpit and thrusters, a locked rotor, and on the rotor's head
    /// another seat with thrusters of its own. Forward from the ship's cockpit fires the ship's forward thruster and not
    /// the head's; forward from the seat on the head fires the head's.
    /// </summary>
    public sealed class SubgridControlCheck : RegressionCheck
    {
        public override string Name => Prefix + "subgrid_control";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 6));
            var ship = Build("subgrid-control", Site.Origin, false,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, -1, 1),
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Base6Directions.Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Base6Directions.Direction.Forward),
                new Part("MotorStator/LargeStator", 0, 0, 1));
            var seat = Block<MyCockpit>(ship);
            var stator = Block<MyMotorStator>(ship);
            yield return AddHead(stator);
            ((IMyTerminalBlock)stator).SetValueFloat("Velocity", 0f);
            ((IMyTerminalBlock)stator).SetValueBool("RotorLock", true);
            var head = stator.TopGrid;
            var top = stator.TopBlock;
            var away = Wave31.Local(head, top.PositionComp.GetPosition() - stator.PositionComp.GetPosition());
            var cell = top.Position + Base6Directions.GetIntVector(away);
            var forward = Wave31.Local(head, seat.WorldMatrix.Forward);
            var right = Base6Directions.GetIntVector(Wave31.Local(head, seat.WorldMatrix.Right));
            var headSeat = Wave31.Add(head, "Cockpit/LargeBlockCockpitSeat", cell, forward, away, Identity)?.FatBlock as MyCockpit;
            Check(headSeat != null, "the seat was not added to the head");
            var headThrust = new[]
            {
                Wave31.Add(head, "Thrust/LargeBlockSmallThrust", cell + right, forward, away, Identity)?.FatBlock as MyThrust,
                Wave31.Add(head, "Thrust/LargeBlockSmallThrust", cell - right, Base6Directions.GetOppositeDirection(forward), away, Identity)?.FatBlock as MyThrust,
            };
            Check(headThrust.All(t => t != null), "the thrusters were not added to the head");
            var shipThrust = Blocks<MyThrust>(ship).ToArray();
            var all = shipThrust.Concat(headThrust).ToArray();

            IEnumerator Push(MyCockpit from, Dictionary<MyThrust, float> strongest)
            {
                for (var f = Frames(2); f > 0; f--)
                {
                    ShipKeys(from, new Vector3(0, 0, -1));
                    foreach (var t in all) strongest[t] = Math.Max(strongest.TryGetValue(t, out var v) ? v : 0, t.CurrentStrength);
                    yield return null;
                }
                for (var f = Frames(3); f > 0; f--)
                {
                    ShipKeys(from, Vector3.Zero);
                    yield return null;
                }
            }
            string Say(Dictionary<MyThrust, float> s) => "ship " + string.Join("/", shipThrust.Select(t => F(s[t]))) + ", head " + string.Join("/", headThrust.Select(t => F(s[t])));

            yield return Sit(seat);
            var fromShip = new Dictionary<MyThrust, float>();
            yield return Push(seat, fromShip);
            seat.RemovePilot();
            yield return WaitForTicks(5);
            yield return Sit(headSeat);
            var fromHead = new Dictionary<MyThrust, float>();
            yield return Push(headSeat, fromHead);

            var shipForward = shipThrust.OrderByDescending(t => fromShip[t]).First();
            var headForward = headThrust.OrderByDescending(t => fromHead[t]).First();
            Check(fromShip[shipForward] > 0.2f, "forward from the ship's cockpit fired none of the ship's thrusters: " + Say(fromShip));
            Check(fromHead[headForward] > 0.2f, "forward from the seat on the head fired none of the head's thrusters: " + Say(fromHead));
            Check(fromShip[headForward] < 0.05f, "forward from the ship's cockpit fired the head's forward thruster: " + Say(fromShip));
            Result("forward from the ship's cockpit: " + Say(fromShip) + "; from the seat on the head: " + Say(fromHead));
        }
    }

    /// <summary>
    /// Weapon damage by the armor it hits (<c>MySlimBlock.DoDamage</c>: a bullet's damage times the block's
    /// <c>GeneralDamageMultiplier</c>, taken out of its integrity): a fixed gatling gun fires the same time at a wall of
    /// light armor, then at one of heavy armor. The damage a hit does is in the ratio of the blocks' multipliers (the
    /// game's damage system sees it); the heavy wall, with more integrity, loses fewer blocks and a smaller part of it.
    /// </summary>
    public sealed class ArmorDamageCheck : RegressionCheck
    {
        public override string Name => Prefix + "armor_damage";
        protected override bool NeedsDamage => true;

        private static readonly Dictionary<long, double> Dealt = new Dictionary<long, double>();
        private static readonly Dictionary<long, int> Hits = new Dictionary<long, int>();
        private static bool _registered;

        protected override IEnumerator Check()
        {
            HoldHighQuality();
            var gunDefinition = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyWeaponBlockDefinition>()
                .Where(d => d.Public && d.CubeSize == MyCubeSize.Small && d.Id.TypeId.ToString().EndsWith("SmallGatlingGun")).OrderBy(d => d.Id.SubtypeName).FirstOrDefault();
            SkipUnless(gunDefinition != null, "no small gatling gun in this game");
            if (!_registered)
            {
                _registered = true;
                MyDamageSystem.Static.RegisterBeforeDamageHandler(0, (object target, ref VRage.Game.ModAPI.MyDamageInformation info) =>
                {
                    if (info.Type == MyDamageType.Bullet && target is MySlimBlock block && block.CubeGrid != null)
                        lock (Dealt)
                            if (Dealt.ContainsKey(block.CubeGrid.EntityId))
                            {
                                Dealt[block.CubeGrid.EntityId] += info.Amount;
                                Hits[block.CubeGrid.EntityId]++;
                            }
                });
            }
            yield return Arrive(Site.At(6, 0, -3));
            var gunId = gunDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + gunDefinition.Id.SubtypeName;
            var station = Build("armor-gun", Site.Origin, true, new[]
            {
                new Part(gunId, 0, 0, 0),
                new Part("BatteryBlock/SmallBlockBatteryBlock", -3, 0, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 3f),
            }, MyCubeSize.Small);
            var gun = station.GetCubeBlock(Vector3I.Zero)?.FatBlock;
            Check(gun != null, gunId + " was not built");
            var magazine = MyDefinitionManager.Static.GetWeaponDefinition(gunDefinition.WeaponDefinitionId).AmmoMagazinesId[0];
            var box = (Sandbox.Game.MyInventory)gun.GetInventory(0);
            box.AddItems((VRage.MyFixedPoint)Math.Min(10, Math.Floor((double)box.ComputeAmountThatFits(magazine))),
                (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine));
            Switch(gun, true);

            var taken = new Dictionary<string, double>();
            var hits = new Dictionary<string, int>();
            var part = new Dictionary<string, double>();
            var lost = new Dictionary<string, int>();
            const string light = "CubeBlock/LargeBlockArmorBlock", heavy = "CubeBlock/LargeHeavyBlockArmorBlock";
            foreach (var armor in new[] { light, heavy })
            {
                var wall = BuildFor(WorldApi.TestIdentityId(), "armor-wall", Site.Origin + Site.Forward * 60, Bodies.Wall(armor, 2, 1).ToArray());
                var blocks = wall.BlocksCount;
                var whole = wall.GetBlocks().Sum(b => (double)b.Integrity);
                lock (Dealt) { Dealt[wall.EntityId] = 0; Hits[wall.EntityId] = 0; }
                yield return Seconds(1);
                ((IMyTerminalBlock)gun).GetActionWithName("Shoot_On")?.Apply(gun);
                yield return WaitForTicks(Frames(4));
                ((IMyTerminalBlock)gun).GetActionWithName("Shoot_Off")?.Apply(gun);
                yield return Seconds(2);
                lock (Dealt) { taken[armor] = Dealt[wall.EntityId]; hits[armor] = Hits[wall.EntityId]; Dealt.Remove(wall.EntityId); Hits.Remove(wall.EntityId); }
                var left = wall.MarkedForClose ? 0 : wall.GetBlocks().Sum(b => (double)b.Integrity);
                part[armor] = (whole - left) / whole;
                lost[armor] = blocks - (wall.MarkedForClose ? 0 : wall.BlocksCount);
                yield return CloseAndWait(wall);
            }
            var expected = Definition(heavy).GeneralDamageMultiplier / Definition(light).GeneralDamageMultiplier;
            string Say(string armor) => F(taken[armor]) + " damage in " + hits[armor] + " hits, " + (part[armor] * 100).ToString("F1") + " % of the integrity, " + lost[armor] + " blocks destroyed";
            Check(taken[light] > 0 && taken[heavy] > 0, "4 s of fire did not hurt both walls: light " + Say(light) + "; heavy " + Say(heavy) + " (" + State(gun) + ")");
            // per hit: through a hole a light wall lost, bullets fly on and hit nothing
            var ratio = (taken[heavy] / hits[heavy]) / (taken[light] / hits[light]);
            Check(Math.Abs(ratio - expected) <= expected * 0.1, "a hit on the heavy wall did x" + ratio.ToString("F2") + " a hit on the light one's damage, the blocks' multipliers make it x" +
                                                                 expected.ToString("F2") + ": light " + Say(light) + "; heavy " + Say(heavy));
            Check(part[heavy] < part[light] && lost[heavy] <= lost[light], "the heavy wall did not hold better: light " + Say(light) + "; heavy " + Say(heavy));
            Result("4 s of " + gunDefinition.DisplayNameText + " fire: light armor " + Say(light) + "; heavy " + Say(heavy) + " (damage a hit x" + ratio.ToString("F2") +
                   ", the multipliers say x" + expected.ToString("F2") + ")");
        }
    }

    /// <summary>
    /// A mechanism destroyed (<c>MyMechanicalConnectionBlockBase</c> losing its head or its base): a rotor's head
    /// destroyed, the rotor is detached and the block on the head stays, a grid of its own; a rotor destroyed, its head
    /// grid goes on with its block.
    /// </summary>
    public sealed class SubgridDamageCheck : RegressionCheck
    {
        public override string Name => Prefix + "subgrid_damage";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            var results = new List<string>();
            var seen = new HashSet<long>();
            foreach (var destroyHead in new[] { true, false })
            {
                // 60 m apart: the first rig's freed head drifts, and in the second's way it kept the second's head from being made
                var grid = Build("subgrid-damage", Site.At(destroyHead ? -30 : 30, 0, 0), true,
                    new Part("MotorStator/LargeStator", 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
                var stator = Block<MyMotorStator>(grid);
                yield return AddHead(stator);
                ((IMyTerminalBlock)stator).SetValueFloat("Velocity", 0f);
                var head = stator.TopGrid;
                var top = stator.TopBlock;
                var away = Wave31.Local(head, top.PositionComp.GetPosition() - stator.PositionComp.GetPosition());
                // a block of the player's: a lone armor block is nobody's, and the world's trash removal takes such a grid
                var tag = WorldApi.EntityPrefix + "subgrid-damage-" + (destroyHead ? "head" : "rotor");
                var armor = Wave31.Add(head, "CargoContainer/LargeBlockSmallContainer", top.Position + Base6Directions.GetIntVector(away), Base6Directions.GetPerpendicular(away), away, Identity,
                    b => ((MyObjectBuilder_TerminalBlock)b).CustomName = tag);
                Check(armor != null, "the container was not added to the head");
                seen.Add(grid.EntityId);
                var armorAt = head.GridIntegerToWorld(armor.Position);
                // who takes the head away when it does go: the first frames of the call that closed it, and its speed then
                string closedBy = null;
                head.OnMarkForClose += e =>
                {
                    if (closedBy == null)
                        closedBy = "at " + F(e.Physics?.LinearVelocity.Length() ?? 0) + " m/s by " +
                                   string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Skip(1).Take(8).Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name));
                };
                var victim = destroyHead ? top.SlimBlock : stator.SlimBlock;
                victim.DoDamage(victim.MaxIntegrity * 1.5f, MyDamageType.Bullet, true, null, 0);
                yield return Until(() => victim.IsDestroyed || victim.FatBlock == null || victim.FatBlock.MarkedForClose ? 1 : 0, v => v > 0,
                    (destroyHead ? "the rotor's head" : "the rotor") + " destroyed", 10);
                yield return Seconds(1);
                if (destroyHead)
                {
                    Check(!stator.MarkedForClose && !((Sandbox.ModAPI.Ingame.IMyMotorStator)stator).IsAttached, "the rotor is still attached with its head destroyed");
                    Check(stator.TopGrid == null, "the rotor still names a head grid");
                }
                // the head's block wherever the game keeps it now: a grid that loses a block may be built anew, and a head let go drifts
                // the container by its name, on whatever grid of the world it is now
                var kept = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose)
                    .SelectMany(g => g.GetFatBlocks()).OfType<MyCargoContainer>().FirstOrDefault(c => !c.MarkedForClose && c.CustomName.ToString() == tag);
                var near = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g != grid && Vector3D.Distance(g.PositionComp.GetPosition(), armorAt) < 40).ToList();
                if (!destroyHead)
                {
                    // the rotor destroyed: what is checked is the head going on as a grid of its own (under the suite's load the
                    // freed head has knocked its own block off against what was left of the base)
                    // the head block itself first: a freed head drifts, under the suite's load further than the 40 m looked round
                    var headLeft = top.MarkedForClose || top.CubeGrid == null || top.CubeGrid.MarkedForClose ? near.FirstOrDefault(g => g.GetBlocks().Any(b => b.BlockDefinition.Id == top.BlockDefinition.Id)) : top.CubeGrid;
                    Check(headLeft != null, "the rotor's head is gone with the rotor (the head " + (top.Closed ? "closed" : top.MarkedForClose ? "closing" : "here") +
                                            ", its grid closed " + (closedBy ?? "by nothing seen") +
                                            ", the container " + (kept == null ? "gone" : F(Vector3D.Distance(kept.PositionComp.GetPosition(), armorAt)) + " m off") +
                                            "; grids near: " + string.Join(", ", near.Select(g => g.DisplayName + " " + g.BlocksCount)) + ")");
                    Check(headLeft != grid, "the rotor's head is on the base grid");
                    Track(headLeft);
                    results.Add("rotor destroyed, the head left on a grid of " + headLeft.BlocksCount + (kept != null ? " with its container" : ""));
                    continue;
                }
                Check(kept != null, "the block on the head is gone with the " + (destroyHead ? "head" : "rotor") + " (the container " +
                                    (armor.FatBlock == null ? "has no block" : armor.FatBlock.Closed ? "closed" : armor.FatBlock.MarkedForClose ? "closing" : "here") +
                                    ", its grid " + (armor.CubeGrid == null ? "none" : armor.CubeGrid.MarkedForClose ? "closed" : armor.CubeGrid.BlocksCount + " blocks") +
                                    "; grids near: " + string.Join(", ", near.Select(g => g.DisplayName + " " + g.BlocksCount)) + ")");
                var left = kept.CubeGrid;
                Check(left != grid, "the block on the head is on the base grid");
                Track(left);
                seen.Add(left.EntityId);
                results.Add((destroyHead ? "head destroyed: rotor detached" : "rotor destroyed") + ", the head's block left on a grid of " + left.BlocksCount);
            }
            Result(string.Join("; ", results));
        }
    }

    /// <summary>
    /// Magnetic boots (<c>MyCharacter.IsMagneticBootsActive</c>): in space, out of gravity, a player on a station's floor
    /// with the jetpack off sticks to it and walks on it; with the jetpack on the boots let go.
    /// </summary>
    public sealed class MagneticBootsCheck : RegressionCheck
    {
        public override string Name => Prefix + "magnetic_boots";

        protected override IEnumerator Check()
        {
            var pad = Pad("magboots-pad", Site.Origin, 5);
            var floor = pad.PositionComp.GetPosition() + Site.Up * 1.25;
            yield return Arrive(floor + Site.Up * 1.5 - Site.Forward * 5);
            FakeClients.SetFree(Me, true);
            var body = Body;
            Check(body.Physics.Gravity.LengthSquared() < 0.001f, "there is gravity here: " + F(body.Physics.Gravity.Length()) + " m/s2");
            // on the jetpack down onto the floor, as a player lands on a station, then the jetpack off
            yield return Hold(2, new Vector3(0, -1, 0), jetpack: true, dampeners: true, face: Site.Forward);
            yield return Hold(2, Vector3.Zero, jetpack: false, face: Site.Forward);
            yield return Until(() => body.IsMagneticBootsActive ? 1 : 0, v => v > 0, "the magnetic boots on the floor", 5,
                () => "enabled " + body.IsMagneticBootsEnabled + ", " + body.GetCurrentMovementState() + ", " + F(Vector3D.Dot(body.PositionComp.GetPosition() - floor, Site.Up)) + " m over the floor");
            var from = body.PositionComp.GetPosition();
            yield return Hold(3, new Vector3(0, 0, -1), jetpack: false, face: Site.Forward);
            var gone = Vector3D.Dot(body.PositionComp.GetPosition() - from, Site.Forward);
            var height = Vector3D.Dot(body.PositionComp.GetPosition() - floor, Site.Up);
            Check(gone > 2, "3 s of walking on the boots took the player " + F(gone) + " m (" + body.GetCurrentMovementState() + ")");
            Check(Math.Abs(height) < 0.6, "the player left the floor walking on the boots: " + F(height) + " m over it");
            yield return Hold(1, Vector3.Zero, jetpack: true, face: Site.Forward);
            Check(!body.IsMagneticBootsActive, "the boots held with the jetpack on");
            Result("in space the boots held on the floor; walked " + F(gone) + " m on them, " + F(height) + " m over the floor; off with the jetpack");
        }
    }

    /// <summary>
    /// The voxel revert of the trash removal (<c>MySessionComponentTrash.VoxelReversion</c>): a hole cut in a planet's
    /// ground, older than the age the settings give - the game puts the ground back. The
    /// revertor is pointed at the hole's chunks alone (its list of voxel maps and chunks), the settings set for that
    /// (age 0, no distance to players and grids, boulders not reverted), so nothing else of the world is reverted; then all is put back.
    /// </summary>
    public sealed class VoxelRevertCheck : RegressionCheck
    {
        public override string Name => Prefix + "voxel_revert";
        public override SiteKind Kind => SiteKind.Planet;

        private readonly SessionSettingsOverride _settings = new SessionSettingsOverride();
        private MySessionComponentTrash _trash;
        private Vector3D _hole;

        private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private void SetField(string name, object value)
        {
            var field = typeof(MySessionComponentTrash).GetField(name, Any) ?? throw new Core.ScenarioFailedException("MySessionComponentTrash." + name + " is not in this game");
            field.SetValue(_trash, value);
        }

        private object Get(string name) => typeof(MySessionComponentTrash).GetField(name, Any)?.GetValue(_trash);

        protected override IEnumerator Check()
        {
            var planet = Site.Planet;
            SkipUnless(planet?.Storage is MyStorageBase, "no planet here");
            _trash = MySession.Static.GetComponent<MySessionComponentTrash>();
            Check(_trash != null, "no trash removal component in this session");
            var ground = Site.Ground(Site.Origin + Site.Right * 30);
            _hole = ground - Site.Up * 1;
            yield return Arrive(ground + Site.Up * 3 - Site.Forward * 10);
            var whole = Wave31.Rock(planet, _hole, 3);
            var sphere = MyAPIGateway.Session.VoxelMaps.GetSphereVoxelHand();
            sphere.Center = _hole;
            sphere.Radius = 3;
            MyAPIGateway.Session.VoxelMaps.CutOutShape(planet, sphere);
            yield return Seconds(1);
            var cut = Wave31.Rock(planet, _hole, 3);
            Check(cut < whole * 0.9, "the cut took nothing: " + whole + " -> " + cut);

            // the storage's chunks the cut touched, as the revertor goes over them
            var storage = (MyStorageBase)planet.Storage;
            var box = new BoundingBoxD(_hole - new Vector3D(4), _hole + new Vector3D(4));
            var chunks = new List<KeyValuePair<Vector3I, VRage.Library.Utils.MyTimeSpan>>();
            using (var all = storage.AccessEnumerator)
                while (all.MoveNext())
                {
                    var coord = all.Current.Key;
                    storage.ConvertAccessCoordinates(ref coord, out var bb);
                    bb.Translate(planet.PositionLeftBottomCorner);
                    if (bb.Intersects(box)) chunks.Add(all.Current);
                }
            Check(chunks.Count > 0, "the cut left no changed chunk in the planet's storage to revert");

            // the revertor pointed at the hole's chunks of this planet first, then switched on, in one frame: on with its own
            // list it would go over every changed voxel of the world with these settings
            SetField("m_voxelTrash_StartFromBegining", false);
            SetField("m_voxel_BaseIds", new List<long> { planet.EntityId });
            SetField("m_voxel_BaseCurrentIndex", 0);
            SetField("m_voxel_CurrentBase", planet);
            SetField("m_voxel_CurrentStorage", storage);
            SetField("m_voxel_CurrentAccessEnumerator", (IEnumerator<KeyValuePair<Vector3I, VRage.Library.Utils.MyTimeSpan>>)chunks.GetEnumerator());
            SetField("m_voxel_CurrentChunk", null);
            SetField("m_voxel_Timer", -1);
            _settings.Set("VoxelTrashRemovalEnabled", true);
            _settings.Set("VoxelAgeThreshold", 0);
            // nothing near counts: the revertor goes over the hole's chunks alone, and a chunk is tens of metres across
            _settings.Set("VoxelPlayerDistanceThreshold", 0f);
            _settings.Set("VoxelGridDistanceThreshold", 0f);
            _settings.Set("TrashFlags", MySession.Static.Settings.TrashFlags & ~MyTrashRemovalFlags.RevertBoulders);
            yield return Until(() => Wave31.Rock(planet, _hole, 3), v => v >= whole * 0.98, "the ground of the hole put back by the trash removal", Math.Max(20, chunks.Count * 2),
                () => chunks.Count + " chunks; rock " + whole + " whole, " + cut + " cut; revertor: timer " + Get("m_voxel_Timer") + ", base " + (Get("m_voxel_CurrentBase") as MyPlanet)?.StorageName +
                      ", chunk " + Get("m_voxel_CurrentChunk") + ", from the start " + Get("m_voxelTrash_StartFromBegining") + ", enabled " + MySession.Static.Settings.VoxelTrashRemovalEnabled);
            var back = Wave31.Rock(planet, _hole, 3);
            _settings.Restore();
            SetField("m_voxelTrash_StartFromBegining", true);
            Result("a 3 m hole (" + whole + " -> " + cut + " of rock) put back by the trash removal: " + back + " (" + chunks.Count + " chunks)");
        }

        public override void Cleanup()
        {
            try
            {
                _settings.Restore();
                if (_trash != null) SetField("m_voxelTrash_StartFromBegining", true);
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            if (Site?.Planet != null && _hole != Vector3D.Zero) WorldApi.RevertTerrain(Site.Planet, _hole, 6);
        }
    }

    /// <summary>
    /// Old identities removed (<c>MySessionComponentTrash.CheckIdentity</c>): two players who left 100 hours ago, with
    /// "remove inactive identities after" 1 hour and "remove characters after" 60 minutes. The one who built nothing
    /// loses the identity and the character left standing; the one with a block keeps the identity, the character goes.
    /// </summary>
    public sealed class OldIdentitiesCheck : RegressionCheck
    {
        public override string Name => Prefix + "old_identities";

        private readonly SessionSettingsOverride _settings = new SessionSettingsOverride();

        protected override IEnumerator Check()
        {
            var trash = MySession.Static.GetComponent<MySessionComponentTrash>();
            Check(trash != null, "no trash removal component in this session");
            var checkIdentity = typeof(MySessionComponentTrash).GetMethod("CheckIdentity", BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? throw new Core.ScenarioFailedException("MySessionComponentTrash.CheckIdentity is not in this game");
            var list = typeof(MySessionComponentTrash).GetField("m_allIdentities", BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new Core.ScenarioFailedException("MySessionComponentTrash.m_allIdentities is not in this game");
            yield return Arrive(Site.At(0, 0, -3));
            int idle = -1, builder = -1;
            yield return Join(Site.At(-3, 0, 3), i => idle = i);
            yield return Join(Site.At(3, 0, 3), i => builder = i);
            var idleIdentity = FakeClients.PlayerOf(idle).Identity;
            var builderIdentity = FakeClients.PlayerOf(builder).Identity;
            Build("old-identity-block", Site.At(3, 0, 10), true, new[] { new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0) }, ownedBy: builderIdentity.IdentityId);
            Check(builderIdentity.BlockLimits.BlocksBuilt > 0, "the builder's identity counts no block built");
            Check(idleIdentity.BlockLimits.BlocksBuilt == 0, "the idle player's identity counts " + idleIdentity.BlockLimits.BlocksBuilt + " blocks built");
            var idleBody = FakeClients.Character(idle);
            var builderBody = FakeClients.Character(builder);
            FakeClients.Quit(idle);
            FakeClients.Quit(builder);
            yield return Seconds(1);
            Check(!idleBody.MarkedForClose && !builderBody.MarkedForClose, "a character did not stay in the world when its player left");
            idleIdentity.LastLogoutTime = DateTime.Now - TimeSpan.FromHours(100);
            builderIdentity.LastLogoutTime = DateTime.Now - TimeSpan.FromHours(100);
            _settings.Set("RemoveOldIdentitiesH", 1);
            _settings.Set("PlayerCharacterRemovalThreshold", 60);

            void Look(MyIdentity identity)
            {
                var identities = (List<MyIdentity>)list.GetValue(trash);
                var index = identities.IndexOf(identity);
                if (index < 0)
                {
                    list.SetValue(trash, MySession.Static.Players.GetAllIdentities().ToList());
                    index = ((List<MyIdentity>)list.GetValue(trash)).IndexOf(identity);
                }
                Check(index >= 0, identity.DisplayName + " is not among the identities the trash removal looks at");
                checkIdentity.Invoke(trash, new object[] { index });
            }
            // the settings only for these two calls: the trash removal's own round over every identity runs on them too
            try
            {
                Look(idleIdentity);
                Look(builderIdentity);
            }
            finally { _settings.Restore(); }
            yield return Seconds(1);
            Check(MySession.Static.Players.TryGetIdentity(idleIdentity.IdentityId) == null, "the identity of the player who left 100 h ago and built nothing is still there");
            Check(idleBody.MarkedForClose, "the character of the removed identity is still in the world");
            Check(MySession.Static.Players.TryGetIdentity(builderIdentity.IdentityId) != null, "the identity of the player with a block was removed");
            Check(builderBody.MarkedForClose, "the character of the player who left 100 h ago is still in the world (characters go after 60 min)");
            _settings.Restore();
            Result("left 100 h ago: the identity with nothing built removed with its character, the one with a block kept, its character removed");
        }

        public override void Cleanup()
        {
            try { _settings.Restore(); }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// The AFK kick (<c>MySessionComponentTrash.AFKKickRequest_Server</c>): the client counts the player's idle time
    /// itself and, past the world's AFK timeout, asks the server to kick it; the server kicks the client that asks (and
    /// only it), and keeps its character in the world as a quit player's.
    /// </summary>
    public sealed class AfkKickCheck : RegressionCheck
    {
        public override string Name => Prefix + "afk_kick";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var other = -1;
            yield return Join(Site.At(3, 0, -3), i => other = i);
            var steamId = Player.Id.SteamId;
            var body = Body;
            Request(typeof(MySessionComponentTrash), "AFKKickRequest_Server");
            yield return Until(() => Sandbox.Game.Multiplayer.Sync.Clients.HasClient(steamId) ? 0 : 1, v => v > 0, "the player who asked kicked", 10);
            FakeClients.Forget(Me);
            Check(MySession.Static.Players.GetOnlinePlayers().All(p => p.Id.SteamId != steamId), "the kicked player is still online");
            Check(FakeClients.PlayerOf(other) != null && Sandbox.Game.Multiplayer.Sync.Clients.HasClient(FakeClients.PlayerOf(other).Id.SteamId), "the other player was kicked too");
            Check(!body.MarkedForClose, "the kicked player's character left the world with them");
            Result("the client's AFK request kicked that client alone; its character stayed");
        }
    }

    /// <summary>
    /// Block groups (<c>MyCubeGrid.OnModifyGroupSuccess</c>, what the terminal sends): a group of two of three batteries is
    /// made, saved with the grid, switched off from the group (the terminal applies a group's action to each of its
    /// blocks) - the third battery stays on; a block of the group taken off, the group keeps the other; sent empty, it is gone.
    /// </summary>
    public sealed class BlockGroupsCheck : RegressionCheck
    {
        public override string Name => Prefix + "block_groups";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -6));
            var grid = Build("block-groups", Site.Origin, true,
                // whole cubes on an armor block, each held by it alone: one taken off leaves the rest as they are (a light
                // has no mount on a battery's side, and the game drops blocks that hang on nothing)
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", -1, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)));
            var batteries = Blocks<MyBatteryBlock>(grid).OrderBy(l => l.Position.Z).ThenByDescending(l => l.Position.X).ToList();
            Check(batteries.Count == 3, "the grid has " + batteries.Count + " batteries");
            const string name = "reg-batteries";
            Request(grid, "OnModifyGroupSuccess", name, new List<long> { batteries[0].EntityId, batteries[1].EntityId });
            var system = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            int? Group()
            {
                var group = system.GetBlockGroupWithName(name);
                if (group == null) return null;
                var blocks = new List<Sandbox.ModAPI.Ingame.IMyTerminalBlock>();
                group.GetBlocks(blocks);
                return blocks.Count;
            }
            Check(Group() == 2, "the group was not made of the two batteries (" + (Group()?.ToString() ?? "no group") + ")");
            var saved = ((MyObjectBuilder_CubeGrid)grid.GetObjectBuilder()).BlockGroups?.FirstOrDefault(g => g.Name == name);
            Check(saved != null && saved.Blocks.Count == 2, "the grid's save has no group of the two batteries");

            var members = new List<Sandbox.ModAPI.Ingame.IMyTerminalBlock>();
            system.GetBlockGroupWithName(name).GetBlocks(members);
            foreach (var block in members) Sandbox.ModAPI.Ingame.TerminalBlockExtentions.ApplyAction(block, "OnOff_Off");
            yield return WaitForTicks(2);
            Check(!batteries[0].Enabled && !batteries[1].Enabled, "the group's batteries are not off after its action");
            Check(batteries[2].Enabled, "the battery out of the group was switched off with it");

            var gridGroupsField = typeof(MyCubeGrid).GetField("BlockGroups", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            string GridGroups() => string.Join(", ", ((IEnumerable)gridGroupsField.GetValue(grid)).Cast<MyBlockGroup>().Select(g => g.Name + " " +
                (((typeof(MyBlockGroup).GetField("Blocks", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(g)
                  ?? typeof(MyBlockGroup).GetProperty("Blocks", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(g)) as IEnumerable)?.Cast<object>().Count())));
            var before = GridGroups();
            var at = batteries[0].PositionComp.GetPosition();
            grid.RazeBlock(batteries[1].Position);
            yield return WaitForTicks(5);
            // the battery left of the group, wherever the game keeps it now (a grid changed may be built anew)
            var kept = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && Vector3D.Distance(g.PositionComp.GetPosition(), at) < 20)
                .SelectMany(g => g.GetFatBlocks()).OfType<MyBatteryBlock>().FirstOrDefault(l => Vector3D.Distance(l.PositionComp.GetPosition(), at) < 0.5);
            Check(kept != null, "the battery left of the group is gone with the other (the grid " + (grid.MarkedForClose ? "closed" : "kept") + ", " +
                                grid.BlocksCount + " blocks; before: " + before + ")");
            if (kept.CubeGrid != grid) Track(kept.CubeGrid);
            var keptSystem = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(kept.CubeGrid);
            var keptGroup = keptSystem.GetBlockGroupWithName(name);
            var keptBlocks = new List<Sandbox.ModAPI.Ingame.IMyTerminalBlock>();
            keptGroup?.GetBlocks(keptBlocks);
            Check(keptBlocks.Count == 1 && keptBlocks[0] == kept, "with one of its blocks gone the group has " + (keptGroup == null ? "no" : keptBlocks.Count.ToString()) +
                                                                 " blocks (the grid " + (grid.MarkedForClose ? "built anew" : "kept") + "; before: " + before + ")");
            Request(kept.CubeGrid, "OnModifyGroupSuccess", name, new List<long>());
            Check(keptSystem.GetBlockGroupWithName(name) == null, "the group sent empty is still there");
            Result("a group of 2 of 3 batteries made, saved, switched off as one (the third on), kept its last block, removed when sent empty");
        }
    }

    /// <summary>
    /// The suit's lamp (<c>MyCharacter.EnableLightsCallback</c>, the client's L): on, the suit asks more power for it and
    /// the save has it on; off, both go back.
    /// </summary>
    public sealed class CharacterLightCheck : RegressionCheck
    {
        public override string Name => Prefix + "character_light";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var body = Body;
            if (body.LightEnabled) Request(body, "EnableLightsCallback", false);
            var sink = body.SinkComp;
            Check(sink != null, "the character has no power sink");
            var power = MyResourceDistributorComponent.ElectricityId;
            var dark = sink.RequiredInputByType(power);
            Request(body, "EnableLightsCallback", true);
            yield return Until(() => body.LightEnabled ? 1 : 0, v => v > 0, "the lamp on", 5);
            var lit = sink.RequiredInputByType(power);
            Check(lit > dark, "the lamp on takes no more power: " + dark + " -> " + lit + " MW");
            Check(((MyObjectBuilder_Character)body.GetObjectBuilder()).LightEnabled, "the save has the lamp off");
            Request(body, "EnableLightsCallback", false);
            yield return Until(() => body.LightEnabled ? 0 : 1, v => v > 0, "the lamp off", 5);
            Check(sink.RequiredInputByType(power) < lit, "the lamp off still takes its power");
            Result("the lamp on asked " + ((lit - dark) * 1e6).ToString("F1") + " W more of the suit and was saved on; off, back");
        }
    }

    /// <summary>
    /// Simple blocks the server keeps (interior light, spotlight, sound block, ore detector): on, each takes power; off,
    /// none; the setting of each (a light's radius, the sound block's volume, the detector's range) is kept in its save.
    /// </summary>
    public sealed class SimpleBlocksCheck : RegressionCheck
    {
        public override string Name => Prefix + "simple_blocks";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -6));
            var grid = Build("simple-blocks", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 1, 0, 0),
                new Part("ReflectorLight/LargeBlockFrontLight", 2, 0, 0),
                new Part("SoundBlock/LargeBlockSoundBlock", 3, 0, 0),
                new Part("OreDetector/LargeOreDetector", -1, 0, 0));
            // the sound block's slider is in per cent, saved as a part of one
            var cases = new (Type Type, string Setting, float Value, string Saved, float Scale)[]
            {
                (typeof(SpaceEngineers.Game.Entities.Blocks.MyInteriorLight), "Radius", 4f, "Radius", 1f),
                (typeof(MyReflectorLight), "Radius", 50f, "ReflectorRadius", 1f),
                (typeof(SpaceEngineers.Game.Entities.Blocks.MySoundBlock), "VolumeSlider", 40f, "Volume", 0.01f),
                (typeof(MyOreDetector), "Range", 80f, "DetectionRadius", 1f),
            };
            var power = MyResourceDistributorComponent.ElectricityId;
            var done = new List<string>();
            foreach (var c in cases)
            {
                var block = grid.GetFatBlocks().FirstOrDefault(b => c.Type.IsInstanceOfType(b)) as MyFunctionalBlock;
                Check(block != null, "no " + c.Type.Name + " on the grid");
                var sink = block.ResourceSink ?? block.Components.Get<MyResourceSinkComponent>();
                Switch(block, true);
                ((IMyTerminalBlock)block).SetValueFloat(c.Setting, c.Value);
                yield return Until(() => sink?.CurrentInputByType(power) ?? 0, v => v > 0, c.Type.Name + " on taking power", 10, () => State(block));
                var on = sink.CurrentInputByType(power);
                Check(Math.Abs(((IMyTerminalBlock)block).GetValueFloat(c.Setting) - c.Value) < c.Value * 0.01f,
                    c.Type.Name + "'s " + c.Setting + " is " + ((IMyTerminalBlock)block).GetValueFloat(c.Setting) + ", set to " + c.Value);
                var copy = (MyObjectBuilder_CubeBlock)block.GetObjectBuilderCubeBlock(true);
                var keeps = SavedValue(copy, c.Saved);
                Check(keeps.HasValue && Math.Abs(keeps.Value - c.Value * c.Scale) < c.Value * c.Scale * 0.01f, c.Type.Name + "'s save has " + c.Saved + " " + (keeps?.ToString() ?? "nowhere") + ", set to " + c.Value);
                Switch(block, false);
                yield return Until(() => sink.CurrentInputByType(power), v => v <= 1e-7, c.Type.Name + " off taking no power", 10);
                done.Add(c.Type.Name.Replace("My", "") + " " + (on * 1000).ToString("F2") + " kW");
            }
            Result("on, each took power, off none; settings saved: " + string.Join(", ", done));
        }

        /// <summary>The saved value of a setting: the builder's field or property of that name.</summary>
        private static float? SavedValue(MyObjectBuilder_CubeBlock block, string setting)
        {
            foreach (var name in new[] { setting })
            {
                var field = block.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (field != null) return Convert.ToSingle(field.GetValue(block));
                var property = block.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null) return Convert.ToSingle(property.GetValue(block));
            }
            return null;
        }
    }
}
