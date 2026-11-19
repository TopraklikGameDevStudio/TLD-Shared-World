using System;
using System.Collections.Generic;
using System.Threading;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using NAudio.Codecs;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Proximity voice. The game plays its sound through Wwise and has Unity's own audio switched off (no microphone,
    /// no AudioSource), so the voice goes around the game: NAudio records the Windows microphone and plays the friends'
    /// voices on the default speakers. Each friend is louder the nearer they stand and comes from their side
    /// (left/right); beyond the range, or in another region, they are not heard. Push to talk (key T by default).
    /// 16 kHz mono, mu-law (8 bits a sample, 128 kbit/s while talking, nothing while quiet).
    /// </summary>
    internal sealed class VoiceChat
    {
        internal const int Rate = 16000, FrameMs = 40, FrameSamples = Rate * FrameMs / 1000;

        private readonly Network _net;
        private readonly RemotePlayers _players;
        private readonly Func<bool> _on;
        private readonly Func<float> _range;
        private readonly Func<bool> _shared;

        private WaveInEvent _in;
        private WaveOutEvent _out;
        private MixingSampleProvider _mixer;
        private readonly object _lock = new();
        private readonly Queue<byte[]> _toSend = new();
        private volatile bool _talking, _toneOn;
        private bool _started, _failedIn, _failedOut;
        private ushort _seq;

        private sealed class Stream
        {
            public BufferedWaveProvider Buffer;
            public VolumeSampleProvider Volume;
            public PanningSampleProvider Pan;
            public float LastHeard;
            public int Packets;
        }
        private readonly Dictionary<byte, Stream> _streams = new();

        // ---- for the state dump and the harness ----
        internal int Sent, Received, Dropped;
        internal string MicName = "-", OutName = "-", Last = "-";
        internal float TestToneUntil;
        private float _tonePhase;

        public VoiceChat(Network net, RemotePlayers players, Func<bool> on, Func<float> range, Func<bool> shared)
        {
            _net = net; _players = players; _on = on; _range = range; _shared = shared;
            _net.OnMessage += OnMessage;
            _net.OnRemoteLeft += id => { lock (_lock) { if (_streams.TryGetValue(id, out var s)) { try { _mixer?.RemoveMixerInput(s.Pan); } catch { } _streams.Remove(id); } } };
        }

        private bool On() { try { return _on(); } catch { return true; } }
        private float Range() { try { return Mathf.Clamp(_range(), 5f, 200f); } catch { return 40f; } }

        private void EnsureStarted()
        {
            if (_started) return;
            _started = true;
            try
            {
                int n = WaveInEvent.DeviceCount;
                if (n > 0)
                {
                    try { MicName = WaveInEvent.GetCapabilities(0).ProductName; } catch { MicName = "device 0"; }
                    _in = new WaveInEvent { DeviceNumber = 0, WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = FrameMs, NumberOfBuffers = 4 };
                    _in.DataAvailable += OnMic;
                    _in.StartRecording();
                }
                else { MicName = "none"; _failedIn = true; }
            }
            catch (Exception e) { _failedIn = true; MicName = "error: " + e.Message; CoopMod.Log("Voice: the microphone could not be opened (" + e.Message + ")"); }
            try
            {
                _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)) { ReadFully = true };
                _out = new WaveOutEvent { DesiredLatency = 150 };
                _out.Init(new SampleToWaveProvider16(_mixer));
                _out.Play();
                OutName = "default speakers";
            }
            catch (Exception e) { _failedOut = true; OutName = "error: " + e.Message; CoopMod.Log("Voice: the speakers could not be opened (" + e.Message + ")"); }
            CoopMod.Log($"Voice: microphone {MicName}, playback {OutName}, push to talk {ModKeys.Talk.Name}, heard up to {Range():0} m");
        }

        /// <summary>Called every frame from the main thread.</summary>
        public void Update(bool keyHeld)
        {
            bool want = On() && _net.IsConnected && WorldSync.InGame();
            if (!want) { _talking = false; return; }
            EnsureStarted();
            bool tone = Time.realtimeSinceStartup < TestToneUntil;
            _toneOn = tone;
            _talking = (keyHeld || tone) && SafeShared();
            if (tone) MakeTone();

            // send what the microphone thread queued
            byte[][] frames;
            lock (_lock) { frames = _toSend.ToArray(); _toSend.Clear(); }
            if (frames.Length > 0)
            {
                var t = GameManager.GetPlayerTransform();
                string scene = CoopMod.CurrentScene() ?? "";
                foreach (var f in frames)
                {
                    var w = _net.Begin(Msg.Voice);
                    w.Put(_net.LocalId); w.Put(scene); w.Put(_seq++); w.PutBytesWithLength(f);
                    _net.Send(w, DeliveryMethod.Sequenced);
                    Sent++;
                }
            }

            // where each friend's voice comes from
            var me = GameManager.GetPlayerTransform();
            if (me == null) return;
            float yaw = me.eulerAngles.y;
            try { var cam = GameManager.GetVpFPSCamera(); if (cam != null) yaw = cam.transform.eulerAngles.y; } catch { }
            string here = CoopMod.CurrentScene() ?? "";
            var bodies = _players.Bodies(here);
            float range = Range(), now = Time.realtimeSinceStartup;
            lock (_lock)
            {
                foreach (var kv in _streams)
                {
                    float vol = 0f, pan = 0f;
                    foreach (var b in bodies)
                    {
                        if (b.Id != kv.Key) continue;
                        var d = b.Pos - me.position; float dist = d.magnitude;
                        if (dist < range)
                        {
                            float x = Mathf.Clamp01((dist - 2f) / (range - 2f));
                            vol = Mathf.Pow(1f - x, 1.6f);
                            float rel = (Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg - yaw) * Mathf.Deg2Rad;
                            pan = Mathf.Clamp(Mathf.Sin(rel), -1f, 1f) * Mathf.Clamp01(dist / 3f) * 0.85f;
                        }
                    }
                    kv.Value.Volume.Volume = vol;
                    kv.Value.Pan.Pan = pan;
                }
            }
        }

        private bool SafeShared() { try { return _shared(); } catch { return false; } }

        /// <summary>Microphone thread: a frame of 16-bit samples; encoded and queued only while talking.</summary>
        private void OnMic(object sender, WaveInEventArgs e)
        {
            if (!_talking || _toneOn) return;
            Encode(e.Buffer, e.BytesRecorded);
        }

        private void Encode(byte[] pcm, int bytes)
        {
            int n = bytes / 2;
            var enc = new byte[n];
            for (int i = 0; i < n; i++) enc[i] = MuLawEncoder.LinearToMuLawSample((short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)));
            lock (_lock) { if (_toSend.Count < 25) _toSend.Enqueue(enc); else Dropped++; }
        }

        /// <summary>Harness: a test tone sent as if spoken (so voice can be tried without anybody at the microphone).</summary>
        private float _toneDue;
        private void MakeTone()
        {
            float now = Time.realtimeSinceStartup;
            if (_toneDue == 0f || now - _toneDue > 0.5f) _toneDue = now;
            while (_toneDue <= now)
            {
                var pcm = new byte[FrameSamples * 2];
                for (int i = 0; i < FrameSamples; i++)
                {
                    short v = (short)(6000 * MathF.Sin(_tonePhase)); _tonePhase += 2f * MathF.PI * 330f / Rate;
                    pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8);
                }
                Encode(pcm, pcm.Length);
                _toneDue += FrameMs / 1000f;
            }
        }

        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.Voice) return;
            try
            {
                byte id = r.GetByte(); string scene = r.GetString(); ushort seq = r.GetUShort(); byte[] data = r.GetBytesWithLength();
                if (_net.Mode == NetMode.Host)
                {
                    id = from;
                    var w = _net.Begin(Msg.Voice); w.Put(id); w.Put(scene); w.Put(seq); w.PutBytesWithLength(data);
                    _net.Send(w, DeliveryMethod.Sequenced, from);
                }
                if (id == _net.LocalId || !On() || data == null || data.Length == 0 || data.Length > 4000) return;
                if (scene != CoopMod.CurrentScene()) return;
                EnsureStarted();
                if (_mixer == null) return;
                Stream s;
                lock (_lock)
                {
                    if (!_streams.TryGetValue(id, out s))
                    {
                        var buf = new BufferedWaveProvider(new WaveFormat(Rate, 16, 1)) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(800) };
                        var vol = new VolumeSampleProvider(buf.ToSampleProvider()) { Volume = 0f };
                        var pan = new PanningSampleProvider(vol) { Pan = 0f };
                        s = new Stream { Buffer = buf, Volume = vol, Pan = pan };
                        _streams[id] = s;
                        _mixer.AddMixerInput(pan);
                    }
                }
                var pcm = new byte[data.Length * 2];
                for (int i = 0; i < data.Length; i++) { short v = MuLawDecoder.MuLawToLinearSample(data[i]); pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8); }
                s.Buffer.AddSamples(pcm, 0, pcm.Length);
                s.LastHeard = Time.realtimeSinceStartup; s.Packets++;
                Received++;
            }
            catch (Exception e) { if (Received == 0) CoopMod.Log("Voice message: " + e.Message); }
        }

        internal void Shutdown()
        {
            try { _in?.StopRecording(); _in?.Dispose(); } catch { }
            try { _out?.Stop(); _out?.Dispose(); } catch { }
        }

        internal void WriteState(System.Text.StringBuilder sb)
        {
            sb.Append("voice_setting: ").Append(On() ? "on" : "off").Append('\n');
            sb.Append("voice_key: ").Append(ModKeys.Talk.Name).Append(" (").Append(ModKeys.Talk.Note).Append(")\n");
            int devs = -1; try { devs = WaveInEvent.DeviceCount; } catch { }
            sb.Append("voice_mic_devices: ").Append(devs).Append('\n');
            sb.Append("voice_mic: ").Append(MicName).Append(_failedIn ? " (failed)" : "").Append('\n');
            sb.Append("voice_out: ").Append(OutName).Append(_failedOut ? " (failed)" : "").Append(_out != null ? $" state={_out.PlaybackState}" : "").Append('\n');
            sb.Append("voice_talking: ").Append(_talking).Append('\n');
            sb.Append("voice_sent: ").Append(Sent).Append(" dropped=").Append(Dropped).Append('\n');
            sb.Append("voice_received: ").Append(Received).Append('\n');
            lock (_lock)
                foreach (var kv in _streams)
                    sb.Append("voice_from: ").Append(kv.Key).Append(FormattableString.Invariant($" packets={kv.Value.Packets} buffered_ms={kv.Value.Buffer.BufferedDuration.TotalMilliseconds:0} volume={kv.Value.Volume.Volume:0.00} pan={kv.Value.Pan.Pan:0.00} heard_ago={Time.realtimeSinceStartup - kv.Value.LastHeard:0.0}")).Append('\n');
        }
    }
}
