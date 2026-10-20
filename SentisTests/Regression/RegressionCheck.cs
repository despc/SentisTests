using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using Sandbox.ModAPI.Interfaces;
using VRageMath;

namespace SentisTests.Regression
{
    /// <summary>One block of a regression check's grid: what, where (its minimum cell), which way it faces.</summary>
    public sealed class Part
    {
        /// <summary>"Type/Subtype" of the block, without MyObjectBuilder_ ("Reactor/LargeBlockSmallGenerator", "OxygenGenerator/").</summary>
        public string Id;
        public Vector3I Min;
        public Base6Directions.Direction Forward = Base6Directions.Direction.Forward;
        public Base6Directions.Direction Up = Base6Directions.Direction.Up;
        /// <summary>Built to this share of its integrity: 1 finished, 0 a frame of its first component.</summary>
        public float Built = 1f;
        /// <summary>Anything else of the block's builder: a name, a setting.</summary>
        public Action<MyObjectBuilder_CubeBlock> Setup;

        public Part(string id, int x, int y, int z)
        {
            Id = id;
            Min = new Vector3I(x, y, z);
        }

        public Part Facing(Base6Directions.Direction forward, Base6Directions.Direction up = Base6Directions.Direction.Up)
        {
            Forward = forward;
            Up = up;
            return this;
        }

        public Part At(float built)
        {
            Built = built;
            return this;
        }

        public Part With(Action<MyObjectBuilder_CubeBlock> setup)
        {
            Setup = setup;
            return this;
        }

        public Part Named(string name) => With(b => { if (b is MyObjectBuilder_TerminalBlock t) t.CustomName = name; });
    }

    /// <summary>
    /// A regression check: one mechanic of the game, the way a player meets it, checked end to end - the block placed,
    /// the reactor running, the ship flying. Each check builds at a <see cref="Site"/> of its own with a fake player of
    /// its own standing by (who keeps the freezer away and is the one who acts: places, welds, pilots, opens terminals),
    /// so the <see cref="RegressionSuite"/> runs them all at once, far apart. Alone, a check runs at site 0 of its kind.
    ///
    /// The checks go through the game's own paths: the request a client sends (run as if from the fake client), the
    /// tool the character holds, the block's own update. What a plugin patches on the way is checked with it: a
    /// regression of the game made by a plugin fails here.
    /// </summary>
    public abstract class RegressionCheck : TestScenario
    {
        public const string Prefix = "reg_";

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 40 };

        /// <summary>Space or planet.</summary>
        public virtual SiteKind Kind => SiteKind.Space;

        public override int TimeoutSeconds => 180;

        /// <summary>
        /// The check needs damage (grinding, guns, explosions): SentisGameplayImprovements blocks all damage for a while
        /// after the start, so it is let through while the check runs (the suite does it once for all).
        /// </summary>
        protected virtual bool NeedsDamage => false;

        private Scenarios.ConfigOverride _gameplay;

        /// <summary>Where this run builds; the suite sets it, alone it is site 0.</summary>
        public Site Site { get; set; }

        /// <summary>Set by the suite: the fake players are shared, a check does not remove them.</summary>
        public bool InSuite { get; set; }

        /// <summary>The fake client of this check (an index of <see cref="FakeClients"/>), -1 before <see cref="Arrive"/>.</summary>
        protected int Me = -1;

        protected MyPlayer Player => FakeClients.PlayerOf(Me);
        protected long Identity => Player.Identity.IdentityId;
        protected MyCharacter Body => FakeClients.Character(Me);

        /// <summary>What the check found worth saying at the end: the PASSED line carries it.</summary>
        protected void Result(string message) => Note(message);

