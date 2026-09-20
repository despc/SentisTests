using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Game;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Library.Collections;
using VRage.Library.Utils;
using VRage.Game.Entity;
using VRage.Network;
using VRageMath;

namespace SentisTests.Game
{
    /// <summary>
    /// In-process fake game clients for replication load tests. Each fake client is registered in
    /// MyReplicationServer the way a joined player is (client state, ready), so the server replicates
    /// entities to it through the normal path: replication layers, REPLICATION_CREATE / STREAM_BEGIN,
    /// state sync, events. Nothing goes to Steam:
    ///
    /// - outgoing packets to fake Steam ids are taken out of MyNetworkWriter.SendPacket (bytes are
    ///   counted per message type) and handed to the fake client after an emulated downlink delay;
    /// - the fake client answers like MyReplicationClient: REPLICATION_READY for created and fully
    ///   streamed replicables, CLIENT_ACKS and CLIENT_UPDATE every frame;
    /// - its answers go through an emulated uplink delay and are injected into the server's receive
    ///   queue, so the server processes them on the game thread exactly like Steam packets.
    ///
    /// Network emulation: round trip time split evenly between both directions, uniform jitter, and
    /// loss of unreliable packets (state sync, client updates). Reliable packets are never lost and
    /// keep their order, like Steam reliable channels.
    ///
    /// Each fake client gets a network client, identity and player like a joined Steam user, and
    /// optionally a character that follows it; the replication position is set directly on the
    /// client state.
    /// </summary>
    public static class FakeClients
    {
        public struct NetworkProfile
        {
            public double RttMs;
            public double JitterMs;
            public double UnreliableLossPercent;

            public override string ToString() =>
                "rtt=" + RttMs.ToString("F0") + "ms jitter=±" + JitterMs.ToString("F0") + "ms loss=" + UnreliableLossPercent.ToString("F1") + "%";
        }

        // Far outside the range of real Steam ids (0x0110...).
        private const ulong BaseSteamId = 0x7E57_0000_0000_0000UL;
        private const int MaxClients = 1024;
        private const int ReplicationChannel = 2;
        private const int CopiedHeaderBytes = 256;
        private const int PacketHeaderBytes = 10;

        private enum ServerMessageKind : byte { Ignore, Create, StreamBegin, StateSync, Destroy }

        private enum StreamPart : byte { None, BadHeader, Empty, ZeroSize, Part }

        // Parsed where the server sends it, so nothing is copied or kept alive for the emulated delay.
        private struct ServerMessage
        {
            public ServerMessageKind Kind;
            public bool Reliable;
            public long Stamp;
            public uint Id;
            public uint Group;
            public bool Streaming;
            public byte PacketId;
            public StreamPart Stream;
            public short Parts;
            public short Part;
        }

        [ThreadStatic] private static BitStream _interceptReader;

        private struct ClientMessage
        {
            public long DeliverAt;
            public bool Reliable;
            public InboundPacket Packet;
        }

        private sealed class PendingStream
        {
            public uint Replicable;
            public readonly HashSet<short> Parts = new HashSet<short>();
        }

        private sealed class FakeClient
        {
            public int Index;
            public EndpointId Id;
            public Endpoint Endpoint;
            public MyClientStateBase State;
            public Sandbox.Game.World.MyPlayer Player;
            public Vector3D Center;
            public double Radius;
            public double Phase;
            public double AngularSpeed;

            public readonly ConcurrentQueue<ServerMessage> Incoming = new ConcurrentQueue<ServerMessage>();
            public readonly List<ServerMessage> Downlink = new List<ServerMessage>();
            public readonly List<long> DownlinkDeliverAt = new List<long>();
            public long LastReliableDown;
            public readonly List<ClientMessage> Uplink = new List<ClientMessage>();
            public long LastReliableUp;

            public byte LastStateSyncId;
            public readonly List<byte> Acks = new List<byte>();
            public byte LastStreamingId;
            public bool ReceivedStreaming;
            public byte ClientPacketId;
            public readonly Dictionary<uint, PendingStream> Streams = new Dictionary<uint, PendingStream>();
            public readonly List<(uint Id, bool Loaded)> ReadyToSend = new List<(uint, bool)>();

            public long BytesDown;
            public long PacketsDown;
            public long LostDown;
            public long LostUp;
            public long ReadySent;
        }

        private sealed class InboundPacket : MyPacket
        {
            public InboundPacket() { BitStream = new BitStream(256); }
            public override void Return() => PacketPool.Add(this);
        }

        private static readonly ConcurrentBag<InboundPacket> PacketPool = new ConcurrentBag<InboundPacket>();
        private static readonly string[] MessageNames = BuildMessageNames();

