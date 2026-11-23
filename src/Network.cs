using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace TLDCoop
{
    internal enum NetMode { Off, Host, Client }

    internal sealed class RemoteInfo
    {
        public byte Id;
        public string Name = "?";
        public NetPeer Peer; // only set on host
        public bool Welcomed;
    }

    /// <summary>
    /// Thin wrapper around LiteNetLib. Host-authoritative star topology:
    /// clients only talk to the host, the host relays player states.
    /// All callbacks run on Unity's main thread because PollEvents() is called from OnUpdate.
    /// </summary>
    internal sealed class Network : INetEventListener
    {
        public NetMode Mode { get; private set; } = NetMode.Off;
        public byte LocalId { get; private set; } = Protocol.HostId;
        private string _status;
        public string Status { get => _status ?? L.T("Kapalı", "Off"); private set => _status = value; }
        public readonly Dictionary<byte, RemoteInfo> Remotes = new();

        public event Action<byte, NetDataReader, Msg> OnMessage;

        /// <summary>
        /// Set while a scene hand-over waits inside the game's own loading code: only the messages it accepts are
        /// handled right away, everything else is kept (in order) and handled at the next normal Poll().
        /// </summary>
        internal Func<Msg, bool> OnlyNow;
        private sealed class Held { public byte From; public Msg Msg; public byte[] Data; public bool Stale; }
        private readonly List<Held> _held = new();
        /// <summary>True while a kept message is handled that the receiver must pass on to others but not apply itself.</summary>
        internal static bool RelayOnly;

        private void Dispatch(byte from, NetDataReader r, Msg msg)
        {
            if (OnlyNow != null && !OnlyNow(msg)) { _held.Add(new Held { From = from, Msg = msg, Data = r.GetRemainingBytes() }); return; }
            OnMessage?.Invoke(from, r, msg);
        }

        private static bool IsSceneChange(Msg m) => m == Msg.ItemRemoved || m == Msg.ItemSpawned || m == Msg.ContainerState
            || m == Msg.ContainerItemAdded || m == Msg.ContainerItemRemoved || m == Msg.FireState || m == Msg.CarcassState || m == Msg.CarcassGone;

        /// <summary>Kept changes for this region are already contained in a region copy that just arrived: do not apply them later.</summary>
        internal int MarkHeldStale(string scene)
        {
            int n = 0;
            foreach (var h in _held)
            {
                if (h.Stale || !IsSceneChange(h.Msg)) continue;
                try { if (new NetDataReader(h.Data).GetString() == scene) { h.Stale = true; n++; } } catch { }
            }
            return n;
        }

        private void ReleaseHeld()
        {
            if (_held.Count == 0) return;
            var list = _held.ToArray(); _held.Clear();
            foreach (var h in list)
            {
                RelayOnly = h.Stale;
                try { OnMessage?.Invoke(h.From, new NetDataReader(h.Data), h.Msg); }
                catch (Exception e) { CoopMod.Log($"Kept packet {h.Msg}: {e.Message}"); }
                finally { RelayOnly = false; }
            }
        }

        /// <summary>Handle what has arrived, without releasing the kept messages (used while waiting inside game code).</summary>
        internal void PollNow() => _net?.PollEvents();
        public event Action<byte> OnRemoteLeft;
        public event Action<byte, string> OnRemoteJoined;
        public event Action OnStopped;

        public void SetStatus(string s) => Status = s;

        /// <summary>Client: asked to join but not in yet.</summary>
        public bool Connecting => Mode == NetMode.Client && !IsConnected;

        /// <summary>Host: send to one client by id.</summary>
        public void SendTo(byte id, NetDataWriter w, DeliveryMethod dm)
        {
            if (Mode == NetMode.Host && Remotes.TryGetValue(id, out var r) && r.Peer != null) r.Peer.Send(w, dm);
            else if (Mode == NetMode.Client) _hostPeer?.Send(w, dm);
        }

        private NetManager _net;
        private NetPeer _hostPeer;
        private readonly NetDataWriter _w = new();
        private byte _nextId = 1;
        private int _maxPlayers = 4;
        private string _localName = "Oyuncu";

        public bool IsConnected => Mode == NetMode.Host || (Mode == NetMode.Client && _hostPeer != null && _hostPeer.ConnectionState == ConnectionState.Connected && LocalId != Protocol.HostId);

        public void StartHost(int port, int maxPlayers, string name)
        {
            Stop();
            _maxPlayers = Math.Max(2, maxPlayers);
            _localName = name;
            _net = new NetManager(this) { AutoRecycle = true, UnconnectedMessagesEnabled = false, DisconnectTimeout = 15000, IPv6Enabled = false };
            if (!_net.Start(port))
            {
                Status = L.T($"Port {port} açılamadı", $"Could not open port {port}");
                _net = null;
                return;
            }
            Mode = NetMode.Host;
            LocalId = Protocol.HostId;
            _nextId = 1;
            Status = L.T($"Sunucu açık (port {port})", $"Server open (port {port})");
            CoopMod.Log($"Hosting on UDP {port}");
        }

        public void StartClient(string address, int port, string name)
        {
            Stop();
            _localName = name;
            _net = new NetManager(this) { AutoRecycle = true, DisconnectTimeout = 15000, IPv6Enabled = false };
            _net.Start();
            Mode = NetMode.Client;
            LocalId = Protocol.HostId; // real id arrives with Welcome
            Status = L.T($"Bağlanıyor: {address}:{port}", $"Connecting: {address}:{port}");
            CoopMod.Log($"Connecting to {address}:{port}");
            try { _hostPeer = _net.Connect(address, port, Protocol.ConnectKey); }
            catch (Exception e) { Status = L.T("Adres hatalı", "Bad address"); CoopMod.Log("Connect failed: " + e.Message); Stop(); }
        }

        public void Stop()
        {
            if (_net != null)
            {
                try { _net.Stop(true); } catch { }
            }
            foreach (var id in new List<byte>(Remotes.Keys)) OnRemoteLeft?.Invoke(id);
            Remotes.Clear();
            _net = null;
            _hostPeer = null;
            _held.Clear(); OnlyNow = null;
            Mode = NetMode.Off;
            LocalId = Protocol.HostId;
            Status = L.T("Kapalı", "Off");
            OnStopped?.Invoke();
        }

        public void Poll()
        {
            if (OnlyNow == null) ReleaseHeld();
            _net?.PollEvents();
        }

        // ---------- sending ----------
        public NetDataWriter Begin(Msg m) { _w.Reset(); _w.Put((byte)m); return _w; }

        /// <summary>Host: send to every client (optionally except one). Client: send to host.</summary>
        public void Send(NetDataWriter w, DeliveryMethod dm, byte exceptId = 255)
        {
            if (_net == null) return;
            if (Mode == NetMode.Client) { _hostPeer?.Send(w, dm); return; }
            foreach (var r in Remotes.Values)
                if (r.Peer != null && r.Welcomed && r.Id != exceptId) r.Peer.Send(w, dm);
        }

        private void SendTo(NetPeer p, NetDataWriter w, DeliveryMethod dm) => p?.Send(w, dm);

        // ---------- LiteNetLib callbacks ----------
        public void OnConnectionRequest(ConnectionRequest request)
        {
            CoopMod.Log($"Connection request from {request.RemoteEndPoint}");
            if (Mode != NetMode.Host) { request.Reject(); return; }
            if (Remotes.Count + 1 >= _maxPlayers) { request.Reject(); return; }
            request.AcceptIfKey(Protocol.ConnectKey);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            if (Mode == NetMode.Client)
            {
                Status = L.T("Bağlandı, el sıkışılıyor…", "Connected, shaking hands…");
                var w = Begin(Msg.Hello);
                w.Put(CoopMod.ModVersion);
                w.Put(CoopMod.GameVersion());
                w.Put(_localName);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                var id = AllocId();
                Remotes[id] = new RemoteInfo { Id = id, Peer = peer };
                peer.Tag = id;
            }
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (Mode == NetMode.Client)
            {
                CoopMod.Log("Disconnected: " + info.Reason);
                var reason = info.Reason switch
                {
                    DisconnectReason.ConnectionFailed => L.T("Sunucuya ulaşılamadı", "Could not reach the server"),
                    DisconnectReason.Timeout => L.T("Bağlantı zaman aşımı", "The connection timed out"),
                    DisconnectReason.ConnectionRejected => L.T("Sunucu bağlantıyı reddetti (dolu olabilir)", "The server refused the connection (it may be full)"),
                    _ => L.T("Bağlantı koptu", "The connection was lost")
                };
                Stop();
                Status = reason;
                CoopMod.Popup(L.T("ORTAK DÜNYA", "SHARED WORLD"), reason);
                return;
            }
            if (peer.Tag is byte id && Remotes.TryGetValue(id, out var r))
            {
                Remotes.Remove(id);
                OnRemoteLeft?.Invoke(id);
                if (r.Welcomed)
                {
                    var w = Begin(Msg.PlayerLeft); w.Put(id);
                    Send(w, DeliveryMethod.ReliableOrdered);
                    CoopMod.Hud(L.T($"{r.Name} ayrıldı", $"{r.Name} left"));
                }
            }
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod dm)
        {
            if (reader.AvailableBytes < 1) return;
            var msg = (Msg)reader.GetByte();
            try
            {
                if (Mode == NetMode.Host) HostReceive(peer, reader, msg);
                else ClientReceive(reader, msg);
            }
            catch (Exception e) { CoopMod.Log($"Bad packet {msg}: {e.Message}"); }
        }

        private void HostReceive(NetPeer peer, NetPacketReader r, Msg msg)
        {
            if (!(peer.Tag is byte id) || !Remotes.TryGetValue(id, out var info)) return;
            if (msg == Msg.Hello)
            {
                var modV = r.GetString(); var gameV = r.GetString(); var name = r.GetString(32);
                if (modV != CoopMod.ModVersion || (!CoopMod.SameGameVersion(gameV) && !CoopMod.AllowVersionMismatch))
                {
                    var rej = Begin(Msg.Reject);
                    rej.Put(L.Both($"Sürüm uyuşmuyor. Sunucu: mod {CoopMod.ModVersion}, oyun {CoopMod.GameVersion()} / Sen: mod {modV}, oyun {gameV}", $"Versions do not match. Server: mod {CoopMod.ModVersion}, game {CoopMod.GameVersion()} / You: mod {modV}, game {gameV}"));
                    peer.Send(rej, DeliveryMethod.ReliableOrdered);
                    _net.TriggerUpdate();
                    peer.Disconnect();
                    return;
                }
                if (!CoopMod.SameGameVersion(gameV)) CoopMod.Log($"WARNING: game version mismatch accepted (test mode): host {CoopMod.GameVersion()} / client {gameV}");
                else if (gameV != CoopMod.GameVersion()) CoopMod.Log($"Same game version from another store or build: host {CoopMod.GameVersion()} / client {gameV}");
                info.Name = string.IsNullOrWhiteSpace(name) ? $"Player {id}" : name;
                info.Welcomed = true;
                var wel = Begin(Msg.Welcome); wel.Put(id); wel.Put(_localName);
                // tell the newcomer who is already here
                byte others = 0;
                foreach (var o in Remotes.Values) if (o.Id != id && o.Welcomed) others++;
                wel.Put(others);
                foreach (var o in Remotes.Values) if (o.Id != id && o.Welcomed) { wel.Put(o.Id); wel.Put(o.Name); }
                peer.Send(wel, DeliveryMethod.ReliableOrdered);
                var j = Begin(Msg.PlayerJoined); j.Put(id); j.Put(info.Name);
                Send(j, DeliveryMethod.ReliableOrdered, id);
                OnRemoteJoined?.Invoke(id, info.Name);
                CoopMod.Hud(L.T($"{info.Name} katıldı", $"{info.Name} joined"));
                return;
            }
            if (!info.Welcomed) return;
            if (msg == Msg.PlayerState)
            {
                // re-stamp with the real id and relay to the others
                r.GetByte();
                var scene = r.GetString(); float x = r.GetFloat(), y = r.GetFloat(), z = r.GetFloat(), yaw = r.GetFloat(); bool cr = r.GetBool();
                byte light = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;
                bool away = r.AvailableBytes > 0 && r.GetBool();
                byte sleep = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;
                byte downed = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;
                ushort downedLeft = r.AvailableBytes > 1 ? r.GetUShort() : (ushort)0;
                sbyte pitch = r.AvailableBytes > 0 ? r.GetSByte() : (sbyte)0;
                var w = Begin(Msg.PlayerState); w.Put(id); w.Put(scene); w.Put(x); w.Put(y); w.Put(z); w.Put(yaw); w.Put(cr); w.Put(light); w.Put(away); w.Put(sleep);
                w.Put(downed); w.Put(downedLeft); w.Put(pitch);
                Send(w, DeliveryMethod.Sequenced, id);
                CoopMod.Instance.Players.ApplyState(id, scene, x, y, z, yaw, cr, light, away, sleep, downed, downedLeft, pitch);
                return;
            }
            Dispatch(id, r, msg);
        }

        private void ClientReceive(NetPacketReader r, Msg msg)
        {
            switch (msg)
            {
                case Msg.Welcome:
                {
                    LocalId = r.GetByte();
                    var hostName = r.GetString();
                    Remotes[Protocol.HostId] = new RemoteInfo { Id = Protocol.HostId, Name = hostName, Welcomed = true };
                    OnRemoteJoined?.Invoke(Protocol.HostId, hostName);
                    int n = r.GetByte();
                    for (int i = 0; i < n; i++)
                    {
                        var oid = r.GetByte(); var on = r.GetString();
                        Remotes[oid] = new RemoteInfo { Id = oid, Name = on, Welcomed = true };
                        OnRemoteJoined?.Invoke(oid, on);
                    }
                    Status = L.T($"Bağlı: {hostName}", $"Connected: {hostName}");
                    CoopMod.Hud(L.T($"{hostName} oyununa bağlandın", $"You joined {hostName}'s game"));
                    break;
                }
                case Msg.Reject:
                {
                    var reason = L.Pick(r.GetString());
                    CoopMod.Log("Rejected: " + reason);
                    Stop();
                    Status = reason;
                    CoopMod.Hud(reason);
                    break;
                }
                case Msg.PlayerJoined:
                {
                    var id = r.GetByte(); var name = r.GetString();
                    Remotes[id] = new RemoteInfo { Id = id, Name = name, Welcomed = true };
                    OnRemoteJoined?.Invoke(id, name);
                    CoopMod.Hud(L.T($"{name} katıldı", $"{name} joined"));
                    break;
                }
                case Msg.PlayerLeft:
                {
                    var id = r.GetByte();
                    if (Remotes.TryGetValue(id, out var info)) CoopMod.Hud(L.T($"{info.Name} ayrıldı", $"{info.Name} left"));
                    Remotes.Remove(id);
                    OnRemoteLeft?.Invoke(id);
                    break;
                }
                case Msg.PlayerState:
                {
                    var id = r.GetByte(); var scene = r.GetString();
                    float x = r.GetFloat(), y = r.GetFloat(), z = r.GetFloat(), yaw = r.GetFloat(); bool cr = r.GetBool();
                    byte light = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;
                    bool away = r.AvailableBytes > 0 && r.GetBool();
                    byte sleep = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;
                    byte downed = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;
                    ushort downedLeft = r.AvailableBytes > 1 ? r.GetUShort() : (ushort)0;
                    sbyte pitch = r.AvailableBytes > 0 ? r.GetSByte() : (sbyte)0;
                    if (id != LocalId) CoopMod.Instance.Players.ApplyState(id, scene, x, y, z, yaw, cr, light, away, sleep, downed, downedLeft, pitch);
                    break;
                }
                default:
                    Dispatch(Protocol.HostId, r, msg);
                    break;
            }
        }

        private byte AllocId()
        {
            for (int i = 0; i < 250; i++)
            {
                var id = _nextId++;
                if (_nextId >= 250) _nextId = 1;
                if (!Remotes.ContainsKey(id)) return id;
            }
            return 249;
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) => CoopMod.Log($"Socket error {socketError}");
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }
}
