using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sandbox.Definitions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.ModAPI;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame;
using VRage;
using VRageMath;

namespace SentisTests.Debug
{
    /// <summary>
    /// Local HTTP + MCP bridge for live world inspection and grid manipulation. 127.0.0.1 only.
    ///
    /// The game world is only legal to touch from the game thread, so every request that reads or
    /// writes game state goes through a job queue drained in <see cref="Tick"/>; the HTTP side
    /// just waits for the answer. MCP clients (SSE transport) get the same capability as named
    /// tools so an agent can look at the world, park a grid where it wants it, and watch the
    /// vanilla systems react - the debugging loop that used to take three minute test runs.
    ///
    /// REST surface (curl-friendly):
    ///   GET  /grids?filter=text
    ///   GET  /grid?id=123
    ///   GET  /at?x=1&y=2&z=3&r=50
    ///   POST /move     {id,x,y,z}
    ///   POST /orient   {id,fwd:[x,y,z],up:[x,y,z]}
    ///   POST /vel      {id,v:[x,y,z],ang:[x,y,z]}
    ///   POST /delete   {id}
    ///   POST /park     {id,x,y,z}        hold a grid at a point (test-grade servo)
    ///   POST /unpark   {id}
    ///   GET  /probe                           platform/projector/ship welding state
    ///   POST /spawn-mixed                     spawn the authored platform + flipped boat
    ///   GET  /log?n=80                        tail of the server log
    ///   POST /test {name}                     queue a scenario
    ///   GET  /status
    ///   MCP  /mcp/sse  +  /mcp/message        JSON-RPC 2.0 over SSE
    /// </summary>
    public static class DebugBridge
    {
        private const int Port = 18899;
        private static readonly Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static HttpListener _listener;
        private static readonly object _lifecycleLock = new object();
        private static volatile bool _started;
        private static string _token;

        private static readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();

        private class Job
        {
            public Func<JToken> Work;
            public TaskCompletionSource<JToken> Done = new TaskCompletionSource<JToken>();
            // 0=pending, 1=claimed by game thread, 2=cancelled before execution
            public int State;
        }

        // grids held at a point by the game-thread servo
        private static readonly ConcurrentDictionary<long, Vector3D> _parked = new ConcurrentDictionary<long, Vector3D>();
        private static JObject _lastSpawnPerfBody;

        // single SSE client (one agent at a time is plenty for debugging)
        private static object _sseLock = new object();
        private static StreamWriter _sse;
        private static DateTime _sseLastSent;

        public static void Start(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
            {
                Log.Error("debug bridge not started: DebugBridgeToken must contain at least 32 characters");
                return;
            }

            lock (_lifecycleLock)
            {
                if (_started) return;
                try
                {
                    _token = token;
                    _listener = new HttpListener();
                    _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                    _listener.Start();
                    _started = true;
                    Task.Run(ListenLoop);
                    Log.Info("SentisTests authenticated debug bridge on http://127.0.0.1:{0}/ (MCP: /mcp/sse)", Port);
                }
                catch (Exception e)
                {
                    _started = false;
                    _token = null;
                    try { _listener?.Close(); } catch { }
                    _listener = null;
                    Log.Error(e, "debug bridge failed to start");
                }
            }
        }

        public static void Stop()
        {
            lock (_lifecycleLock)
            {
                _started = false;
                _token = null;
                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                _listener = null;
            }

            lock (_sseLock)
            {
                try { _sse?.Dispose(); } catch { }
                _sse = null;
            }
            _parked.Clear();
            while (_jobs.TryDequeue(out var job))
            {
                Interlocked.CompareExchange(ref job.State, 2, 0);
                job.Done.TrySetResult(Err("debug bridge stopped"));
            }
        }

        /// <summary>Game thread: drain queued requests and run the park servos.</summary>
        public static void Tick()
        {
            if (!_started) return;
            for (int i = 0; i < 4 && _jobs.TryDequeue(out var job); i++)
            {
                if (Interlocked.CompareExchange(ref job.State, 1, 0) != 0)
                    continue;
                try { job.Done.TrySetResult(job.Work()); }
                catch (Exception e) { job.Done.TrySetResult(Err(e.Message)); }
            }
            foreach (var kv in _parked)
            {
                var grid = MyEntities.GetEntityById(kv.Key) as MyCubeGrid;
                if (grid == null)
                { _parked.TryRemove(kv.Key, out _); continue; }
                if (!grid.Physics.IsActive) grid.Physics.Activate();
                var err = kv.Value - grid.PositionComp.GetPosition();
                var velDes = err * 2.0;
                var len = velDes.Length();
                if (len > 8.0) velDes = velDes / len * 8.0;
                var vCur = new Vector3D(grid.Physics.LinearVelocity);
                var v = vCur + (velDes - vCur) * 0.3f;
                grid.Physics.LinearVelocity = new VRageMath.Vector3((float)v.X, (float)v.Y, (float)v.Z);
                grid.Physics.AngularVelocity = VRageMath.Vector3.Zero;
            }
        }

        // ------------------------------------------------------------------ HTTP plumbing

