using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRageMath;
using LaserStatus = Sandbox.ModAPI.Ingame.MyLaserAntennaStatus;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Two stations 2 km apart in open space, each with a powered laser antenna given the other's coordinates (as a player
    /// pastes a GPS and presses Connect): the heads turn, find each other and connect; one switched to idle, the link is
    /// gone; the other antenna picked from the terminal's list (<c>MyLaserAntenna.OnConnectToRecRequest</c>) and the link
    /// made permanent (<c>ChangePermRequest</c>), they connect again.
    /// </summary>
    public sealed class LaserAntennaCheck : RegressionCheck
    {
        public override string Name => Prefix + "laser_antenna";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, -10));
            MyCubeGrid Station(string name, Vector3D at) => Build(name, at, true,
                new Part("LaserAntenna/LargeBlockLaserAntenna", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, -1, 0).With(b => Motion.Charged(b)));
            var one = Block<MyLaserAntenna>(Station("laser-one", Site.Origin));
            var two = Block<MyLaserAntenna>(Station("laser-two", Site.At(0, 0, 2000)));
            yield return Until(() => one.IsWorking && two.IsWorking ? 1 : 0, v => v > 0, "both laser antennas working", 10, () => State(one) + "; " + State(two));
            string Gps(MyLaserAntenna to)
            {
                var p = to.PositionComp.GetPosition();
                return "GPS:regression:" + p.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" +
                       p.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" + p.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":";
            }
            var a = (Sandbox.ModAPI.Ingame.IMyLaserAntenna)one;
            var b = (Sandbox.ModAPI.Ingame.IMyLaserAntenna)two;
            a.SetTargetCoords(Gps(two));
            a.Connect();
            b.SetTargetCoords(Gps(one));
            b.Connect();
            var started = DateTime.UtcNow;
            yield return Until(() => a.Status == LaserStatus.Connected && b.Status == LaserStatus.Connected ? 1 : 0, v => v > 0, "the laser antennas connected", 90,
                () => "one " + a.Status + ", two " + b.Status);
            var took = (DateTime.UtcNow - started).TotalSeconds;
            ((Sandbox.ModAPI.Ingame.IMyTerminalBlock)one).GetActionWithName("Idle").Apply(one);
            yield return Until(() => b.Status != LaserStatus.Connected ? 1 : 0, v => v > 0, "the link gone with one antenna idle", 15, () => "one " + a.Status + ", two " + b.Status);
            var lost = b.Status;
            // linked again the way the terminal does it: the other antenna picked from the list, the link made permanent
            Request(one, "OnConnectToRecRequest", two.EntityId);
            yield return Until(() => a.Status == LaserStatus.Connected && b.Status == LaserStatus.Connected ? 1 : 0, v => v > 0, "the laser antennas connected again", 60,
                () => "one " + a.Status + ", two " + b.Status);
            // permanent only once connected
            Request(one, "ChangePermRequest", true);
            Check(a.IsPermanent && b.IsPermanent, "the link was not made permanent: one " + a.IsPermanent + ", two " + b.IsPermanent);
            Result("2 km apart, the laser antennas turned to each other's coordinates and connected in " + F(took) + " s; one idle, the other " + lost +
                   "; picked from the list, connected again, permanent");
        }
    }

    /// <summary>
    /// An event controller set to "Door opened" on a door, its first slot switching a light off: the player opens the door
    /// and the light goes off.
    /// </summary>
    public sealed class EventControllerCheck : RegressionCheck
    {
        public override string Name => Prefix + "event_controller";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            // the event's number is given to it by its definition: read off a controller built out of the way
            var probe = Build("event-probe", Site.At(0, 60, 0), true, new Part("EventControllerBlock/EventControllerLarge", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            object doorEvent = null;
            foreach (var component in ((VRage.Game.Entity.MyEntity)probe.GetCubeBlock(Vector3I.Zero).FatBlock).Components)
                if (component.GetType().Name == "MyEventDoorOpened") doorEvent = component;
            Check(doorEvent != null, "the event controller has no \"Door opened\" event");
            var eventId = (long)doorEvent.GetType().GetProperty("UniqueSelectionId").GetValue(doorEvent);
            probe.Close();

            var doorId = MyEntityIdentifier.AllocateId();
            var lightId = MyEntityIdentifier.AllocateId();
            void Set(MyObjectBuilder_CubeBlock block, string field, object value) =>
                (block.GetType().GetField(field) ?? throw new Core.ScenarioFailedException(block.GetType().Name + "." + field + " is not in this game")).SetValue(block, value);
            // what happened, with the thread it happened on: the selected blocks looked up, the slot run, the light
            // switched (probes on the controller's methods, taken off at the end)
            var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
            string Where() => "thread " + System.Threading.Thread.CurrentThread.ManagedThreadId + (System.Threading.Thread.CurrentThread == Sandbox.MySandboxGame.Static.UpdateThread ? " (game)" : "") +
                               " frame " + Sandbox.Game.World.MySession.Static.GameplayFrameCounter;
            var controllerType = typeof(SpaceEngineers.Game.Entities.Blocks.MyGravityGenerator).Assembly.GetType("SpaceEngineers.Game.Entities.Blocks.MyEventControllerBlock");
            var harmonyProbe = new HarmonyLib.Harmony("sentistests.event_controller_probe");
            _probe = harmonyProbe;
            TriggerProbe.Seen = seen;
            TriggerProbe.Where = Where;
            var trigger = controllerType?.GetMethod("TriggerAction", new[] { typeof(int) });
            if (trigger != null) harmonyProbe.Patch(trigger, prefix: new HarmonyLib.HarmonyMethod(typeof(TriggerProbe).GetMethod(nameof(TriggerProbe.Prefix))));
            var process = controllerType?.GetMethod("ProcessSelectedBlocks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (process != null) harmonyProbe.Patch(process, prefix: new HarmonyLib.HarmonyMethod(typeof(TriggerProbe).GetMethod(nameof(TriggerProbe.Before))),
                postfix: new HarmonyLib.HarmonyMethod(typeof(TriggerProbe).GetMethod(nameof(TriggerProbe.After))));
            seen.Enqueue("built on " + Where());
            var grid = Build("event", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("Door/", 1, 0, 0).With(b => b.EntityId = doorId),
                new Part("InteriorLight/SmallLight", -1, 0, 0).With(b => b.EntityId = lightId),
                new Part("EventControllerBlock/EventControllerLarge", 0, 1, 0).With(b =>
                {
                    Set(b, "SelectedEvent", eventId);
                    // the builder's own list type (a serializable list of entity ids)
                    var blocks = Activator.CreateInstance(b.GetType().GetField("SelectedBlocks").FieldType);
                    ((System.Collections.Generic.ICollection<long>)blocks).Add(doorId);
                    Set(b, "SelectedBlocks", blocks);
                    Set(b, "Toolbar", Toolbars.One(lightId, "OnOff_Off"));
                }));
            var door = Block<MyDoor>(grid);
            seen.Enqueue("ours: controller " + grid.GetCubeBlock(new Vector3I(0, 1, 0)).FatBlock.EntityId + ", door " + door.EntityId);
            var light = Block<MyLightingBlock>(grid);
            var controller = grid.GetCubeBlock(new Vector3I(0, 1, 0)).FatBlock;
            yield return Until(() => controller.IsWorking && light.IsWorking ? 1 : 0, v => v > 0, "the event controller and the light working", 10, () => State(controller) + "; " + State(light));
            yield return Seconds(1);
            Check(light.Enabled, "the light went off before the door was opened");
            controller.GetType().GetEvent("ActionTriggered")?.AddEventHandler(controller, new Action<int>(slot => seen.Enqueue("slot " + slot + " run on " + Where())));
            ((Sandbox.Game.Entities.Cube.MyFunctionalBlock)(object)light).EnabledChanged += b => seen.Enqueue("light " + (((Sandbox.Game.Entities.Cube.MyFunctionalBlock)b).Enabled ? "on" : "off") + " on " + Where());
            ((Sandbox.ModAPI.IMyTerminalBlock)door).SetValueBool("Open", true);
            yield return Until(() => light.Enabled ? 0 : 1, v => v > 0, "the light switched off by the event of the door opening", 10,
                () => "seen: " + string.Join("; ", seen) + "; door " + ((Sandbox.ModAPI.Ingame.IMyDoor)door).Status + "; " + ((Sandbox.ModAPI.IMyTerminalBlock)controller).DetailedInfo.Trim().Replace("\n", " ") +
                      "; blocks still to find " + ((controller.GetType().GetField("m_selectedBlockIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                          ?.GetValue(controller) as System.Collections.ICollection)?.Count.ToString() ?? "none") + ", updates " + controller.NeedsUpdate + "; " + Inside(controller, door));
            Note("seen: " + string.Join("; ", seen));
            Result("the door opened, the event controller ran its slot and switched the light off");
        }

        private HarmonyLib.Harmony _probe;

        public override void Cleanup()
        {
            try { _probe?.UnpatchAll("sentistests.event_controller_probe"); }
            catch (Exception) { }
            base.Cleanup();
        }

        /// <summary>A prefix on MyEventControllerBlock.TriggerAction: what its first conditions were.</summary>
        public static class TriggerProbe
        {
            public static System.Collections.Concurrent.ConcurrentQueue<string> Seen;
            public static Func<string> Where;

            public static void Prefix(object __instance, int startIndex)
            {
                try
                {
                    const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var t = __instance.GetType();
                    var b = (Sandbox.Game.Entities.MyCubeBlock)__instance;
                    Seen?.Enqueue("TriggerAction(" + startIndex + ") of " + b.EntityId + " on " + Where?.Invoke() + ": server " + Sandbox.Game.Multiplayer.Sync.IsServer + ", working " + b.IsWorking +
                                  ", functional " + b.IsFunctional + ", ids left " + (t.GetField("m_selectedBlockIds", any)?.GetValue(__instance) == null ? "none" : "some") +
                                  ", triggering " + t.GetField("m_isTriggeringAction", any)?.GetValue(__instance) + ", items " +
                                  (t.GetProperty("Toolbar", any)?.GetValue(__instance) as Sandbox.Game.Screens.Helpers.MyToolbar)?.ItemCount + "; " + Slot(b, startIndex));
                }
                catch (Exception e) { Seen?.Enqueue("probe: " + e.Message); }
            }

            // the slot's block the way TriggerAction finds it
            private static string Slot(Sandbox.Game.Entities.MyCubeBlock controller, int index)
            {
                var toolbar = controller.GetType().GetProperty("Toolbar")?.GetValue(controller) as Sandbox.Game.Screens.Helpers.MyToolbar;
                var item = toolbar?.GetItemAtIndex(index) as Sandbox.Game.Screens.Helpers.MyToolbarItemTerminalBlock;
                if (item == null) return "slot " + index + ": " + toolbar?.GetItemAtIndex(index)?.GetType().Name;
                var id = item.BlockEntityId;
                var entity = Sandbox.Game.Entities.MyEntities.GetEntityById(id, true);
                var block = entity as Sandbox.Game.Entities.MyCubeBlock;
                return "slot " + index + " block " + id + ": " + (entity == null ? "not found" + Store(id) : entity.GetType().Name + (block == null ? "" :
                    ", same logical group " + block.CubeGrid.IsInSameLogicalGroupAs(controller.CubeGrid) + ", closed " + block.Closed +
                    ", id module " + (block.IDModule != null) + ", big owners " + string.Join(",", block.CubeGrid.BigOwners)));
            }

            private static string Left(object controller)
            {
                var ids = controller.GetType().GetField("m_selectedBlockIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(controller) as System.Collections.IEnumerable;
                if (ids == null) return "none";
                return string.Join(",", ids.Cast<object>().Select(id => id + (Sandbox.Game.Entities.MyEntities.GetEntityById((long)id) != null ? " found" : " not found" + Store((long)id))));
            }

            // where the entity is: the world's store of entities, this thread's own, swapped
            private static string Store(long id)
            {
                const System.Reflection.BindingFlags st = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                var t = typeof(VRage.MyEntityIdentifier);
                var main = t.GetField("m_mainData", st)?.GetValue(null);
                var own = t.GetField("m_perThreadData", st)?.GetValue(null);
                System.Collections.IDictionary List(object data) => data?.GetType().GetField("EntityList")?.GetValue(data) as System.Collections.IDictionary;
                var mainList = List(main);
                var ownList = List(own);
                var e = mainList != null && mainList.Contains(id) ? mainList[id] as VRage.Game.Entity.MyEntity : null;
                return " (world store " + (mainList == null ? "?" : mainList.Contains(id) + " of " + mainList.Count) + (e != null ? ", closed " + e.Closed + ", top closed " + e.GetTopMostParent().Closed : "") +
                       ", own store " + (ownList == null ? "none" : ownList.Contains(id) + " of " + ownList.Count) + ", swapped " + t.GetField("m_isSwapped", st)?.GetValue(null) + ")";
            }

            public static void Before(object __instance)
            {
                try { Seen?.Enqueue("ProcessSelectedBlocks of " + ((VRage.ModAPI.IMyEntity)__instance).EntityId + " on " + Where?.Invoke() + ", ids left " + Left(__instance)); }
                catch (Exception e) { Seen?.Enqueue("probe: " + e.Message); }
            }

            public static void After(object __instance, bool __result)
            {
                try { Seen?.Enqueue("ProcessSelectedBlocks of " + ((VRage.ModAPI.IMyEntity)__instance).EntityId + " done: " + __result + ", ids left " + Left(__instance)); }
                catch (Exception e) { Seen?.Enqueue("probe: " + e.Message); }
            }
        }

        /// <summary>The event controller's and its event's own state, for a check that failed.</summary>
        private static string Inside(object controller, object door)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object Get(object o, string name)
            {
                for (var t = o?.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, any);
                    if (f != null) return f.GetValue(o);
                    var p = t.GetProperty(name, any);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
                }
                return "?";
            }
            string Count(object o) => o is System.Collections.ICollection c ? c.Count.ToString() : o?.ToString() ?? "null";
            try
            {
                var selected = Get(controller, "m_selectedEvent");
                var generic = Get(selected, "m_eventGeneric");
                var states = Get(generic, "m_triggerStates") as System.Collections.IDictionary;
                var stateText = states == null ? "?" : string.Join(",", states.Values.Cast<object>().Select(v => v?.ToString() ?? "null"));
                var opened = door.GetType().BaseType?.GetField("DoorStateChanged", any) ?? door.GetType().GetField("OnDoorOpened", any);
                var handlers = 0;
                for (var t = door.GetType(); t != null; t = t.BaseType)
                    foreach (var f in t.GetFields(any))
                        if (f.Name.Contains("DoorOpened") && f.GetValue(door) is Delegate d)
                            handlers += d.GetInvocationList().Length;
                return "event " + selected?.GetType().Name + ", observed " + Count(Get(generic, "m_observedBlocks")) + ", states " + stateText +
                       ", last slot " + Get(generic, "m_lastSlot") + ", triggering " + Get(controller, "m_isTriggeringAction") +
                       ", toolbar items " + Get(Get(controller, "Toolbar"), "ItemCount") + ", working " + Get(controller, "IsWorking") +
                       ", functional " + Get(controller, "IsFunctional") + ", door opened handlers " + handlers + "; " + Slot(Get(controller, "Toolbar"));
            }
            catch (Exception e)
            {
                return "inside: " + e.GetType().Name + " " + e.Message;
            }

            string Slot(object toolbar)
            {
                var item = toolbar?.GetType().GetMethod("GetItemAtIndex")?.Invoke(toolbar, new object[] { 0 });
                if (item == null) return "slot 1 empty";
                var action = item.GetType().GetMethod("GetCurrentAction", any)?.Invoke(item, null);
                var block = Get(item, "m_block");
                var grid = (block as Sandbox.Game.Entities.MyCubeBlock)?.CubeGrid;
                var own = (controller as Sandbox.Game.Entities.MyCubeBlock)?.CubeGrid;
                return "slot 1 " + item.GetType().Name + " enabled " + Get(item, "Enabled") + ", block " + (block as VRage.Game.Entity.MyEntity)?.EntityId +
                       ", action " + (action == null ? "null" : Get(action, "Id")) + ", same logical group " +
                       (grid == null || own == null ? "?" : MyCubeGridGroups.Static.Logical.HasSameGroup(own, grid).ToString()) +
                       ", block owner " + Get(block, "OwnerId") + ", controller owner " + Get(controller, "OwnerId") +
                       ", block id module " + (Get(block, "IDModule") == null ? "none" : "yes") + ", grid big owners " +
                       (grid == null ? "?" : string.Join(",", grid.BigOwners)) + ", relation to big owner " +
                       (grid == null || grid.BigOwners.Count == 0 ? "-" : ((Sandbox.Game.Entities.MyCubeBlock)controller).GetUserRelationToOwner(grid.BigOwners[0]).ToString()) + "; " + AsCluster(item);
            }

            // the slot's action and update on a thread that SentisClusters takes for one updating a cluster
            string AsCluster(object item)
            {
                var flag = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisClusters.Run.ClusterThread")).FirstOrDefault(t => t != null)
                    ?.GetField("_cluster", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (flag == null) return "no SentisClusters";
                string seen = null;
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        flag.SetValue(null, 1);
                        var action = item.GetType().GetMethod("GetCurrentAction", any)?.Invoke(item, null);
                        item.GetType().GetMethod("Update")?.Invoke(item, new object[] { controller, 0L, false });
                        seen = "as a cluster's thread: action " + (action == null ? "null" : Get(action, "Id")) + ", enabled " + Get(item, "Enabled");
                    }
                    catch (Exception e) { seen = "as a cluster's thread: " + (e.InnerException ?? e).GetType().Name + " " + (e.InnerException ?? e).Message; }
                    finally { flag.SetValue(null, 0); }
                });
                thread.Start();
                thread.Join(5000);
                var again = item.GetType().GetMethod("GetCurrentAction", any)?.Invoke(item, null);
                item.GetType().GetMethod("Update")?.Invoke(item, new object[] { controller, 0L, false });
                return seen + "; on the game thread again: action " + (again == null ? "null" : Get(again, "Id")) + ", enabled " + Get(item, "Enabled");
            }
        }
    }

    /// <summary>
    /// A ship with an AI flight block (AI Move), thrusters every way and a gyro: given a waypoint 150 m off (as the AI
    /// recorder and the basic task hand it over, <c>MyFlightMovementBlock.SetWaypoints</c>) and its AI behavior switched on in the
    /// terminal, it flies there by itself and stops there.
    /// </summary>
    public sealed class AiFlightCheck : RegressionCheck
    {
        public override string Name => Prefix + "ai_flight";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            var parts = new System.Collections.Generic.List<Part>
            {
                new Part("FlightMovementBlock/LargeFlightMovement", 0, 0, 0),
                new Part("Gyro/LargeBlockGyro", -1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)),
            };
            foreach (var (face, x, y, z) in new[]
                     {
                         (Base6Directions.Direction.Right, 2, 0, 0), (Base6Directions.Direction.Left, -3, 0, 0), (Base6Directions.Direction.Up, 0, 1, 0),
                         (Base6Directions.Direction.Down, 0, -2, 0), (Base6Directions.Direction.Backward, 0, 0, 1), (Base6Directions.Direction.Forward, 0, 0, -2),
                     })
                parts.Add(new Part("Thrust/LargeBlockSmallThrust", x, y, z).Facing(face, Base6Directions.GetPerpendicular(face)));
            var ship = Build("ai-flight", Site.Origin, false, parts);
            var block = ship.GetCubeBlock(Vector3I.Zero).FatBlock;
            var flight = (Sandbox.ModAPI.IMyFlightMovementBlock)block;
            yield return Until(() => block.IsWorking ? 1 : 0, v => v > 0, "the AI flight block working", 10, () => State(block));
            var waypoint = Site.At(150, 0, 0);
            // the waypoints as the AI recorder and the basic task hand them over; the mod API's AddWaypoint throws on a
            // server ("Event 'AddWaypointBroadcast' not found": it raises the component's event on the block)
            ((SpaceEngineers.Game.Entities.Blocks.MyFlightMovementBlock)block).SetWaypoints(
                new System.Collections.Generic.List<Sandbox.Game.Entities.MyAutopilotWaypoint> { new Sandbox.Game.Entities.MyAutopilotWaypoint(waypoint, "regression") }, false, false);
            flight.FlightMode = Sandbox.ModAPI.Ingame.FlightMode.OneWay;
            ((Sandbox.ModAPI.IMyTerminalBlock)block).SetValueFloat("SpeedLimit", 20f);
            ((Sandbox.ModAPI.IMyTerminalBlock)block).SetValueBool("ActivateBehavior", true);
            yield return Until(() => flight.IsAutoPilotEnabled ? 1 : 0, v => v > 0, "the AI flight's autopilot on", 10,
                () => ((Sandbox.ModAPI.IMyTerminalBlock)block).DetailedInfo.Trim().Replace("\n", " "));
            Func<double> off = () => Vector3D.Distance(ship.PositionComp.WorldAABB.Center, waypoint);
            yield return Until(off, v => v < 10, "the ship flying to the waypoint by AI, m off it", 90,
                () => F(Motion.Speed(ship)) + " m/s; " + ((Sandbox.ModAPI.IMyTerminalBlock)block).DetailedInfo.Trim().Replace("\n", " "));
            yield return Until(() => Motion.Speed(ship), v => v < 1, "the ship stopping at the waypoint, m/s", 30);
            Result("the AI flight block flew the ship to the waypoint 150 m off and stopped " + F(off()) + " m from it");
        }
    }
}