        public sealed override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            if (Site == null) Site = Sites.Get(Kind, 0);
            if (!InSuite) FakeClients.RemoveAll();
            if (!InSuite && NeedsDamage && Integrations.Missing(new[] { Integrations.Gameplay }).Count == 0)
            {
                _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
                _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
            }
            Note(Site.ToString());
            yield return Check();
        }

        /// <summary>The check itself.</summary>
        protected abstract IEnumerator Check();

        // ------------------------------------------------------------------ the player

        /// <summary>
        /// The check's fake player joins with a character at the point (an astronaut held there; in gravity too), and owns
        /// what the check builds from then on.
        /// </summary>
        protected IEnumerator Arrive(Vector3D at)
        {
            var index = FakeClients.Count;
            FakeClients.Add(1, Network, i => (at, 0, 0), withCharacters: true);
            Me = index;
            yield return Wait(() => FakeClients.HasLiveCharacter(Me), "the fake player's character", 30);
            Track(Body);
            Body.PositionComp.SetWorldMatrix(MatrixD.CreateWorld(at, Site.Forward, Site.Up));
        }

        /// <summary>Moves the player's character (held there from then on), facing the point.</summary>
        protected void StandAt(Vector3D at, Vector3D lookAt)
        {
            FakeClients.MoveTo(Me, at);
            var forward = lookAt - at;
            if (forward.LengthSquared() < 1e-6) forward = Site.Forward;
            forward.Normalize();
            var up = Vector3D.Reject(Site.Up, forward);
            if (up.LengthSquared() < 1e-6) up = Vector3D.CalculatePerpendicularVector(forward);
            up.Normalize();
            Body.PositionComp.SetWorldMatrix(MatrixD.CreateWorld(at, forward, up));
            Body.SetHeadLocalXAngle(0);
        }

        private static readonly System.Reflection.FieldInfo LastClientState =
            typeof(MyCharacter).GetField("m_lastClientState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// The keys the player holds this frame, as the client's update carries them (<c>MyCharacter.DeserializeControls</c>):
        /// the move keys in the character's own axes (-Z forward, +X right, +Y up on the jetpack), the jetpack and its
        /// dampeners, which way the character faces. Held only while sent: send them every frame of the move. The
        /// character has to be let go first (<see cref="FakeClients.SetFree"/>).
        /// </summary>
        protected void Keys(Vector3 move, bool? jetpack = null, bool? dampeners = null, Vector3D? face = null, bool crouch = false)
        {
            var character = Body ?? throw new ScenarioFailedException("the player has no character");
            character.GetNetState(out var state);
            state.MoveIndicator = move;
            if (face.HasValue)
            {
                var up = Vector3D.Reject(Site.Up, Vector3D.Normalize(face.Value));
                if (up.LengthSquared() < 1e-6) up = Vector3D.CalculatePerpendicularVector(face.Value);
                state.Rotation = Quaternion.CreateFromRotationMatrix(MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(face.Value), Vector3D.Normalize(up)));
            }
            if (jetpack.HasValue) state.Jetpack = jetpack.Value;
            if (dampeners.HasValue) state.Dampeners = dampeners.Value;
            if (crouch) state.MovementFlags |= Sandbox.Game.Entities.Character.MyCharacterMovementFlags.Crouch;
            else state.MovementFlags &= ~Sandbox.Game.Entities.Character.MyCharacterMovementFlags.Crouch;
            state.Valid = true;
            if (LastClientState == null) throw new ScenarioFailedException("MyCharacter.m_lastClientState is not in this game");
            LastClientState.SetValue(character, state);
            character.SetNetState(ref state);
        }

        private static readonly System.Reflection.FieldInfo LastGridState =
            typeof(MyCubeGrid).GetField("m_lastNetState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// The keys the pilot holds this frame, as the client's update carries them for the grid it controls
        /// (<c>MyCubeGrid.DeserializeControls</c>): move in the seat's axes, mouse turn, roll. Send them every frame.
        /// </summary>
        protected static void ShipKeys(MyShipController seat, Vector3 move, Vector2 rotate = default(Vector2), float roll = 0)
        {
            var state = new Sandbox.Game.Replication.ClientStates.MyGridClientState { Valid = true, Move = move, Rotation = rotate, Roll = roll };
            if (LastGridState == null) throw new ScenarioFailedException("MyCubeGrid.m_lastNetState is not in this game");
            LastGridState.SetValue(seat.CubeGrid, state);
            seat.SetNetState(state);
        }

        /// <summary>Game frames in a span of game time: the simulation runs slower than the clock when the server is loaded.</summary>
        protected static int Frames(double seconds) => (int)Math.Round(seconds * 60);

        protected IEnumerator HoldShip(double seconds, MyShipController seat, Vector3 move, Vector2 rotate = default(Vector2), float roll = 0)
        {
            for (var i = Frames(seconds); i > 0; i--)
            {
                ShipKeys(seat, move, rotate, roll);
                yield return null;
            }
        }

        /// <summary>Holds the keys for a while, every frame.</summary>
        protected IEnumerator Hold(double seconds, Vector3 move, bool? jetpack = null, bool? dampeners = null, Vector3D? face = null)
        {
            for (var i = Frames(seconds); i > 0; i--)
            {
                Keys(move, jetpack, dampeners, face);
                yield return null;
            }
        }

        /// <summary>The player sits in the seat (as when they press F on it) and controls it.</summary>
        protected IEnumerator Sit(MyShipController seat)
        {
            FakeClients.SetFree(Me, true);
            var character = Body;
            StandAt(seat.PositionComp.GetPosition() + seat.WorldMatrix.Backward * 1.5, seat.PositionComp.GetPosition());
            FakeClients.SetFree(Me, true);
            Request(seat, "AttachPilotEvent", VRage.Game.Entity.UseObject.UseActionEnum.Manipulate, character.EntityId, -1);
            yield return Until(() => seat.Pilot == character ? 1 : 0, v => v > 0, "the player seated", 10,
                () => "pilot " + (((MyCockpit)seat).Pilot?.DisplayName ?? "none"));
            yield return Until(() => Player.Controller.ControlledEntity == seat ? 1 : 0, v => v > 0, "the player controlling the seat", 10);
        }

        /// <summary>Runs a request as if the check's fake client sent it.</summary>
        protected void AsMe(Action request) => FakeClients.AsClient(Me, request);

        /// <summary>
        /// A client's request arriving at the server: the target's event handler (a private [Event] method - what
        /// MyMultiplayer.RaiseEvent calls on the server) run in the context of the check's fake client. The network's
        /// own validation (access, ownership) is not run; what the handler and the plugins' patches on it do is.
        /// </summary>
        protected object Request(object target, string method, params object[] args)
        {
            System.Reflection.MethodInfo found = null;
            for (var type = target.GetType(); type != null && found == null; type = type.BaseType)
                found = type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == args.Length);
            if (found == null) throw new ScenarioFailedException(target.GetType().Name + "." + method + " with " + args.Length + " arguments is not in this game");
            object result = null;
            AsMe(() =>
            {
                try { result = found.Invoke(found.IsStatic ? null : target, args); }
                catch (System.Reflection.TargetInvocationException e) { throw e.InnerException ?? e; }
            });
            return result;
        }

        /// <summary><see cref="Request(object,string,object[])"/> for a static event handler of a type.</summary>
        protected object Request(Type type, string method, params object[] args)
        {
            var found = type.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == args.Length)
                        ?? throw new ScenarioFailedException(type.Name + "." + method + " with " + args.Length + " arguments is not in this game");
            object result = null;
            AsMe(() =>
            {
                try { result = found.Invoke(null, args); }
                catch (System.Reflection.TargetInvocationException e) { throw e.InnerException ?? e; }
            });
            return result;
        }

        // ------------------------------------------------------------------ grids

        public static MyCubeBlockDefinition Definition(string id)
        {
            var slash = id.IndexOf('/');
            var type = slash < 0 ? id : id.Substring(0, slash);
            var subtype = slash < 0 ? "" : id.Substring(slash + 1);
            if (!type.StartsWith("MyObjectBuilder_")) type = "MyObjectBuilder_" + type;
            if (!MyObjectBuilderType.TryParse(type, out var builderType) ||
                !MyDefinitionManager.Static.TryGetCubeBlockDefinition(new MyDefinitionId(builderType, subtype), out var definition))
                throw new ScenarioSkippedException("no block " + id + " in this game");
            return definition;
        }

        /// <summary>
        /// A grid of the parts at the point, its forward and up those of the site (or the ones given), every block the
        /// player's (shared with nobody), tracked. Spawned as a paste is: the game wires power, conveyors and the rest itself.
        /// </summary>
        protected MyCubeGrid Build(string name, Vector3D at, bool isStatic, IEnumerable<Part> parts, MyCubeSize size = MyCubeSize.Large,
            Vector3D? forward = null, Vector3D? up = null, long? ownedBy = null)
        {
            var owner = ownedBy ?? (Me >= 0 ? Identity : WorldApi.TestIdentityId());
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + name,
                DisplayName = WorldApi.EntityPrefix + name,
                GridSizeEnum = size,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(at, (Vector3)(forward ?? Site.Forward), (Vector3)(up ?? Site.Up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>(),
            };
            foreach (var part in parts)
            {
                var definition = Definition(part.Id);
                var block = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializer.CreateNewObject(definition.Id);
                block.Min = part.Min;
                block.BlockOrientation = new SerializableBlockOrientation(part.Forward, part.Up);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                if (part.Built < 1f)
                {
                    block.BuildPercent = part.Built;
                    block.IntegrityPercent = part.Built;
                }
                part.Setup?.Invoke(block);
                ob.CubeBlocks.Add(block);
            }
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            // a spawned grid comes out finished whatever the builder said (hand_weld): taken down to the share the way a
            // grinder takes it
            foreach (var part in parts.Where(p => p.Built < 1f))
            {
                var slim = grid.GetCubeBlock(part.Min);
                if (slim != null && slim.Integrity / slim.MaxIntegrity > part.Built + 0.01f)
                    slim.DecreaseMountLevelToDesiredRatio(part.Built, null);
            }
            return grid;
        }

        protected MyCubeGrid Build(string name, Vector3D at, bool isStatic, params Part[] parts) => Build(name, at, isStatic, (IEnumerable<Part>)parts);

        /// <summary>A static grid of another owner (an enemy station for a turret).</summary>
        protected MyCubeGrid BuildFor(long owner, string name, Vector3D at, params Part[] parts) => Build(name, at, true, parts, ownedBy: owner);

        /// <summary>
        /// A static tool rig: the ship tool (welder, grinder, drill) behind a reactor with uranium, facing along the
        /// direction, placed so its sensor sphere is centred at the point - whatever is there is in its reach. The sensor's
        /// place on the tool is read from a first rig built out of the way (it comes from the model), then the rig is built
        /// where it belongs. The tool is left switched off.
        /// </summary>
        protected MyCubeGrid ToolRig(string name, string toolId, Vector3D sensorAt, Vector3D direction, bool isStatic = true)
        {
            direction.Normalize();
            var up = Vector3D.CalculatePerpendicularVector(direction);
            // the reactor right behind the tool, whatever its length
            var behind = Definition(toolId).Size.Z;
            var parts = new[] { new Part(toolId, 0, 0, 0), new Part("Reactor/LargeBlockSmallGenerator", 0, 0, behind) };
            var probeAt = sensorAt - direction * 40 + up * 40;
            var probe = Build(name + "-probe", probeAt, true, parts, forward: direction, up: up);
            var tool = probe.GetCubeBlock(Vector3I.Zero).FatBlock;
            var offset = WorldApi.SensorSphere(tool).Center - probeAt;
            probe.Close();
            var rig = Build(name, sensorAt - offset, isStatic, parts, forward: direction, up: up);
            Put(Inventory(rig.GetCubeBlock(new Vector3I(0, 0, behind)).FatBlock), "Ingot/Uranium", 5);
            return rig;
        }

        /// <summary>The head of a rotor, a hinge, a piston, a wheel of a suspension, as a player adds it ("Add Head"), finished.</summary>
        protected IEnumerator AddHead(MyMechanicalConnectionBlockBase block)
        {
            yield return Seconds(0.5);
            if (block.TopGrid == null)
                Request(block, "DoRecreateTop", Identity, MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal, true);
            yield return Until(() => block.TopGrid != null && ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)block).IsAttached ? 1 : 0, v => v > 0,
                "the head on " + block.BlockDefinition.Id.SubtypeName, 10);
            Track(block.TopGrid);
        }

        /// <summary>The size of a block in grid cells, turned the way the part faces.</summary>
        protected static Vector3I Oriented(Vector3I size, Base6Directions.Direction forward, Base6Directions.Direction up) =>
            Vector3I.Abs(Vector3I.Round(Vector3.Transform((Vector3)size, Matrix.CreateWorld(Vector3.Zero, Base6Directions.GetVector(forward), Base6Directions.GetVector(up)))));

        /// <summary>The cells a part takes.</summary>
        protected static HashSet<Vector3I> Cells(Part part)
        {
            var size = Oriented(Definition(part.Id).Size, part.Forward, part.Up);
            var cells = new HashSet<Vector3I>();
            for (var x = 0; x < size.X; x++)
            for (var y = 0; y < size.Y; y++)
            for (var z = 0; z < size.Z; z++)
                cells.Add(part.Min + new Vector3I(x, y, z));
            return cells;
        }

        /// <summary>
        /// Where a block goes next to another for the two to be on one conveyor line: conveyor ports come from the models,
        /// so each place touching the first block (and each way the second can face) is built as a pair of its own out of
        /// the way, the conveyor system is given a few frames to join what it joins, and the first pair that connects is
        /// given back (<paramref name="found"/>).
        /// </summary>
        protected IEnumerator ConveyorNeighbour(Part first, string second, Func<Part, bool> allowed, Action<Part> found)
        {
            var taken = Cells(first);
            var size = Definition(second).Size;
            var faces = new[]
            {
                Base6Directions.Direction.Forward, Base6Directions.Direction.Backward, Base6Directions.Direction.Left,
                Base6Directions.Direction.Right, Base6Directions.Direction.Up, Base6Directions.Direction.Down,
            };
            var low = taken.Aggregate(Vector3I.Min);
            var high = taken.Aggregate(Vector3I.Max);
            var candidates = new List<Part>();
            foreach (var face in faces)
            {
                var up = Base6Directions.GetPerpendicular(face);
                var oriented = Oriented(size, face, up);
                for (var x = low.X - oriented.X; x <= high.X + 1; x++)
                for (var y = low.Y - oriented.Y; y <= high.Y + 1; y++)
                for (var z = low.Z - oriented.Z; z <= high.Z + 1; z++)
                {
                    var part = new Part(second, x, y, z).Facing(face, up);
                    var cells = Cells(part);
                    if (cells.Overlaps(taken)) continue;
                    if (!cells.Any(c => Base6Directions.IntDirections.Any(d => taken.Contains(c + d)))) continue;
                    if (allowed != null && !allowed(part)) continue;
                    candidates.Add(part);
                }
            }
            // the pairs in a row high over the site, 20 m apart
            var probes = new List<KeyValuePair<Part, MyCubeGrid>>();
            var row = Site.Up * 80 - Site.Forward * 80;
            for (var i = 0; i < candidates.Count; i++)
            {
                // a charged battery against the first block: a grid's conveyors join nothing without power
                var used = new HashSet<Vector3I>(taken.Concat(Cells(candidates[i])));
                var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).First(c => !used.Contains(c));
                var battery = new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z)
                    .With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f);
                try { probes.Add(new KeyValuePair<Part, MyCubeGrid>(candidates[i], Build("conveyor-probe", Site.Origin + row + Site.Right * (i * 20), true, new[] { first, candidates[i], battery }))); }
                catch (ScenarioFailedException) { }
            }
            yield return WaitForTicks(10);
            Part connected = null;
            foreach (var probe in probes)
            {
                var one = probe.Value.GetCubeBlock(first.Min)?.FatBlock as Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock;
                var two = probe.Value.GetCubeBlock(probe.Key.Min)?.FatBlock as Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock;
                if (connected == null && one != null && two != null && one != two &&
                    Sandbox.Game.GameSystems.MyGridConveyorSystem.Reachable(one.ConveyorEndpoint, two.ConveyorEndpoint))
                    connected = probe.Key;
                probe.Value.Close();
            }
            if (connected == null)
                throw new ScenarioFailedException("no place next to " + first.Id + " puts " + second + " on its conveyors (" + probes.Count + " tried)");
            found(connected);
        }

        /// <summary>Switches a block on or off as its terminal switch does.</summary>
        protected static void Switch(MyCubeBlock block, bool on) => ((Sandbox.ModAPI.IMyTerminalBlock)block).SetValueBool("OnOff", on);

        protected static T Block<T>(MyCubeGrid grid) where T : class =>
            grid.GetFatBlocks().OfType<T>().FirstOrDefault() ?? throw new ScenarioFailedException(grid.DisplayName + " has no " + typeof(T).Name);

        protected static List<T> Blocks<T>(MyCubeGrid grid) where T : class => grid.GetFatBlocks().OfType<T>().ToList();

        /// <summary>A static floor of armor under a planet check: its top at the highest ground under it plus a little.</summary>
        protected MyCubeGrid Pad(string name, Vector3D centre, int half = 4)
        {
            var parts = new List<Part>();
            for (var x = -half; x <= half; x++)
            for (var z = -half; z <= half; z++)
                parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, 0, z));
            var top = Highest(centre, half * 2.5 + 2);
            // the floor's cells are centred on the grid's origin: its top is 1.25 m above
            return Build(name, top - Site.Up * 0.25, true, parts);
        }

        /// <summary>The highest point of the ground in a square around the point, at its height (the point in space).</summary>
        protected Vector3D Highest(Vector3D centre, double half)
        {
            if (Site.Planet == null) return centre;
            var centreOfPlanet = Site.Planet.PositionComp.GetPosition();
            var best = 0.0;
            for (var x = -1; x <= 1; x++)
            for (var z = -1; z <= 1; z++)
            {
                var probe = centre + Site.Right * (x * half) + Site.Forward * (z * half);
                var ground = Site.Planet.GetClosestSurfacePointGlobal(ref probe);
                best = Math.Max(best, (ground - centreOfPlanet).Length());
            }
            var up = Vector3D.Normalize(centre - centreOfPlanet);
            return centreOfPlanet + up * best;
        }

        // ------------------------------------------------------------------ items

        /// <summary>"Ore/Iron", "Ingot/Uranium", "Component/SteelPlate", "PhysicalGunObject/WelderItem", "GasContainerObject/HydrogenBottle".</summary>
        public static MyDefinitionId Item(string id)
        {
            var slash = id.IndexOf('/');
            var type = id.Substring(0, slash);
            if (!type.StartsWith("MyObjectBuilder_")) type = "MyObjectBuilder_" + type;
            if (!MyDefinitionId.TryParse(type, id.Substring(slash + 1), out var definitionId))
                throw new ScenarioFailedException("no item " + id);
            return definitionId;
        }

        protected static MyInventory Inventory(MyEntity entity, int index = 0) =>
            entity.GetInventory(index) ?? throw new ScenarioFailedException((entity.DisplayName ?? entity.GetType().Name) + " has no inventory " + index);

        protected static void Put(MyInventory inventory, string item, double amount)
        {
            var content = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(Item(item));
            if (!inventory.AddItems((MyFixedPoint)amount, content))
                throw new ScenarioFailedException("cannot put " + amount + " " + item + " into " + (inventory.Owner?.DisplayName ?? "?"));
        }

        protected static double Amount(MyInventory inventory, string item) => (double)inventory.GetItemAmount(Item(item));

        /// <summary>The amount of an item in every inventory of the grid's blocks.</summary>
        protected static double Total(MyCubeGrid grid, string item)
        {
            var id = Item(item);
            var total = 0.0;
            foreach (var block in grid.GetFatBlocks())
                for (var i = 0; i < block.InventoryCount; i++)
                    total += (double)block.GetInventory(i).GetItemAmount(id);
            return total;
        }

        protected static string F(double value) => value.ToString(value >= 100 ? "F0" : "F2");

        // ------------------------------------------------------------------ waits

        /// <summary>Waits for a number to pass a bound, saying where it is every few seconds; fails with its last value.</summary>
        protected IEnumerator Until(Func<double> read, Func<double, bool> done, string what, int seconds = 60, Func<string> why = null)
        {
            var started = DateTime.UtcNow;
            var told = DateTime.UtcNow;
            while (true)
            {
                double value;
                try { value = read(); }
                catch (Exception e) { throw new ScenarioFailedException(what + ": " + e.GetType().Name + ": " + e.Message); }
                if (done(value)) yield break;
                if ((DateTime.UtcNow - started).TotalSeconds > seconds)
                    throw new ScenarioFailedException(what + ": not in " + seconds + " s, at " + F(value) + (why == null ? "" : " (" + Describe(why) + ")"));
                if ((DateTime.UtcNow - told).TotalSeconds > 10)
                {
                    told = DateTime.UtcNow;
                    Note(what + ": " + F(value));
                }
                yield return null;
            }
        }

        private static string Describe(Func<string> why)
        {
            try { return why(); }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>What a block is doing, for a failure: working, built, on, powered, and a tool's activation.</summary>
        protected static string State(MyCubeBlock block)
        {
            var functional = block as MyFunctionalBlock;
            var sink = block.Components.Get<Sandbox.Game.EntityComponents.MyResourceSinkComponent>();
            var text = block.BlockDefinition.Id.SubtypeName + ": working " + block.IsWorking + ", functional " + block.IsFunctional +
                       (functional != null ? ", on " + functional.Enabled : "") +
                       (sink != null ? ", powered " + sink.IsPoweredByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId) +
                                       " (" + sink.CurrentInputByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId).ToString("F4") + " MW)" : "") +
                       ", grid power " + block.CubeGrid.GridSystems.ResourceDistributor?.ResourceStateByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId);
            for (var type = block.GetType(); type != null; type = type.BaseType)
            {
                var activated = type.GetField("m_isActivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (activated != null) { text += ", activated " + activated.GetValue(block); break; }
            }
            return text;
        }

        /// <summary>Waits a span of game time (frames, not the clock).</summary>
        protected IEnumerator Seconds(double seconds)
        {
            for (var i = Frames(seconds); i > 0; i--) yield return null;
        }

        public override void Cleanup()
        {
            try
            {
                _gameplay?.Restore();
                if (!InSuite) FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