        private static void ListenLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); } catch { return; }
                Task.Run(() => Handle(ctx));
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
            try
            {
                if (!Authorized(req))
                {
                    ctx.Response.AddHeader("WWW-Authenticate", "Bearer");
                    SendJson(ctx, 401, Err("unauthorized"));
                    return;
                }
                if (req.ContentLength64 > 65536)
                {
                    SendJson(ctx, 413, Err("request body too large"));
                    return;
                }
                if (path == "/mcp/sse") { HandleSse(ctx); return; }
                if (path == "/mcp/message") { HandleMcpMessage(ctx); return; }
                if (req.HttpMethod == "GET") { HandleGet(ctx, path, req); return; }
                if (req.HttpMethod == "POST") { HandlePost(ctx, path, req); return; }
                SendJson(ctx, 405, Err("method not allowed"));
            }
            catch (Exception e)
            {
                SendJson(ctx, 500, Err(e.Message));
            }
        }

        private static JToken RunGameThread(Func<JToken> work)
        {
            if (!_started) return Err("debug bridge stopped");
            var job = new Job { Work = work };
            _jobs.Enqueue(job);
            if (job.Done.Task.Wait(TimeSpan.FromSeconds(8)))
                return job.Done.Task.Result;
            if (Interlocked.CompareExchange(ref job.State, 2, 0) == 0)
                return Err("game thread timeout; request cancelled");
            return Err("game thread timeout; request already executing");
        }

        private static bool Authorized(HttpListenerRequest req)
        {
            var expected = _token;
            var header = req.Headers["Authorization"];
            const string prefix = "Bearer ";
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(header) ||
                !header.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var actual = header.Substring(prefix.Length);
            var diff = actual.Length ^ expected.Length;
            var n = Math.Max(actual.Length, expected.Length);
            for (var i = 0; i < n; i++)
            {
                var a = i < actual.Length ? actual[i] : '\0';
                var b = i < expected.Length ? expected[i] : '\0';
                diff |= a ^ b;
            }
            return diff == 0;
        }

        private static void HandleGet(HttpListenerContext ctx, string path, HttpListenerRequest req)
        {
            static string Q(HttpListenerRequest r, string key)
            {
                foreach (var part in (r.Url?.Query ?? "").TrimStart('?').Split('&'))
                {
                    var kv = part.Split('=');
                    if (kv.Length == 2 && kv[0] == key) return Uri.UnescapeDataString(kv[1]);
                }
                return null;
            }
            string qkey(string key) => Q(req, key);
            switch (path)
            {
                case "/grids":
                    SendJson(ctx, 200, RunGameThread(() => ListGrids(qkey("filter"))));
                    break;
                case "/grid":
                    SendJson(ctx, 200, RunGameThread(() => GridDetail(long.Parse(qkey("id")))));
                    break;
                case "/at":
                    SendJson(ctx, 200, RunGameThread(() => Near(new Vector3D(double.Parse(qkey("x")), double.Parse(qkey("y")), double.Parse(qkey("z"))),
                        double.TryParse(qkey("r"), out var r) ? r : 50)));
                    break;
                case "/probe":
                    SendJson(ctx, 200, RunGameThread(Probe));
                    break;
                case "/projection":
                    SendJson(ctx, 200, RunGameThread(() => Projection(long.Parse(qkey("id")))));
                    break;

                case "/projector":
                    SendJson(ctx, 200, RunGameThread(() => Projector(long.Parse(qkey("id")))));
                    break;
                case "/sensors":
                    SendJson(ctx, 200, RunGameThread(() => Sensors(long.Parse(qkey("id")), long.Parse(qkey("proj")))));
                    break;
                case "/inv":
                    SendJson(ctx, 200, RunGameThread(() => Inventory(long.Parse(qkey("id")))));
                    break;
                case "/partial":
                    SendJson(ctx, 200, RunGameThread(() => PartialBlocks(long.Parse(qkey("id")))));
                    break;
                case "/conveyor":
                    SendJson(ctx, 200, RunGameThread(() => ConveyorStates(long.Parse(qkey("id")))));
                    break;
                case "/tool-radii":
                    SendJson(ctx, 200, RunGameThread(ToolRadii));
                    break;
                case "/freezer":
                    SendJson(ctx, 200, RunGameThread(FreezerState));
                    break;
                case "/status":
                    SendJson(ctx, 200, RunGameThread(() =>
                    {
                        var t = Core.TestRunner.Active;
                        return JObject.FromObject(new
                        {
                            active = t == null ? (string)null : t.Name,
                            progress = t == null ? (string)null : t.Progress,
                            parked = _parked.Select(kv => kv.Key).ToArray(),
                        });
                    }));
                    break;
                case "/log":
                    SendJson(ctx, 200, TailLog(int.TryParse(qkey("n"), out var n) ? n : 80));
                    break;
                default:
                    SendJson(ctx, 404, Err("unknown path " + path));
                    break;
            }
        }

        private static void HandlePost(HttpListenerContext ctx, string path, HttpListenerRequest req)
        {
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
            {
                var text = sr.ReadToEnd();
                var body = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                var id = body.Value<long>("id");
                switch (path)
                {
                    case "/move":
                        SendJson(ctx, 200, RunGameThread(() => Move(id, body.Value<double>("x"), body.Value<double>("y"), body.Value<double>("z"))));
                        break;
                    case "/orient":
                        SendJson(ctx, 200, RunGameThread(() => Orient(id, Vec(body["fwd"]), Vec(body["up"]))));
                        break;
                    case "/vel":
                        SendJson(ctx, 200, RunGameThread(() => SetVel(id, Vec(body["v"]), Vec(body["ang"]))));
                        break;
                    case "/delete":
                        SendJson(ctx, 200, RunGameThread(() => Delete(id)));
                        break;
                    case "/park":
                        _parked[id] = new Vector3D(body.Value<double>("x"), body.Value<double>("y"), body.Value<double>("z"));
                        SendJson(ctx, 200, JObject.FromObject(new { parked = id, at = _parked[id].ToString("F1") }));
                        break;
                    case "/give":
                        SendJson(ctx, 200, RunGameThread(() => Give(id, body.Value<string>("subtype"), body.Value<long>("amount"))));
                        break;
                    case "/fill-welder":
                        SendJson(ctx, 200, RunGameThread(() => FillWelder(id, body.Value<string>("subtype"), body.Value<long>("amount"), body.Value<string>("at"))));
                        break;
                    case "/unpark":
                        _parked.TryRemove(id, out _);
                        SendJson(ctx, 200, JObject.FromObject(new { unparked = id }));
                        break;
                    case "/spawn-mixed":
                        SendJson(ctx, 200, RunGameThread(SpawnMixed));
                        break;
                    case "/spawn-perf":
                        _lastSpawnPerfBody = body;
                        SendJson(ctx, 200, RunGameThread(SpawnPerfProjection));
                        break;
                    case "/projector-enabled":
                        SendJson(ctx, 200, RunGameThread(() =>
                        {
                            if (!MyEntities.TryGetEntityById(id, out var entity)) return Err("grid not found " + id);
                            var projector = Game.WorldApi.FindFunctional<MyProjectorBase>((MyCubeGrid)entity);
                            if (projector == null) return Err("grid has no projector");
                            projector.Enabled = body.Value<bool>("enabled");
                            return JObject.FromObject(new { projector = projector.EntityId, enabled = projector.Enabled });
                        }));
                        break;
                    case "/set-projection":
                        SendJson(ctx, 200, RunGameThread(() => SetProjectionCube(id, body)));
                        break;
                    case "/tool-radii":
                        SendJson(ctx, 200, RunGameThread(() => SetToolRadii(body)));
                        break;
                    case "/freezer":
                        SendJson(ctx, 200, RunGameThread(() => SetFreezer(body.Value<bool>("enabled"))));
                        break;
                    case "/spawn-tool-radii":
                        SendJson(ctx, 200, RunGameThread(() => SpawnToolRadiusRig(body.Value<string>("tool"))));
                        break;
                    case "/test":
                        Core.TestRunner.Enqueue(new[] { body.Value<string>("name") });
                        SendJson(ctx, 200, JObject.FromObject(new { queued = body.Value<string>("name") }));
                        break;
                    default:
                        SendJson(ctx, 404, Err("unknown path " + path));
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ world queries

        private static string V(Vector3D v) => v.ToString("F1");

        private static System.Collections.Generic.List<MyCubeGrid> AllGrids(double r)
        {
            var box = new BoundingBoxD(-r * new Vector3D(1, 1, 1), r * new Vector3D(1, 1, 1));
            var list = new System.Collections.Generic.List<MyCubeGrid>();
            foreach (var e in MyEntities.GetEntitiesInAABB(ref box, true))
                if (e is MyCubeGrid g) list.Add(g);
            return list;
        }

        private static JObject ListGrids(string filter)
        {
            var arr = new JArray();
            foreach (var g in AllGrids(1e6)
                .Where(g => string.IsNullOrEmpty(filter)
                    || (g.Name ?? g.EntityId.ToString()).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                    || (g.DisplayName ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(g => g.EntityId))
            {
                try
                {
                    if (g.PositionComp == null || g.Physics == null) continue;
                    var bb = g.PositionComp.WorldAABB;
                    arr.Add(Obj("id", g.EntityId, "name", g.Name, "displayName", g.DisplayName,
                            "pos", V(g.PositionComp.GetPosition()), "fwd", V(g.WorldMatrix.Forward), "up", V(g.WorldMatrix.Up),
                        "aabb", bb.Min.ToString("F0") + ".." + bb.Max.ToString("F0"),
                        "blocks", g.CubeBlocks == null ? 0 : g.CubeBlocks.Count,
                        "isStatic", g.Physics.IsStatic,
                        "vel", V(new Vector3D(g.Physics.LinearVelocity))));
                }
                catch { continue; }   // entity mid-teardown: skip, never take the bridge down
            }
            return new JObject { ["grids"] = arr };
        }

        private static JObject GridDetail(long id)
        {
            var g = MyEntities.GetEntityById(id) as MyCubeGrid;
            if (g == null)
                return Err("grid " + id + " not found");
            // modern SE: concrete block types live in FatBlock, structural blocks are plain slims
            var sub = g.CubeBlocks
                .Select(b => b.FatBlock == null ? "slim" : b.FatBlock.GetType().Name)
                .GroupBy(n => n).OrderBy(x => x.Key)
                .Select(x => x.Key + "=" + x.Count()).ToArray();
            return JObject.FromObject(new
            {
                id,
                    name = g.Name,
                    displayName = g.DisplayName,
                    pos = V(g.PositionComp.GetPosition()),
                fwd = V(g.WorldMatrix.Forward),
                up = V(g.WorldMatrix.Up),
                isStatic = g.Physics.IsStatic,
                mass = g.Physics.Mass,
                vel = V(new Vector3D(g.Physics.LinearVelocity)),
                blocks = g.CubeBlocks.Count,
                blockTypes = string.Join(", ", sub),
            });
        }

        private static JObject Near(Vector3D p, double r)
        {
            var box = new BoundingBoxD(p - r * new Vector3D(1, 1, 1), p + r * new Vector3D(1, 1, 1));
            var arr = new JArray();
            foreach (var x in MyEntities.GetEntitiesInAABB(ref box, true)
                .Where(e => e != null && !string.IsNullOrEmpty(e.Name))
                .Select(e => new { e, d = (e.PositionComp == null ? Vector3D.Zero : e.PositionComp.GetPosition()) - p })
                .Where(x => x.d.Length() <= r)
                .GroupBy(x => x.e.EntityId)
                .Select(x => x.First())
                .OrderBy(x => x.d.Length())
                .Take(30))
            {
                try
                {
                    arr.Add(Obj("id", x.e.EntityId, "name", x.e.Name, "type", x.e.GetType().Name,
                        "d", x.d.Length().ToString("F1"), "pos", V(x.e.PositionComp.GetPosition())));
                }
                catch { }
            }
            return new JObject { ["near"] = arr };
        }

        // ------------------------------------------------------------------ world writes

        private static JObject Move(long id, double x, double y, double z)
        {
            var g = MyEntities.GetEntityById(id) as MyCubeGrid;
            if (g == null) return Err("grid " + id + " not found");
                        var m2 = g.WorldMatrix;
            m2.Translation = new Vector3D(x, y, z);
            g.PositionComp.SetWorldMatrix(ref m2);
            return Moved(id, g);
        }

        private static JObject Orient(long id, Vector3D fwd, Vector3D up)
        {
            var g = MyEntities.GetEntityById(id) as MyCubeGrid;
            if (g == null) return Err("grid " + id + " not found");
            fwd = Vector3D.Normalize(fwd);
            up = Vector3D.Normalize(up - fwd * Vector3D.Dot(up, fwd));
            var right = Vector3D.Cross(up, fwd);
            var m = g.WorldMatrix;
            m.M11 = right.X; m.M12 = right.Y; m.M13 = right.Z;
            m.M21 = up.X; m.M22 = up.Y; m.M23 = up.Z;
            m.M31 = fwd.X; m.M32 = fwd.Y; m.M33 = fwd.Z;
            g.PositionComp.SetWorldMatrix(ref m);
            return Moved(id, g);
        }

        private static JObject Moved(long id, MyCubeGrid g)
        {
            g.Physics.ForceActivate();
            return JObject.FromObject(new { id, pos = V(g.PositionComp.GetPosition()) });
        }

        private static JObject SetVel(long id, Vector3D v, Vector3D a)
        {
            var g = MyEntities.GetEntityById(id) as MyCubeGrid;
            if (g == null) return Err("grid " + id + " not found");
            if (!g.Physics.IsActive) g.Physics.Activate();
            g.Physics.LinearVelocity = new VRageMath.Vector3((float)v.X, (float)v.Y, (float)v.Z);
            g.Physics.AngularVelocity = new VRageMath.Vector3((float)a.X, (float)a.Y, (float)a.Z);
            return JObject.FromObject(new { id });
        }

        private static JObject Delete(long id)
        {
            var g = MyEntities.GetEntityById(id) as MyCubeGrid;
            if (g == null) return Err("grid " + id + " not found");
            var name = g.Name;
            g.Delete();
            _parked.TryRemove(id, out _);
            return JObject.FromObject(new { deleted = name });
        }

        private static JObject Projection(long platformId)
        {
            var g = MyEntities.GetEntityById(platformId) as MyCubeGrid;
            if (g == null) return Err("grid " + platformId + " not found");
            var proj = Game.WorldApi.FindFunctional<MyProjectorBase>(g);
            var pg = proj == null ? null : proj.ProjectedGrid;
            if (pg == null || pg.CubeBlocks == null) return Err("no projection");
            var min = new Vector3D(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new Vector3D(double.MinValue, double.MinValue, double.MinValue);
            var ys = new System.Collections.Generic.SortedSet<double>();
            var samples = new JArray();
            int n = 0;
            foreach (var slim in pg.CubeBlocks)
            {
                var c = (slim.WorldAABB.Min + slim.WorldAABB.Max) * 0.5;
                min = new Vector3D(Math.Min(min.X, c.X), Math.Min(min.Y, c.Y), Math.Min(min.Z, c.Z));
                max = new Vector3D(Math.Max(max.X, c.X), Math.Max(max.Y, c.Y), Math.Max(max.Z, c.Z));
                ys.Add(Math.Round(c.Y, 1));
                if (n++ < 5) samples.Add(c.ToString("F1"));
            }
            return Obj("cells", pg.CubeBlocks.Count,
                "min", min.ToString("F1"), "max", max.ToString("F1"),
                "layers", ys.Count, "ys", string.Join(",", ys), "samples", samples.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static JObject Projector(long gridId)
        {
            var g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null) return Err("grid " + gridId + " not found");
            var proj = Game.WorldApi.FindFunctional<MyProjectorBase>(g);
            if (proj == null) return Err("no projector on grid");
            var obj = new JObject
            {
                ["id"] = g.EntityId,
                ["projector"] = proj.EntityId,
                ["enabled"] = (bool)proj.Enabled,
                ["isWorking"] = (bool)proj.IsWorking,
                ["isFunctional"] = (bool)proj.IsFunctional,
                ["isActivating"] = (bool)proj.IsActivating,
                ["scale"] = proj.Scale,
                ["projectionOffset"] = proj.ProjectionOffset.ToString(),
            };
            var pg = proj.ProjectedGrid;
            obj["projectedGrid"] = pg == null || pg.CubeBlocks == null ? 0 : pg.CubeBlocks.Count;
            if (pg != null && pg.CubeBlocks != null)
            {
                var census = new Dictionary<string, int>();
                var buildableComponents = new Dictionary<string, int>();
                foreach (var slim in pg.CubeBlocks)
                {
                    string verdict;
                    try
                    {
                        var check = proj.CanBuild(slim, true);
                        verdict = check.ToString();
                        if (check == Sandbox.ModAPI.BuildCheckResult.OK)
                        {
                            var components = slim.BlockDefinition?.Components;
                            var subtype = components != null && components.Length > 0
                                ? components[0].Definition.Id.SubtypeName
                                : "<none>";
                            int componentCount;
                            buildableComponents.TryGetValue(subtype, out componentCount);
                            buildableComponents[subtype] = componentCount + 1;
                        }
                    }
                    catch (Exception e) { verdict = "EX:" + e.GetBaseException().GetType().Name; }
                    int count;
                    census.TryGetValue(verdict, out count);
                    census[verdict] = count + 1;
                }
                obj["buildCheckCensus"] = JObject.FromObject(census);
                obj["buildableComponents"] = JObject.FromObject(buildableComponents);
            }
            return obj;
        }

        private static JObject Sensors(long shipId, long platformId)
        {
            var ship = MyEntities.GetEntityById(shipId) as MyCubeGrid;
            var plat = MyEntities.GetEntityById(platformId) as MyCubeGrid;
            if (ship == null || plat == null) return Err("grid not found");
            var proj = Game.WorldApi.FindFunctional<MyProjectorBase>(plat);
            var pg = proj == null ? null : proj.ProjectedGrid;
            var targets = new System.Collections.Generic.List<Vector3D>();
            if (pg != null && pg.CubeBlocks != null)
                foreach (var slim in pg.CubeBlocks)
                    targets.Add((slim.WorldAABB.Min + slim.WorldAABB.Max) * 0.5);
            var arr = new JArray();
            var best = double.MaxValue; var bestIdx = -1;
            var welders = Game.WorldApi.FindFunctionals<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(ship);
            for (int i = 0; i < welders.Count; i++)
            {
                var sp = Game.WorldApi.SensorSphere(welders[i]).Center;
                var d = double.MaxValue;
                foreach (var t in targets)
                {
                    var dd = Vector3D.DistanceSquared(sp, t);
                    if (dd < d) d = dd;
                }
                d = Math.Sqrt(d);
                if (d < best) { best = d; bestIdx = i; }
                if (d < 6.0)
                    arr.Add(Obj("i", i, "at", sp.ToString("F1"), "d", d.ToString("F2")));
            }
            return Obj("count", welders.Count, "nearest", best.ToString("F2"), "nearestIdx", bestIdx, "within6", arr);
        }

        private static JObject Inventory(long gridId)
        {
            var g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null) return Err("grid " + gridId + " not found");
            var arr = new JArray();
            var diag = new JArray();
            foreach (var slim in g.CubeBlocks)
            {
                VRage.Game.Entity.MyInventoryBase inv = null;
                var owner = slim.FatBlock as VRage.Game.ModAPI.Ingame.IMyInventoryOwner;
                if (owner != null && owner.HasInventory)
                {
                    try { inv = (VRage.Game.Entity.MyInventoryBase)(object)owner.GetInventory(0); } catch { }
                }
                if (inv == null)
                {
                    try { inv = (slim.FatBlock as dynamic)?.GetInventory(); } catch { }
                }
                if (slim.FatBlock != null && diag.Count < 6 && inv == null)
                {
                    var d0 = Obj("block", slim.FatBlock.GetType().Name);
                    if (inv == null)
                    {
                        var members = new JArray();
                        foreach (var mm in slim.FatBlock.GetType().GetMembers(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                            if (mm.Name.ToLowerInvariant().Contains("invent"))
                                members.Add(mm.MemberType + " " + mm.Name);
                        d0["invMembers"] = members;
                    }
                    if (inv == null) diag.Add(d0);
                }
                if (inv == null) continue;
                var items = new JArray();
                long total = 0;
                foreach (dynamic item in inv.GetItems())
                {
                    string name = (string)item.Content.SubtypeName;
                    long amount = (long)item.Amount;
                    total += amount;
                    items.Add(Obj("subtype", name, "amount", amount));
                }
                arr.Add(Obj("block", slim.BlockDefinition.Id.TypeId.ToString(),
                    "blockSub", slim.BlockDefinition.Id.SubtypeName ?? "",
                    "at", slim.WorldAABB.Center.ToString("F1"),
                    "capacity", inv.MaxVolume.ToString(), "used", inv.CurrentVolume.ToString(),
                    "items", items, "itemTotal", total));
            }
            return Obj("id", gridId, "inventories", arr, "noInvSample", diag);
        }

        private static JObject PartialBlocks(long gridId)
        {
            var g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null) return Err("grid " + gridId + " not found");
            var arr = new JArray();
            foreach (var slim in g.CubeBlocks)
            {
                if (slim.IsFullIntegrity) continue;
                var dump = new JObject();
                foreach (var pr in slim.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance))
                {
                    try
                    {
                        if (pr.GetIndexParameters().Length > 0) continue;
                        var v = pr.GetValue(slim);
                        if (v == null || v is string || v.GetType().IsValueType)
                        {
                            var s = v?.ToString() ?? "";
                            if (s.Length <= 60) dump[pr.Name] = s;
                        }
                    }
                    catch { }
                }
                dump["remaining"] = BuildRemaining(slim);
                arr.Add(dump);
            }
            return Obj("id", gridId, "count", arr.Count, "blocks", arr);
        }

        private static JArray BuildRemaining(Sandbox.Game.Entities.Cube.MySlimBlock slim)
        {
            // reflection: RequiredModules / ComponentUpdates differ across game builds; never hard-code
            var arr = new JArray();
            try
            {
                var t = slim.GetType();
                var rm = t.GetProperty("RequiredModules", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var modules = rm?.GetValue(slim);
                if (modules != null)
                {
                    dynamic dmodules = modules;
                    try
                    {
                        foreach (dynamic m in (System.Collections.IEnumerable)dmodules.Modules)
                            arr.Add(Obj("component", (string)m.Id.SubtypeName, "count", (int)m.Count));
                    }
                    catch { arr.Add(Obj("modulesDump", modules.ToString())); }
                }
            }
            catch (Exception e) { arr.Add(Obj("err", e.Message)); }
            return arr;
        }

        private static JObject Give(long gridId, string subtype, long amount)
        {
            var g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null) return Err("grid " + gridId + " not found");
            foreach (var slim in g.CubeBlocks)
            {
                VRage.Game.Entity.MyInventoryBase inv = null;
                var owner = slim.FatBlock as VRage.Game.ModAPI.Ingame.IMyInventoryOwner;
                if (owner != null && owner.HasInventory)
                {
                    try { inv = (VRage.Game.Entity.MyInventoryBase)(object)owner.GetInventory(0); } catch { }
                }
                if (inv == null) continue;
                var content = new VRage.Game.MyObjectBuilder_Component { SubtypeName = subtype };
                inv.AddItems((VRage.MyFixedPoint)(float)amount, content);
                return Obj("gave", subtype, "amount", amount, "into", slim.BlockDefinition.Id.TypeId.ToString(),
                           "at", slim.WorldAABB.Center.ToString("F1"));
            }
            return Err("no cargo container on grid");
        }

        private static JObject ConveyorStates(long gridId)
        {
            var g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null) return Err("grid " + gridId + " not found");
            var arr = new JArray();
            int on = 0, off = 0;
            foreach (var slim in g.CubeBlocks)
            {
                var fb = slim.FatBlock;
                if (fb == null) continue;
                string tn = fb.GetType().Name;
                if (!tn.Contains("Conveyor")) continue;
                bool en = true;
                try { en = ((dynamic)fb).IsConveyorRunning; }
                catch
                {
                    try { en = (bool)((dynamic)fb).Enabled; } catch { }
                }
                if (en) on++; else off++;
                if (arr.Count < 30 && !en)
                    arr.Add(Obj("type", tn, "at", slim.WorldAABB.Center.ToString("F1"), "enabled", en));
            }
            return Obj("id", gridId, "conveyorOn", on, "conveyorOff", off, "offSample", arr);
        }

        private static JObject FillWelder(long gridId, string subtype, long amount, string atStr)
        {
            var g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null) return Err("grid " + gridId + " not found");
            // find the welder nearest to atStr (or first with empty cargo)
            SpaceEngineers.Game.Entities.Blocks.MyShipWelder best = null;
            Vector3D target = Vector3D.Zero;
            bool haveAt = false;
            var m = System.Text.RegularExpressions.Regex.Match(atStr ?? "", @"(-?[\d.]+)[, ]+(-?[\d.]+)[, ]+(-?[\d.]+)");
            if (m.Success)
            {
                target = new Vector3D(double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value), double.Parse(m.Groups[3].Value));
                haveAt = true;
            }
            double bestD = double.MaxValue;
            foreach (var slim in g.CubeBlocks)
            {
                var w = slim.FatBlock as SpaceEngineers.Game.Entities.Blocks.MyShipWelder;
                if (w == null) continue;
                if (!haveAt) { best = w; break; }
                var d = Vector3D.DistanceSquared(slim.WorldAABB.Center, target);
                if (d < bestD) { bestD = d; best = w; }
            }
            if (best == null) return Err("no welder found");
            var inv = best.GetInventory();
            var content = new VRage.Game.MyObjectBuilder_Component { SubtypeName = subtype };
            inv.AddItems((VRage.MyFixedPoint)(float)amount, content);
            return Obj("filled", subtype, "amount", amount);
        }

        // ------------------------------------------------------------------ weld probing

        private static JObject Probe()
        {
            var outp = new JObject();
            var all = AllGrids(1e6).ToArray();
            var platforms = all.Where(g => (g.Name ?? "").StartsWith("ST-") || (g.Name ?? "").StartsWith("STDBG-")).ToArray();
            var ships = all.Where(g => (g.Name ?? "").Contains("ship")).ToArray();
            var p = new JArray();
            foreach (var g in platforms)
            {
                try
                {
                var proj = Game.WorldApi.FindFunctional<MyProjectorBase>(g);
                var projected = proj == null ? null : proj.ProjectedGrid;
                p.Add(Obj("id", g.EntityId, "name", g.Name,
                    "blocks", g.CubeBlocks.Count,
                    "partial", g.CubeBlocks.Count(b => !b.IsFullIntegrity),
                    "projected", projected == null || projected.CubeBlocks == null ? 0 : projected.CubeBlocks.Count,
                    "projector", proj == null ? "none" : (proj.Enabled ? "on" : "off") + (projected == null ? "" : " " + projected.CubeBlocks.Count + " cells"),
                    "pos", V(g.PositionComp.GetPosition()),
                    "aabb", g.PositionComp.WorldAABB.Min.ToString("F0") + ".." + g.PositionComp.WorldAABB.Max.ToString("F0")));
                }
                catch { }
            }
            outp["platforms"] = p;
            var sh = new JArray();
            foreach (var s0 in ships)
            {
                try
                {
                var w = Game.WorldApi.FindFunctionals<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(s0).ToArray();
                sh.Add(Obj("id", s0.EntityId, "name", s0.Name,
                    "pos", V(s0.PositionComp.GetPosition()),
                    "vel", V(new Vector3D(s0.Physics.LinearVelocity)),
                    "up", V(s0.WorldMatrix.Up), "fwd", V(s0.WorldMatrix.Forward),
                    "welders", w.Length,
                    "working", w.Count(x => x.IsWorking),
                    "enabled", w.Count(x => x.Enabled)));
                }
                catch { }
            }
            outp["ships"] = sh;
            return outp;
        }

        private static JObject SpawnMixed()
        {
            // platform: authored placement, static
            var pOb = Game.WorldApi.LoadAuthoredGrid("SentisTests.Resources.PlatformWithProjection.xml",
                "STDBG-mixed-platform");
            pOb.IsStatic = true;
            var platform = Game.WorldApi.SpawnGrid(pOb);
            Game.WorldApi.EnsureDistributor(platform);
            Game.WorldApi.ChargeBatteries(platform);

            // ship: the plate grows out of the deck's EAST face, so the boat works the open
            // (east) wall - leant 90 deg, tools looking -X at the wall, hull out in open space.
            var sOb = Game.WorldApi.LoadAuthoredGrid("SentisTests.Resources.MixedShip.xml", "STDBG-mixed-ship");
            var paabb = platform.PositionComp.WorldAABB;
            var pos = new Vector3D(paabb.Max.X + 25.0, paabb.Center.Y, paabb.Center.Z);
            sOb.PositionAndOrientation = new MyPositionAndOrientation(pos, new Vector3D(0, 0, 1), new Vector3D(-1, 0, 0));
            var ship = Game.WorldApi.SpawnGrid(sOb);
            Game.WorldApi.ChargeBatteries(ship);
            return JObject.FromObject(new
            {
                platform = new { id = platform.EntityId, name = platform.Name, pos = V(platform.PositionComp.GetPosition()) },
                ship = new { id = ship.EntityId, name = ship.Name, pos = V(ship.PositionComp.GetPosition()) },
                note = "poll /probe until projected=80, then /park the ship over the plate",
            });
        }

        /// <summary>
        /// Drops the welder_perf fixture platform (Projector-Test-REACTORS copy shipped as
        /// PerfProjection.xml) directly in front of the first live player character, so the
        /// operator can eyeball how the fixture actually materializes. With
        /// <c>withProjection=false</c> the embedded hologram is stripped from the projector
        /// object-builder before spawn - the grid carries no blueprint at all, which also
        /// isolates the heavy-blueprint replication from the plain grid replication.
        /// </summary>
        private static JObject SpawnPerfProjection()
        {
            var withProjection = true;
            try { withProjection = _lastSpawnPerfBody == null || _lastSpawnPerfBody.Value<bool?>("withProjection") != false; }
            catch { }
            var ob = Game.WorldApi.LoadAuthoredGrid("SentisTests.Resources.PerfProjection.xml",
                "STDBG-perf-projection");
            var projectors = ob.CubeBlocks.OfType<VRage.Game.MyObjectBuilder_ProjectorBase>().ToList();
            if (!withProjection)
                foreach (var p in projectors) p.ProjectedGrids = null;
            Vector3D anchor;
            var character = MyEntities.GetEntities().OfType<Sandbox.Game.Entities.Character.MyCharacter>()
                .FirstOrDefault(c => c != null && !c.MarkedForClose &&
                    MyAPIGateway.Players.GetPlayerControllingEntity(c) != null);
            if (character != null)
            {
                var eye = character.PositionComp.GetPosition();
                var dir = character.PositionComp.WorldMatrixRef.Forward;
                if (dir.LengthSquared() < 0.001) dir = character.PositionComp.WorldMatrixRef.Up;
                anchor = eye + Vector3D.Normalize(dir) * 120.0;
            }
            else
            {
                anchor = ob.PositionAndOrientation.Value.Position;
            }
            var pose = ob.PositionAndOrientation.Value;
            ob.PositionAndOrientation = new MyPositionAndOrientation(anchor, pose.Forward, pose.Up);
            ob.IsStatic = true;
            var grid = Game.WorldApi.SpawnGrid(ob);
            Game.WorldApi.EnsureDistributor(grid);
            Game.WorldApi.ChargeBatteries(grid);
            var projector = Game.WorldApi.FindFunctional<MyProjectorBase>(grid);
            if (projector != null) projector.Enabled = withProjection;
            return JObject.FromObject(new
            {
                id = grid.EntityId,
                name = grid.Name,
                pos = V(grid.PositionComp.GetPosition()),
                blocks = grid.BlocksCount,
                projector = projector == null ? (object)"none"
                    : new { id = projector.EntityId, enabled = projector.Enabled,
                            projected = projector.ProjectedGrid?.CubeBlocks.Count ?? 0 },
            });
        }

        /// <summary>
        /// Loads a fresh cube-shaped blueprint (N x N x N solid heavy-armor large blocks) into
        /// the grid's live projector via the vanilla IMyProjector.SetProjectedGrid path, then
        /// re-enables the projector so the clipboard rebuilds the preview. Used to eyeball how
        /// the client syncs a freshly authored projection.
        /// </summary>
        private static JObject SetProjectionCube(long gridId, JObject body)
        {
            if (!MyEntities.TryGetEntityById(gridId, out var entity)) return Err("grid not found " + gridId);
            var projector = Game.WorldApi.FindFunctional<MyProjectorBase>((MyCubeGrid)entity);
            if (projector == null) return Err("grid has no projector");
            int size = body.Value<int?>("size") ?? 10;
            size = Math.Max(1, Math.Min(size, 40));
            int sx = 0, sy = 0, sz = 0;
            try
            {
                var shift = body["shift"] as JArray;
                if (shift != null && shift.Count == 3)
                {
                    sx = shift.Value<int>(0);
                    sy = shift.Value<int>(1);
                    sz = shift.Value<int>(2);
                }
            }
            catch { }
            var grid = new MyObjectBuilder_CubeGrid
            {
                Name = "STDBG-armor-cube",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(Vector3D.Zero, new Vector3D(0, 0, 1), new Vector3D(0, 1, 0)),
                CubeBlocks = new System.Collections.Generic.List<MyObjectBuilder_CubeBlock>(),
            };
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                    for (int z = 0; z < size; z++)
                    {
                        grid.CubeBlocks.Add(new MyObjectBuilder_CubeBlock
                        {
                            SubtypeName = "LargeHeavyBlockArmorBlock",
                            Min = new SerializableVector3I(x + sx, y + sy, z + sz),
                        });
                    }
            // SetNewBlueprint only (re)initializes the clipboard when the projector is enabled
            // and working, so arm it first and swap the blueprint while it is live.
            projector.Enabled = true;
            ((Sandbox.ModAPI.IMyProjector)projector).SetProjectedGrid(grid);
            return JObject.FromObject(new
            {
                projector = projector.EntityId,
                blocks = grid.CubeBlocks.Count,
                size,
                shift = new { sx, sy, sz },
                enabled = projector.Enabled,
                projected = projector.ProjectedGrid?.CubeBlocks.Count ?? 0,
                previewAabb = projector.ProjectedGrid?.PositionComp.WorldAABB.ToString(),
                platformAabb = ((MyCubeGrid)entity).PositionComp.WorldAABB.ToString(),
                note = "projection is applied relative to the projector's current offset",
            });
        }


        // ------------------------------------------------------------------ ship-tool radius verification

        private static Assembly GameplayAssembly()
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(a.GetName().Name, "SentisGameplayImprovements", StringComparison.Ordinal));
        }

        private static object GameplayConfig(out Type pluginType)
        {
            var assembly = GameplayAssembly();
            if (assembly == null) throw new InvalidOperationException("SentisGameplayImprovements assembly is not loaded");
            pluginType = assembly.GetType("SentisGameplayImprovements.SentisGameplayImprovementsPlugin", true);
            return pluginType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        }

        private static JObject ToolRadii()
        {
            Type pluginType;
            var config = GameplayConfig(out pluginType);
            var samples = new JArray();
            foreach (var grid in AllGrids(1e6))
            foreach (var slim in grid.CubeBlocks)
            {
                var block = slim.FatBlock;
                if (block is MyShipWelder welder)
                {
                    var definition = (MyShipWelderDefinition)welder.BlockDefinition;
                    samples.Add(Obj("grid", grid.EntityId, "block", welder.EntityId, "tool", "welder",
                        "frozen", IsFrozenGrid(grid.EntityId),
                        "definition", definition.SensorRadius, "runtime", welder.DetectorSphere.Radius,
                        "optimizedProjectionRuntime", OptimizedWelderRadius(welder)));
                }
                else if (block is MyShipGrinder grinder)
                {
                    var definition = (MyShipGrinderDefinition)grinder.BlockDefinition;
                    samples.Add(Obj("grid", grid.EntityId, "block", grinder.EntityId, "tool", "grinder",
                        "frozen", IsFrozenGrid(grid.EntityId),
                        "definition", definition.SensorRadius, "runtime", grinder.DetectorSphere.Radius));
                }
                else if (block is MyShipDrill drill)
                {
                    var definition = (MyShipDrillDefinition)drill.BlockDefinition;
                    var sensor = drill.DrillBase?.Sensor;
                    var sensorRadius = sensor == null ? null : sensor.GetType().GetField("m_radius",
                        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sensor);
                    samples.Add(Obj("grid", grid.EntityId, "block", drill.EntityId, "tool", "drill",
                        "frozen", IsFrozenGrid(grid.EntityId),
                        "definitionSensor", definition.SensorRadius, "runtimeSensor", sensorRadius,
                        "definitionCutout", definition.CutOutRadius, "runtimeCutout", drill.GetDrillingSphere().Radius));
                }
            }

            float Value(string property) => (float)config.GetType().GetProperty(property).GetValue(config);
            return Obj("welder", Value("WelderRadiusMultiplier"),
                "grinder", Value("GrinderRadiusMultiplier"),
                "drill", Value("DrillRadiusMultiplier"), "samples", samples);
        }

        private static bool IsFrozenGrid(long gridId)
        {
            return Game.RuntimePluginControls.IsGridFrozen(gridId);
        }

        private static JObject FreezerState()
        {
            return Obj("enabled", Game.RuntimePluginControls.FreezerEnabled,
                "frozenGridCount", Game.RuntimePluginControls.FrozenGridCount);
        }

        private static JObject SetFreezer(bool enabled)
        {
            Game.RuntimePluginControls.SetFreezerEnabled(enabled);
            return FreezerState();
        }

        private static float OptimizedWelderRadius(MyShipWelder welder)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(a.GetName().Name, "SentisOptimisations", StringComparison.Ordinal));
            var type = assembly?.GetType("SentisOptimisationsPlugin.ShipTool.ShipToolPatch");
            var method = type?.GetMethod("GetWelderRadius", BindingFlags.Public | BindingFlags.Static);
            if (method == null) throw new InvalidOperationException("SentisOptimisations GetWelderRadius is unavailable");
            return (float)method.Invoke(null, new object[] { welder });
        }

        private static JObject SetToolRadii(JObject body)
        {
            Type pluginType;
            var config = GameplayConfig(out pluginType);
            foreach (var pair in new[]
            {
                (Json: "welder", Property: "WelderRadiusMultiplier"),
                (Json: "grinder", Property: "GrinderRadiusMultiplier"),
                (Json: "drill", Property: "DrillRadiusMultiplier"),
            })
            {
                if (body[pair.Json] != null)
                    config.GetType().GetProperty(pair.Property).SetValue(config, body.Value<float>(pair.Json));
            }
            pluginType.GetMethod("SaveConfig", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            return ToolRadii();
        }

        private static JObject SpawnToolRadiusRig(string tool)
        {
            var specs = new Dictionary<string, (string Subtype, Vector3D Position)>(StringComparer.OrdinalIgnoreCase)
            {
                ["welder"] = ("LargeShipWelder", new Vector3D(100, 100, 100)),
                ["grinder"] = ("LargeShipGrinder", new Vector3D(120, 100, 100)),
                ["drill"] = ("LargeBlockDrill", new Vector3D(140, 100, 100)),
            };
            if (string.IsNullOrEmpty(tool) || !specs.TryGetValue(tool, out var spec))
                return Err("tool must be welder, grinder or drill");

            var name = "STDBG-tool-radius-" + tool.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N");
            var ob = Game.WorldApi.GridOb(name, VRage.Game.MyCubeSize.Large, true,
                spec.Position, new[] { new Game.BlockSpec(spec.Subtype, Vector3I.Zero) });
            if (ob.CubeBlocks[0] is MyObjectBuilder_Drill drill)
            {
                drill.Inventory = new VRage.Game.MyObjectBuilder_Inventory();
                drill.Enabled = false;
            }
            var grid = Game.WorldApi.SpawnGrid(ob);
            return Obj("id", grid.EntityId, "name", grid.Name, "blocks", grid.CubeBlocks.Count);
        }

        // ------------------------------------------------------------------ misc

        private static JObject TailLog(int n)
        {
            try
            {
                var dir = Path.Combine(Environment.CurrentDirectory, "Logs");
                var file = Directory.GetFiles(dir, "Torch-*.log").OrderByDescending(f => f).FirstOrDefault();
                if (file == null) return Err("no log file");
                var lines = new Stack<string>();
                using (var sr = new StreamReader(file))
                    while (sr.ReadLine() is string l) { lines.Push(l); if (lines.Count > n) lines.Pop(); }
                return new JObject { ["lines"] = new JArray(lines.Reverse().ToArray()) };
            }
            catch (Exception e) { return Err(e.Message); }
        }

        // ------------------------------------------------------------------ MCP (SSE)

        private static readonly JArray McpTools = new JArray
        {
            Tool("world_grids", "List all cube grids: id, name, position, forward, up, AABB, block count, static flag",
                new { filter = "substring of the grid name (optional)" }),
            Tool("grid_detail", "One grid in detail: subtypes, functionals, physics", new { id = "grid entity id" }),
            Tool("world_at", "Entities within a radius of a world point", new { x = 0.0, y = 0.0, z = 0.0, r = 50.0 }),
            Tool("grid_move", "Teleport a grid", new { id = 0L, x = 0.0, y = 0.0, z = 0.0 }),
            Tool("grid_orient", "Set a grid's forward/up (kept right-handed)", new { id = 0L, fwd = new { x = 0.0, y = 0.0, z = 1.0 }, up = new { x = 0.0, y = 1.0, z = 0.0 } }),
            Tool("grid_velocity", "Set linear/angular velocity", new { id = 0L, v = new { x = 0.0, y = 0.0, z = 0.0 }, ang = new { x = 0.0, y = 0.0, z = 0.0 } }),
            Tool("grid_delete", "Delete a grid", new { id = 0L }),
            Tool("grid_park", "Hold a grid at a world point with the test-grade servo", new { id = 0L, x = 0.0, y = 0.0, z = 0.0 }),
            Tool("grid_unpark", "Stop holding a grid", new { id = 0L }),
            Tool("weld_probe", "Platform/projector/ship welding state (blocks, partial, projected, welders working)", new object()),
            Tool("spawn_mixed", "Spawn the authored platform + flipped welding boat for live debugging", new object()),
            Tool("log_tail", "Tail of the dedicated server log", new { n = 80 }),
            Tool("test_status", "Active test + progress", new object()),
            Tool("test_run", "Queue a scenario by name", new { name = "smoke|projector_weld|mixed_weld|hand_weld|production" }),
        };

        private static JObject Tool(string name, string description, object input) => new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = JObject.FromObject(input),
            },
        };

        private static void HandleSse(HttpListenerContext ctx)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.AddHeader("Cache-Control", "no-cache");
            ctx.Response.OutputStream.Flush();
            lock (_sseLock)
            {
                _sse?.Dispose();
                _sse = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
                _sseLastSent = DateTime.UtcNow;
                _sse.Write("event: endpoint\ndata: /mcp/message\n\n");
            }
            // keepalive until the client disconnects
            try
            {
                while (true)
                {
                    Thread.Sleep(15000);
                    lock (_sseLock)
                    {
                        if (_sse == null) break;
                        if ((DateTime.UtcNow - _sseLastSent).TotalSeconds > 10)
                        {
                            _sse.Write(": ping\n\n");
                            _sseLastSent = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch { /* client went away */ }
            lock (_sseLock)
            {
                try { _sse?.Dispose(); } catch { }
                _sse = null;
            }
        }

        private static void PushMcp(long id, JToken result)
        {
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            lock (_sseLock)
            {
                if (_sse == null) return;
                _sse.Write("event: message\ndata: " + msg.ToString(Formatting.None) + "\n\n");
                _sseLastSent = DateTime.UtcNow;
            }
        }

        private static void HandleMcpMessage(HttpListenerContext ctx)
        {
            using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            {
                var text = sr.ReadToEnd();
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                JObject rpc;
                try { rpc = JObject.Parse(text); } catch { return; }
                if (!rpc.TryGetValue("id", out var idTok) || idTok.Type == JTokenType.Null) return; // notification
                var id = idTok.Value<long>();
                var method = rpc.Value<string>("method");
                switch (method)
                {
                    case "initialize":
                        PushMcp(id, new JObject
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["capabilities"] = new JObject { ["tools"] = new JObject() },
                            ["serverInfo"] = new JObject { ["name"] = "se-debug", ["version"] = "1.0" },
                        });
                        break;
                    case "tools/list":
                        PushMcp(id, new JObject { ["tools"] = McpTools });
                        break;
                    case "tools/call":
                        var args = rpc["params"]["arguments"] as JObject ?? new JObject();
                        var name = rpc["params"].Value<string>("name");
                        var res = CallTool(name, args);
                        PushMcp(id, new JObject
                        {
                            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = res.ToString(Formatting.None) }),
                            ["isError"] = res is JObject errObj && errObj["error"] != null,
                        });
                        break;
                    default:
                        PushMcp(id, new JObject());
                        break;
                }
            }
        }

        private static JToken CallTool(string name, JObject a)
        {
            switch (name)
            {
                case "world_grids": return RunGameThread(() => ListGrids(a.Value<string>("filter")));
                case "grid_detail": return RunGameThread(() => GridDetail(a.Value<long>("id")));
                case "world_at":
                    return RunGameThread(() => Near(new Vector3D(a.Value<double>("x"), a.Value<double>("y"), a.Value<double>("z")),
                        a.Value<double?>("r") ?? 50));
                case "grid_move":
                    return RunGameThread(() => Move(a.Value<long>("id"), a.Value<double>("x"), a.Value<double>("y"), a.Value<double>("z")));
                case "grid_orient":
                    return RunGameThread(() => Orient(a.Value<long>("id"), Vec(a["fwd"]), Vec(a["up"])));
                case "grid_velocity":
                    return RunGameThread(() => SetVel(a.Value<long>("id"), Vec(a["v"]), Vec(a["ang"])));
                case "grid_delete":
                    return RunGameThread(() => Delete(a.Value<long>("id")));
                case "grid_park":
                    _parked[a.Value<long>("id")] = new Vector3D(a.Value<double>("x"), a.Value<double>("y"), a.Value<double>("z"));
                    return JObject.FromObject(new { ok = true, parked = a.Value<long>("id") });
                case "grid_unpark":
                    _parked.TryRemove(a.Value<long>("id"), out _);
                    return JObject.FromObject(new { ok = true });
                case "weld_probe": return RunGameThread(Probe);
                case "spawn_mixed": return RunGameThread(SpawnMixed);
                case "log_tail": return TailLog(a.Value<int?>("n") ?? 80);
                case "test_status":
                    return RunGameThread(() =>
                    {
                        var t = Core.TestRunner.Active;
                        return JObject.FromObject(new
                        {
                            active = t == null ? (string)null : t.Name,
                            progress = t == null ? (string)null : t.Progress,
                        });
                    });
                case "test_run":
                    Core.TestRunner.Enqueue(new[] { a.Value<string>("name") });
                    return JObject.FromObject(new { queued = a.Value<string>("name") });
                default:
                    return Err("unknown tool " + name);
            }
        }

        private static JObject Err(string msg) => new JObject { ["error"] = msg };

        private static JObject Obj(params object[] kv)
        {
            var o = new JObject();
            for (int i = 0; i + 1 < kv.Length; i += 2)
                o[(string)kv[i]] = kv[i + 1] as JToken ?? (kv[i + 1] == null ? null : new JValue(kv[i + 1]));
            return o;
        }

        // ------------------------------------------------------------------ helpers

        private static Vector3D Vec(JToken t)
        {
            if (t is JArray arr)
                return new Vector3D(arr.Count > 0 ? arr.Value<double>(0) : 0, arr.Count > 1 ? arr.Value<double>(1) : 0,
                    arr.Count > 2 ? arr.Value<double>(2) : 0);
            if (t is JObject o)
                return new Vector3D(o.Value<double?>("x") ?? 0, o.Value<double?>("y") ?? 0, o.Value<double?>("z") ?? 0);
            return Vector3D.Zero;
        }



        private static void SendJson(HttpListenerContext ctx, int code, JToken json)
        {
            var bytes = Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

    }
}