        // Game thread only.
        private static FakeClient[] _clients = new FakeClient[0];
        private static volatile int _clientCount;
        private static NetworkProfile _profile;
        private static readonly Random Rng = new Random(12345);
        private static readonly BitStream Writer = new BitStream(1024);
        private static byte[] _queueBuffer = new byte[256];
        private static ConcurrentQueue<MyPacket> _receiveQueue;
        private static MyReplicationServer _server;
        private static Action<MyClientStateBase, Vector3D?> _setPosition;
        private static IDictionary _serverClients;
        private static long _frames;

        // Any thread (SendPacket runs wherever the server sends from).
        private static volatile bool _intercepting;
        private static readonly long[] _bytesByMessage = new long[256];
        private static readonly long[] _packetsByMessage = new long[256];
        private static long _interceptTicks;
        private static long _tickTicks;
        // Streaming diagnostics: packets, bad header, unknown group, empty, zero size, completed.
        private static readonly long[] _streamStats = new long[6];

        private static Action<MyNetworkWriter.MyPacketDescriptor> _returnDescriptor;
        private static bool _installed;

        public static int Count => _clientCount;
        public static NetworkProfile Profile => _profile;

        public static void Install(PatchManager patchManager)
        {
            if (_installed || patchManager == null) return;
            var poolField = typeof(MyNetworkWriter).GetField("m_descriptorPool", BindingFlags.Static | BindingFlags.NonPublic);
            var pool = poolField?.GetValue(null) ?? throw new MissingFieldException("MyNetworkWriter.m_descriptorPool");
            _returnDescriptor = (Action<MyNetworkWriter.MyPacketDescriptor>)Delegate.CreateDelegate(
                typeof(Action<MyNetworkWriter.MyPacketDescriptor>), pool, pool.GetType().GetMethod("Return"));

            var ctx = patchManager.AcquireContext();
            var sendPacket = typeof(MyNetworkWriter).GetMethod(nameof(MyNetworkWriter.SendPacket), BindingFlags.Static | BindingFlags.Public);
            ctx.GetPattern(sendPacket).Prefixes.Add(typeof(FakeClients).GetMethod(nameof(SendPacketPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var getCheckpoint = typeof(Sandbox.Game.World.MySession).GetMethod(nameof(Sandbox.Game.World.MySession.GetCheckpoint), BindingFlags.Instance | BindingFlags.Public);
            ctx.GetPattern(getCheckpoint).Suffixes.Add(typeof(FakeClients).GetMethod(nameof(ClientCheckpointSuffix), BindingFlags.Static | BindingFlags.NonPublic));
            patchManager.Commit();
            _installed = true;
        }

        /// <summary>Adds fake clients placed on circles around the given centers. Game thread.</summary>
        public static void Add(int count, NetworkProfile profile, Func<int, (Vector3D Center, double Radius, double SpeedMps)> placement, bool withCharacters = false)
        {
            if (!_installed) throw new InvalidOperationException("FakeClients patch is not installed");
            if (_clientCount + count > MaxClients) throw new ArgumentOutOfRangeException(nameof(count));
            Bind();
            _profile = profile;
            var list = _clients.Take(_clientCount).ToList();
            for (var i = 0; i < count; i++)
            {
                var index = list.Count;
                var place = placement(index);
                var client = new FakeClient
                {
                    Index = index,
                    Id = new EndpointId(BaseSteamId + (ulong)index),
                    State = Activator.CreateInstance(MyPerGameSettings.ClientStateType) as MyClientStateBase,
                    Center = place.Center,
                    Radius = place.Radius,
                    Phase = Rng.NextDouble() * Math.PI * 2,
                    AngularSpeed = place.Radius > 1 ? place.SpeedMps / place.Radius : 0,
                };
                client.Endpoint = new Endpoint(client.Id, 0);
                MovePosition(client, 0);
                list.Add(client);
                _clients = list.ToArray();
                _clientCount = list.Count;
                _intercepting = true;

                // Like a joined Steam user: network client, identity and player (no character). Plugins
                // look players up by Steam id when replicating to a client.
                var name = NamePrefix + index.ToString("D3");
                Sync.Clients.AddClient(client.Id.Value, name);
                var identity = Sync.Players.CreateNewIdentity(name);
                client.Player = Sync.Players.CreateNewPlayer(identity, new Sandbox.Game.World.MyPlayer.PlayerId(client.Id.Value, 0), name,
                    realPlayer: true, initialPlayer: false, newIdentity: true);
                if (withCharacters && client.Player != null)
                {
                    // A floating astronaut that follows the client's circle (steered in Tick).
                    var world = MatrixD.CreateWorld(TargetPosition(client, _frames / 60.0), Vector3.Forward, Vector3.Up);
                    client.Player.SpawnAt(world, Vector3.Zero, null, null, findFreePlace: false);
                }
                _server.OnClientJoined(client.Id, client.State);
                var ready = new ClientReadyDataMsg
                {
                    ForcePlayoutDelayBuffer = false,
                    UsePlayoutDelayBufferForCharacter = true,
                    UsePlayoutDelayBufferForJetpack = true,
                    UsePlayoutDelayBufferForGrids = true,
                };
                _server.OnClientReady(client.Endpoint, ref ready);
            }
        }

        public static void SetProfile(NetworkProfile profile) => _profile = profile;

        /// <summary>
        /// Moves a fake client to a new spot at once: its replication position and its character
        /// (teleported, then held there). Game thread.
        /// </summary>
        public static void MoveTo(int index, Vector3D center)
        {
            var client = _clients[index];
            client.Center = center;
            client.Radius = 0;
            client.AngularSpeed = 0;
            MovePosition(client, _frames / 60.0);
            var character = client.Player?.Character;
            if (character == null || character.MarkedForClose || character.IsDead) return;
            character.PositionComp.SetPosition(center);
            if (character.Physics != null) character.Physics.LinearVelocity = Vector3.Zero;
        }

        /// <summary>Whether the fake client still has a live character. Game thread.</summary>
        public static bool HasLiveCharacter(int index)
        {
            var character = _clients[index].Player?.Character;
            return character != null && !character.MarkedForClose && !character.IsDead;
        }

        /// <summary>The client state of one fake client, to look at what the server has queued for it.</summary>
        public static MyClientStateBase StateOf(int index) => _clients[index].State;

        /// <summary>
        /// Puts an entity in front of a fake client, the way opening a terminal does on a real one.
        /// The property is only settable from inside the client state, so its setter is called directly.
        /// </summary>
        public static void LookAt(int index, MyEntity entity)
        {
            var state = _clients[index].State;
            var setter = state.GetType().GetProperty("ContextEntity",
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         ?.GetSetMethod(nonPublic: true)
                         ?? throw new InvalidOperationException("MyClientState.ContextEntity cannot be set");
            setter.Invoke(state, new object[] { entity });
        }

        /// <summary>Removes all fake clients from the replication server. Game thread.</summary>
        public static void RemoveAll()
        {
            var clients = _clients;
            var count = _clientCount;
            _clientCount = 0;
            _clients = new FakeClient[0];
            if (_server != null)
            {
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        _server.OnClientLeft(clients[i].Id);
                        var player = clients[i].Player;
                        if (player != null) Sync.Players.RemovePlayer(player);
                        if (Sync.Clients.HasClient(clients[i].Id.Value)) Sync.Clients.RemoveClient(clients[i].Id.Value);
                    }
                    catch (Exception e) { SentisTestsPlugin.Log.Error(e, "FakeClients: removing client " + i + " failed"); }
                    foreach (var message in clients[i].Uplink) message.Packet.Return();
                }
            }
            PurgeLeftovers();
            // Packets queued before removal still carry fake ids; keep dropping them for a while.
            _frames = 0;
        }

        public const string NamePrefix = "FakeClient ";

        /// <summary>
        /// Removes every identity and character named like a fake client, including ones left by
        /// earlier runs or saved into the world. Removing a player kills it, and with permanent death
        /// that gives the player a fresh identity, so identities are removed by name. Game thread.
        /// </summary>
        public static int PurgeLeftovers()
        {
            var removed = 0;
            foreach (var character in Sandbox.Game.Entities.MyEntities.GetEntities().OfType<Sandbox.Game.Entities.Character.MyCharacter>().ToList())
            {
                if (character.MarkedForClose) continue;
                // The identity may already be gone (removing a player kills it and, with permanent
                // death, hands it a new one), so the character's own name has to be checked as well.
                var identity = Sync.Players.TryGetIdentity(character.GetPlayerIdentityId());
                var byIdentity = identity != null && (identity.DisplayName ?? "").StartsWith(NamePrefix, StringComparison.Ordinal);
                var byName = (character.DisplayName ?? "").StartsWith(NamePrefix, StringComparison.Ordinal) ||
                             (character.Name ?? "").StartsWith(NamePrefix, StringComparison.Ordinal);
                if (!byIdentity && !byName) continue;
                character.Close();
                removed++;
            }
            foreach (var identity in Sync.Players.GetAllIdentities().ToList())
            {
                if (!(identity.DisplayName ?? "").StartsWith(NamePrefix, StringComparison.Ordinal)) continue;
                try
                {
                    if (Sync.Players.TryGetPlayerId(identity.IdentityId, out var playerId))
                    {
                        var player = Sync.Players.GetPlayerById(playerId);
                        if (player != null) Sync.Players.RemovePlayer(player);
                        Sync.Players.RemoveIdentity(identity.IdentityId, playerId);
                    }
                    else Sync.Players.RemoveIdentity(identity.IdentityId);
                    removed++;
                }
                catch (Exception e) { SentisTestsPlugin.Log.Error(e, "FakeClients: removing identity " + identity.DisplayName + " failed"); }
            }
            return removed;
        }

        private static void Bind()
        {
            _server = MyMultiplayer.Static?.ReplicationLayer as MyReplicationServer
                      ?? throw new InvalidOperationException("no replication server (not a multiplayer server session)");
            var readerType = typeof(MyNetworkWriter).Assembly.GetType("Sandbox.Engine.Networking.MyNetworkReader", true);
            var channels = (IDictionary)readerType.GetField("m_channels", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var info = channels[ReplicationChannel] ?? throw new InvalidOperationException("replication channel reader not found");
            var queue = info.GetType().GetField("Queue").GetValue(info);
            _receiveQueue = (ConcurrentQueue<MyPacket>)queue.GetType().GetField("m_receiveQueue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(queue);
            if (_setPosition == null)
            {
                var setter = typeof(MyClientStateBase).GetProperty(nameof(MyClientStateBase.Position)).GetSetMethod(true);
                _setPosition = (Action<MyClientStateBase, Vector3D?>)Delegate.CreateDelegate(typeof(Action<MyClientStateBase, Vector3D?>), setter);
            }
            _serverClients = (IDictionary)typeof(MyReplicationServer).GetField("m_clientStates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_server);
        }

        private static bool IsFake(ulong id) => id >= BaseSteamId && id < BaseSteamId + MaxClients;

        /// <summary>
        /// Fake players out of the world a real client downloads when it joins. The client loads
        /// every connected player of the checkpoint and dereferences InitNewPlayer's result, which is
        /// null for a player whose Steam client it does not know: a real player could not join while a
        /// test with fake clients ran ("error loading world", NullReferenceException in
        /// MyPlayerCollection.LoadPlayerInternal). Saves are not touched.
        /// </summary>
        private static void ClientCheckpointSuffix(VRage.Game.MyObjectBuilder_Checkpoint __result, bool isClientRequest)
        {
            if (!isClientRequest || __result == null) return;
            StripFake(__result.ConnectedPlayers);
            StripFake(__result.DisconnectedPlayers);
            StripFake(__result.AllPlayersData);
            StripFake(__result.AllPlayersColors);
            __result.AllPlayers?.RemoveAll(p => IsFake(p.SteamId));
        }

        private static void StripFake<T>(VRage.Serialization.SerializableDictionary<VRage.Game.MyObjectBuilder_Checkpoint.PlayerId, T> players)
        {
            if (players?.Dictionary == null) return;
            foreach (var key in players.Dictionary.Keys.Where(k => IsFake(k.GetClientId())).ToList())
                players.Dictionary.Remove(key);
        }

        private static bool SendPacketPrefix(MyNetworkWriter.MyPacketDescriptor packet)
        {
            if (!_intercepting) return true;
            var recipients = packet.Recipients;
            var anyFake = false;
            for (var i = 0; i < recipients.Count; i++)
                if (IsFake(recipients[i].Value)) { anyFake = true; break; }
            if (!anyFake) return true;

            var start = Stopwatch.GetTimestamp();
            var header = packet.Header;
            var messageId = header.Position > 0 ? header.Data[0] : (byte)0;
            var dataSize = packet.Data?.Size ?? 0;
            var kind = KindOf((MyMessageId)messageId);
            var reliable = packet.MsgType != VRage.GameServices.MyP2PMessageEnum.Unreliable &&
                           packet.MsgType != VRage.GameServices.MyP2PMessageEnum.UnreliableNoDelay;
            var parsed = new ServerMessage { Kind = kind, Reliable = reliable, Stamp = start };
            if (kind != ServerMessageKind.Ignore && packet.Data != null && dataSize > 0)
                parsed = Parse(parsed, packet.Data, Math.Min(dataSize, CopiedHeaderBytes));

            var clients = _clients;
            var count = _clientCount;
            var size = dataSize + (int)header.Position + PacketHeaderBytes;
            for (var i = recipients.Count - 1; i >= 0; i--)
            {
                var id = recipients[i].Value;
                if (!IsFake(id)) continue;
                recipients.RemoveAt(i);
                Interlocked.Add(ref _bytesByMessage[messageId], size);
                Interlocked.Increment(ref _packetsByMessage[messageId]);
                var index = (int)(id - BaseSteamId);
                if (index >= count || index >= clients.Length) continue;
                var client = clients[index];
                Interlocked.Add(ref client.BytesDown, size);
                Interlocked.Increment(ref client.PacketsDown);
                client.Incoming.Enqueue(parsed);
            }
            Interlocked.Add(ref _interceptTicks, Stopwatch.GetTimestamp() - start);
            if (recipients.Count > 0) return true;
            _returnDescriptor(packet);
            return false;
        }

        private static ServerMessageKind KindOf(MyMessageId id)
        {
            switch (id)
            {
                case MyMessageId.REPLICATION_CREATE: return ServerMessageKind.Create;
                case MyMessageId.REPLICATION_STREAM_BEGIN: return ServerMessageKind.StreamBegin;
                case MyMessageId.SERVER_STATE_SYNC: return ServerMessageKind.StateSync;
                case MyMessageId.REPLICATION_DESTROY: return ServerMessageKind.Destroy;
                default: return ServerMessageKind.Ignore;
            }
        }

        /// <summary>Delivers delayed traffic and runs the fake clients' network update. Game thread, once per frame.</summary>
        public static void Tick()
        {
            if (!_intercepting) return;
            var clients = _clients;
            var count = _clientCount;
            if (count == 0)
            {
                // Keep swallowing late packets for fake ids for a few seconds after removal.
                if (++_frames > 600) _intercepting = false;
                return;
            }
            var start = Stopwatch.GetTimestamp();
            _frames++;
            var ticksPerMs = Stopwatch.Frequency / 1000.0;
            var halfRtt = _profile.RttMs / 2;
            for (var i = 0; i < count; i++)
            {
                var client = clients[i];
                try
                {
                    ReceiveFromServer(client, start, ticksPerMs, halfRtt);
                    if (_frames % 10 == 0) MovePosition(client, _frames / 60.0);
                    SteerCharacter(client, _frames / 60.0);
                    SendToServer(client, start, ticksPerMs, halfRtt);
                    DeliverToServer(client, start);
                }
                catch (Exception e)
                {
                    SentisTestsPlugin.Log.Error(e, "FakeClients: client " + i + " update failed");
                }
            }
            Interlocked.Add(ref _tickTicks, Stopwatch.GetTimestamp() - start);
        }

        private static long Delay(long from, double baseMs, double ticksPerMs) =>
            from + (long)(Math.Max(0, baseMs + (Rng.NextDouble() * 2 - 1) * _profile.JitterMs) * ticksPerMs);

        private static bool Lost() => _profile.UnreliableLossPercent > 0 && Rng.NextDouble() * 100 < _profile.UnreliableLossPercent;

        private static void ReceiveFromServer(FakeClient client, long now, double ticksPerMs, double halfRtt)
        {
            while (client.Incoming.TryDequeue(out var message))
            {
                if (message.Kind == ServerMessageKind.Ignore) continue;
                if (!message.Reliable && Lost()) { client.LostDown++; continue; }
                var at = Delay(message.Stamp, halfRtt, ticksPerMs);
                if (message.Reliable)
                {
                    at = Math.Max(at, client.LastReliableDown);
                    client.LastReliableDown = at;
                }
                client.Downlink.Add(message);
                client.DownlinkDeliverAt.Add(at);
            }
            var write = 0;
            for (var i = 0; i < client.Downlink.Count; i++)
            {
                if (client.DownlinkDeliverAt[i] <= now) Process(client, client.Downlink[i]);
                else
                {
                    client.Downlink[write] = client.Downlink[i];
                    client.DownlinkDeliverAt[write] = client.DownlinkDeliverAt[i];
                    write++;
                }
            }
            client.Downlink.RemoveRange(write, client.Downlink.Count - write);
            client.DownlinkDeliverAt.RemoveRange(write, client.DownlinkDeliverAt.Count - write);
        }

        private static ServerMessage Parse(ServerMessage message, IPacketData data, int length)
        {
            var reader = _interceptReader ?? (_interceptReader = new BitStream(0));
            if (data.Data != null) reader.ResetRead(data.Data, data.Offset, length * 8L, false);
            else reader.ResetRead(data.Ptr + data.Offset, length * 8L, false);
            return ParseFrom(message, reader, length);
        }

        private static ServerMessage ParseFrom(ServerMessage message, BitStream reader, int length)
        {
            switch (message.Kind)
            {
                case ServerMessageKind.Create:
                    reader.ReadTypeId();
                    message.Id = reader.ReadUInt32Variant();
                    break;
                case ServerMessageKind.StreamBegin:
                    reader.ReadTypeId();
                    message.Id = reader.ReadUInt32Variant();
                    reader.ReadUInt32Variant();
                    if (reader.ReadByte() == 0) message.Kind = ServerMessageKind.Ignore;
                    else message.Group = reader.ReadUInt32Variant();
                    break;
                case ServerMessageKind.Destroy:
                    message.Id = reader.ReadUInt32Variant();
                    break;
                case ServerMessageKind.StateSync:
                    message.Streaming = reader.ReadBool();
                    message.PacketId = reader.ReadByte();
                    if (message.Streaming) ParseStreamPart(ref message, reader, length);
                    break;
            }
            return message;
        }

        private static void ParseStreamPart(ref ServerMessage message, BitStream reader, int length)
        {
            // Header: statistics, three timestamps, custom state (three floats).
            new VRage.Replication.MyPacketStatistics().Read(reader);
            reader.ReadDouble();
            reader.ReadDouble();
            reader.ReadDouble();
            reader.ReadFloat();
            reader.ReadFloat();
            reader.ReadFloat();
            if (reader.BytePosition + 2 >= length || !reader.CheckTerminator()) { message.Stream = StreamPart.BadHeader; return; }
            message.Group = reader.ReadUInt32Variant();
            reader.ReadInt32();
            if (!reader.ReadBool()) { message.Stream = StreamPart.Empty; return; }
            if (reader.ReadInt64(34) == 0) { message.Stream = StreamPart.ZeroSize; return; }
            message.Parts = reader.ReadInt16();
            message.Part = reader.ReadInt16();
            message.Stream = StreamPart.Part;
        }

        private static void Process(FakeClient client, ServerMessage message)
        {
            switch (message.Kind)
            {
                case ServerMessageKind.Create:
                    client.ReadyToSend.Add((message.Id, true));
                    break;
                case ServerMessageKind.StreamBegin:
                    client.Streams[message.Group] = new PendingStream { Replicable = message.Id };
                    break;
                case ServerMessageKind.Destroy:
                {
                    uint remove = 0;
                    foreach (var stream in client.Streams)
                        if (stream.Value.Replicable == message.Id) { remove = stream.Key; break; }
                    if (remove != 0) client.Streams.Remove(remove);
                    break;
                }
                case ServerMessageKind.StateSync:
                    if (!message.Streaming)
                    {
                        client.LastStateSyncId = message.PacketId;
                        if (!client.Acks.Contains(message.PacketId)) client.Acks.Add(message.PacketId);
                        break;
                    }
                    client.LastStreamingId = message.PacketId;
                    client.ReceivedStreaming = true;
                    ReceiveStreamPart(client, message);
                    break;
            }
        }

        private static void ReceiveStreamPart(FakeClient client, ServerMessage message)
        {
            _streamStats[0]++;
            if (message.Stream == StreamPart.BadHeader) { _streamStats[1]++; return; }
            if (!client.Streams.TryGetValue(message.Group, out var pending)) { _streamStats[2]++; return; }
            if (message.Stream == StreamPart.Empty)
            {
                _streamStats[3]++;
                // Empty stream: the client cancels loading.
                client.Streams.Remove(message.Group);
                client.ReadyToSend.Add((pending.Replicable, false));
                return;
            }
            if (message.Stream == StreamPart.ZeroSize) { _streamStats[4]++; return; }
            pending.Parts.Add(message.Part);
            if (pending.Parts.Count < message.Parts) return;
            _streamStats[5]++;
            client.Streams.Remove(message.Group);
            client.ReadyToSend.Add((pending.Replicable, true));
        }

        private static void SendToServer(FakeClient client, long now, double ticksPerMs, double halfRtt)
        {
            foreach (var ready in client.ReadyToSend)
            {
                Writer.ResetWrite();
                Writer.WriteVariant(ready.Id);
                Writer.WriteBool(ready.Loaded);
                Writer.Terminate();
                Queue(client, MyMessageId.REPLICATION_READY, true, now, ticksPerMs, halfRtt);
                client.ReadySent++;
            }
            client.ReadyToSend.Clear();

            Writer.ResetWrite();
            Writer.WriteByte(client.LastStateSyncId);
            Writer.WriteBool(client.ReceivedStreaming);
            client.ReceivedStreaming = false;
            Writer.WriteByte(client.LastStreamingId);
            var acks = (byte)Math.Min(client.Acks.Count, 255);
            Writer.WriteByte(acks);
            for (var i = 0; i < acks; i++) Writer.WriteByte(client.Acks[i]);
            Writer.Terminate();
            client.Acks.Clear();
            Queue(client, MyMessageId.CLIENT_ACKS, true, now, ticksPerMs, halfRtt);

            client.ClientPacketId++;
            Writer.ResetWrite();
            Writer.WriteByte(client.ClientPacketId);
            Writer.WriteDouble(MySandboxGame.Static.SimulationTimeWithSpeed.Milliseconds - halfRtt);
            Writer.WriteDouble(MyTimeSpan.FromTicks(Stopwatch.GetTimestamp()).Milliseconds);
            // MyClientState without a controlled entity or spectator position.
            Writer.WriteBool(false);
            Writer.WriteBool(false);
            Writer.WriteInt16(16);
            Writer.WriteInt16((short)Math.Min(_profile.RttMs, short.MaxValue));
            Writer.WriteInt64(0);
            Writer.WriteInt64(0);
            Writer.Terminate();
            Queue(client, MyMessageId.CLIENT_UPDATE, false, now, ticksPerMs, halfRtt);
        }

        private static void Queue(FakeClient client, MyMessageId id, bool reliable, long now, double ticksPerMs, double halfRtt)
        {
            if (!reliable && Lost()) { client.LostUp++; return; }
            if (!PacketPool.TryTake(out var packet)) packet = new InboundPacket();
            var payloadBits = Writer.BitPosition;
            // Transport header (message id, receiver index) followed by the payload.
            var bytes = 2 + (int)((payloadBits + 7) / 8);
            if (_queueBuffer.Length < bytes) _queueBuffer = new byte[Math.Max(bytes, _queueBuffer.Length * 2)];
            var buffer = _queueBuffer;
            buffer[0] = (byte)id;
            buffer[1] = 0;
            Marshal.Copy(Writer.DataPointer, buffer, 2, bytes - 2);
            packet.BitStream.ResetRead(buffer, 0, 16 + payloadBits, true);
            packet.ByteStream = null;
            packet.Sender = client.Endpoint;
            var at = Delay(now, halfRtt, ticksPerMs);
            if (reliable)
            {
                at = Math.Max(at, client.LastReliableUp);
                client.LastReliableUp = at;
            }
            client.Uplink.Add(new ClientMessage { DeliverAt = at, Reliable = reliable, Packet = packet });
        }

        private static void DeliverToServer(FakeClient client, long now)
        {
            var write = 0;
            for (var i = 0; i < client.Uplink.Count; i++)
            {
                var message = client.Uplink[i];
                if (message.DeliverAt <= now)
                {
                    message.Packet.ReceivedTime = MyTimeSpan.FromTicks(Stopwatch.GetTimestamp());
                    message.Packet.BitStream.SetBitPositionRead(0);
                    _receiveQueue.Enqueue(message.Packet);
                }
                else client.Uplink[write++] = message;
            }
            client.Uplink.RemoveRange(write, client.Uplink.Count - write);
        }

        private static Vector3D TargetPosition(FakeClient client, double seconds)
        {
            var angle = client.Phase + client.AngularSpeed * seconds;
            return client.Center + new Vector3D(Math.Cos(angle), 0, Math.Sin(angle)) * client.Radius;
        }

        private static void SteerCharacter(FakeClient client, double seconds)
        {
            var character = client.Player?.Character;
            var physics = character?.Physics;
            if (physics == null || character.MarkedForClose || character.IsDead) return;
            var ahead = TargetPosition(client, seconds + 1.0 / 60);
            var target = TargetPosition(client, seconds);
            var velocity = (ahead - target) * 60 + (target - character.PositionComp.GetPosition()) * 0.5;
            physics.LinearVelocity = velocity;
        }

        private static void MovePosition(FakeClient client, double seconds)
        {
            var position = TargetPosition(client, seconds);
            _setPosition(client.State, position);
        }

        private static string[] BuildMessageNames()
        {
            var names = new string[256];
            foreach (MyMessageId id in Enum.GetValues(typeof(MyMessageId))) names[(byte)id] = id.ToString();
            return names;
        }

        /// <summary>Traffic and replication state since the last call; clears the traffic counters. Game thread.</summary>
        public static string Take(double windowSeconds)
        {
            var clients = _clients;
            var count = _clientCount;
            var sb = new StringBuilder();
            sb.Append("fakeClients=").Append(count).Append(" (").Append(_profile).Append(')');
            long total = 0;
            var byMessage = new StringBuilder();
            for (var i = 0; i < 256; i++)
            {
                var bytes = Interlocked.Exchange(ref _bytesByMessage[i], 0);
                var packets = Interlocked.Exchange(ref _packetsByMessage[i], 0);
                if (packets == 0) continue;
                total += bytes;
                byMessage.AppendFormat(" {0}={1:F1}KB/s({2:F0}pkt/s)", MessageNames[i] ?? i.ToString(),
                    bytes / 1024.0 / windowSeconds, packets / windowSeconds);
            }
            sb.AppendFormat(" | downlink total={0:F1}KB/s", total / 1024.0 / windowSeconds);
            if (count > 0) sb.AppendFormat(" per client={0:F1}KB/s", total / 1024.0 / windowSeconds / count);
            sb.Append(byMessage);

            if (count > 0)
            {
                var replicables = new List<int>();
                var pending = new List<int>();
                var dirty = new List<int>();
                long lostDown = 0, lostUp = 0, ready = 0, streams = 0;
                for (var i = 0; i < count; i++)
                {
                    var client = clients[i];
                    lostDown += Interlocked.Exchange(ref client.LostDown, 0);
                    lostUp += Interlocked.Exchange(ref client.LostUp, 0);
                    ready += Interlocked.Exchange(ref client.ReadySent, 0);
                    Interlocked.Exchange(ref client.BytesDown, 0);
                    Interlocked.Exchange(ref client.PacketsDown, 0);
                    streams += client.Streams.Count;
                    var serverClient = ServerClient(client);
                    if (serverClient != null)
                    {
                        replicables.Add(CountOf(ClientReplicablesField.GetValue(serverClient)));
                        pending.Add((int)ClientPendingField.GetValue(serverClient));
                        dirty.Add(CountOf(ClientDirtyQueueField.GetValue(serverClient)));
                    }
                }
                sb.AppendFormat(" | per client replicables min/avg/max={0}", MinAvgMax(replicables))
                    .AppendFormat(" pending={0}", MinAvgMax(pending))
                    .AppendFormat(" dirtyQueue={0}", MinAvgMax(dirty))
                    .AppendFormat(" | stream packets={0} badHeader={1} unknownGroup={2} empty={3} zeroSize={4} completed={5}",
                        _streamStats[0], _streamStats[1], _streamStats[2], _streamStats[3], _streamStats[4], _streamStats[5])
                    .Append(ClearStreamStats())
                    .AppendFormat(" | streams in progress={0} ready sent={1} lost down/up={2}/{3}", streams, ready, lostDown, lostUp);
            }
            var interceptMs = Interlocked.Exchange(ref _interceptTicks, 0) * 1000.0 / Stopwatch.Frequency;
            var tickMs = Interlocked.Exchange(ref _tickTicks, 0) * 1000.0 / Stopwatch.Frequency;
            sb.AppendFormat(" | harness cost: intercept={0:F0}ms tick={1:F0}ms over {2:F0}s", interceptMs, tickMs, windowSeconds);
            return sb.ToString();
        }

        private static string ClearStreamStats()
        {
            Array.Clear(_streamStats, 0, _streamStats.Length);
            return "";
        }

        /// <summary>Replicables the server still waits to be confirmed by fake clients. Game thread.</summary>
        public static int PendingReplicables()
        {
            var clients = _clients;
            var count = _clientCount;
            var pending = 0;
            for (var i = 0; i < count; i++)
            {
                var serverClient = ServerClient(clients[i]);
                if (serverClient != null) pending += (int)ClientPendingField.GetValue(serverClient);
            }
            return pending;
        }

        // MyClient is internal to VRage.
        private static readonly Type ClientType = typeof(MyReplicationServer).Assembly.GetType("VRage.Network.MyClient", true);
        private static readonly FieldInfo ClientPendingField = ClientType.GetField("PendingReplicables");
        private static readonly FieldInfo ClientReplicablesField = ClientType.GetField("Replicables");
        private static readonly FieldInfo ClientDirtyQueueField = ClientType.GetField("DirtyQueue");

        private static object ServerClient(FakeClient client) =>
            _serverClients != null && _serverClients.Contains(client.Endpoint) ? _serverClients[client.Endpoint] : null;

        private static int CountOf(object collection) =>
            collection == null ? 0 : (int)collection.GetType().GetProperty("Count").GetValue(collection);

        private static string MinAvgMax(List<int> values) =>
            values.Count == 0 ? "-" : values.Min() + "/" + values.Average().ToString("F0") + "/" + values.Max();
    }
}
