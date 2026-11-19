using System;
using System.Runtime.InteropServices;
using Il2Cpp;
using LiteNetLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(TLDCoop.CoopMod), "TLD Shared World (Ortak Dunya)", TLDCoop.CoopMod.ModVersion, "Topraklik Game Dev Studio")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace TLDCoop
{
    public sealed class CoopMod : MelonMod
    {
        public const string ModVersion = "0.14.0";
        internal static bool AllowVersionMismatch => Instance?._allowMismatch?.Value ?? false;

        internal static CoopMod Instance;
        internal Network Net;
        internal RemotePlayers Players;
        internal WorldSync World;
        internal WorldTransfer Transfer;
        internal ItemSync Items;
        internal AnimalSync Animals;
        internal SceneHandoff Handoff;
        internal SleepSync Sleep;
        internal DownedSync Downed;
        internal WhistleSync Whistle;
        internal HandoverSync Handover;
        internal FriendlyFire Fire2;
        internal MapShare Map;
        internal WorkTime Work;
        private bool _saidWork;
        private MelonPreferences_Entry<bool> _workTime;
        private MelonPreferences_Entry<string> _lang;
        internal VoiceChat Voice;
        internal LookSync Looks;
        private MelonPreferences_Entry<bool> _voiceOn, _openPort;
        private MelonPreferences_Entry<float> _voiceRange;
        private MelonPreferences_Entry<string> _talkKey;
        private MelonPreferences_Entry<bool> _friendlyFire;
        internal TestHarness Harness;
        internal string JoinAddress => _joinAddress.Value;
        internal int Port => _port.Value;
        internal void SetJoinAddress(string a) { _joinAddress.Value = a; try { _cat.SaveToFile(false); } catch { } }

        /// <summary>Join from the menu: the same guard as the key (a second press while joining would restart it).</summary>
        internal void JoinFromMenu(string address)
        {
            if (Net.Mode == NetMode.Client && (Transfer.Busy || Net.Connecting)) { ShowStatus(); return; }
            if (!string.IsNullOrEmpty(address) && address != _joinAddress.Value) SetJoinAddress(address);
            Join(_joinAddress.Value);
        }

        private MelonPreferences_Category _cat;
        private MelonPreferences_Entry<string> _name, _joinAddress;
        private MelonPreferences_Entry<int> _port, _maxPlayers;
        private MelonPreferences_Entry<bool> _allowMismatch, _handoff, _sleepTogether, _downed, _animalOwnership;
        private MelonPreferences_Entry<int> _downedMinutes;
        private MelonPreferences_Entry<string> _coopSlots, _sleepPlace;
        private MelonPreferences_Entry<bool> _figure, _look;
        private MelonPreferences_Entry<string> _whistleKey, _giveKey, _whistleSound;

        private float _nextStateSend;
        private readonly bool[] _keyWasDown = new bool[256];

        public override void OnInitializeMelon()
        {
            Instance = this;
            _cat = MelonPreferences.CreateCategory("TLDCoop", "TLD Ortak Dunya");
            _name = _cat.CreateEntry("OyuncuAdi", Environment.UserName, "Your name as your friends see it / Oyuncu adi");
            _joinAddress = _cat.CreateEntry("BaglanilacakAdres", "127.0.0.1", "Address to join (IP or host name) / Katilirken baglanilacak adres (IP veya alan adi)");
            _port = _cat.CreateEntry("Port", 27500, "Server UDP port / Sunucu UDP portu");
            _openPort = _cat.CreateEntry("OtomatikPortAc", true, "When hosting, ask the router to open the port (UPnP) and close it again afterwards / Sunucu acilinca modemden portu otomatik acmasini ister (UPnP); kapaninca geri kapatir");
            _maxPlayers = _cat.CreateEntry("MaksOyuncu", 4, "Most players on the server (you included) / Sunucudaki en fazla oyuncu (sen dahil)");
            _allowMismatch = _cat.CreateEntry("TestModu_FarkliOyunSurumuneIzinVer", false, "Testing only: let different game versions connect (the world may go out of sync) / Sadece test icin: farkli oyun surumlerinin baglanmasina izin ver (dunya senkronu bozulabilir)");

            Net = new Network();
            Players = new RemotePlayers();
            World = new WorldSync(Net);
            _coopSlots = _cat.CreateEntry("OrtakKayitlar", "sandbox99", "Saves on this PC used for the shared world (the last one is current) / Bu bilgisayarda ortak dunya icin kullanilan kayitlar (en sondaki gecerli)");
            SaveMerge.CoopSlots = () => { var h = new System.Collections.Generic.HashSet<string>(_coopSlots.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)); h.Add(SaveMerge.FixedCoopSlot); return h; };
            Transfer = new WorldTransfer(Net)
            {
                GetLastCoopSlot = () => SaveMerge.FixedCoopSlot,
                RememberCoopSlot = name =>
                {
                    var list = new System.Collections.Generic.List<string>(_coopSlots.Value.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    list.Remove(name); list.Add(name);
                    while (list.Count > 12) list.RemoveAt(0);
                    _coopSlots.Value = string.Join(",", list);
                    _cat.SaveToFile(false);
                },
            };
            // a client shares items only while it is actually playing the newest co-op copy,
            // however it was opened (automatically or by hand from the Load screen)
            Items = new ItemSync(Net, () => Net.Mode == NetMode.Host
                || (Net.IsConnected && Transfer.FreshWorldHere && CurrentSaveIs(Transfer.GetLastCoopSlot())));
            _animalOwnership = _cat.CreateEntry("HayvanSahipligi", true, "Animal ownership: wolves, bears and other predators deal with the nearest player (stalk and attack them) and everyone sees it; off = animals only notice the host's player / Hayvan sahipligi: kurt, ayi gibi yirtici hayvanlar en yakinindaki oyuncuyla ilgilenir (onu izler, ona saldirir) ve bunu herkes gorur; kapatilirsa hayvanlar 0.8 gibi sadece sunucunun oyuncusunu fark eder");
            Animals = new AnimalSync(Net, () => Items.SharedWorld, () => _animalOwnership.Value);
            try { Animals.ApplyPatches(HarmonyInstance); } catch (Exception e) { Log("Animal save hook: " + e.Message); }
            _handoff = _cat.CreateEntry("BolgeDevri", true, "Region hand-over: a player entering a region gets its newest state from the other player (off = regions load from the host's save) / Bolge devri: bir bolgeye giren oyuncu o bolgenin en guncel halini diger oyuncudan alir (kapatilirsa bolgeler eski usulle, sunucunun kaydindan yuklenir)");
            Handoff = new SceneHandoff(Net, () => Items.SharedWorld, () => _handoff.Value);
            try { Handoff.ApplyPatches(HarmonyInstance); } catch (Exception e) { Log("Scene hand-over hooks: " + e.Message); }
            _sleepTogether = _cat.CreateEntry("BirlikteUyku", true, "Sleep together: time only speeds up while everyone sleeps (or passes time); off = sleep works as in the base game / Birlikte uyku: zaman sadece herkes uyurken (veya zaman gecirirken) hizlanir; kapatilirsa uyku eskisi gibi calisir");
            Sleep = new SleepSync(Net, Players, () => Items.SharedWorld, () => _sleepTogether.Value);
            try { Sleep.ApplyPatches(HarmonyInstance); } catch (Exception e) { Log("Shared sleep hook: " + e.Message); }
            _downed = _cat.CreateEntry("YereYigilma", true, "Downed state: nobody dies for good in the shared world. At zero condition you go down and a friend can revive you (F3); off = death works as in the base game / Yere yigilma: ortak dunyada kimse kalici olarak olmez. Kondisyon sifira inince oyuncu yere yigilir, arkadasi kaldirabilir (F3); kapatilirsa olum oyunun kendisindeki gibi calisir");
            _downedMinutes = _cat.CreateEntry("YerdeKalmaDakika", 10, "How long a downed player waits to be revived (minutes) / Yere yigilan oyuncunun kaldirilmayi bekleyecegi sure (dakika)");
            _sleepPlace = _cat.CreateEntry("SonUyunanYer", "", "Where you last slept (written by the mod, do not edit) / Son uyunan yer (mod kendisi yazar, elle degistirme)");
            Downed = new DownedSync(Net, Players, () => Items.SharedWorld, () => _downed.Value, () => _downedMinutes.Value, _sleepPlace.Value,
                place => { _sleepPlace.Value = place; _cat.SaveToFile(false); });
            try { Downed.ApplyPatches(HarmonyInstance); } catch (Exception e) { Log("Downed and revive hooks: " + e.Message); }
            _figure = _cat.CreateEntry("Figur", true, "Your friend is drawn as a person (walking, crouching, lying, holding a light); off = the old capsule / Arkadas insan figuru olarak cizilir (yuruyen, comelen, yatan, isik tutan bir insan); kapatilirsa eski kapsul cizilir");
            RemotePlayers.FigureWanted = () => _figure.Value;
            _look = _cat.CreateEntry("KiyafetGorunumu", true, "Your friend's figure shows what they really wear (hat, scarf, gloves, colours); off = everyone gets the same clothes / Arkadasin figuru onun gercekten giydiklerini gosterir (sapka, atki ve eldiven var mi, parcalarin renkleri); kapatilirsa herkes sabit kiyafetle cizilir");
            Looks = new LookSync(Net, Players, () => _look.Value);
            _whistleKey = _cat.CreateEntry("IslikTusu", "B", "Whistle key (a letter, a digit, F1, F2 or F12): friends within 250 m in the same region hear where it comes from. If the game uses the key, the mod picks a free one and logs it / Islik tusu (bir harf, bir rakam, F1, F2 ya da F12): ayni bolgede 250 m icindeki arkadaslar nereden geldigini duyar. Oyun bu tusu kullaniyorsa mod bos bir tusa gecer ve bunu gunluge yazar");
            _giveKey = _cat.CreateEntry("VerTusu", "V", "Give key: hands the item in your hands to the friend standing within 2.5 m in front of you / Verme tusu: elindeki esyayi 2.5 m icinde, onunde duran arkadasina verir");
            _whistleSound = _cat.CreateEntry("IslikSesi", WhistleSync.DefaultSounds, "Game sounds to try for the whistle (the game's own sound event names, comma separated; the first that plays is used) / Islik icin denenecek oyun sesleri (oyunun kendi ses olayi adlari, virgulle, sirayla; ilk calabilen kullanilir)");
            _talkKey = _cat.CreateEntry("KonusTusu", "T", "Push-to-talk key: your friends hear you while you hold it (louder when closer) / Bas-konus tusu: basili tuttugun surece arkadaslarin seni duyar (yakinda olanlar daha yuksek)");
            ModKeys.Configure(_whistleKey.Value, _giveKey.Value, _talkKey.Value);
            Whistle = new WhistleSync(Net, () => _whistleSound.Value);
            Handover = new HandoverSync(Net, Players, () => Items.SharedWorld);
            _friendlyFire = _cat.CreateEntry("DostAtesi", true, "Friendly fire: shooting your friend (rifle, revolver, flare gun, arrow) hurts them / Arkadasini vurursan (tufek, tabanca, isaret fisegi, ok) hasar alir");
            Map = new MapShare(Net, () => Items.SharedWorld);
            _workTime = _cat.CreateEntry("IsSuresi", true, "Long jobs (harvesting, quartering, crafting, reading...) do not skip the shared clock: you stay at it until its time has passed (F8 carries on without waiting) / Uzun isler (et cikarma, parcalama, yapma, okuma...) ortak saati atlatmaz: is bitince suresi dolana kadar basinda kalirsin (F8 beklemeden devam)");
            Work = new WorkTime(Net, () => _workTime.Value, () => Items.SharedWorld);
            _voiceOn = _cat.CreateEntry("SesliKonusma", true, "Proximity voice chat (the microphone is on while the push-to-talk key is held) / Yakin mesafe sesli konusma (bas-konus tusu basili tutulunca mikrofon acilir)");
            _lang = _cat.CreateEntry("Dil", "auto", "Language of the mod: auto (Turkish if the game is in Turkish, English otherwise), tr or en / Modun dili: auto (oyun Turkce ise Turkce, degilse Ingilizce), tr ya da en");
            L.Setting = _lang.Value;
            _voiceRange = _cat.CreateEntry("SesMenzili", 40f, "Farthest distance your friend's voice carries (metres) / Arkadasin sesinin duyuldugu en uzak mesafe (metre)");
            Voice = new VoiceChat(Net, Players, () => _voiceOn.Value, () => _voiceRange.Value, () => Items.SharedWorld);
            Fire2 = new FriendlyFire(Net, Players, () => _friendlyFire.Value, () => Items.SharedWorld);
            Net.OnRemoteJoined += (id, name) => { Players.Add(id); World.ForceSendSoon(); };
            Net.OnRemoteLeft += id => Players.Remove(id);

            CleanupOldCoopSlots();
            try { Harness = new TestHarness(this); } catch (Exception e) { Log("Test harness: " + e.Message); }
            Log($"Loaded v{ModVersion}. Co-op is in the main menu and the Esc menu (CO-OP / ORTAK OYUN). Keys: F3 revive / give up (hold), F6 status, F8 get up / skip a long job's wait, F9 host, F10 join, F11 leave, {ModKeys.Talk.Name} talk, {ModKeys.Whistle.Name} whistle, {ModKeys.Give.Name} give. Feedback: {L.FeedbackMail}");
        }

        public override void OnApplicationQuit() { try { Voice?.Shutdown(); } catch { } try { PortOpener.Close(); } catch { } Net?.Stop(); }

        public override void OnUpdate()
        {
            // The game stops ticking when its window loses focus; a host must keep answering
            // its friends, so keep the player loop running in the background.
            if (!Application.runInBackground) Application.runInBackground = true;
            HandleKeys();
            MovieGuard.Update();
            Harness?.Update(); // test mode only: commands from UserData/TLDCoop-cmd.txt
            Net.Poll();

            string scene = CurrentScene();
            Downed?.Update(); // before the state is sent: who went down or got up this frame goes out this frame
            if (Net.IsConnected && WorldSync.InGame())
            {
                float now = Time.realtimeSinceStartup;
                if (now >= _nextStateSend)
                {
                    _nextStateSend = now + 0.05f; // 20 Hz
                    SendLocalState(scene);
                }
            }
            PauseSync.Update();
            Sleep.Update(); // before the clock is sent: what is decided this frame goes out this frame
            World.Update();
            Transfer.Update();
            Handoff.Update();
            Items.Update();
            Animals.Update();
            Looks?.Update(); // before the figures are drawn: a look that arrived this frame is worn this frame
            Players.Update(WorldSync.InGame() ? scene : null);
            try { Work.Update(); } catch (Exception e) { if (!_saidWork) { _saidWork = true; Log("Job time: " + e); } }
            try { CoopMenu.Update(); } catch { }
            try { ModKeys.Refresh(Time.realtimeSinceStartup); Whistle.Update(); Handover.Update(); Map.Update(); Voice.Update(TalkHeld()); ReportPort(); Hello(); }
            catch (Exception e) { if (!_saidSignals) { _saidSignals = true; Log("Whistle and hand-over: " + e.Message + " (said once)"); } }
        }
        private bool _saidSignals;

        public override void OnLateUpdate() { Sleep?.LateUpdate(); Downed?.LateUpdate(); }

        private Vector3 _lastSentPos; private float _lastSentYaw;
        private void SendLocalState(string scene)
        {
            try
            {
                var t = GameManager.GetPlayerTransform();
                if (t == null) return;
                var p = t.position;
                float yaw = t.eulerAngles.y;
                var cam = GameManager.GetVpFPSCamera();
                sbyte pitch = 0; // where the player looks, up or down: the friend's figure tilts its head with it
                if (cam != null)
                {
                    var look = cam.transform.eulerAngles;
                    yaw = look.y;
                    float px = look.x > 180f ? look.x - 360f : look.x; // Unity counts looking down as positive
                    pitch = (sbyte)Mathf.Clamp(Mathf.RoundToInt(px), -89, 89);
                }
                bool crouch = false;
                var pm = GameManager.GetPlayerManagerComponent();
                if (pm != null) crouch = pm.PlayerIsCrouched() || (Work != null && Work.Kneeling);

                byte light = 0;
                try { light = LightSync.HeldLight(pm); } catch { }

                // moving or looking around is playing, whatever the input device
                if ((p - _lastSentPos).sqrMagnitude > 0.0004f || Mathf.Abs(Mathf.DeltaAngle(yaw, _lastSentYaw)) > 0.5f) PauseSync.Touch();
                _lastSentPos = p; _lastSentYaw = yaw;

                var w = Net.Begin(Msg.PlayerState);
                w.Put(Net.LocalId); w.Put(scene ?? ""); w.Put(p.x); w.Put(p.y); w.Put(p.z); w.Put(yaw); w.Put(crouch); w.Put(light); w.Put(PauseSync.LocalAway);
                w.Put(SleepSync.LocalSleep());
                w.Put(Downed != null ? Downed.LocalByte : (byte)0); w.Put(Downed != null ? Downed.LocalSecondsLeft : (ushort)0);
                w.Put(pitch); // new fields go at the end: an older reader simply stops before them
                Net.Send(w, DeliveryMethod.Sequenced);
            }
            catch (Exception e) { Log("State send failed: " + e.Message); _nextStateSend = Time.realtimeSinceStartup + 2f; }
        }

        // ---------------- controls ----------------
        // The game strips Unity's IMGUI (GUI.Box/Button/TextField), so there is no on-screen panel:
        // everything is hotkeys + the game's own HUD messages. Settings live in UserData/MelonPreferences.cfg.
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private const int VK_F4 = 0x73, VK_F5 = 0x74, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;

        private bool Pressed(int vk)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            bool was = _keyWasDown[vk];
            _keyWasDown[vk] = down;
            return down && !was && Application.isFocused;
        }

        private void HandleKeys()
        {
            if (Pressed(VK_F6)) ShowStatus();
            if (Pressed(VK_F5)) TestTeleport();
            if (Pressed(VK_F4)) TestHuntingKit();
            if (Pressed(VK_F8)) { if (Work != null && Work.Abort()) Hud(L.T("Beklemeden devam ediyorsun (işin süresi dünyaya sayılmadı)", "You carry on without waiting (the job's time was not added to the world)")); else Sleep?.WakeKey(); } // get up early while waiting for the others to sleep
            if (Pressed(VK_F9)) Host();
            if (Pressed(VK_F10))
            {
                // pressing again while a join is under way would restart it and pile up save slots
                if (Net.Mode == NetMode.Client && (Transfer.Busy || Net.Connecting)) ShowStatus();
                else
                {
                    // an address copied (from a message, say) is taken straight from the clipboard: nobody has to edit the settings file
                    string clip = ClipboardAddress();
                    if (clip != null && clip != _joinAddress.Value)
                    {
                        _joinAddress.Value = clip; _cat.SaveToFile(false);
                        Log("Join address taken from the clipboard: " + clip);
                    }
                    Join(_joinAddress.Value);
                }
            }
            if (Pressed(VK_F11)) Disconnect();

            // the two letter keys only do something while playing together, and never while a menu or a text box is up
            bool whistle = Pressed(ModKeys.Whistle.Vk), give = Pressed(ModKeys.Give.Vk);
            if ((whistle || give) && Net.IsConnected && WorldSync.InGame() && !OverlayUp())
            {
                if (whistle) { try { Whistle.Press(); } catch (Exception e) { Log("Whistle key: " + e.Message); } }
                if (give) Handover.KeyPressed();
            }
        }

        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);

        /// <summary>The clipboard's text if it looks like a host address (1.2.3.4, name.example.com, either with :port); else null.</summary>
        internal static string ClipboardAddress()
        {
            string text = null;
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return null;
                try
                {
                    var h = GetClipboardData(13); // CF_UNICODETEXT
                    if (h == IntPtr.Zero) return null;
                    var p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { text = Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            }
            catch { return null; }
            return LooksLikeAddress(text);
        }

        internal static string LooksLikeAddress(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length < 4 || text.Length > 100) return null;
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^[A-Za-z0-9][A-Za-z0-9.\-]*\.[A-Za-z0-9.\-]*[A-Za-z0-9](:\d{1,5})?$")) return null;
            return text;
        }

        private bool TalkHeld()
        {
            try { return (GetAsyncKeyState(ModKeys.Talk.Vk) & 0x8000) != 0 && Application.isFocused && !OverlayUp(); } catch { return false; }
        }


        private static bool OverlayUp()
        {
            try { return InterfaceManager.IsOverlayActiveImmediate(); } catch { return false; }
        }

        private void ShowStatus()
        {
            string who = Net.Mode switch { NetMode.Host => L.T("Sunucu", "Host"), NetMode.Client => L.T("Katılımcı", "Joined"), _ => L.T("Kapalı", "Off") };
            string names = "";
            foreach (var r in Net.Remotes.Values) if (r.Welcomed) names += (names.Length > 0 ? ", " : "") + r.Name;
            Hud(L.T("Ortak Dünya", "Shared World") + $" {ModVersion} | {who} | {Net.Status}" + (names.Length > 0 ? $" | {names}" : "") + (_allowMismatch.Value ? L.T(" | test modu", " | test mode") + Distances() : ""));
            if (Net.Mode == NetMode.Host)
            {
                string lan = LanAddress(), pub = PortOpener.PublicAddress;
                Hud(L.T($"Adresin: ev ağı {lan ?? "?"}", $"Your address: home network {lan ?? "?"}") + (pub != null ? $", internet {pub}" + (PortOpener.Opened ? L.T(" (port açık)", " (port open)") : L.T(" (modemde UDP " + _port.Value + " yönlendirilmeli)", " (forward UDP " + _port.Value + " on your router)")) : ""));
            }
            Hud(L.T($"Ortak oyun: Esc menüsünde ORTAK OYUN (kısayollar F9 sunucu, F10 katıl, F11 ayrıl). Tuşlar: {ModKeys.Talk.Name} bas-konuş, {ModKeys.Whistle.Name} ıslık, {ModKeys.Give.Name} ver, F3 yerdekini kaldır",
                $"Co-op: CO-OP in the Esc menu (shortcuts F9 host, F10 join, F11 leave). Keys: {ModKeys.Talk.Name} push to talk, {ModKeys.Whistle.Name} whistle, {ModKeys.Give.Name} give, F3 revive a downed friend"));
        }

        private bool _saidHello;
        /// <summary>Once a session, the first time a game is up: how to start playing together.</summary>
        private void Hello()
        {
            if (_saidHello || Net.Mode != NetMode.Off || !WorldSync.Settled()) return;
            _saidHello = true;
            Hud(L.T("Ortak Dünya modu hazır: menüde ORTAK OYUN (F6 yardım)", "Shared World mod ready: CO-OP in the menu (F6 help)"));
        }

        /// <summary>Test mode only: a rifle, a bow and ammunition, to try hunting together.</summary>
        internal void TestHuntingKit()
        {
            if (!_allowMismatch.Value) { Hud(L.T("Av seti sadece test modunda", "The hunting kit is for test mode only")); return; }
            if (!WorldSync.InGame()) return;
            var pm = GameManager.GetPlayerManagerComponent();
            if (pm == null) return;
            foreach (var (name, n) in new[] { ("GEAR_Rifle", 1), ("GEAR_RifleAmmoSingle", 20), ("GEAR_Bow", 1), ("GEAR_Arrow", 10), ("GEAR_Stone", 5), ("GEAR_Knife", 1), ("GEAR_Hatchet", 1) })
            {
                try { pm.AddItemCONSOLE(name, n, 1f); } catch (Exception e) { Log($"Test kit {name}: {e.Message}"); }
            }
            Hud(L.T("Test: tüfek, 20 mermi, yay, 10 ok, 5 taş, bıçak, balta eklendi", "Test: rifle, 20 rounds, bow, 10 arrows, 5 stones, knife, hatchet added"));
        }

        /// <summary>Test mode only: jump next to the first other player in this region.</summary>
        private void TestTeleport()
        {
            if (!_allowMismatch.Value) { Hud(L.T("Işınlanma sadece test modunda", "Teleporting is for test mode only")); return; }
            if (!WorldSync.InGame()) return;
            string scene = CurrentScene();
            foreach (var r in Net.Remotes.Values)
            {
                if (!Players.TryGetState(r.Id, out var s, out var pos)) continue;
                if (s != scene) { Hud(L.T($"{r.Name} başka bölgede: {s}", $"{r.Name} is in another region: {s}")); continue; }
                var pm = GameManager.GetPlayerManagerComponent();
                if (pm == null) return;
                var target = pos + new Vector3(2f, 0.5f, 0f);
                pm.TeleportPlayer(target, GameManager.GetPlayerTransform().rotation);
                Hud(L.T($"{r.Name} yanına ışınlandın", $"You teleported to {r.Name}"));
                return;
            }
            Hud(L.T("Yakında kimse yok", "Nobody is nearby"));
        }

        private string Distances()
        {
            string scene = CurrentScene(), outS = "";
            var me = GameManager.GetPlayerTransform();
            foreach (var r in Net.Remotes.Values)
            {
                if (!Players.TryGetState(r.Id, out var s, out var pos)) continue;
                outS += s == scene && me != null ? $" | {r.Name}: {Vector3.Distance(me.position, pos):0} m" : $" | {r.Name}: {s}";
            }
            return outS;
        }

        internal void Host()
        {
            if (Net.Mode == NetMode.Host) { ShowStatus(); return; }
            try
            {
                if (WorldSync.InGame() && SaveMerge.IsCoopSlot(SaveGameSystem.GetCurrentSaveName()))
                { Hud(L.T("Bu ORTAK DUNYA kaydı: sunucu açmak için kendi kaydını yükle", "This is the ORTAK DUNYA (shared world) save: load your own save to host")); return; }
            }
            catch { }
            Players.Clear();
            Net.StartHost(_port.Value, _maxPlayers.Value, _name.Value);
            Hud(Net.Status);
            string lan = LanAddress();
            if (Net.Mode == NetMode.Host && lan != null)
            {
                Hud(L.T($"Sunucu açık. Esc menüsü, ORTAK OYUN: adresini kopyalayıp arkadaşına gönder (ev ağı {lan}).", $"Server open. Esc menu, CO-OP: copy your address and send it to your friend (home network {lan})."));
                Log("LAN address of this PC: " + lan);
            }
            if (Net.Mode == NetMode.Host && _openPort.Value) { PortOpener.OpenAsync(_port.Value, lan); _portReportAt = Time.realtimeSinceStartup + 8f; }
        }

        private float _portReportAt = -1f;
        /// <summary>Main thread: say once what the modem said and which address a friend from outside uses.</summary>
        private void ReportPort()
        {
            if (_portReportAt < 0f || Time.realtimeSinceStartup < _portReportAt) return;
            _portReportAt = -1f;
            string pub = PortOpener.PublicAddress;
            Log($"Port opener: {PortOpener.Status}; internet address {pub ?? "unknown"}");
            if (Net.Mode != NetMode.Host) return;
            if (PortOpener.Opened && pub != null) Hud(L.T($"Modemde port açıldı. İnternet adresin: {pub} (ORTAK OYUN menüsünden kopyalanabilir)", $"Port opened on your router. Your internet address: {pub} (copy it from the CO-OP menu)"));
            else if (pub != null) Hud(L.T($"Modem portu kendisi açmadı: başka evden bağlanmak için modemde UDP {_port.Value} yönlendir. İnternet adresin: {pub}", $"Your router did not open the port itself: to join from another home, forward UDP {_port.Value} on your router. Your internet address: {pub}"));
        }

        /// <summary>This PC's address on the home network (192.168.x.x and the like), or null.</summary>
        internal static string LanAddress()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue; // the network that leads somewhere
                    foreach (var ua in props.UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        var b = a.GetAddressBytes();
                        if (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)) return a.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        internal void Join(string address)
        {
            address = (address ?? "").Trim();
            if (address.Length == 0) { Hud(L.T("Bir adres yaz: ORTAK OYUN > ADRESİ YAZ", "Type an address: CO-OP > TYPE ADDRESS")); return; }
            int port = _port.Value;
            var colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out var p)) { port = p; address = address.Substring(0, colon); }
            Players.Clear();
            Net.StartClient(address, port, _name.Value);
            Hud(Net.Status);
        }

        internal void Disconnect()
        {
            PortOpener.Close();
            Net.Stop();
            Players.Clear();
            Hud(L.T("Ortak oyun kapatıldı", "Co-op stopped"));
        }

        // ---------------- helpers ----------------
        internal static string CurrentScene()
        {
            try { return GameManager.m_ActiveScene; } catch { return null; }
        }

        /// <summary>
        /// At game start (before the game reads its saves) remove outdated co-op copies, keeping the newest:
        /// deleting them while playing raced with the game's own save handling.
        /// </summary>
        private void CleanupOldCoopSlots()
        {
            try
            {
                var list = new System.Collections.Generic.List<string>(_coopSlots.Value.Split(',', StringSplitOptions.RemoveEmptyEntries));
                var keep = SaveMerge.FixedCoopSlot;
                if (list.Count == 0 || (list.Count == 1 && list[0] == keep)) return;
                var dir = SaveMerge.SaveDir();
                int removed = 0;
                foreach (var name in list)
                {
                    if (name == keep) continue;
                    var p = System.IO.Path.Combine(dir, name);
                    if (System.IO.File.Exists(p)) { System.IO.File.Delete(p); removed++; }
                }
                _coopSlots.Value = keep;
                _cat.SaveToFile(false);
                if (removed > 0) Log($"Removed {removed} outdated co-op save(s), kept {keep}");
            }
            catch (Exception e) { Log("Co-op save cleanup: " + e.Message); }
        }

        private static string _cachedSave; private static float _cachedSaveAt;
        internal static bool CurrentSaveIs(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            if (Time.realtimeSinceStartup - _cachedSaveAt > 1f)
            {
                _cachedSaveAt = Time.realtimeSinceStartup;
                try { _cachedSave = SaveGameSystem.GetCurrentSaveName(); } catch { _cachedSave = null; }
            }
            return _cachedSave == slot;
        }

        /// <summary>Main menu has no HUD, so show a game popup there; in game use the HUD line.</summary>
        internal static void Popup(string title, string message)
        {
            Log($"{title}: {message}");
            if (WorldSync.InGame()) { Hud(message); return; }
            try
            {
                var panel = UnityEngine.Object.FindObjectOfType<Panel_Confirmation>();
                if (panel == null) panel = InterfaceManager.GetPanel<Panel_Confirmation>();
                if (panel == null) Log("Popup: confirmation panel not available");
                panel?.AddConfirmation(Panel_Confirmation.ConfirmationType.GenericMessage, title, message,
                    Panel_Confirmation.ButtonLayout.Button_1, Panel_Confirmation.Background.Transperent, null, null, "TLDCoop");
            }
            catch (Exception e) { Log("Popup failed: " + e.Message); }
        }

        /// <summary>Our own message boxes from the main menu must not stay open over a game that has loaded meanwhile.</summary>
        internal static void DismissPopups()
        {
            try
            {
                var panel = InterfaceManager.GetPanel<Panel_Confirmation>();
                if (panel == null) return;
                int n = 0;
                for (int i = 0; i < 5 && panel.IsConfirmationEnabled(Panel_Confirmation.ConfirmationType.GenericMessage); i++)
                {
                    panel.CancelConfirmation(Panel_Confirmation.ConfirmationType.GenericMessage); n++;
                }
                if (n > 0) Log($"Closed {n} message box(es) left open from the main menu");
            }
            catch (Exception e) { Log("Closing message boxes: " + e.Message); }
        }

        internal static string GameVersion()
        {
            try { return GameManager.m_GameVersionString ?? "?"; } catch { return "?"; }
        }

        // own log per game process: two copies of the game on one PC share MelonLoader's log files
        internal static readonly string OwnLog = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
            $"TLDCoop-{DateTime.Now:HHmmss}-{Environment.ProcessId}.log");
        internal static void Log(string s)
        {
            MelonLogger.Msg(s);
            try { System.IO.File.AppendAllText(OwnLog, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n"); } catch { }
        }

        internal static void Status(string s)
        {
            Instance?.Net?.SetStatus(s);
            Hud(s);
        }

        internal static void Hud(string s)
        {
            Log(s);
            try { if (WorldSync.InGame()) HUDMessage.AddMessage(s, 4f, false, true); } catch { }
        }
    }
}
