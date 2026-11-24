using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Il2Cpp;
using Il2CppTLD.Boot;
using Il2CppTLD.IntBackedUnit;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Test mode only: runs commands from a text file so co-op checks can be made without a person playing.
    /// Command file (shared by every game process on this PC): UserData/TLDCoop-cmd.txt
    ///   one command per line:  &lt;seq&gt; &lt;target&gt; &lt;command&gt; [args...]
    ///   seq = ever-increasing whole number, target = host | client | off | all | pid:&lt;n&gt;
    /// Each command writes one "TEST &lt;seq&gt; &lt;command&gt;: OK|FAIL ..." line to this process's own log.
    /// "state" / "snap" write a dump of what this game sees to UserData/TLDCoop-state-&lt;pid&gt;[-tag].txt
    /// UserData/TLDCoop-latest-&lt;role&gt;.txt (role = host | client | off) names the process that has that role now:
    ///   pid, log file name, mod version (rewritten at start and whenever the role changes).
    /// UserData/TLDCoop-progress-&lt;pid&gt;.txt holds "seq: N": every command for this process up to N is finished.
    ///   "waitpeer N [timeout]" waits until every other running game is past N, so two queues cannot drift apart.
    ///   It FAILs when it finds no other running game at all (two games with the same role share one latest-* file).
    /// </summary>
    internal sealed class TestHarness
    {
        private sealed class Cmd { public long Seq; public string Name; public string[] Args; public string Target; }
        private struct Res { public bool Ok; public string Detail; public float After; public Func<Res> Then; public Action AfterLog; public bool Skipped; }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly CoopMod _mod;
        private readonly string _dir, _cmdPath;
        private readonly int _pid = Environment.ProcessId;
        private long _lastSeq;
        private DateTime _lastWrite; private long _lastLen = -1;
        private float _nextPoll;
        private readonly Queue<Cmd> _queue = new();
        private Cmd _running; private Func<Res> _then; private float _thenAt;
        private bool _loggedError, _announced;
        private static bool _guard = true;
        // so the two games can be found and kept in step without looking through the folder
        private NetMode _latestMode = (NetMode)(-1);
        private long _progressWritten = long.MinValue;
        private float _nextLatest;

        public TestHarness(CoopMod mod)
        {
            _mod = mod;
            _dir = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
            _cmdPath = Path.Combine(_dir, "TLDCoop-cmd.txt");
            // whatever is already in the file belongs to an earlier run: never replay it
            try { Read(prime: true); } catch { }
            CoopMod.Log($"Test harness ready: pid {_pid}, test mode {(CoopMod.AllowVersionMismatch ? "on" : "off")}, commands after seq {_lastSeq} in {_cmdPath}");
            StartWatchdog();
        }

        // A game whose main thread hangs cannot run "quit". The watchdog is a thread of its own that only looks for
        // "killgame" lines (same file, same targets) and ends the process, so a stuck game never needs a hand to close it.
        private static volatile string _roleNow = "off";
        private long _killFloor;
        private void StartWatchdog()
        {
            if (!CoopMod.AllowVersionMismatch) return;
            _killFloor = _lastSeq;
            var t = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        System.Threading.Thread.Sleep(2000);
                        if (!File.Exists(_cmdPath)) continue;
                        string text;
                        using (var fs = new FileStream(_cmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                        int end = text.LastIndexOf('\n');
                        if (end < 0) continue;
                        foreach (var raw in text.Substring(0, end).Split('\n'))
                        {
                            var tok = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (tok.Length < 3 || tok[2].ToLowerInvariant() != "killgame") continue; // not "kill": that one kills an animal
                            if (!long.TryParse(tok[0], NumberStyles.Integer, Inv, out var seq) || seq <= _killFloor) continue;
                            string tgt = tok[1].ToLowerInvariant();
                            bool me = tgt == "all" || tgt == _roleNow || (tgt.StartsWith("pid:") && int.TryParse(tgt.Substring(4), out var p) && p == _pid);
                            if (!me) continue;
                            try { File.WriteAllText(Path.Combine(_dir, $"TLDCoop-killed-{_pid}.txt"), $"seq {seq} at {DateTime.Now:HH:mm:ss}\n"); } catch { }
                            System.Diagnostics.Process.GetCurrentProcess().Kill();
                        }
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "TLDCoop test watchdog" };
            t.Start();
        }

        private static Res Ok(string detail = "") => new() { Ok = true, Detail = detail };
        private static Res Fail(string reason) => new() { Ok = false, Detail = reason };
        private static Res Later(float after, Func<Res> then) => new() { After = after, Then = then };

        public void Update()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                _roleNow = _mod.Net.Mode == NetMode.Host ? "host" : _mod.Net.Mode == NetMode.Client ? "client" : "off";
                if (now >= _nextPoll) { _nextPoll = now + 0.5f; Read(prime: false); }
                Step(now);
                if (_mod.Net.Mode != _latestMode || now >= _nextLatest) WriteLatest(now);
                WriteProgress();
            }
            catch (Exception e) { if (!_loggedError) { _loggedError = true; CoopMod.Log("Test harness: " + e); } }
        }

        // ---------------- who is who, and how far each game is ----------------
        private static string RoleName(NetMode m) => m == NetMode.Host ? "host" : m == NetMode.Client ? "client" : "off";
        private string LatestPath(string role) => Path.Combine(_dir, $"TLDCoop-latest-{role}.txt");
        private string ProgressPath(int pid) => Path.Combine(_dir, $"TLDCoop-progress-{pid}.txt");

        /// <summary>UserData/TLDCoop-latest-&lt;role&gt;.txt names this process (pid, log file, version); rewritten when the role changes.</summary>
        private void WriteLatest(float now)
        {
            var mode = _mod.Net.Mode;
            bool changed = mode != _latestMode;
            _nextLatest = now + 15f;
            try
            {
                if (changed && (int)_latestMode >= 0)
                {
                    // the file of the role we no longer have must not keep pointing at us
                    var oldPath = LatestPath(RoleName(_latestMode));
                    if (File.Exists(oldPath) && ReadKey(oldPath, "pid") == _pid.ToString(Inv)) File.Delete(oldPath);
                }
                _latestMode = mode;
                var path = LatestPath(RoleName(mode));
                // another running game with the same role (two "off" games before one hosts) keeps its file
                if (!changed && File.Exists(path) && ReadKey(path, "pid") != _pid.ToString(Inv)) return;
                var text = $"pid: {_pid}\nrole: {RoleName(mode)}\nlog: {Path.GetFileName(CoopMod.OwnLog)}\nmod_version: {CoopMod.ModVersion}\nwritten: {DateTime.Now.ToString("HH:mm:ss", Inv)}\n";
                var tmp = path + "." + _pid + ".tmp";
                File.WriteAllText(tmp, text);
                File.Copy(tmp, path, true); File.Delete(tmp);
            }
            catch { }
        }

        private static string ReadKey(string path, string key)
        {
            try
            {
                foreach (var line in File.ReadAllLines(path))
                    if (line.StartsWith(key + ":")) return line.Substring(key.Length + 1).Trim();
            }
            catch { }
            return null;
        }

        /// <summary>Everything addressed to this game up to this seq is finished.</summary>
        private long Progress() => _running != null && _then != null ? _running.Seq - 1 : _queue.Count > 0 ? _queue.Peek().Seq - 1 : _lastSeq;

        private void WriteProgress()
        {
            long p = Progress();
            if (p == _progressWritten) return;
            try
            {
                var path = ProgressPath(_pid);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, $"seq: {p}\nrole: {RoleName(_mod.Net.Mode)}\n");
                File.Copy(tmp, path, true); File.Delete(tmp);
                _progressWritten = p;
            }
            catch { }
        }

        /// <summary>Other running games of this test (named in the latest-* files) and how far each one is.</summary>
        private List<(int Pid, string Role, long Seq)> Peers()
        {
            // Every game writes its own TLDCoop-progress-<pid>.txt (seq and role). The TLDCoop-latest-<role>.txt files
            // cannot be used here: two games with the same role (both still "off") overwrite each other's entry.
            var list = new List<(int, string, long)>();
            string[] files;
            try { files = Directory.GetFiles(_dir, "TLDCoop-progress-*.txt"); } catch { return list; }
            foreach (var path in files)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!int.TryParse(name.Substring("TLDCoop-progress-".Length), out var pid) || pid == _pid) continue;
                bool running = false;
                try { using var pr = System.Diagnostics.Process.GetProcessById(pid); running = !pr.HasExited && pr.ProcessName.ToLowerInvariant().Contains("tld"); } catch { }
                if (!running) continue;
                long seq = -1;
                long.TryParse(ReadKey(path, "seq") ?? "-1", NumberStyles.Integer, Inv, out seq);
                list.Add((pid, ReadKey(path, "role") ?? "?", seq));
            }
            return list;
        }

        private Res WaitPeer(Cmd c)
        {
            var a = c.Args;
            if (a.Length < 1 || !long.TryParse(a[0], NumberStyles.Integer, Inv, out var want)) return Fail("usage: waitpeer <seq> [timeoutSeconds=30]");
            float timeout = a.Length > 1 && TryF(a[1], out var tv) ? Mathf.Clamp(tv, 1f, 600f) : 30f;
            float start = Time.realtimeSinceStartup;
            Func<Res> tick = null;
            tick = () =>
            {
                var peers = Peers();
                string behind = "";
                foreach (var p in peers) if (p.Seq < want) behind += $" {p.Role}({p.Pid}) at {p.Seq}";
                float waited = Time.realtimeSinceStartup - start;
                if (peers.Count == 0)
                {
                    // nobody to wait for is not "everybody is past it": two games with the same role (both "off") share one
                    // TLDCoop-latest-<role>.txt, so the one that owns the file cannot see the other.
                    // (looked for a few seconds first: a latest-* file can be unreadable for a moment while it is rewritten)
                    if (waited >= Mathf.Min(timeout, 3f))
                        return Fail($"found no other running game after {F(waited, "0.0")} s");
                    return Later(0.2f, tick);
                }
                if (behind.Length == 0) return Ok($"{peers.Count} other game(s) are past {want} after {F(waited, "0.0")} s");
                if (waited >= timeout) return Fail($"after {F(waited, "0.0")} s still behind {want}:{behind}");
                return Later(0.2f, tick);
            };
            return tick();
        }

        // ---------------- command file ----------------
        private void Read(bool prime)
        {
            if (!File.Exists(_cmdPath)) return;
            var fi = new FileInfo(_cmdPath);
            if (!prime && fi.LastWriteTimeUtc == _lastWrite && fi.Length == _lastLen) return;
            string text;
            try
            {
                using var fs = new FileStream(_cmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
            }
            catch (Exception) { return; } // being written right now: next poll
            _lastWrite = fi.LastWriteTimeUtc; _lastLen = fi.Length;

            // only complete lines (ending in a newline) count, so a half-written file is never misread
            int end = text.LastIndexOf('\n');
            if (end < 0) return;
            bool testMode = CoopMod.AllowVersionMismatch;
            long max = _lastSeq;
            foreach (var raw in text.Substring(0, end).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 3 || !long.TryParse(tok[0], NumberStyles.Integer, Inv, out var seq)) continue;
                if (seq <= _lastSeq) continue;
                if (seq > max) max = seq;
                if (prime || !testMode) continue;
                // a pid target is fixed; a role target (host/client/off) is decided when the command's turn comes,
                // because an earlier command of the same file may change this game's role
                string tgt = tok[1].ToLowerInvariant();
                if (tgt.StartsWith("pid:") && !Matches(tgt)) continue;
                var args = new string[tok.Length - 3];
                Array.Copy(tok, 3, args, 0, args.Length);
                _queue.Enqueue(new Cmd { Seq = seq, Name = tok[2].ToLowerInvariant(), Args = args, Target = tgt });
            }
            _lastSeq = max;
            if (!prime && testMode && !_announced && _queue.Count > 0) { _announced = true; CoopMod.Log($"Test harness active (pid {_pid}), reading {_cmdPath}"); }
        }

        private bool Matches(string target)
        {
            target = target.ToLowerInvariant();
            var mode = _mod.Net.Mode;
            if (target == "all") return true;
            if (target == "host") return mode == NetMode.Host;
            if (target == "client") return mode == NetMode.Client;
            if (target == "off") return mode == NetMode.Off;
            if (target.StartsWith("pid:")) return int.TryParse(target.Substring(4), out var p) && p == _pid;
            return false;
        }

        // ---------------- running ----------------
        private void Step(float now)
        {
            if (_then != null)
            {
                if (now < _thenAt) return;
                var f = _then; _then = null;
                Finish(_running, Safe(f));
                return;
            }
            if (_queue.Count == 0) return;
            _running = _queue.Dequeue();
            var c = _running;
            if (c.Target != null && !Matches(c.Target)) { Finish(c, new Res { Ok = true, Detail = null, Skipped = true }); return; } // not for this game (now)
            Finish(c, Safe(() => Exec(c)));
        }

        private static Res Safe(Func<Res> f)
        {
            try { return f(); }
            catch (Exception e) { return Fail("exception: " + e.Message); }
        }

        private void Finish(Cmd c, Res r)
        {
            if (r.Then != null) { _then = r.Then; _thenAt = Time.realtimeSinceStartup + r.After; return; }
            if (!r.Skipped) CoopMod.Log($"TEST {c.Seq} {c.Name}: {(r.Ok ? "OK" : "FAIL")} {r.Detail}".TrimEnd());
            try { r.AfterLog?.Invoke(); } catch { }
        }

        // Commands (seq target name args):
        //   any time : nop, wait s, waitpeer seq [timeout], handoff on|off|auto, sleepsync on|off|auto, ownership on|off|auto, sleepui 0-7 [alpha], host, join [addr], leave,
        //              save, quit, killgame (the watchdog ends this game), touch, presence active|away|auto, guard on|off, skipboot [timeout], spawn,
        //              loadslot slot|newest, menu [save] [direct], state [invFilter], snap tag [invFilter]
        //   in game  : kit, give prefab [n], tp x y z, tpfriend, tpanimal kind [dist], tpcarcass kind [dist],
        //              tpcontainer [name] [dist], tpfire [dist], look yaw [pitch], hit kind dmg, kill kind, scare kind,
        //              own kind [nearest], disown kind, aggro kind [attack|stalk], struggleinfo,
        //              harvest kind meat kg|hide|gut, pickup prefab, drop prefab, door [maxDist], esc on|off,
        //              wake, fatigue percentTired,
        //              hurt percent, down, revive [seconds], giveup, downtimer seconds, afflict bleed|sprain|none,
        //              figure show [dist] | figure pose <stand|walk|run|sneak|crouch|down|sleep|sit|light [1-4]|look up|look down> [light [1-4]] |
        //                figure colors n | figure off  (a preview of the friend's figure, no connection needed),
        //                figure kind auto|skinned|primitives (which body the preview gets; it is built again),
        //                figure wear hat|scarf|mitts on|off | figure tint <index> <r> <g> <b> | figure tint clear |
        //                figure look mine|off (the preview wears what the local player wears / the fixed outfit again),
        //              look (no number after it: what the local player wears, as it is sent to the friends),
        //              wear <prefab> (adds that clothing item and puts it on),
        //              whistle | whistle here [dist] [bearing] | whistle try EVENT [dist] | whistle sound EVENTS|auto | whistle near metres,
        //              handover [seconds] | handover lose offer|answer
        //   as a player (refused while paused or a menu is open, unless "guard off"):
        //              csearch [maxDist], cput prefab [maxDist], ctake prefab [maxDist],
        //              fire start [hours] [maxDist] | fire feed prefab [maxDist] | fire out [maxDist],
        //              equip prefab, stow, light on|off, throw, sleep hours [ui], passtime hours [ui]
        private Res Exec(Cmd c)
        {
            var a = c.Args;
            // show on screen what the test is doing, so whoever watches the game can follow along
            try
            {
                if (c.Name != "wait" && c.Name != "waitpeer" && c.Name != "snap" && c.Name != "state" && c.Name != "nop" && c.Name != "touch" && c.Name != "presence" && WorldSync.InGame())
                    HUDMessage.AddMessage($"TEST {c.Seq}: {c.Name} {string.Join(" ", a)}", 4f, false, true);
            }
            catch { }
            switch (c.Name)
            {
                case "nop": return Ok();
                case "ground": return Ok(_mod.Items.GroundSummary(a.Length > 0 ? a[0] : null));
                case "groundreset": _mod.Items.GroundAskAgain(); return Ok("asking again");
                case "ghost":
                {
                    // ghost <prefab>: an item in this game only (not sent), 2 m ahead: for testing that ground items follow the host
                    if (a.Length < 1) return Fail("usage: ghost <prefab>");
                    var t = GameManager.GetPlayerTransform();
                    ItemSync.Applying = true;
                    try
                    {
                        var gi = GearItem.InstantiateGearItem(a[0]);
                        if (gi == null) return Fail("unknown prefab " + a[0]);
                        gi.transform.position = t.position + t.forward * 2f + Vector3.up * 0.3f;
                        gi.gameObject.SetActive(true);
                        return Ok($"{a[0]} made here only at {P(gi.transform.position)}");
                    }
                    finally { ItemSync.Applying = false; }
                }
                case "wait":
                {
                    float s = a.Length > 0 && TryF(a[0], out var v) ? Mathf.Clamp(v, 0f, 600f) : 1f;
                    return Later(s, () => Ok($"{F(s, "0.#")} s"));
                }
                case "host":
                    if (_mod.Net.Mode == NetMode.Host) return Ok("already hosting");
                    _mod.Host();
                    return Later(0.3f, () => _mod.Net.Mode == NetMode.Host ? Ok(_mod.Net.Status) : Fail(_mod.Net.Status));
                case "join":
                {
                    if (_mod.Net.Mode == NetMode.Client && (_mod.Transfer.Busy || _mod.Net.Connecting)) return Fail("a join is already under way");
                    string addr = a.Length > 0 ? a[0] : _mod.JoinAddress;
                    _mod.Join(addr);
                    return Ok("connecting to " + addr);
                }
                case "leave": _mod.Disconnect(); return Ok();
                case "killgame": return Later(4f, () => Fail("the watchdog should have ended this game by now"));
                case "waitworld":
                {
                    // a joining game: wait until the host's world of this connection is here and ready to be loaded
                    float t = a.Length > 0 && TryF(a[0], out var tv) ? Mathf.Clamp(tv, 1f, 600f) : 60f;
                    float t0 = Time.realtimeSinceStartup;
                    return Until(t, () => _mod.Transfer.FreshWorldHere,
                        ok => ok ? Ok($"the host's world is here after {F(Time.realtimeSinceStartup - t0, "0.0")} s ({_mod.Transfer.ClientStateName})")
                                 : Fail($"no world from the host within {F(t, "0")} s (join state {_mod.Transfer.ClientStateName}, connected {_mod.Net.IsConnected})"), 0.25f);
                }
                case "save":
                    if (!WorldSync.InGame()) return Fail("not in game");
                    GameManager.ForceSaveGame();
                    return Ok("save requested");
                case "quit":
                    return new Res
                    {
                        Ok = true, Detail = "closing the game",
                        AfterLog = () =>
                        {
                            // on the start-up notices the game's own quit never finishes: end the process ourselves if it is still here
                            try
                            {
                                new System.Threading.Thread(() =>
                                {
                                    System.Threading.Thread.Sleep(6000);
                                    try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
                                }) { IsBackground = true }.Start();
                            }
                            catch { }
                            Application.Quit();
                        }
                    };
                case "skipboot":
                {
                    // the game's start-up notices wait for a key: step through them so a test can start without anybody at the PC
                    float end = Time.realtimeSinceStartup + (a.Length > 0 && TryF(a[0], out var bt) ? Mathf.Clamp(bt, 5f, 300f) : 90f);
                    int steps = 0, lastPageTries = 0;
                    bool lastPage = false;
                    _bootLogged = -1;
                    Func<Res> tick = null;
                    tick = () =>
                    {
                        string sc = CoopMod.CurrentScene();
                        if (sc == "MainMenu" || WorldSync.InGame())
                        {
                            if (lastPageTries > 0) CoopMod.Log($"skipboot: the last notice went away at step {BootStage(lastPageTries - 1)} (0 its wait ran out, 1 its finished event, 2 the panel was told to go on)");
                            return Ok($"at the main menu after {steps} step(s)");
                        }
                        if (Time.realtimeSinceStartup >= end) return Fail($"still not at the main menu (scene '{sc ?? "-"}') after {steps} step(s)");
                        try
                        {
                            var pb = UnityEngine.Object.FindObjectOfType<Panel_Boot>();
                            if (pb != null && pb.gameObject.activeInHierarchy)
                            {
                                try { pb.m_HoldLoadingUntilInputGiven = false; } catch { }
                                // the pages of the list go by with "next"; the last one (the main disclaimer) only ends on a key press.
                                // Once it has been up, "next" is never called again: it could bring that page back.
                                if (!lastPage && BootOnLastPage(pb)) lastPage = true;
                                if (!lastPage) { try { pb.OnNextDisclaimerScreen(); steps++; } catch { } }
                                else { BootPassLastPage(pb, lastPageTries++); steps++; }
                            }
                        }
                        catch { }
                        return Later(1.0f, tick);
                    };
                    return tick();
                }
                case "spawn":
                {
                    // start one more copy of this very game (the second player on the same PC), so nobody has to click for it
                    string exe = null;
                    try { exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return Fail("could not find this game's own program file");
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true };
                        var proc = System.Diagnostics.Process.Start(psi);
                        return Ok($"started {Path.GetFileName(exe)}" + (proc != null ? $" as pid {proc.Id}" : ""));
                    }
                    catch (Exception e) { return Fail("could not start another copy: " + e.Message); }
                }
                case "touch": PauseSync.Touch(); return Ok();
                case "guard":
                {
                    // guard off: skip the "paused / menu open" check before the commands that act like a player
                    string g = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                    if (g != "on" && g != "off") return Fail("usage: guard on|off");
                    _guard = g == "on";
                    return Ok(g);
                }
                case "presence":
                {
                    string v = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                    int p = v == "active" ? 1 : v == "away" ? 2 : v == "auto" ? 0 : -1;
                    if (p < 0) return Fail("usage: presence active|away|auto");
                    PauseSync.TestPresence = p;
                    return Ok(v);
                }
                case "waitpeer": return WaitPeer(c);
                case "handoff":
                {
                    string v = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                    int p = v == "on" ? 1 : v == "off" ? 2 : v == "auto" ? 0 : -1;
                    if (p < 0) return Fail("usage: handoff on|off|auto");
                    SceneHandoff.TestOverride = p;
                    return Ok(v);
                }
                case "sleepsync":
                {
                    // shared sleep on / off whatever the setting says (off = sleeping works as before 0.7.0)
                    string v = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                    int p = v == "on" ? 1 : v == "off" ? 2 : v == "auto" ? 0 : -1;
                    if (p < 0) return Fail("usage: sleepsync on|off|auto");
                    SleepSync.TestOverride = p;
                    return Ok(v);
                }
                case "ownership":
                {
                    // animal ownership on / off whatever the setting says (off = every animal stays with the host, as before 0.9.0)
                    string v = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                    int p = v == "on" ? 1 : v == "off" ? 2 : v == "auto" ? 0 : -1;
                    if (p < 0) return Fail("usage: ownership on|off|auto");
                    AnimalSync.TestOwnOverride = p;
                    return Ok($"{v} (animal ownership is {(_mod.Animals.OwnershipOn ? "on" : "off")} on this game now)");
                }
                case "sleepui": { if (a.Length < 1 || !int.TryParse(a[0], out var m)) return Fail("usage: sleepui <0-7> [alpha]"); SleepSync.TestUiMode = m; if (a.Length > 1 && TryF(a[1], out var al)) SleepSync.TestUiAlpha = Mathf.Clamp01(al); return Ok($"mode {m}, alpha {F(SleepSync.TestUiAlpha, "0.00")}"); }
                case "menu": return MenuCmd(a);
                case "blobs":
                {
                    string v = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                    if (v != "on" && v != "off") return Fail("usage: blobs on|off");
                    SceneHandoff.DumpOn = v == "on";
                    return Ok(v);
                }
                case "loadslot": return LoadSlot(a);
                case "state": return WriteState(c.Seq, null, a.Length > 0 ? a[0] : null);
                case "snap": return a.Length == 0 ? Fail("usage: snap <tag> [invFilter]") : WriteState(c.Seq, a[0], a.Length > 1 ? a[1] : null);
            }

            // everything below needs a loaded game
            if (!WorldSync.InGame()) return Fail("not in game");
            var pm = GameManager.GetPlayerManagerComponent();
            var me = GameManager.GetPlayerTransform();
            if (pm == null || me == null) return Fail("no player");
            switch (c.Name)
            {
                case "kit": _mod.TestHuntingKit(); return Ok();
                case "give":
                {
                    if (a.Length < 1) return Fail("usage: give <prefab> [n]");
                    int n = a.Length > 1 && int.TryParse(a[1], out var v) ? Mathf.Max(1, v) : 1;
                    var gi = pm.AddItemCONSOLE(a[0], n, 1f);
                    return gi != null ? Ok($"{a[0]} x{n}") : Fail("the game did not create " + a[0]);
                }
                case "tp":
                {
                    if (a.Length < 3 || !TryF(a[0], out var x) || !TryF(a[1], out var y) || !TryF(a[2], out var z)) return Fail("usage: tp <x> <y> <z>");
                    pm.TeleportPlayer(new Vector3(x, y, z), me.rotation);
                    return Later(0.3f, () => Ok("now at " + P(GameManager.GetPlayerTransform().position)));
                }
                case "tpitem":
                {
                    if (a.Length < 1) return Fail("usage: tpitem <prefab>");
                    GearItem best = null; float bestD = float.MaxValue;
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GearItem>())
                    {
                        if (!Loose(g) || ItemSync.PrefabName(g.gameObject).IndexOf(a[0], StringComparison.OrdinalIgnoreCase) < 0) continue;
                        float d = Vector3.Distance(g.transform.position, me.position);
                        if (d < bestD) { bestD = d; best = g; }
                    }
                    if (best == null) return Fail("no loose " + a[0] + " here");
                    var at = best.transform.position;
                    pm.TeleportPlayer(at + new Vector3(0.6f, 0.3f, 0f), me.rotation);
                    var tag = $"{ItemSync.PrefabName(best.gameObject)} at {P(at)} guid={ItemSync.GuidOf(best.gameObject) ?? "-"} was {F(bestD, "0.0")} m away";
                    return Later(0.5f, () => Ok(tag));
                }
                case "tpfriend":
                {
                    string scene = CoopMod.CurrentScene(), other = null;
                    foreach (var r in _mod.Net.Remotes.Values)
                    {
                        if (!_mod.Players.TryGetState(r.Id, out var s, out var pos)) continue;
                        if (s != scene) { other = s; continue; }
                        pm.TeleportPlayer(pos + new Vector3(2f, 0.5f, 0f), me.rotation);
                        return Later(0.3f, () => Ok($"next to player {r.Id}, now at " + P(GameManager.GetPlayerTransform().position)));
                    }
                    return Fail(other != null ? "the other player is in another scene: " + other : "no other player known yet");
                }
                case "tpanimal":
                {
                    if (a.Length < 1) return Fail("usage: tpanimal <kind> [dist]");
                    float d = a.Length > 1 && TryF(a[1], out var v) ? Mathf.Clamp(v, 1f, 100f) : 6f;
                    var ai = NearestAnimal(a[0], me.position, true, out _);
                    if (ai == null) return Fail("no live animal like " + a[0]);
                    return StandNear(pm, me, ai.transform, d, ItemSync.TestKind(ai.gameObject));
                }
                case "tpcarcass":
                {
                    if (a.Length < 1) return Fail("usage: tpcarcass <kind> [dist]");
                    float d = a.Length > 1 && TryF(a[1], out var v) ? Mathf.Clamp(v, 0.5f, 100f) : 2f;
                    var b = NearestCarcass(a[0], me.position, float.MaxValue, out _);
                    if (b == null) return Fail("no carcass like " + a[0]);
                    return StandNear(pm, me, b.transform, d, ItemSync.TestKind(b.gameObject));
                }
                case "hit":
                case "kill":
                {
                    if (a.Length < 1) return Fail($"usage: {c.Name} <kind>" + (c.Name == "hit" ? " <damage>" : ""));
                    float dmg = 100000f;
                    if (c.Name == "hit" && (a.Length < 2 || !TryF(a[1], out dmg))) return Fail("usage: hit <kind> <damage>");
                    var ai = NearestAnimal(a[0], me.position, true, out var dist);
                    if (ai == null) return Fail("no live animal like " + a[0]);
                    string kind = ItemSync.TestKind(ai.gameObject);
                    float before = ai.m_CurrentHP; bool puppet = AnimalSync.IsPuppet(ai.Pointer);
                    string owner = OwnerText(ai);
                    // exactly what a player's hit does; on a puppet this goes through the forwarding patch to the game that runs the animal
                    ai.ApplyDamage(dmg, 0f, DamageSource.Player, "capsule_torso");
                    return Later(1.5f, () =>
                    {
                        string after;
                        try { after = ai == null ? "gone" : $"{F(ai.m_CurrentHP, "0.#")} mode={ai.GetAiMode()}"; } catch { after = "gone"; }
                        return Ok($"{kind} dist={F(dist, "0.0")} puppet={puppet}{owner} damage={F(dmg, "0.#")} hp={F(before, "0.#")}->{after}");
                    });
                }
                case "scare":
                {
                    if (a.Length < 1) return Fail("usage: scare <kind>");
                    var ai = NearestAnimal(a[0], me.position, true, out var dist);
                    if (ai == null) return Fail("no live animal like " + a[0]);
                    string kind = ItemSync.TestKind(ai.gameObject); bool puppet = AnimalSync.IsPuppet(ai.Pointer);
                    string owner = OwnerText(ai);
                    ai.SetAiMode(AiMode.Flee);
                    return Later(1.5f, () =>
                    {
                        string mode; try { mode = ai == null ? "gone" : ai.GetAiMode().ToString(); } catch { mode = "gone"; }
                        return Ok($"{kind} dist={F(dist, "0.0")} puppet={puppet}{owner} mode={mode}{AnimalSync.TestInfo(ai)}");
                    });
                }
                case "own": case "disown": return OwnCmd(c.Name == "own", a, me.position);
                case "aggro": return AggroCmd(a, me.position);
                case "struggleinfo": return Ok(_mod.Animals.StruggleLine() + " last=[" + _mod.Animals.LastStruggle + "]");
                case "harvest": return Harvest(a, me.position);
                case "pickup": return Pickup(a, pm, me.position);
                case "drop":
                {
                    if (a.Length < 1) return Fail("usage: drop <prefab>");
                    var inv = GameManager.GetInventoryComponent();
                    var gi = inv != null ? inv.GetBestGearItemWithName(a[0]) : null;
                    if (gi == null) return Fail(a[0] + " is not in the inventory");
                    var dropped = gi.Drop(1, false, true, true); // at the player's feet, like dropping from the bag; our drop patch sends it
                    if (dropped == null) return Fail("the game refused to drop it");
                    return Later(0.5f, () =>
                    {
                        try { return Ok($"{ItemSync.PrefabName(dropped.gameObject)} at {P(dropped.transform.position)} guid={ItemSync.GuidOf(dropped.gameObject) ?? "-"}"); }
                        catch { return Fail("the dropped item vanished"); }
                    });
                }
                case "door":
                {
                    float max = a.Length > 0 && TryF(a[0], out var v) ? v : 6f;
                    if (_mod.Downed.LocalDowned) return Fail("the player is down: a downed player cannot use a door");
                    LoadScene best = null; float bestD = float.MaxValue;
                    foreach (var ls in UnityEngine.Object.FindObjectsOfType<LoadScene>())
                    {
                        if (ls == null || !ls.gameObject.activeInHierarchy) continue;
                        float d = Vector3.Distance(ls.transform.position, me.position);
                        if (d < bestD) { bestD = d; best = ls; }
                    }
                    if (best == null) return Fail("no scene door in this scene");
                    if (bestD > max) return Fail($"nearest door is {F(bestD, "0.0")} m away at {P(best.transform.position)}");
                    string to = "?"; try { to = best.GetSceneToLoad(); } catch { }
                    string from = CoopMod.CurrentScene();
                    best.Activate();
                    return Ok($"using the door {F(bestD, "0.0")} m away: {from} -> {to}");
                }
                case "tpdoor":
                {
                    // tpdoor <sceneNameLike> [dist=1.5]: stand in front of the nearest door that leads to such a scene
                    if (a.Length < 1) return Fail("usage: tpdoor <sceneNameLike> [dist]");
                    float dd = a.Length > 1 && TryF(a[1], out var dv) ? Mathf.Clamp(dv, 0.5f, 20f) : 1.5f;
                    LoadScene pick = null; float pickD = float.MaxValue; string pickTo = null;
                    foreach (var ls in UnityEngine.Object.FindObjectsOfType<LoadScene>())
                    {
                        try
                        {
                            if (ls == null || !ls.gameObject.activeInHierarchy) continue;
                            string to2 = ls.GetSceneToLoad();
                            if (string.IsNullOrEmpty(to2) || to2.IndexOf(a[0], StringComparison.OrdinalIgnoreCase) < 0) continue;
                            float d2 = Vector3.Distance(ls.transform.position, me.position);
                            if (d2 < pickD) { pickD = d2; pick = ls; pickTo = to2; }
                        }
                        catch { }
                    }
                    if (pick == null) return Fail("no door to a scene like " + a[0]);
                    return StandNear(pm, me, pick.transform, dd, "door to " + pickTo);
                }
                case "esc": return Esc(a);
                case "wake":
                {
                    // what the player's F8 does, also when playing alone
                    byte was = SleepSync.LocalSleep(); float left = SleepSync.HoursLeft();
                    if (was == 0) return Fail("not sleeping or passing time");
                    string no = _mod.Sleep.WakeUp("test command");
                    if (no != null) return Fail(no);
                    string what = was == 1 ? "sleep" : "pass time";
                    return Until(3f, () => SleepSync.LocalSleep() == 0, ok => ok
                        ? Ok($"{what} ended with {F(left)} h left, time_accel={F(_mod.Sleep.TimeAccel(), "0.0")}")
                        : Fail($"still in {what} ({F(SleepSync.HoursLeft())} h left)"));
                }
                case "fatigue":
                {
                    // how tired the player is: 0 = rested, 100 = exhausted (a rested player's sleep can end at once)
                    if (a.Length < 1 || !TryF(a[0], out var pct)) return Fail("usage: fatigue <percentTired 0-100>");
                    var fat = GameManager.GetFatigueComponent();
                    if (fat == null) return Fail("no fatigue in this game");
                    float before = fat.m_CurrentFatigue;
                    fat.m_CurrentFatigue = Mathf.Clamp01(pct / 100f) * fat.m_MaxFatigue;
                    return Ok($"{F(before, "0.0")} -> {F(fat.m_CurrentFatigue, "0.0")} of {F(fat.m_MaxFatigue, "0.0")}");
                }
                case "look": return a.Length == 0 || !TryF(a[0], out _) ? LookCmd() : Look(a);
                case "wear": return WearCmd(a, pm);
                case "hurt": return HurtCmd(a);
                case "heal":
                {
                    // heal [percent=100]: condition to that much (test worlds carry low condition from earlier rounds)
                    float want = a.Length > 0 && TryF(a[0], out var hv) ? Mathf.Clamp(hv, 1f, 100f) : 100f;
                    var cond = GameManager.GetConditionComponent();
                    if (cond == null) return Fail("no condition in this game");
                    if (_mod.Downed.LocalDowned) return Fail("the player is down: lift them first");
                    float before = DownedSync.ConditionPercent();
                    cond.m_CurrentHP = want / 100f * cond.m_MaxHP;
                    return Ok($"condition {F(before, "0.0")} -> {F(DownedSync.ConditionPercent(), "0.0")} %");
                }
                case "down": return DownCmd();
                case "revive": return ReviveCmd(a);
                case "giveup": return GiveUpCmd();
                case "downtimer":
                {
                    // the time a downed local player has left (so the end of the timer can be tried without waiting it out)
                    if (a.Length < 1 || !TryF(a[0], out var secs) || secs < 0f) return Fail("usage: downtimer <seconds>");
                    string no = _mod.Downed.SetSecondsLeft(secs);
                    return no != null ? Fail(no) : Ok($"{F(_mod.Downed.SecondsLeft, "0.0")} s left");
                }
                case "afflict": return AfflictCmd(a);
                case "figure": return FigureCmd(a);
                case "whistle": return WhistleCmd(a);
                case "handover": return HandoverCmd(a, pm);
                case "findmodels":
                {
                    // findmodels [filter]: addressable keys and skinned meshes in the scene that might be people (written to TLDCoop-models.txt)
                    string flt = a.Length > 0 ? a[0] : "";
                    var sbm = new StringBuilder();
                    int nkeys = 0, shown = 0;
                    int nsk = 0;
                    try
                    {
                        foreach (var sk in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
                        {
                            if (sk == null) continue; nsk++;
                            var root = sk.transform.root;
                            int bones = 0; try { bones = sk.bones.Length; } catch { }
                            sbm.Append($"skinned: root={root.name} obj={sk.gameObject.name} bones={bones} mesh={(sk.sharedMesh != null ? sk.sharedMesh.name : "-")} pos={P(sk.transform.position)} active={sk.gameObject.activeInHierarchy}\n");
                        }
                    }
                    catch (Exception e) { sbm.Append("skinned error: ").Append(e.Message).Append('\n'); }
                    try { File.WriteAllText(Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "TLDCoop-models.txt"), sbm.ToString()); } catch { }
                    return Ok($"{nkeys} addressable keys ({shown} shown), {nsk} skinned meshes: TLDCoop-models.txt");
                }
                case "addr":
                {
                    // addr [text]: what the clipboard (or the given text) would give as a join address, and this PC's home-network address
                    string t = a.Length > 0 ? string.Join(" ", a) : null;
                    string got = t != null ? CoopMod.LooksLikeAddress(t) : CoopMod.ClipboardAddress();
                    return Ok($"{(t != null ? "text" : "clipboard")} -> {got ?? "not an address"}; lan={CoopMod.LanAddress() ?? "-"}; join address now {_mod.JoinAddress}");
                }
                case "voicetone":
                {
                    // voicetone [seconds=3]: send a test tone as if this player talked
                    float vs = a.Length > 0 && TryF(a[0], out var vv) ? Mathf.Clamp(vv, 0.5f, 30f) : 3f;
                    int s0 = _mod.Voice.Sent;
                    _mod.Voice.TestToneUntil = Time.realtimeSinceStartup + vs;
                    return Later(vs + 0.3f, () => _mod.Voice.Sent > s0 ? Ok($"sent {_mod.Voice.Sent - s0} voice frames") : Fail("no voice frames went out (not connected, or not in the shared world?)"));
                }
                case "voiceprobe":
                {
                    // voiceprobe: can this game record the microphone and play sound through Unity's own audio (the game itself uses Wwise)?
                    string devs = "?";
                    try { var d = Microphone.devices; devs = d == null ? "null" : d.Length + ": " + string.Join(" | ", d); } catch (Exception e) { devs = "error " + e.Message; }
                    AudioClip rec = null; string recNote;
                    try { rec = Microphone.Start(null, true, 2, 16000); recNote = rec != null ? "started" : "null clip"; } catch (Exception e) { recNote = "error " + e.Message; }
                    GameObject go = null; AudioSource src = null; string playNote;
                    try
                    {
                        go = new GameObject("TLDCoop_VoiceProbe");
                        src = go.AddComponent<AudioSource>();
                        var clip = AudioClip.Create("probe", 16000, 1, 16000, false);
                        var data = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(16000);
                        for (int i = 0; i < 16000; i++) data[i] = 0.2f * Mathf.Sin(i * 2f * Mathf.PI * 440f / 16000f);
                        clip.SetData(data, 0);
                        src.clip = clip; src.volume = 0.6f; src.Play();
                        playNote = "play called";
                    }
                    catch (Exception e) { playNote = "error " + e.Message; }
                    return Later(1.5f, () =>
                    {
                        string recRes = recNote, playRes = playNote;
                        try
                        {
                            if (rec != null)
                            {
                                int pos = Microphone.GetPosition(null);
                                var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(rec.samples * rec.channels);
                                rec.GetData(buf, 0);
                                float peak = 0f; for (int i = 0; i < buf.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(buf[i]));
                                recRes = $"position={pos} samples={rec.samples} freq={rec.frequency} peak={F(peak, "0.0000")} recording={Microphone.IsRecording(null)}";
                                Microphone.End(null);
                            }
                        }
                        catch (Exception e) { recRes += " / read error " + e.Message; }
                        try { if (src != null) playRes = $"isPlaying={src.isPlaying} time={F(src.time, "0.00")} listener={(UnityEngine.Object.FindObjectOfType<AudioListener>() != null)}"; } catch (Exception e) { playRes += " / " + e.Message; }
                        string cfg = "?"; try { cfg = $"rate={AudioSettings.outputSampleRate} speaker={AudioSettings.speakerMode}"; } catch (Exception e) { cfg = "error " + e.Message; }
                        try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
                        return Ok($"mic devices {devs}; record {recRes}; unity audio {playRes}; settings {cfg}");
                    });
                }
                case "worktest":
                {
                    // worktest <game minutes> [real seconds=3]: speed the clock up the way the game's jobs do (TimeOfDay.AccelerateTime)
                    float wm = a.Length > 0 && TryF(a[0], out var wv) ? Mathf.Clamp(wv, 1f, 600f) : 10f;
                    float ws = a.Length > 1 && TryF(a[1], out var wsv) ? Mathf.Clamp(wsv, 0.5f, 30f) : 3f;
                    var tod = GameManager.GetTimeOfDayComponent();
                    float h0 = GameManager.GetUniStorm().GetElapsedHours();
                    tod.AccelerateTime(wm, ws);
                    return Later(ws + 2f, () => Ok($"clock +{F((GameManager.GetUniStorm().GetElapsedHours() - h0) * 60f, "0.0")} game min; busy={_mod.Work.Busy} left={F(_mod.Work.LeftSeconds, "0")} s"));
                }
                case "workabort": return _mod.Work.Abort() ? Ok("stopped waiting") : Fail("not waiting");
                case "lang":
                {
                    // lang tr|en|auto: the mod's language for this run (not saved)
                    if (a.Length > 0) { L.Setting = a[0]; L.ForceRecheck(); }
                    return Ok($"setting={L.Setting} english={L.En} game={L.GameLanguage}");
                }
                case "coopmenu":
                {
                    // coopmenu open|click <id>|back: the ORTAK OYUN menu as a player would use it
                    string w0 = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                    string err = CoopMenu.Test(w0, a.Length > 1 ? a[1] : "");
                    if (err != null) return Fail(err);
                    return Later(0.5f, () => { var sbm = new StringBuilder(); CoopMenu.WriteState(sbm); return Ok(sbm.ToString().Replace("\n", " ; ")); });
                }
                case "mapsurvey":
                {
                    // mapsurvey: map the spot here as charcoal does (the game's own check), and tell the friends as a finished survey would
                    var map = InterfaceManager.GetPanel<Panel_Map>();
                    if (map == null) return Fail("no map panel");
                    string sc = CoopMod.CurrentScene() ?? "";
                    string b0 = MapShare.Count(map, sc);
                    try { map.DoNearbyDetailsCheck(map.m_DetailSurveyRadiusMeters, true, false, Vector3.zero, true); } catch (Exception e) { return Fail("map check: " + e.Message); }
                    _mod.Map.LocalSurvey();
                    return Later(0.5f, () => Ok($"mapped here (radius {F(map.m_DetailSurveyRadiusMeters, "0")} m): elements {b0} -> {MapShare.Count(map, sc)}; {_mod.Map.Last}"));
                }
                case "shoot":
                {
                    // shoot rifle|revolver|flare|arrow [aim]: as if this player's weapon fired now (aim: look at the nearest friend's chest first)
                    string wk = a.Length > 0 ? a[0].ToLowerInvariant() : "rifle";
                    byte wb = wk == "revolver" ? FriendlyFire.Revolver : wk == "flare" ? FriendlyFire.Flare : wk == "arrow" ? FriendlyFire.Arrow : FriendlyFire.Rifle;
                    if (a.Length > 1 && a[1].ToLowerInvariant().StartsWith("aim"))
                    {
                        float hy = a.Length > 2 && TryF(a[2], out var hv) ? hv : 1.1f;
                        var bodies = _mod.Players.Bodies(CoopMod.CurrentScene() ?? "");
                        if (bodies.Count == 0) return Fail("no friend in this scene to aim at");
                        var cam0 = GameManager.GetMainCamera();
                        var to = bodies[0].Pos + Vector3.up * hy - cam0.transform.position;
                        float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, pitch = -Mathf.Atan2(to.y, new Vector2(to.x, to.z).magnitude) * Mathf.Rad2Deg;
                        GameManager.GetVpFPSCamera().SetAngle(yaw, pitch);
                        return Later(0.4f, () => { string r1 = _mod.Fire2.Shot(wb); return r1.StartsWith("hit") ? Ok(r1) : Fail(r1); });
                    }
                    string r0 = _mod.Fire2.Shot(wb);
                    return r0.StartsWith("hit") ? Ok(r0) : Fail(r0);
                }
                case "tpcontainer":
                {
                    // tpcontainer [nameFilter] [dist=1.5]
                    string like = a.Length > 0 && !TryF(a[0], out _) ? a[0] : null;
                    int di = like == null ? 0 : 1;
                    float d = a.Length > di && TryF(a[di], out var v) ? Mathf.Clamp(v, 0.5f, 20f) : 1.5f;
                    var c2 = NearestContainer(me.position, like, out _);
                    if (c2 == null) return Fail(like != null ? "no container like " + like : "no container in this scene");
                    // a kit on the wall or a shelf: stand on the floor we are on, unless it is on another storey
                    return StandNear(pm, me, c2.transform, d, ItemSync.PrefabName(c2.gameObject), FloorRef(me.position.y, c2.transform.position.y));
                }
                case "tpfire":
                {
                    float d = a.Length > 0 && TryF(a[0], out var v) ? Mathf.Clamp(v, 0.5f, 20f) : 2f;
                    var f2 = NearestFire(me.position, out _);
                    if (f2 == null) return Fail("no fireplace, stove or campfire in this scene");
                    return StandNear(pm, me, f2.transform, d, ItemSync.PrefabName(f2.gameObject), FloorRef(me.position.y, f2.transform.position.y));
                }
            }

            // everything below changes the world the way a player would: not while paused or in a menu
            switch (c.Name)
            {
                case "csearch": case "cput": case "ctake": case "fire":
                case "equip": case "stow": case "light": case "throw":
                case "sleep": case "passtime":
                    string why = _guard ? Blocked(pm) : null;
                    if (why != null) return Fail(why + " (guard off skips this check)");
                    break;
                default: return Fail("unknown command");
            }
            switch (c.Name)
            {
                case "csearch": return CSearch(a, me.position);
                case "cput": return CPut(a, pm, me.position);
                case "ctake": return CTake(a, pm, me.position);
                case "fire": return FireCmd(a, pm, me.position);
                case "equip": return Equip(a, pm);
                case "stow": return Stow(pm);
                case "light": return LightCmd(a, pm);
                case "throw": return ThrowCmd(pm);
                case "sleep": return SleepCmd(a, me.position);
                case "passtime": return PassTimeCmd(a);
            }
            return Fail("unknown command");
        }

        // ---------------- start-up notices ----------------
        /// <summary>The list of notices is through and the main disclaimer (which waits for any key) is up.</summary>
        private static bool BootOnLastPage(Panel_Boot pb)
        {
            try { var md = pb.m_MainDisclaimer; if (md != null && md.activeInHierarchy) return true; } catch { }
            try { var list = pb.m_DisclaimerScreenList; if (list != null && pb.m_DisclaimerScreenIndex >= list.Length) return true; } catch { }
            return false;
        }

        private static int _bootLogged = -1;
        private static int BootStage(int tries) => tries < 1 ? 0 : tries < 2 ? 1 : 2;
        /// <summary>
        /// End the last notice's wait without a key, one step stronger each second:
        /// its own timer runs out, then its "finished" event is fired, then the panel is told it may show the main menu.
        /// </summary>
        private static void BootPassLastPage(Panel_Boot pb, int tries)
        {
            var waits = new List<InterruptablePause>();
            try
            {
                foreach (var w in UnityEngine.Object.FindObjectsOfType<InterruptablePause>())
                    if (w != null && w.isActiveAndEnabled) waits.Add(w);
            }
            catch { }
            int stage = BootStage(tries);
            if (stage != _bootLogged)
            {
                _bootLogged = stage;
                string what = "";
                foreach (var w in waits)
                {
                    try
                    {
                        what += $" [{w.gameObject.name}: waited {F(w.m_Elapsed, "0.0")} s of {F(w.m_MinPause, "0.0")}-{F(w.m_MaxPause, "0.0")}";
                        var ev = w.m_OnFinished;
                        int n = ev != null ? ev.GetPersistentEventCount() : 0;
                        for (int i = 0; i < n; i++) what += $", then {ev.GetPersistentTarget(i)?.name}.{ev.GetPersistentMethodName(i)}";
                        what += "]";
                    }
                    catch { }
                }
                bool loaded = false, hold = false; try { loaded = pb.m_SceneLoaded; hold = pb.m_HoldLoadingUntilInputGiven; } catch { }
                CoopMod.Log($"skipboot: last notice, step {stage} ({(stage == 0 ? "let its wait run out" : stage == 1 ? "fire its finished event" : "tell the panel to go on")}); "
                    + $"{waits.Count} wait(s) active{what}; main menu scene loaded={loaded}, hold={hold}");
            }
            foreach (var w in waits)
            {
                try
                {
                    if (stage == 0)
                    {
                        // as if its longest wait were over: the component ends itself on its next update
                        w.m_MaxPause = 0.01f;
                        if (w.m_Elapsed < w.m_MinPause + 1f) w.m_Elapsed = w.m_MinPause + 1f;
                    }
                    else if (stage == 1) w.m_OnFinished?.Invoke(); // exactly what the component does when a key is pressed
                }
                catch { }
            }
            if (stage == 2)
            {
                // nothing left to end: go on to the loading page and let the main menu scene come up as soon as it is loaded
                if (tries == 2) { try { pb.ShowBootLoading(); } catch { } }
                try { pb.ReadyToActivateScene = true; } catch { }
            }
        }

        // ---------------- sleep, pass time, back to the main menu ----------------
        /// <summary>The nearest bed, or bedroll that is laid out, a player could lie down in.</summary>
        private static Bed NearestBed(Vector3 from, float max, out float dist, out string why)
        {
            Bed best = null; dist = float.MaxValue; why = null; int seen = 0, rolled = 0;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<Bed>())
            {
                try
                {
                    if (b == null || !b.gameObject.activeInHierarchy) continue;
                    seen++;
                    bool roll = false; try { roll = b.m_Bedroll != null; } catch { }
                    if (roll && b.GetState() != BedRollState.Placed) { rolled++; continue; } // still rolled up: not a bed yet
                    try { if (b.IsOccupied()) continue; } catch { }
                    float d = Vector3.Distance(b.transform.position, from);
                    if (d < dist) { dist = d; best = b; }
                }
                catch { }
            }
            if (best == null) { why = seen == 0 ? "no bed or bedroll in this scene" : $"no usable bed in this scene ({rolled} bedroll(s) still rolled up)"; return null; }
            if (dist > max) { why = $"nearest bed is {F(dist, "0.0")} m away at {P(best.transform.position)} (it must be within {F(max, "0")} m)"; best = null; }
            return best;
        }

        private string SleepInfo() =>
            $"hours_left={F(SleepSync.HoursLeft())} time_accel={F(_mod.Sleep.TimeAccel(), "0.0")} everyone_sleeping={_mod.Sleep.EveryoneSleeping}";

        private Res SleepCmd(string[] a, Vector3 from)
        {
            if (a.Length < 1 || !int.TryParse(a[0], NumberStyles.Integer, Inv, out var hours) || hours < 1 || hours > 24)
                return Fail("usage: sleep <hours 1-24> [ui]");
            bool ui = a.Length > 1 && a[1].ToLowerInvariant() == "ui";
            var rest = GameManager.GetRestComponent();
            if (rest == null) return Fail("no rest system in this game");
            if (SleepSync.LocalSleep() != 0) return Fail("already sleeping or passing time (wake first)");
            var bed = NearestBed(from, 10f, out float dist, out string why);
            if (bed == null) return Fail(why);
            string name = ItemSync.PrefabName(bed.gameObject), how = "Rest.BeginSleeping";
            Panel_Rest panel = null;
            float start = Time.realtimeSinceStartup;
            bool began = false; // seen asleep once is enough: a short sleep that everybody shares can be over within a second
            Func<Res> check = null;
            check = () =>
            {
                if (began || rest.IsSleeping()) return Later(1f, () => Ok($"sleeping {hours} h in {name} {F(dist, "0.0")} m away via {how}: {SleepInfo()}"));
                if (Time.realtimeSinceStartup - start < 3.5f) return Later(0.25f, check);
                try { if (panel != null && panel.IsEnabled()) panel.Enable(false); } catch { }
                return Fail($"the game did not start the sleep via {how} (not tired enough? try: fatigue 80)");
            };
            if (ui)
            {
                // through the rest screen: open it on this bed, set the hours, press its Sleep button
                try { panel = InterfaceManager.GetPanel<Panel_Rest>(); } catch { }
                if (panel == null) return Fail("no rest screen in this game");
                panel.StartRest(bed);
                if (!panel.IsEnabled()) panel.Enable(true);
                panel.m_Bed = bed;
                how = "the rest screen (Panel_Rest.OnRest)";
                return Later(0.5f, () => { panel.m_SleepHours = hours; panel.OnRest(); began = rest.IsSleeping(); return Later(0.2f, check); });
            }
            // the call the rest screen ends in, with the game's own fade and speed-up
            rest.BeginSleeping(bed, hours, hours);
            began = rest.IsSleeping();
            return Later(0.2f, check);
        }

        private Res PassTimeCmd(string[] a)
        {
            if (a.Length < 1 || !TryF(a[0], out var hours) || hours < 0.25f || hours > 24f) return Fail("usage: passtime <hours 0.25-24> [ui]");
            bool ui = a.Length > 1 && a[1].ToLowerInvariant() == "ui";
            var pt = GameManager.GetPassTime();
            if (pt == null) return Fail("no pass time in this game");
            if (SleepSync.LocalSleep() != 0) return Fail("already sleeping or passing time (wake first)");
            string how = "PassTime.Begin";
            Panel_Rest panel = null;
            Func<Res> check = null;
            float start = Time.realtimeSinceStartup;
            bool began = false;
            check = () =>
            {
                if (began || pt.IsPassingTime()) return Later(1f, () => Ok($"passing {F(hours)} h via {how}: {SleepInfo()}"));
                if (Time.realtimeSinceStartup - start < 3.5f) return Later(0.25f, check);
                try { if (panel != null && panel.IsEnabled()) panel.Enable(false); } catch { }
                return Fail($"the game did not start passing time via {how}");
            };
            if (ui)
            {
                // through the pass-time screen, as opened from the radial menu, and its own start button
                try { panel = InterfaceManager.GetPanel<Panel_Rest>(); } catch { }
                if (panel == null) return Fail("no rest screen in this game");
                panel.Enable(true, true);
                how = "the pass-time screen (Panel_Rest.DoPassTime)";
                return Later(0.5f, () =>
                {
                    bool took = panel.DoPassTime(hours);
                    if (!took) { try { panel.Enable(false); } catch { } return Fail("the pass-time screen refused (the game shows why on screen)"); }
                    began = pt.IsPassingTime();
                    return Later(0.2f, check);
                });
            }
            pt.Begin(hours, null); // what the screen's button ends in; no bed, as from the radial menu
            began = pt.IsPassingTime();
            return Later(0.2f, check);
        }

        /// <summary>Leave the loaded game for the main menu without closing the game.</summary>
        private static Res MenuCmd(string[] a)
        {
            bool save = false, direct = false;
            foreach (var x in a) { string v = x.ToLowerInvariant(); if (v == "save") save = true; else if (v == "direct") direct = true; else return Fail("usage: menu [save] [direct]"); }
            bool AtMenu() => CoopMod.CurrentScene() == "MainMenu" && !WorldSync.InGame();
            if (AtMenu()) return Ok("already at the main menu");
            if (!WorldSync.InGame()) return Fail("no game is loaded");
            string from = CoopMod.CurrentScene(), how = null;
            if (save) { try { GameManager.ForceSaveGame(); } catch (Exception e) { return Fail("save: " + e.Message); } }
            Func<Res> leave = () =>
            {
                if (!direct)
                {
                    // what the pause menu's Quit does once the player has confirmed it
                    try
                    {
                        var pause = InterfaceManager.GetPanel<Panel_PauseMenu>();
                        if (pause != null) { pause.DoQuitGame(); how = "the pause menu's quit (Panel_PauseMenu.DoQuitGame)"; }
                    }
                    catch (Exception e) { CoopMod.Log("Test harness: the pause menu would not quit (" + e.Message + "), asking the game directly"); how = null; }
                }
                if (how == null)
                {
                    try { GameManager.LoadMainMenu(); how = "GameManager.LoadMainMenu"; }
                    catch (Exception e) { return Fail("the game would not go to the main menu: " + e.Message); }
                }
                float start = Time.realtimeSinceStartup; bool pushed = direct;
                Func<Res> tick = null;
                tick = () =>
                {
                    float waited = Time.realtimeSinceStartup - start;
                    if (AtMenu()) return Ok($"at the main menu after {F(waited, "0.0")} s via {how} (left {from}, {(save ? "saved first" : "not saved by this command")})");
                    if (waited >= 90f) return Fail($"still not at the main menu after 90 s via {how} (scene '{CoopMod.CurrentScene() ?? "-"}', in game {WorldSync.InGame()})");
                    // the pause menu's way did nothing at all: ask the game directly, once
                    if (!pushed && waited >= 10f && WorldSync.InGame() && CoopMod.CurrentScene() == from)
                    {
                        pushed = true;
                        try { GameManager.LoadMainMenu(); how += ", then GameManager.LoadMainMenu"; } catch { }
                    }
                    return Later(0.5f, tick);
                };
                return Later(0.5f, tick);
            };
            return save ? Later(3f, leave) : leave();
        }

        // ---------------- the friend's figure, the whistle, handing an item over ----------------
        /// <summary>
        /// figure show|pose|colors|off: a preview of the human figure in front of the player, to be looked at in screenshots;
        /// kind: which body it gets (skinned or basic shapes); wear, tint, look: what it wears.
        /// </summary>
        private Res FigureCmd(string[] a)
        {
            const string usage = "usage: figure show [dist] | figure pose <stand|walk|run|sneak|crouch|down|sleep|sit|light [1-4]|look up|look down> [light [1-4]] | figure colors <n> | figure off"
                + " | figure kind auto|skinned|primitives | figure wear hat|scarf|mitts on|off | figure tint <index> <r> <g> <b> | figure tint clear | figure look mine|off";
            var pl = _mod.Players;
            switch (a.Length > 0 ? a[0].ToLowerInvariant() : "")
            {
                case "show":
                {
                    float d = a.Length > 1 && TryF(a[1], out var v) ? Mathf.Clamp(v, 0.5f, 60f) : 3f;
                    string no = pl.PreviewShow(d);
                    if (no != null) return Fail(no);
                    return Later(0.6f, () =>
                    {
                        string line = pl.PreviewLine();
                        return line.Contains("figure parts=")
                            ? Ok($"{line} shader={Figure.ShaderName} errors={RemotePlayers.FigureErrors} | {Figure.Kind}")
                            : Fail($"{line} (errors={RemotePlayers.FigureErrors}, last: {RemotePlayers.FigureLastError})");
                    });
                }
                case "pose":
                {
                    var words = new string[Math.Max(0, a.Length - 1)];
                    Array.Copy(a, 1, words, 0, words.Length);
                    int errors = RemotePlayers.FigureErrors;
                    string no = pl.PreviewPose(words);
                    if (no != null) return Fail(no);
                    // long enough for the figure to have glided into the pose (and for a walk to have got going)
                    return Later(2.0f, () => RemotePlayers.FigureErrors == errors ? Ok(pl.PreviewLine()) : Fail($"{pl.PreviewLine()} (new errors: {RemotePlayers.FigureLastError})"));
                }
                case "colors": case "colours":
                {
                    if (a.Length < 2 || !int.TryParse(a[1], out var n)) return Fail("usage: figure colors <0-" + (Figure.Sets - 1) + ">");
                    return Ok(pl.PreviewColours(n));
                }
                case "off": return Ok(pl.PreviewOff() ? "preview figure removed" : "there was no preview figure");
                case "kind":
                {
                    // which body the preview figure gets: the skinned one and the basic shapes can be compared in one session
                    string v = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                    int k = v == "auto" ? Figure.KindAuto : v == "skinned" ? Figure.KindSkinned : v == "primitives" ? Figure.KindPrimitives : -1;
                    if (k < 0) return Fail("usage: figure kind auto|skinned|primitives");
                    pl.PreviewKindSet(k);
                    if (!pl.PreviewOn) return Ok($"{v} (no preview figure yet: figure show builds it that way)");
                    int errors = RemotePlayers.FigureErrors;
                    return Later(0.8f, () =>
                    {
                        string line = pl.PreviewLine();
                        bool skinned = line.Contains("kind=skinned");
                        string text = $"{v}: {Figure.Kind} | mesh_error={Figure.MeshError} | {line} | api: {SkinnedBody.ApiText}";
                        bool ok = line.Contains("figure parts=") && RemotePlayers.FigureErrors == errors
                            && (k != Figure.KindSkinned || skinned) && (k != Figure.KindPrimitives || !skinned);
                        return ok ? Ok(text) : Fail(text + (RemotePlayers.FigureErrors != errors ? $" (new errors: {RemotePlayers.FigureLastError})" : ""));
                    });
                }
                case "wear":
                {
                    string on = a.Length > 2 ? a[2].ToLowerInvariant() : "";
                    if (a.Length < 3 || (on != "on" && on != "off")) return Fail("usage: figure wear hat|scarf|mitts on|off");
                    string no = pl.PreviewWear(a[1], on == "on");
                    return no != null ? Fail(no) : Ok(pl.PreviewLine());
                }
                case "tint":
                {
                    if (a.Length > 1 && a[1].ToLowerInvariant() == "clear")
                    {
                        string no = pl.PreviewTintClear();
                        return no != null ? Fail(no) : Ok(pl.PreviewLine());
                    }
                    // r g b: 0 to 1, or 0 to 255 (any number above 1 means the latter)
                    if (a.Length < 5 || !int.TryParse(a[1], out var idx) || !TryF(a[2], out var r) || !TryF(a[3], out var g) || !TryF(a[4], out var b))
                        return Fail("usage: figure tint <index 0-" + (Figure.Colours - 1) + "> <r> <g> <b> (0-1 or 0-255) | figure tint clear");
                    if (r > 1f || g > 1f || b > 1f) { r /= 255f; g /= 255f; b /= 255f; }
                    string no2 = pl.PreviewTint(idx, Figure.Rgb(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b)));
                    return no2 != null ? Fail(no2) : Ok($"{Figure.ColourName(idx)}: {pl.PreviewLine()}");
                }
                case "look":
                {
                    // the preview wears what the local player wears right now: clothes -> colours -> figure, without a second game
                    string v = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                    if (v == "off") { string no = pl.PreviewLook(null); return no != null ? Fail(no) : Ok(pl.PreviewLine()); }
                    if (v != "mine") return Fail("usage: figure look mine|off");
                    if (!pl.PreviewOn) return Fail("no preview figure (use: figure show)");
                    var look = _mod.Looks.LocalFigureLook(out string text);
                    if (look == null) return Fail(text);
                    string no3 = pl.PreviewLook(look);
                    return no3 != null ? Fail(no3) : Ok($"{text} | {pl.PreviewLine()}");
                }
                default: return Fail(usage);
            }
        }

        /// <summary>look: what the local player wears, read from the game now, as it is sent to the friends.</summary>
        private Res LookCmd()
        {
            string text = _mod.Looks.LocalText(out bool ok);
            text += $" | api: {LookSync.ApiText} | setting={(_mod.Looks.On ? "on" : "off")} sent={_mod.Looks.Sent} received={_mod.Looks.Received}";
            return ok ? Ok(text) : Fail(text);
        }

        /// <summary>
        /// wear prefab: adds that clothing item (the game's own console call) and puts it on with the game's own
        /// PlayerManager.PutOnClothingItem, on the outermost of the item's layers that is free (if none is, on its
        /// outermost layer: what is worn there comes off, as in the clothing screen).
        /// </summary>
        private Res WearCmd(string[] a, PlayerManager pm)
        {
            if (a.Length < 1) return Fail("usage: wear <prefab>");
            var gi = pm.AddItemCONSOLE(a[0], 1, 1f);
            if (gi == null) return Fail("the game did not create " + a[0]);
            ClothingItem ci = null;
            try { ci = gi.m_ClothingItem; } catch { }
            if (ci == null) return Fail(a[0] + " is not clothing (it is in the inventory now)");
            ClothingRegion region = ci.m_Region;
            ClothingLayer min = ci.m_MinLayer, max = ci.m_MaxLayer, layer = max;
            for (int l = (int)max; l >= (int)min && l >= 0; l--)
            {
                GearItem there = null;
                try { there = pm.GetClothingInSlot(region, (ClothingLayer)l); } catch { }
                if (there == null) { layer = (ClothingLayer)l; break; }
            }
            pm.PutOnClothingItem(gi, layer);
            // the look is read about once a second: long enough for it to have gone out to the friends too
            return Later(1.5f, () =>
            {
                bool worn = false;
                try { worn = pm.IsWearingClothingItem(gi); } catch { }
                string look = _mod.Looks.LocalText(out _);
                string where = $"region {region}, layers {min}..{max}, put on {layer}";
                return worn ? Ok($"{a[0]} on ({where}) | {look}") : Fail($"{a[0]} was added, but the game does not say it is worn ({where}) | {look}");
            });
        }

        /// <summary>whistle: as if the key were pressed. The other forms try the hearing side and the sound in one game.</summary>
        private Res WhistleCmd(string[] a)
        {
            var wh = _mod.Whistle;
            switch (a.Length > 0 ? a[0].ToLowerInvariant() : "")
            {
                case "":
                {
                    string no = wh.Press();
                    return no != null ? Fail(no) : Ok(wh.Last);
                }
                case "here":
                {
                    // as if a friend whistled from there: whistle here [dist=40] [compassBearing=90]
                    float d = a.Length > 1 && TryF(a[1], out var dv) ? Mathf.Clamp(dv, 0f, 1000f) : 40f;
                    float b = a.Length > 2 && TryF(a[2], out var bv) ? bv : 90f;
                    if (d > WhistleSync.Range) return Fail($"{F(d, "0")} m is beyond the {F(WhistleSync.Range, "0")} m a whistle carries");
                    int heard = wh.Heard;
                    string no = wh.TestHear(d, b);
                    if (no != null) return Fail(no);
                    return wh.Heard > heard ? Ok(wh.Last) : Fail("the whistle was not heard: " + wh.Last);
                }
                case "try":
                {
                    if (a.Length < 2) return Fail("usage: whistle try <EVENT> [dist=5]");
                    float d = a.Length > 2 && TryF(a[2], out var dv) ? Mathf.Clamp(dv, 0f, 300f) : 5f;
                    string how = wh.TestTry(a[1], d);
                    return how.StartsWith("sound ") ? Ok($"{how} at {F(d, "0.#")} m in front") : Fail(how);
                }
                case "sound":
                {
                    if (a.Length < 2) return Fail("usage: whistle sound <EVENT[,EVENT...]> | whistle sound auto");
                    wh.TestSounds = a[1].ToLowerInvariant() == "auto" ? null : a[1];
                    return Ok("sounds tried, in this order: " + wh.SoundList);
                }
                case "near":
                {
                    if (a.Length < 2 || !TryF(a[1], out var m) || m < 0f) return Fail("usage: whistle near <metres> (0 = the sound sits where the whistler really stands)");
                    wh.NearMax = m;
                    return Ok($"the farthest whistle now seems {F(m, "0.#")} m away");
                }
                default: return Fail("usage: whistle | whistle here [dist] [bearing] | whistle try <EVENT> [dist] | whistle sound <EVENTS>|auto | whistle near <metres>");
            }
        }

        /// <summary>handover [seconds]: as if the give key were pressed, then wait that long for the friend's answer. handover lose offer|answer: lose one message on purpose.</summary>
        private Res HandoverCmd(string[] a, PlayerManager pm)
        {
            var ho = _mod.Handover;
            if (a.Length > 0 && a[0].ToLowerInvariant() == "lose")
            {
                string what = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                if (what == "offer") { ho.LoseNextOffer = true; return Ok("the next offer this game sends is swallowed once"); }
                if (what == "answer") { ho.LoseNextAnswer = true; return Ok("the next answer this game sends is swallowed once"); }
                return Fail("usage: handover lose offer|answer");
            }
            if (a.Length > 0 && a[0].ToLowerInvariant() == "bag")
            {
                // handover bag <prefab> [seconds]: as if the inventory were open with that item selected
                if (a.Length < 2) return Fail("usage: handover bag <prefab> [seconds]");
                var inv = GameManager.GetInventoryComponent();
                var bi = inv != null ? inv.GetBestGearItemWithName(a[1]) : null;
                if (bi == null) return Fail(a[1] + " is not in the inventory");
                float bs = a.Length > 2 && TryF(a[2], out var bv) ? Mathf.Clamp(bv, 0.5f, 120f) : 6f;
                int g0 = ho.Given;
                ho.TestBagOpen = true; ho.TestBagItem = bi;
                string bno;
                try { bno = ho.TestPress(); } finally { ho.TestBagOpen = false; ho.TestBagItem = null; }
                if (bno != null) return Fail(bno);
                return Until(bs, () => !ho.Pending, done =>
                {
                    int left = 0; try { var inv2 = GameManager.GetInventoryComponent(); left = inv2.GetBestGearItemWithName(a[1]) != null ? 1 : 0; } catch { }
                    if (!done) return Fail($"no answer within {F(bs, "0.#")} s");
                    return ho.Given > g0 ? Ok($"{ho.Last}; still one in the bag: {left == 1}") : Fail(ho.Last);
                }, 0.1f);
            }
            float secs = a.Length > 0 && TryF(a[0], out var sv) ? Mathf.Clamp(sv, 0.5f, 120f) : 6f;
            int given = ho.Given;
            string held = Name(InHands(pm));
            string no = ho.TestPress();
            if (no != null) return Fail($"{no} (hands: {held})");
            return Until(secs, () => !ho.Pending, done =>
            {
                if (!done) return Fail($"no answer within {F(secs, "0.#")} s: {held} is still here and the hand-over keeps waiting");
                return ho.Given > given ? Ok($"{ho.Last}; hands now: {Name(InHands(pm))}") : Fail($"{ho.Last}; hands: {Name(InHands(pm))}");
            }, 0.1f);
        }

        // ---------------- downed and revive ----------------
        private string AfterDeath()
        {
            var dn = _mod.Downed;
            string at = "?"; try { at = P(GameManager.GetPlayerTransform().position); } catch { }
            return $"co-op death ({dn.RespawnReason}) at {P(dn.LastDeathPos)} in {dn.LastDeathScene}: now at {at} ({dn.RespawnPoint}), condition {F(DownedSync.ConditionPercent(), "0.0")} %, "
                + $"gear dropped={dn.LastDropped} kept={dn.LastKept}, afflictions cleared={dn.LastCured}, deaths_coop={dn.Deaths}";
        }

        /// <summary>hurt &lt;percent&gt;: take that many points of condition away through the call every injury in the game ends in.</summary>
        private Res HurtCmd(string[] a)
        {
            if (a.Length < 1 || !TryF(a[0], out var pct) || pct <= 0f) return Fail("usage: hurt <percent>");
            var cond = GameManager.GetConditionComponent();
            if (cond == null) return Fail("no condition in this game");
            var dn = _mod.Downed;
            if (dn.LocalDowned) return Fail("the player is down: nothing hurts them now");
            float max = cond.m_MaxHP, before = cond.m_CurrentHP, take = pct / 100f * max;
            // outside a shared world a deadly hit is the game's own death, and that deletes the save
            if (before - take <= 0f && !dn.Guarded) return Fail($"that would kill, and the co-op death rules are off here ({dn.GuardWhy}): not done, the game's own death deletes the save");
            int deaths = dn.Deaths;
            cond.AddHealth(-take, DamageSource.Unspecified);
            return Later(1f, () => Ok($"condition {F(before / max * 100f, "0.0")} -> {F(DownedSync.ConditionPercent(), "0.0")} %: " + (dn.Deaths > deaths ? AfterDeath() : dn.Summary())));
        }

        /// <summary>down: to zero condition through the same call, so the game itself decides the player is dead and our hook has to catch it.</summary>
        private Res DownCmd()
        {
            var cond = GameManager.GetConditionComponent();
            if (cond == null) return Fail("no condition in this game");
            var dn = _mod.Downed;
            if (dn.LocalDowned) return Fail("already down");
            if (!dn.Guarded) return Fail($"the co-op death rules are off here ({dn.GuardWhy}): not done, the game's own death deletes the save");
            int deaths = dn.Deaths, downs = dn.Downs; float before = DownedSync.ConditionPercent();
            cond.AddHealth(-(cond.m_CurrentHP + cond.m_MaxHP), DamageSource.Unspecified);
            return Until(4f, () => dn.Downs > downs && (dn.LocalDowned || dn.Deaths > deaths), ok =>
            {
                if (!ok) return Fail($"condition {F(before, "0.0")} -> {F(DownedSync.ConditionPercent(), "0.0")} %, but the player is neither down nor back from a co-op death (rules {(dn.Guarded ? "on" : "off")}: {dn.GuardWhy}; hooks: {dn.HookSummary()})");
                // (a moment more, so a move to the safe place has happened before we say where the player stands)
                return Later(0.7f, () =>
                {
                    if (dn.LocalDowned)
                    {
                        string at = "?"; try { at = P(GameManager.GetPlayerTransform().position); } catch { }
                        return Ok($"down via {dn.LastTrigger} at {at}: condition {F(DownedSync.ConditionPercent(), "0.0")} %, {F(dn.SecondsLeft, "0")} s left, can be lifted by {dn.ReviversInRange()}");
                    }
                    return Ok($"went down via {dn.LastTrigger}, then " + AfterDeath());
                });
            }, 0.1f);
        }

        /// <summary>revive [seconds]: be the helper, as if the key were held that long (default: the time needed) next to a downed friend.</summary>
        private Res ReviveCmd(string[] a)
        {
            float secs = a.Length > 0 && TryF(a[0], out var sv) ? Mathf.Clamp(sv, 0.1f, 60f) : DownedSync.ReviveHoldSeconds;
            var dn = _mod.Downed;
            int sent = dn.RevivesSent;
            string no = dn.TestHold(secs, out byte target, out float dist);
            if (no != null) return Fail(no);
            return Later(Mathf.Min(secs, DownedSync.ReviveHoldSeconds) + 0.6f, () =>
            {
                if (dn.RevivesSent == sent) return Fail($"held {F(secs, "0.0")} s next to player {target} ({F(dist, "0.0")} m): nobody was lifted ({dn.LastHelperResult})");
                return Until(5f, () => !dn.RemoteDowned(target), ok => ok
                    ? Ok($"held {F(secs, "0.0")} s next to player {target} ({F(dist, "0.0")} m): {dn.LastHelperResult}")
                    : Fail($"lifted player {target}, but this game still sees them on the ground ({dn.LastHelperResult})"), 0.1f);
            });
        }

        /// <summary>giveup: what holding the key does for a downed player.</summary>
        private Res GiveUpCmd()
        {
            var dn = _mod.Downed;
            int deaths = dn.Deaths;
            string no = dn.GiveUp();
            if (no != null) return Fail(no);
            return Until(4f, () => dn.Deaths > deaths, ok => ok ? Later(0.7f, () => Ok(AfterDeath())) : Fail("still down after giving up"), 0.1f);
        }

        /// <summary>afflict bleed|sprain|none: something a revive must keep and a co-op death must clear.</summary>
        private Res AfflictCmd(string[] a)
        {
            string k = a.Length > 0 ? a[0].ToLowerInvariant() : "";
            switch (k)
            {
                case "bleed":
                {
                    var b = GameManager.GetBloodLossComponent();
                    if (b == null) return Fail("no blood loss in this game");
                    b.BloodLossStart("TLDCoop test", true, AfflictionOptions.None);
                    return Later(0.5f, () => b.HasBloodLoss() ? Ok("bleeding") : Fail("the game did not start a blood loss"));
                }
                case "sprain":
                {
                    var s = GameManager.GetSprainedAnkleComponent();
                    if (s == null) return Fail("no sprains in this game");
                    s.SprainedAnkleStart("TLDCoop test", AfflictionOptions.None);
                    return Later(0.5f, () => s.HasSprainedAnkle() ? Ok("sprained ankle") : Fail("the game did not start a sprain"));
                }
                case "none":
                    return Ok($"{_mod.Downed.TestCureAll()} affliction(s) cleared");
            }
            return Fail("usage: afflict bleed|sprain|none");
        }

        // ---------------- animal ownership ----------------
        private string OwnerText(BaseAi ai)
        {
            try { byte o = _mod.Animals.TestOwnerOf(ai); return o == 255 ? "" : $" owner={o}"; } catch { return ""; }
        }

        private static BaseAi NearestAnimalWhere(string kind, Vector3 from, Func<BaseAi, bool> pick, out float dist)
        {
            BaseAi best = null; dist = float.MaxValue;
            foreach (var ai in UnityEngine.Object.FindObjectsOfType<BaseAi>())
            {
                try
                {
                    if (ai == null || !Like(ItemSync.TestKind(ai.gameObject), kind) || ai.GetAiMode() == AiMode.Dead || !pick(ai)) continue;
                    float d = Vector3.Distance(ai.transform.position, from);
                    if (d < dist) { dist = d; best = ai; }
                }
                catch { }
            }
            return best;
        }

        /// <summary>
        /// own &lt;kind&gt; [nearest]: ask for the nearest living animal of that kind to run on this game now, whatever the
        /// distances (it stays until "disown", or until its owner leaves or it dies). disown &lt;kind&gt;: give the nearest
        /// one that runs here back to the host. On the host both bring the nearest one a friend's game runs back home.
        /// </summary>
        private Res OwnCmd(bool own, string[] a, Vector3 from)
        {
            string name = own ? "own" : "disown";
            if (a.Length < 1) return Fail($"usage: {name} <kind>" + (own ? " [nearest]" : ""));
            var animals = _mod.Animals;
            bool host = _mod.Net.Mode == NetMode.Host;
            BaseAi ai; float dist;
            if (host || !own)
            {
                // the nearest one that this command could move: on the host one a friend's game runs, on a client one that runs here
                ai = NearestAnimalWhere(a[0], from, x => host ? !animals.TestRunsHere(x) : animals.TestMine(x), out dist);
                if (ai == null)
                {
                    var any = NearestAnimal(a[0], from, true, out _);
                    if (any == null) return Fail("no live animal like " + a[0]);
                    return Ok(host ? $"nothing to do: no {ItemSync.TestKind(any.gameObject)} runs on a friend's game (the nearest:{AnimalSync.TestInfo(any)})"
                                   : $"nothing to do: no {ItemSync.TestKind(any.gameObject)} runs on this game (the nearest:{AnimalSync.TestInfo(any)})");
                }
            }
            else
            {
                ai = NearestAnimal(a[0], from, true, out dist);
                if (ai == null) return Fail("no live animal like " + a[0]);
            }
            string kind = ItemSync.TestKind(ai.gameObject);
            float asked = Time.realtimeSinceStartup;
            string no = animals.TestOwn(ai, own, out string already);
            if (no != null) return Fail($"{kind} dist={F(dist, "0.0")}: {no}");
            if (already != null) return Ok($"{kind} dist={F(dist, "0.0")}: {already}{AnimalSync.TestInfo(ai)}");
            int res = 0; string text = "";
            return Until(8f, () => { res = animals.TestOwnResult(ai, own, asked, out text); return res != 0; }, done =>
            {
                float took = Time.realtimeSinceStartup - asked;
                if (res == 1) return Ok($"{kind} dist={F(dist, "0.0")} settled after {F(took, "0.0")} s: {text} handovers={animals.Handovers}");
                if (res == 2) return Fail($"{kind} dist={F(dist, "0.0")} refused after {F(took, "0.0")} s: {text}");
                return Fail($"{kind} dist={F(dist, "0.0")}: not settled within 8 s (now:{AnimalSync.TestInfo(ai)})");
            }, 0.1f);
        }

        /// <summary>
        /// aggro &lt;kind&gt; [attack|stalk]: make the nearest living animal of that kind notice the local player and go for
        /// them, through the game's own calls. Only on the game that runs the animal.
        /// </summary>
        private Res AggroCmd(string[] a, Vector3 from)
        {
            if (a.Length < 1) return Fail("usage: aggro <kind> [attack|stalk]");
            string how = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            // the nearest one whose AI runs on this game; if there is none, the nearest one at all says whose game to ask
            var ai = NearestAnimalWhere(a[0], from, x => _mod.Animals.TestRunsHere(x), out var dist) ?? NearestAnimal(a[0], from, true, out dist);
            if (ai == null) return Fail("no live animal like " + a[0]);
            string kind = ItemSync.TestKind(ai.gameObject), before = "?";
            try { before = ai.GetAiMode().ToString(); } catch { }
            string no = _mod.Animals.TestAggro(ai, how, out string did);
            if (no != null) return Fail($"{kind} dist={F(dist, "0.0")}: {no}");
            return Later(1f, () =>
            {
                string mode; try { mode = ai == null ? "gone" : ai.GetAiMode().ToString(); } catch { mode = "gone"; }
                bool going = mode == "Attack" || mode == "Stalking" || mode == "Struggle" || mode == "PassingAttack" || mode == "HoldGround" || mode == "HideAndSeek";
                string line = $"{kind} dist={F(dist, "0.0")} mode={before}->{mode} via {did}{(ai != null ? AnimalSync.TestInfo(ai) : "")}";
                return going ? Ok(line) : Fail(line + " (the game did not let it go for the player)");
            });
        }

        // ---------------- containers, fire, hands ----------------
        /// <summary>Why a player could not act right now (null = free to act).</summary>
        private static string Blocked(PlayerManager pm)
        {
            try { if (pm.PlayerIsDead()) return "the player is dead"; } catch { }
            try { if (DownedSync.Instance != null && DownedSync.Instance.LocalDowned) return "the player is down"; } catch { }
            try { if (GameManager.m_IsPaused || Time.timeScale <= 0f) return "the game is paused"; } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_PauseMenu>(); if (p != null && p.IsEnabled()) return "the Esc menu is open"; } catch { }
            try { if (InterfaceManager.IsOverlayActiveImmediate()) return "a menu or screen is open"; } catch { }
            try { if (Fire.IsStartingFireUIInProgress()) return "a fire is being started"; } catch { }
            return null;
        }

        /// <summary>Wait (up to timeout) until cond is true, then finish(true); finish(false) when time runs out.</summary>
        private static Res Until(float timeout, Func<bool> cond, Func<bool, Res> finish, float step = 0.25f)
        {
            float end = Time.realtimeSinceStartup + timeout;
            Func<Res> tick = null;
            tick = () =>
            {
                bool ok = false; try { ok = cond(); } catch { }
                if (ok || Time.realtimeSinceStartup >= end) return finish(ok);
                return Later(step, tick);
            };
            return Later(step, tick);
        }

        private static float? FloorRef(float myY, float targetY) => Mathf.Abs(myY - targetY) < 2.5f ? myY : (float?)null;

        private static bool IsPrefab(GearItem g, string prefab)
        {
            try { return g != null && string.Equals(ItemSync.PrefabName(g.gameObject), prefab, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        /// <summary>How many units of a kind lie in a container (everything made real first).</summary>
        private static int UnitsIn(Container c, string prefab)
        {
            int n = 0;
            try
            {
                var list = new Il2CppSystem.Collections.Generic.List<GearItem>();
                c.GetItems(list);
                for (int i = 0; i < list.Count; i++) if (IsPrefab(list[i], prefab)) n += ItemSync.UnitsOf(list[i]);
            }
            catch { }
            return n;
        }

        /// <summary>How many units of a kind the player carries.</summary>
        private static int BagUnits(string prefab)
        {
            int n = 0;
            try
            {
                var items = GameManager.GetInventoryComponent().m_Items;
                for (int i = 0; items != null && i < items.Count; i++)
                {
                    try { var g = items[i].m_GearItem; if (IsPrefab(g, prefab)) n += ItemSync.UnitsOf(g); } catch { }
                }
            }
            catch { }
            return n;
        }

        private static string Name(GearItem gi) { try { return gi == null ? "nothing" : ItemSync.PrefabName(gi.gameObject); } catch { return "nothing"; } }
        private static bool Same(GearItem x, GearItem y) { try { return x != null && y != null && x.Pointer == y.Pointer; } catch { return false; } }
        private static GearItem InHands(PlayerManager pm) { try { return pm.m_ItemInHands; } catch { return null; } }

        private static Container NearestContainer(Vector3 from, string like, out float dist)
        {
            Container best = null; dist = float.MaxValue;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Container>())
            {
                try
                {
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    if (like != null && !Like(ItemSync.PrefabName(c.gameObject), like)) continue;
                    float d = Vector3.Distance(c.transform.position, from);
                    if (d < dist) { dist = d; best = c; }
                }
                catch { }
            }
            return best;
        }

        /// <summary>The nearest container a player could use from here, or a FAIL saying why not.</summary>
        private static Container ReachContainer(string[] a, int maxArg, Vector3 from, out float dist, out string name, out Res fail)
        {
            fail = default; name = null;
            // after the fixed arguments: a number is the reach, a word picks the container by name (e.g. BackPack)
            float max = 6f; string like = null;
            for (int i = maxArg; i < a.Length; i++) { if (TryF(a[i], out var v)) max = v; else like = a[i]; }
            var c = NearestContainer(from, like, out dist);
            if (c == null) { fail = Fail(like != null ? "no container like " + like + " in this scene" : "no container in this scene"); return null; }
            name = ItemSync.PrefabName(c.gameObject);
            if (dist > max) { fail = Fail($"nearest container is {F(dist, "0.0")} m away ({name} at {P(c.transform.position)})"); return null; }
            bool locked = false;
            try { locked = c.IsLocked() || c.IsSafeLocked() || c.CanNeverBeOpened(); } catch { }
            if (locked) { fail = Fail($"{name} is locked"); return null; }
            return c;
        }

        // ItemSync compares containers within 5 m of the player; further away a change is not noticed
        private static string Far(float dist) => dist > 5f ? " (beyond the 5 m the sync watches!)" : "";

        private static Res CSearch(string[] a, Vector3 from)
        {
            var c = ReachContainer(a, 0, from, out var dist, out var name, out var fail);
            if (c == null) return fail;
            bool was = c.IsInspected();
            c.InstantiateContents();          // the loot the search would reveal
            if (!was) c.MarkAsInspected();    // what the game sets when the search bar completes
            return Later(0.4f, () => c.IsInspected()
                ? Ok($"{name} dist={F(dist, "0.0")} searched_before={was} items={ItemSync.TestContainerItems(c)}{Far(dist)}")
                : Fail($"{name} still counts as unsearched"));
        }

        private static Res CPut(string[] a, PlayerManager pm, Vector3 from)
        {
            if (a.Length < 1) return Fail("usage: cput <prefab> [maxDist]");
            var c = ReachContainer(a, 1, from, out var dist, out var name, out var fail);
            if (c == null) return fail;
            if (!c.IsInspected()) return Fail($"{name} has not been searched yet (use csearch)");
            var inv = GameManager.GetInventoryComponent();
            var gi = inv != null ? inv.GetBestGearItemWithName(a[0]) : null;
            if (gi == null) return Fail(a[0] + " is not in the inventory");
            if (Same(InHands(pm), gi)) return Fail(a[0] + " is in the hands (use stow first)");
            try { if (gi.m_ClothingItem != null && gi.m_ClothingItem.IsWearing()) return Fail(a[0] + " is being worn"); } catch { }
            try
            {
                float cap = c.m_Capacity.ToQuantity(1f), have = c.GetTotalWeightKG().ToQuantity(1f), add = gi.GetItemWeightKG(false).ToQuantity(1f);
                if (cap > 0f && have + add > cap + 0.001f) return Fail($"{name} is full ({F(have)} + {F(add)} of {F(cap)} kg)");
            }
            catch { }
            int units = ItemSync.UnitsOf(gi);
            c.InstantiateContents();
            int inBefore = UnitsIn(c, a[0]), bagBefore = BagUnits(a[0]);
            // what the container window does when an item is moved across
            inv.RemoveGear(gi.gameObject, true);
            c.AddGear(gi);
            return Later(0.7f, () =>
            {
                int inAfter = UnitsIn(c, a[0]), bagAfter = BagUnits(a[0]);
                if (inAfter != inBefore + units || bagAfter != bagBefore - units)
                {
                    string flags = "?";
                    try { flags = $"in_bag={gi.m_InPlayerInventory} in_container={gi.IsInsideContainer()}"; } catch { }
                    return Fail($"{a[0]} x{units}: container {inBefore}->{inAfter}, bag {bagBefore}->{bagAfter} ({flags})");
                }
                return Ok($"{a[0]} x{units} -> {name} dist={F(dist, "0.0")} container {inBefore}->{inAfter} bag {bagBefore}->{bagAfter} items={ItemSync.TestContainerItems(c)}{Far(dist)}");
            });
        }

        private static Res CTake(string[] a, PlayerManager pm, Vector3 from)
        {
            if (a.Length < 1) return Fail("usage: ctake <prefab> [maxDist]");
            var c = ReachContainer(a, 1, from, out var dist, out var name, out var fail);
            if (c == null) return fail;
            if (!c.IsInspected()) return Fail($"{name} has not been searched yet (use csearch)");
            c.InstantiateContents();
            var items = new Il2CppSystem.Collections.Generic.List<GearItem>();
            c.GetItems(items);
            GearItem match = null;
            for (int i = 0; i < items.Count && match == null; i++) if (IsPrefab(items[i], a[0])) match = items[i];
            if (match == null) return Fail($"{a[0]} is not in {name}: {ItemSync.TestContainerItems(c)}");
            int units = ItemSync.UnitsOf(match);
            int inBefore = UnitsIn(c, a[0]), bagBefore = BagUnits(a[0]);
            // what the container window does when an item is moved across
            c.RemoveGear(match, true);
            pm.AddItemToPlayerInventory(match, false, false);
            return Later(0.7f, () =>
            {
                int inAfter = UnitsIn(c, a[0]), bagAfter = BagUnits(a[0]);
                if (inAfter != inBefore - units || bagAfter != bagBefore + units)
                    return Fail($"{a[0]} x{units}: container {inBefore}->{inAfter}, bag {bagBefore}->{bagAfter}");
                return Ok($"{a[0]} x{units} <- {name} dist={F(dist, "0.0")} container {inBefore}->{inAfter} bag {bagBefore}->{bagAfter} items={ItemSync.TestContainerItems(c)}{Far(dist)}");
            });
        }

        private static Fire NearestFire(Vector3 from, out float dist)
        {
            Fire best = null; dist = float.MaxValue;
            foreach (var f in UnityEngine.Object.FindObjectsOfType<Fire>())
            {
                try
                {
                    if (f == null || !f.gameObject.activeInHierarchy) continue;
                    float d = Vector3.Distance(f.transform.position, from);
                    if (d < dist) { dist = d; best = f; }
                }
                catch { }
            }
            return best;
        }

        private static string FireInfo(Fire f)
        {
            try { return $"burning={f.IsBurning()} state={f.GetFireState()} left={F(f.GetRemainingLifeTimeHours())}"; }
            catch (Exception e) { return "? " + e.Message; }
        }

        private static Res FireCmd(string[] a, PlayerManager pm, Vector3 from)
        {
            const string usage = "usage: fire start [hours=1] [maxDist=8] | fire feed <prefab> [maxDist=8] | fire out [maxDist=8]";
            if (a.Length < 1) return Fail(usage);
            string sub = a[0].ToLowerInvariant();
            if (sub != "start" && sub != "feed" && sub != "out") return Fail(usage);
            if (sub == "feed" && a.Length < 2) return Fail(usage);
            int maxArg = sub == "out" ? 1 : 2;
            float max = a.Length > maxArg && TryF(a[maxArg], out var mv) ? mv : 8f;
            var f = NearestFire(from, out var dist);
            if (f == null) return Fail("no fireplace, stove or campfire in this scene");
            string name = ItemSync.PrefabName(f.gameObject);
            if (dist > max) return Fail($"nearest fire place is {F(dist, "0.0")} m away ({name} at {P(f.transform.position)})");
            var inv = GameManager.GetInventoryComponent();
            bool was = f.IsBurning();
            float before = f.GetRemainingLifeTimeHours();

            if (sub == "start")
            {
                float hours = a.Length > 1 && TryF(a[1], out var hv) ? Mathf.Clamp(hv, 0.1f, 12f) : 1f;
                if (!was)
                {
                    // one log from the bag is what catches, as when the player's attempt succeeds
                    var log = inv != null ? inv.GetBestGearItemWithName("GEAR_Softwood") : null;
                    if (log == null) log = pm.AddItemCONSOLE("GEAR_Softwood", 1, 1f);
                    var fuel = log != null ? (log.m_FuelSourceItem ?? log.GetComponent<FuelSourceItem>()) : null;
                    if (fuel == null) return Fail("could not get a GEAR_Softwood to light it with");
                    f.TurnOn(fuel, false);
                    try { f.m_StartedByPlayer = true; } catch { }
                    try { var cf = f.m_Campfire; if (cf != null) cf.SetState(CampfireState.Lit); } catch { }
                    try { pm.ConsumeUnitFromInventory(log.gameObject); } catch { }
                }
                f.ForceBurnTimeInMinutes(hours * 60f);
                if (f.GetFireState() != FireState.FullBurn) f.FireStateSet(FireState.FullBurn);
                return Later(1.2f, () => f.IsBurning()
                    ? Ok($"{name} dist={F(dist, "0.0")} {FireInfo(f)} (was {(was ? "burning, " + F(before) + " h left" : "out")})")
                    : Fail($"{name} did not catch: {FireInfo(f)}"));
            }

            if (sub == "out")
            {
                if (!was) return Ok($"{name} dist={F(dist, "0.0")} already out");
                f.TurnOff();
                return Until(4f, () => !f.IsBurning(), ok =>
                {
                    if (ok) return Ok($"{name} dist={F(dist, "0.0")} {FireInfo(f)} (had {F(before)} h left)");
                    f.TurnOffImmediate();
                    return Later(0.6f, () => !f.IsBurning()
                        ? Ok($"{name} dist={F(dist, "0.0")} {FireInfo(f)} via TurnOffImmediate (had {F(before)} h left)")
                        : Fail($"{name} is still burning: {FireInfo(f)}"));
                });
            }

            // feed
            if (!was) return Fail($"{name} is not burning (use fire start)");
            var gi = inv != null ? inv.GetBestGearItemWithName(a[1]) : null;
            if (gi == null) return Fail(a[1] + " is not in the inventory");
            var src = gi.m_FuelSourceItem ?? gi.GetComponent<FuelSourceItem>();
            if (src == null) return Fail(a[1] + " is not fuel");
            if (Same(InHands(pm), gi)) return Fail(a[1] + " is in the hands (use stow first)");
            try { if (!f.CanFeedFire()) return Fail($"the game does not let {name} be fed right now"); } catch { }
            try
            {
                float need = src.m_FireAgeMinutesBeforeAdding, age = f.GetUnmodifiedMinutesBurning();
                if (need > 0f && age < need) return Fail($"{a[1]} needs a fire that has burned {F(need, "0")} min (this one: {F(age, "0")})");
            }
            catch { }
            int had = BagUnits(a[1]);
            // what the feed-fire screen does: add to the fire, take one from the bag
            f.AddFuel(gi, false);
            try { pm.ConsumeUnitFromInventory(gi.gameObject); } catch { }
            return Later(0.8f, () =>
            {
                float after = f.GetRemainingLifeTimeHours();
                int have = BagUnits(a[1]);
                if (after <= before + 0.001f) return Fail($"{name} did not gain time: left {F(before)} -> {F(after)} h");
                return Ok($"{a[1]} -> {name} dist={F(dist, "0.0")} left {F(before)} -> {F(after)} h, bag {had}->{have}");
            });
        }

        private static Res Equip(string[] a, PlayerManager pm)
        {
            if (a.Length < 1) return Fail("usage: equip <prefab>");
            var inv = GameManager.GetInventoryComponent();
            var gi = inv != null ? inv.GetBestGearItemWithName(a[0]) : null;
            if (gi == null) return Fail(a[0] + " is not in the inventory");
            if (Same(InHands(pm), gi)) return Ok(a[0] + " is already in the hands");
            if (!pm.ItemCanEquipInHands(gi)) return Fail($"the game says {a[0]} cannot be held");
            pm.EquipItem(gi, false);
            return Until(5f, () => Same(InHands(pm), gi), ok => ok
                ? Ok($"{a[0]} in the hands, lit={LightSync.LitKind(gi)}")
                : Fail($"the hands hold {Name(InHands(pm))} after 5 s"));
        }

        private static Res Stow(PlayerManager pm)
        {
            var gi = InHands(pm);
            if (gi == null) return Ok("hands already empty");
            string name = Name(gi);
            pm.UnequipItemInHands();
            return Until(4f, () => InHands(pm) == null, ok =>
            {
                if (ok) return Ok(name + " put away");
                pm.UnequipImmediate(false);
                return Later(0.4f, () => InHands(pm) == null ? Ok(name + " put away (without the animation)") : Fail(name + " is still in the hands"));
            });
        }

        private static Res LightCmd(string[] a, PlayerManager pm)
        {
            string v = a.Length > 0 ? a[0].ToLowerInvariant() : "";
            if (v != "on" && v != "off") return Fail("usage: light on | light off");
            bool want = v == "on";
            var gi = InHands(pm);
            if (gi == null) return Fail("nothing in the hands (use equip first)");
            string name = Name(gi);
            var flare = gi.m_FlareItem ?? gi.GetComponent<FlareItem>();
            var torch = gi.m_TorchItem ?? gi.GetComponent<TorchItem>();
            var lamp = gi.m_KeroseneLampItem ?? gi.GetComponent<Il2CppTLD.Gear.KeroseneLampItem>();
            var flash = gi.m_FlashlightItem ?? gi.GetComponent<FlashlightItem>();
            byte kindId = flare != null ? LightSync.Flare : torch != null ? LightSync.Torch : lamp != null ? LightSync.Lamp : flash != null ? LightSync.Flashlight : LightSync.None;
            string kind = flare != null ? "flare" : torch != null ? "torch" : lamp != null ? "lantern" : flash != null ? "flashlight" : null;
            if (kind == null) return Fail(name + " is not a light");
            if ((LightSync.LitKind(gi) != LightSync.None) == want) return Ok($"{kind} {name} already {(want ? "lit" : "off")} (lit={LightSync.LitKind(gi)})");
            string how, note = "";
            if (want)
            {
                if (flare != null) { if (flare.IsBurnedOut()) return Fail($"{name} is burned out"); flare.Ignite(); how = "FlareItem.Ignite"; }
                else if (torch != null) { if (torch.IsBurnedOut()) return Fail($"{name} is burned out"); torch.Ignite(); how = "TorchItem.Ignite"; }
                else if (lamp != null)
                {
                    try { if (lamp.m_CurrentFuelLiters.ToQuantity(1f) <= 0.01f) { lamp.m_CurrentFuelLiters = lamp.m_MaxFuel; note = " (was empty: filled first)"; } } catch { }
                    lamp.TurnOn(false); how = "KeroseneLampItem.TurnOn";
                }
                else
                {
                    try { if (flash.m_CurrentBatteryCharge <= 0.01f) { flash.m_CurrentBatteryCharge = 1f; note = " (was flat: charged first)"; } } catch { }
                    flash.TurnOn(); how = "FlashlightItem.TurnOn";
                }
            }
            else
            {
                if (flare != null) return Fail("a burning flare cannot be put out, it burns down");
                if (torch != null) { torch.ExtinguishDelayed(); how = "TorchItem.ExtinguishDelayed"; }
                else if (lamp != null) { lamp.TurnOff(true); how = "KeroseneLampItem.TurnOff"; }
                else { flash.TurnOff(); how = "FlashlightItem.TurnOff"; }
            }
            Func<bool> done = () => (LightSync.LitKind(gi) != LightSync.None) == want;
            return Until(4f, done, ok =>
            {
                if (ok) return Ok($"{kind} {name} lit={LightSync.LitKind(gi)} via {how}{note}");
                // the game's own call did not take: set the state the way the mod does for a friend's light
                string forced;
                if (want) { LightSync.Relight(gi, kindId); forced = "LightSync.Relight"; }
                else if (torch != null) { torch.Extinguish(TorchState.Extinguished); forced = "TorchItem.Extinguish"; }
                else return Fail($"{kind} {name} lit={LightSync.LitKind(gi)} after {how}");
                return Later(0.6f, () => done()
                    ? Ok($"{kind} {name} lit={LightSync.LitKind(gi)} via {forced} ({how} had no effect){note}")
                    : Fail($"{kind} {name} lit={LightSync.LitKind(gi)} after {how} and {forced}"));
            });
        }

        private static Res ThrowCmd(PlayerManager pm)
        {
            var gi = InHands(pm);
            if (gi == null) return Fail("nothing in the hands (use equip first)");
            string name = Name(gi);
            bool can = false; try { can = pm.ItemInHandsThrowable(); } catch { }
            if (!can) return Fail(name + " cannot be thrown");
            int units = ItemSync.UnitsOf(gi);
            var start = GameManager.GetPlayerTransform().position;
            // loose items of this kind that are already lying around
            var old = new HashSet<IntPtr>();
            foreach (var g in UnityEngine.Object.FindObjectsOfType<GearItem>())
            {
                try { if (Loose(g) && ItemSync.PrefabName(g.gameObject) == name) old.Add(g.Pointer); } catch { }
            }
            // the game's own throw (hand animation, then the release our patches hook)
            pm.Throw(gi, null);
            Func<bool> released = () =>
            {
                var h = InHands(pm);
                return h == null || !Same(h, gi) || ItemSync.UnitsOf(h) < units;
            };
            return Until(5f, released, ok =>
            {
                if (!ok) return Fail($"{name} is still in the hands after 5 s (the throw animation did not release it)");
                return Later(2.5f, () =>
                {
                    GearItem flown = null; float far = -1f;
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GearItem>())
                    {
                        try
                        {
                            if (!Loose(g) || ItemSync.PrefabName(g.gameObject) != name || old.Contains(g.Pointer)) continue;
                            float d = Vector3.Distance(g.transform.position, start);
                            if (d > far) { far = d; flown = g; }
                        }
                        catch { }
                    }
                    if (flown == null) return Fail($"{name} left the hands but no new loose {name} was found");
                    return Ok($"{name} landed at {P(flown.transform.position)} {F(far, "0.0")} m away guid={ItemSync.GuidOf(flown.gameObject) ?? "-"} lit={LightSync.LitKind(flown)} hands={Name(InHands(pm))}");
                });
            });
        }

        private static Res Look(string[] a)
        {
            if (a.Length < 1 || !TryF(a[0], out var yaw)) return Fail("usage: look <yawDegrees> [pitchDegrees]");
            float pitch = a.Length > 1 && TryF(a[1], out var pv) ? Mathf.Clamp(pv, -80f, 80f) : 0f;
            var cam = GameManager.GetVpFPSCamera();
            if (cam == null) return Fail("no camera");
            cam.SetAngle(yaw, pitch);
            return Later(0.3f, () =>
            {
                var e = cam.transform.eulerAngles;
                return Ok($"yaw={F(e.y, "0.0")} pitch={F(e.x > 180f ? e.x - 360f : e.x, "0.0")}");
            });
        }

        // ---------------- helpers ----------------
        private static bool TryF(string s, out float v) => float.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, Inv, out v);
        private static string F(float v, string fmt = "0.00") => v.ToString(fmt, Inv);
        private static string P(Vector3 p) => $"{F(p.x)},{F(p.y)},{F(p.z)}";
        private static bool Like(string name, string text) => (name ?? "").IndexOf(text ?? "", StringComparison.OrdinalIgnoreCase) >= 0;

        private static BaseAi NearestAnimal(string kind, Vector3 from, bool live, out float dist)
        {
            BaseAi best = null; dist = float.MaxValue;
            foreach (var ai in UnityEngine.Object.FindObjectsOfType<BaseAi>())
            {
                try
                {
                    if (ai == null || !Like(ItemSync.TestKind(ai.gameObject), kind)) continue;
                    if (live && ai.GetAiMode() == AiMode.Dead) continue;
                    float d = Vector3.Distance(ai.transform.position, from);
                    if (d < dist) { dist = d; best = ai; }
                }
                catch { }
            }
            return best;
        }

        private static BodyHarvest NearestCarcass(string kind, Vector3 from, float max, out float dist)
        {
            BodyHarvest best = null; dist = max;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<BodyHarvest>())
            {
                try
                {
                    if (b == null || !ItemSync.TestIsCarcass(b) || !Like(ItemSync.TestKind(b.gameObject), kind)) continue;
                    float d = Vector3.Distance(b.transform.position, from);
                    if (d < dist) { dist = d; best = b; }
                }
                catch { }
            }
            return best;
        }

        /// <summary>Ground height near a reference height (ignores trigger volumes; picks the surface closest to refY).</summary>
        private static float GroundY(Vector3 p, float refY)
        {
            try
            {
                var hits = Physics.RaycastAll(new Vector3(p.x, refY + 12f, p.z), Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.NaN, bestDiff = float.MaxValue;
                for (int i = 0; hits != null && i < hits.Length; i++)
                {
                    float y = hits[i].point.y, diff = Mathf.Abs(y - refY);
                    if (diff < bestDiff) { bestDiff = diff; best = y; }
                }
                if (!float.IsNaN(best)) return best + 0.15f;
            }
            catch { }
            return refY + 0.5f;
        }

        private static Res StandNear(PlayerManager pm, Transform me, Transform target, float dist, string what, float? refY = null)
        {
            var t = target.position;
            var dir = me.position - t; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = -target.forward;
            dir.y = 0f; dir.Normalize();
            var p = t + dir * dist;
            p.y = GroundY(p, refY ?? t.y);
            var rot = Quaternion.LookRotation(new Vector3(t.x - p.x, 0f, t.z - p.z));
            pm.TeleportPlayer(p, rot);
            try { GameManager.GetVpFPSCamera()?.SetAngle(rot.eulerAngles.y, 0f); } catch { }
            return Later(0.3f, () =>
            {
                var now = GameManager.GetPlayerTransform().position;
                float d; try { d = Vector3.Distance(now, target.position); } catch { d = -1f; }
                return Ok($"{what} is {F(d, "0.0")} m away, now at {P(now)}");
            });
        }

        private static Res Harvest(string[] a, Vector3 from)
        {
            if (a.Length < 2) return Fail("usage: harvest <kind> meat <kg> | hide | gut");
            var b = NearestCarcass(a[0], from, 10f, out var dist);
            if (b == null) return Fail($"no carcass like {a[0]} within 10 m");
            string kind = ItemSync.TestKind(b.gameObject);
            switch (a[1].ToLowerInvariant())
            {
                case "meat":
                {
                    if (a.Length < 3 || !TryF(a[2], out var kg)) return Fail("usage: harvest <kind> meat <kg>");
                    float cur = b.m_MeatAvailableKG.ToQuantity(1f);
                    float left = Mathf.Max(0f, cur - kg);
                    b.m_MeatAvailableKG = ItemWeight.FromKilograms(left);
                    return Ok($"{kind} dist={F(dist, "0.0")} meat {F(cur)} -> {F(b.m_MeatAvailableKG.ToQuantity(1f))} kg");
                }
                case "hide":
                {
                    int cur = b.m_HideAvailableUnits; b.m_HideAvailableUnits = 0;
                    return Ok($"{kind} dist={F(dist, "0.0")} hide {cur} -> 0");
                }
                case "gut":
                case "guts":
                {
                    int cur = b.m_GutAvailableUnits; b.m_GutAvailableUnits = 0;
                    return Ok($"{kind} dist={F(dist, "0.0")} gut {cur} -> 0");
                }
            }
            return Fail("usage: harvest <kind> meat <kg> | hide | gut");
        }

        private static bool Loose(GearItem g)
        {
            try { return g != null && g.gameObject.activeInHierarchy && !g.m_InPlayerInventory && !g.IsInsideContainer(); }
            catch { return false; }
        }

        private static Res Pickup(string[] a, PlayerManager pm, Vector3 from)
        {
            if (a.Length < 1) return Fail("usage: pickup <prefab>");
            GearItem best = null; float bestD = 5f;
            foreach (var g in UnityEngine.Object.FindObjectsOfType<GearItem>())
            {
                if (!Loose(g) || !string.Equals(ItemSync.PrefabName(g.gameObject), a[0], StringComparison.OrdinalIgnoreCase)) continue;
                float d = Vector3.Distance(g.transform.position, from);
                if (d < bestD) { bestD = d; best = g; }
            }
            if (best == null) return Fail($"no loose {a[0]} within 5 m");
            var item = best; float dist = bestD;
            // the game's own pickup path, so our pickup patch fires
            pm.ProcessPickupItemInteraction(item, false, false, true);
            Func<Res> report = () =>
            {
                bool gone, inBag = false;
                try { gone = item == null; if (!gone) inBag = item.m_InPlayerInventory; } catch { gone = true; }
                if (gone) return Ok($"{a[0]} dist={F(dist, "0.0")} taken (merged into a stack)");
                return inBag ? Ok($"{a[0]} dist={F(dist, "0.0")} taken") : Fail($"{a[0]} is still on the ground");
            };
            return Later(0.7f, () =>
            {
                // some items open the look-at-it view first: press "take" there
                bool inspecting = false; try { inspecting = pm.IsInspectModeActive(); } catch { }
                if (inspecting) { try { pm.OnPickupFromStandardGearItemInspection(); } catch (Exception e) { return Fail("inspect view: " + e.Message); } }
                return Later(0.7f, report);
            });
        }

        private static Res LoadSlot(string[] a)
        {
            if (a.Length < 1) return Fail("usage: loadslot <slotName> | newest");
            var chooser = UnityEngine.Object.FindObjectOfType<Panel_ChooseSandbox>();
            if (chooser == null || !chooser.gameObject.activeInHierarchy)
            {
                // from the main menu, without anybody opening the Load screen: hand the slot to the game directly
                if (WorldSync.InGame()) return Fail("a game is already loaded");
                string want = a[0];
                SaveSlotInfo ssi = null;
                try { SaveGameSlotHelper.RefreshSandboxSaveSlots(); } catch { }
                if (want.Equals("newest", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var all = SaveGameSlotHelper.GetSaveSlotInfoList(SaveSlotType.SANDBOX);
                        for (int i = 0; all != null && i < all.Count && ssi == null; i++)
                            if (all[i] != null && !SaveMerge.IsCoopSlot(all[i].m_SaveSlotName)) ssi = all[i];
                    }
                    catch { }
                }
                else { try { ssi = SaveGameSlotHelper.GetSaveSlotInfo(SaveSlotType.SANDBOX, want); } catch { } }
                if (ssi == null) return Fail($"no save slot like {want} (and the Load screen is not open)");
                string slot = ssi.m_SaveSlotName;
                // The game's own menu path first: it also tells the save system which save is the current one
                // (loading the slot behind its back leaves the current save name empty, and the co-op code needs it).
                string how = null;
                try
                {
                    int idx = -1;
                    int n = SaveGameSlotHelper.GetNumSaveSlots(SaveSlotType.SANDBOX);
                    for (int i = 0; i < n && idx < 0; i++)
                    {
                        var s2 = SaveGameSlotHelper.GetSaveSlotInfo(SaveSlotType.SANDBOX, i);
                        if (s2 != null && s2.m_SaveSlotName == slot) idx = i;
                    }
                    var mm = InterfaceManager.GetPanel<Panel_MainMenu>();
                    if (idx >= 0 && mm != null) { mm.OnLoadSaveSlot(SaveSlotType.SANDBOX, idx); how = $"through the main menu (row {idx})"; }
                }
                catch (Exception e) { CoopMod.Log("Test harness: the main menu would not load the slot (" + e.Message + "), loading it directly"); how = null; }
                if (how == null)
                {
                    try { SaveGameSystem.SetCurrentSaveInfo(ssi.m_Episode, ssi.m_GameMode, ssi.m_GameId, slot); } catch { }
                    bool started; try { started = GameManager.LoadSaveGameSlot(ssi); } catch (Exception e) { return Fail("direct load: " + e.Message); }
                    if (!started) return Fail($"the game refused to load {slot} directly (open the Load screen instead)");
                    how = "directly";
                }
                return Until(120f, () => WorldSync.Settled(),
                    ok =>
                    {
                        string cur = null; try { cur = SaveGameSystem.GetCurrentSaveName(); } catch { }
                        if (!ok) return Fail($"{slot} did not finish loading within 120 s ({how})");
                        return cur == slot ? Ok($"loaded {slot} {how}, now in {CoopMod.CurrentScene()}")
                                           : Fail($"in game ({how}) but the game says the current save is '{cur}', not {slot}");
                    }, 0.5f);
            }
            string names = "";
            for (int i = 0; i < 40; i++)
            {
                SaveSlotInfo info = null;
                try { info = chooser.GetSaveSlotInfo(i); } catch { }
                if (info == null) break;
                string n = info.m_SaveSlotName;
                names += n + " ";
                bool match = a[0].Equals("newest", StringComparison.OrdinalIgnoreCase) ? !SaveMerge.IsCoopSlot(n) : n == a[0];
                if (!match) continue;
                chooser.OnLoadSlotClicked(i);
                return Ok($"clicked {n} (row {i})");
            }
            return Fail($"{a[0]} is not in the list: {names}".TrimEnd());
        }

        private static Res Esc(string[] a)
        {
            if (a.Length < 1 || (a[0] != "on" && a[0] != "off")) return Fail("usage: esc on | esc off");
            var panel = InterfaceManager.GetPanel<Panel_PauseMenu>();
            if (panel == null) return Fail("use the real key (no pause menu panel)");
            bool want = a[0] == "on";
            if (panel.IsEnabled() == want) return Ok(want ? "already open" : "already closed");
            string how;
            if (want)
            {
                if (!InputManager.CanInvokePauseMenu()) return Fail("the game does not allow the pause menu right now");
                InputManager.PauseGame(); how = "InputManager.PauseGame";
            }
            else { panel.OnDone(); how = "Panel_PauseMenu.OnDone"; }
            return Later(0.4f, () =>
            {
                if (panel.IsEnabled() == want) return Ok($"{(want ? "opened" : "closed")} via {how}");
                panel.Enable(want);
                return Later(0.4f, () => panel.IsEnabled() == want
                    ? Ok($"{(want ? "opened" : "closed")} via Panel_PauseMenu.Enable")
                    : Fail("use the real key"));
            });
        }

        // ---------------- state dump ----------------
        private Res WriteState(long seq, string tag, string invFilter)
        {
            var sb = new StringBuilder();
            void Line(string k, object v) => sb.Append(k).Append(": ").Append(v is float f ? F(f) : Convert.ToString(v, Inv)).Append('\n');
            void Try(string k, Func<object> f) { try { Line(k, f()); } catch (Exception e) { Line(k, "? " + e.Message); } }

            var net = _mod.Net;
            Line("seq", seq);
            Line("tag", tag ?? "-");
            Line("wall_clock", DateTime.Now.ToString("HH:mm:ss.fff", Inv));
            Line("mod_version", CoopMod.ModVersion);
            Line("pid", _pid);
            Line("role", net.Mode == NetMode.Host ? "host" : net.Mode == NetMode.Client ? "client" : "none");
            Line("connected", net.IsConnected);
            Line("local_id", net.LocalId);
            Line("net_status", net.Status);
            Line("peers", net.Remotes.Count);
            Line("transfer_busy", _mod.Transfer.Busy);
            Try("shared_world", () => _mod.Items.SharedWorld);
            Try("loading", () => WorldSync.Loading());
            Try("settled", () => WorldSync.Settled());
            Try("join_state", () => _mod.Transfer.ClientStateName);
            Try("host_sending_world", () => _mod.Transfer.HostSending);
            bool inGame = WorldSync.InGame();
            Line("in_game", inGame);
            Try("scene", () => CoopMod.CurrentScene() ?? "-");
            Try("save_name", () => SaveGameSystem.GetCurrentSaveName() ?? "-");
            Line("realtime", F(Time.realtimeSinceStartup, "0.00"));
            Line("unity_time", F(Time.time, "0.00"));
            Line("frame", Time.frameCount);

            // pause
            Try("paused", () => GameManager.m_IsPaused);
            Line("time_scale", F(Time.timeScale, "0.###"));
            Line("focused", Application.isFocused);
            Try("test_presence", () => PauseSync.TestPresence);
            Try("local_away", () => PauseSync.LocalAway);
            Try("keep_running", () => PauseSync.KeepRunning);
            Try("paused_when_focus_lost", () => GameManager.m_PausedWhenFocusLost || GameManager.s_PausedWhenFocusLost);
            Try("pause_menu_open", () => { var p = InterfaceManager.GetPanel<Panel_PauseMenu>(); return p != null && p.IsEnabled(); });
            Try("any_remote_active", () => _mod.Players.AnyRemoteActive());

            // sleep and pass time
            byte sleepNow = inGame ? SleepSync.LocalSleep() : (byte)0;
            Line("sleeping", sleepNow == 1);
            Line("passing_time", sleepNow == 2);
            Line("sleep_hours_left", F(inGame ? SleepSync.HoursLeft() : 0f, "0.000"));
            Try("time_accel", () => F(_mod.Sleep.TimeAccel(), "0.0"));
            Try("everyone_sleeping", () => _mod.Sleep.EveryoneSleeping);
            try { _mod.Sleep.WriteState(sb); } catch (Exception e) { Line("sleep_sync", "? " + e.Message); }

            // downed and revive
            Line("condition", inGame ? F(DownedSync.ConditionPercent(), "0.0") : "-");
            try { _mod.Downed.WriteState(sb); } catch (Exception e) { Line("downed", "? " + e.Message); }

            // load screen (main menu)
            try
            {
                var chooser = UnityEngine.Object.FindObjectOfType<Panel_ChooseSandbox>();
                bool open = chooser != null && chooser.gameObject.activeInHierarchy;
                Line("load_screen_open", open);
                Line("movie", MovieGuard.StateLine());
                if (open)
                    for (int i = 0; i < 40; i++)
                    {
                        SaveSlotInfo info = null;
                        try { info = chooser.GetSaveSlotInfo(i); } catch { }
                        if (info == null) break;
                        sb.Append($"slot: {i} {info.m_SaveSlotName} name={info.m_UserDefinedName}\n");
                    }
            }
            catch { Line("load_screen_open", "?"); }

            // remote players as this game sees them
            try
            {
                // how each of them is drawn: the pose of the figure, how fast it moves, where its head looks (down is positive)
                var drawn = new Dictionary<byte, (string Pose, float Speed, float Pitch, bool Figure)>();
                try { foreach (var d in _mod.Players.Drawn()) drawn[d.Id] = (d.Pose, d.Speed, d.Pitch, d.Figure); } catch { }
                foreach (var r in _mod.Players.Snapshot())
                {
                    string name = net.Remotes.TryGetValue(r.Id, out var info) ? info.Name : "?";
                    drawn.TryGetValue(r.Id, out var dr);
                    sb.Append($"remote: id={r.Id} name={name} known={r.HasState} scene={r.Scene} pos={P(r.Pos)} away={r.Away} light={r.Light} age={F(r.Age, "0.0")} sleeping={r.Sleep != 0} passing_time={r.Sleep == 2}"
                        + $" downed={r.Downed != 0} downed_left={F(r.Downed != 0 ? Mathf.Max(0f, r.DownedLeft - r.Age) : 0f, "0")}"
                        + $" pose={dr.Pose ?? "-"} speed={F(dr.Speed)} pitch={F(dr.Pitch, "0")} drawn_as={(dr.Pose == null ? "-" : dr.Figure ? "figure" : "capsule")}\n");
                }
            }
            catch (Exception e) { Line("remote", "? " + e.Message); }

            // the friend's figure, the whistle, handing an item over
            Try("figure_setting", () => RemotePlayers.FigureWanted() ? "on" : "off (capsule)");
            Try("figure_preview", () => _mod.Players.PreviewLine());
            Line("figure_parts", Figure.LastParts);
            Line("figure_shader", Figure.ShaderName);
            Line("figure_errors", RemotePlayers.FigureErrors);
            Line("figure_last_error", RemotePlayers.FigureLastError);
            Line("figure_kind", Figure.Kind);
            Line("figure_kind_forced", Figure.KindName(RemotePlayers.PreviewKind) + " (preview figure only)");
            Line("figure_mesh_error", Figure.MeshError);
            Line("figure_mesh_file", SkinnedBody.FileText);
            Line("figure_mesh_api", SkinnedBody.ApiText);
            Line("figure_mesh_builds", $"{SkinnedBody.Builds} built, {SkinnedBody.Failures} failed");
            try { _mod.Looks.WriteState(sb); } catch (Exception e) { Line("look", "? " + e.Message); }
            Line("key_whistle", $"{ModKeys.Whistle.Name} ({ModKeys.Whistle.Note})");
            Line("key_give", $"{ModKeys.Give.Name} ({ModKeys.Give.Note})");
            Line("game_keys", ModKeys.GameKeys);
            try { _mod.Whistle.WriteState(sb); } catch (Exception e) { Line("whistle", "? " + e.Message); }
            try { _mod.Handover.WriteState(sb); } catch (Exception e) { Line("handover", "? " + e.Message); }
            try { _mod.Fire2.WriteState(sb); } catch (Exception e) { Line("ff", "? " + e.Message); }
            try { _mod.Work.WriteState(sb); } catch (Exception e) { Line("work", "? " + e.Message); }
            try { CoopMenu.WriteState(sb); } catch (Exception e) { Line("menu", "? " + e.Message); }
            try { Line("lang", $"setting={L.Setting} english={L.En} game={L.GameLanguage}"); } catch (Exception e) { Line("lang", "? " + e.Message); }
            try { _mod.Map.WriteState(sb); } catch (Exception e) { Line("map", "? " + e.Message); }
            try { sb.Append("ground: ").Append(_mod.Items.GroundLast).Append(" | ").Append(_mod.Items.GroundSummary(null)).Append('\n'); } catch (Exception e) { Line("ground", "? " + e.Message); }
            try { _mod.Voice.WriteState(sb); } catch (Exception e) { Line("voice", "? " + e.Message); }
            try { var pr = System.Diagnostics.Process.GetCurrentProcess(); Line("memory_mb", $"working_set={pr.WorkingSet64 / 1048576} private={pr.PrivateMemorySize64 / 1048576} managed={GC.GetTotalMemory(false) / 1048576}"); } catch { }
            Line("port_opener", PortOpener.Status); Line("port_opened", PortOpener.Opened); Line("public_address", PortOpener.PublicAddress ?? "-");

            int counts_animals = 0, counts_carcass = 0, counts_fire = 0, counts_ground = 0, counts_cont = 0;
            if (inGame)
            {
                Vector3 me = default;
                try
                {
                    var t = GameManager.GetPlayerTransform(); me = t.position;
                    Line("player_pos", P(me));
                    float yaw = t.eulerAngles.y;
                    try { var cam = GameManager.GetVpFPSCamera(); if (cam != null) yaw = cam.transform.eulerAngles.y; } catch { }
                    Line("player_yaw", F(yaw, "0.0"));
                }
                catch (Exception e) { Line("player_pos", "? " + e.Message); }

                // clock and weather
                Try("clock_hours", () => F(GameManager.GetUniStorm().GetElapsedHours(), "0.0000"));
                Try("clock_day", () => GameManager.GetUniStorm().GetDayNumber());
                Try("clock_time_of_day", () =>
                {
                    var tod = GameManager.GetTimeOfDayComponent();
                    return F(tod != null ? tod.GetNormalizedTime() : GameManager.GetUniStorm().m_NormalizedTime, "0.0000");
                });
                Try("hours_played_not_paused", () => F(GameManager.GetTimeOfDayComponent().GetHoursPlayedNotPaused(), "0.0000"));
                Try("weather", () => GameManager.GetUniStorm().GetWeatherStage());

                // the player's own body
                Try("condition_hp", () => { var c = GameManager.GetConditionComponent(); return $"{F(c.m_CurrentHP, "0.0")}/{F(c.m_MaxHP, "0.0")}"; });
                Try("hunger_calories", () => { var c = GameManager.GetHungerComponent(); return $"{F(c.m_CurrentReserveCalories, "0")}/{F(c.m_MaxReserveCalories, "0")}"; });
                Try("thirst", () => { var c = GameManager.GetThirstComponent(); return $"{F(c.m_CurrentThirst, "0.0")}/{F(c.m_MaxThirst, "0.0")}"; });
                Try("fatigue", () => { var c = GameManager.GetFatigueComponent(); return $"{F(c.m_CurrentFatigue, "0.0")}/{F(c.m_MaxFatigue, "0.0")}"; });
                Try("freezing", () => { var c = GameManager.GetFreezingComponent(); return $"{F(c.m_CurrentFreezing, "0.0")}/{F(c.m_MaxFreezing, "0.0")}"; });
                Try("held_item", () => { var h = GameManager.GetPlayerManagerComponent().m_ItemInHands; return h == null ? "none" : ItemSync.PrefabName(h.gameObject); });
                Try("held_light", () => LightSync.LitKind(GameManager.GetPlayerManagerComponent().m_ItemInHands));
                Try("host_animals_known", () => AnimalSync.TestCounts().Known);
                Try("puppets_linked", () => AnimalSync.TestCounts().Linked);
                Try("puppets_asleep", () => AnimalSync.TestCounts().Asleep);
                Try("following_host_animals", () => AnimalSync.Following);
                Try("struggle", () => _mod.Animals.StruggleLine());
                Try("struggle_last", () => _mod.Animals.LastStruggle);

                // animals
                try
                {
                    var rows = new List<(string Kind, float Dist, string Text)>();
                    foreach (var ai in UnityEngine.Object.FindObjectsOfType<BaseAi>())
                    {
                        try
                        {
                            if (ai == null) continue;
                            var p = ai.transform.position; float d = Vector3.Distance(p, me);
                            string kind = ItemSync.TestKind(ai.gameObject);
                            rows.Add((kind, d, $"animal: {kind} pos={P(p)} mode={ai.GetAiMode()} hp={F(ai.m_CurrentHP, "0.#")}/{F(ai.m_MaxHP, "0.#")} puppet={AnimalSync.IsPuppet(ai.Pointer)} dist={F(d, "0.0")}{AnimalSync.TestInfo(ai)}"));
                        }
                        catch { }
                    }
                    rows.Sort((x, y) => { int c = string.CompareOrdinal(x.Kind, y.Kind); return c != 0 ? c : x.Dist.CompareTo(y.Dist); });
                    foreach (var r in rows) sb.Append(r.Text).Append('\n');
                    counts_animals = rows.Count;
                }
                catch (Exception e) { Line("animal", "? " + e.Message); }

                // carcasses
                try
                {
                    var rows = new List<(string Kind, float Dist, string Text)>();
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<BodyHarvest>())
                    {
                        try
                        {
                            if (b == null || !ItemSync.TestIsCarcass(b)) continue;
                            ItemSync.TestCarcassInfo(b, out var kind, out var meat, out var hide, out var gut, out var p);
                            float d = Vector3.Distance(p, me);
                            rows.Add((kind, d, $"carcass: {kind} pos={P(p)} meat={F(meat)} hide={hide} gut={gut} dist={F(d, "0.0")}"));
                        }
                        catch { }
                    }
                    rows.Sort((x, y) => { int c = string.CompareOrdinal(x.Kind, y.Kind); return c != 0 ? c : x.Dist.CompareTo(y.Dist); });
                    foreach (var r in rows) sb.Append(r.Text).Append('\n');
                    counts_carcass = rows.Count;
                }
                catch (Exception e) { Line("carcass", "? " + e.Message); }

                // fires
                try
                {
                    // every fire place in the scene (so always more than the 12 m around the player), nearest first
                    var rows = new List<(float Dist, string Text)>();
                    foreach (var f in UnityEngine.Object.FindObjectsOfType<Fire>())
                    {
                        try
                        {
                            if (f == null) continue;
                            var p = f.transform.position; float d = Vector3.Distance(p, me);
                            string st = "?"; try { st = f.GetFireState().ToString(); } catch { }
                            string heat = "?"; try { heat = F(f.GetCurrentTempIncrease(), "0.0"); } catch { }
                            rows.Add((d, $"fire: {ItemSync.PrefabName(f.gameObject)} pos={P(p)} burning={f.IsBurning()} left={F(f.GetRemainingLifeTimeHours())} state={st} heat={heat} dist={F(d, "0.0")}"));
                        }
                        catch { }
                    }
                    rows.Sort((x, y) => x.Dist.CompareTo(y.Dist));
                    foreach (var r in rows) sb.Append(r.Text).Append('\n');
                    counts_fire = rows.Count;
                }
                catch (Exception e) { Line("fire", "? " + e.Message); }

                // loose items near the player, and (optionally) matching inventory items
                try
                {
                    var rows = new List<(float Dist, string Text)>();
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GearItem>())
                    {
                        if (!Loose(g)) continue;
                        try
                        {
                            var p = g.transform.position; float d = Vector3.Distance(p, me);
                            if (d > 20f) continue;
                            byte lit = 0; try { lit = LightSync.LitKind(g); } catch { }
                            string guid = ItemSync.GuidOf(g.gameObject); if (string.IsNullOrEmpty(guid)) guid = "-";
                            rows.Add((d, $"ground: {ItemSync.PrefabName(g.gameObject)} pos={P(p)} guid={guid} lit={lit} dist={F(d, "0.0")}"));
                        }
                        catch { }
                    }
                    rows.Sort((x, y) => x.Dist.CompareTo(y.Dist));
                    foreach (var r in rows) sb.Append(r.Text).Append('\n');
                    counts_ground = rows.Count;
                }
                catch (Exception e) { Line("ground", "? " + e.Message); }

                // containers within reach
                try
                {
                    var rows = new List<(float Dist, string Text)>();
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<Container>())
                    {
                        try
                        {
                            if (c == null) continue;
                            var p = c.transform.position; float d = Vector3.Distance(p, me);
                            if (d > 8f) continue;
                            rows.Add((d, $"container: {ItemSync.PrefabName(c.gameObject)} pos={P(p)} inspected={c.IsInspected()} dist={F(d, "0.0")} items={ItemSync.TestContainerItems(c)}"));
                        }
                        catch { }
                    }
                    rows.Sort((x, y) => x.Dist.CompareTo(y.Dist));
                    foreach (var r in rows) sb.Append(r.Text).Append('\n');
                    counts_cont = rows.Count;
                }
                catch (Exception e) { Line("container", "? " + e.Message); }

                // doors to other scenes
                try
                {
                    foreach (var ls in UnityEngine.Object.FindObjectsOfType<LoadScene>())
                    {
                        try
                        {
                            if (ls == null || !ls.gameObject.activeInHierarchy) continue;
                            // every door of the scene, so a test can pick a building nobody has entered yet
                            var p = ls.transform.position; float d = Vector3.Distance(p, me);
                            string to = "?"; try { to = ls.GetSceneToLoad(); } catch { }
                            sb.Append($"door: to={to} pos={P(p)} dist={F(d, "0.0")}\n");
                        }
                        catch { }
                    }
                }
                catch { }

                // inventory
                try
                {
                    var inv = GameManager.GetInventoryComponent();
                    var items = inv.m_Items;
                    int n = items != null ? items.Count : 0;
                    if (!string.IsNullOrEmpty(invFilter))
                        for (int i = 0; i < n; i++)
                        {
                            try
                            {
                                var g = items[i].m_GearItem;
                                if (g == null) continue;
                                string name = ItemSync.PrefabName(g.gameObject);
                                if (Like(name, invFilter)) sb.Append($"inv: {name} x{ItemSync.UnitsOf(g)}\n");
                            }
                            catch { }
                        }
                    Line("inv_count", n);
                }
                catch (Exception e) { Line("inv_count", "? " + e.Message); }
            }
            Line("count_animals", counts_animals);
            Line("count_carcasses", counts_carcass);
            Line("count_fires", counts_fire);
            Line("count_ground", counts_ground);
            Line("count_containers", counts_cont);
            Try("animal_ownership", () => _mod.Animals.OwnershipOn ? "on" : "off");
            Try("animals_owned_here", () => _mod.Animals.TestOwned().Here);
            Try("animals_with_friends", () => _mod.Animals.TestOwned().Away);
            Try("animal_handovers", () => _mod.Animals.Handovers);
            Try("animal_handover_fails", () => _mod.Animals.HandoverFails);
            Line("animal_save_hook", AnimalSync.SaveHook);
            Try("struggles_here", () => _mod.Animals.Struggles);
            try { SceneHandoff.Instance?.WriteState(sb); } catch (Exception e) { Line("scene_handoff", "? " + e.Message); }

            string file = tag == null ? $"TLDCoop-state-{_pid}.txt" : $"TLDCoop-state-{_pid}-{Sanitize(tag)}.txt";
            string path = Path.Combine(_dir, file), tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, path, true);
            return Ok(file);
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s) if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
            return sb.Length > 0 ? sb.ToString() : "x";
        }
    }

    // read-only views of the sync code's private bits, for the test harness
    internal sealed partial class ItemSync
    {
        internal static string TestKind(GameObject go) => KindOf(go);
        internal static bool TestIsCarcass(BodyHarvest b) => IsCarcass(b);
        internal static void TestCarcassInfo(BodyHarvest b, out string kind, out float meatKg, out int hide, out int gut, out Vector3 pos)
        {
            var s = SigOf(b);
            kind = s.Kind; meatKg = s.MeatG / 1000f; hide = s.Hide; gut = s.Gut; pos = s.Pos;
        }
        internal static string TestContainerItems(Container c)
        {
            try { return Names(Take(c)).Trim(); } catch (Exception e) { return "? " + e.Message; }
        }
    }
}
