using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// "ORTAK OYUN" in the game's own main menu and pause menu (Esc), drawn by the game's own menu (BasicMenu), so it
    /// looks like the rest of the game. Choosing it swaps the menu's items for ours: open/close the server, join the
    /// friend (typed address, or one copied from a message), copy this PC's address to send to the friend, leave,
    /// back. The F keys keep working as shortcuts.
    /// </summary>
    internal static class CoopMenu
    {
        private const string EntryId = "TLDCoop";
        private const int EntryValue = 0x544C4443; // "TLDC"

        // ---- what is shown now ----
        private static BasicMenu _menu;
        private static Action _back;
        private static bool _inSub;
        private static string _shownKey = "";
        private static float _nextCheck;

        // ---- for the state dump ----
        internal static int EntriesAdded, SubOpened, Actions;
        internal static string Last = "-", LastError = "-";

        // ---- the address box (the game's rename window, made to accept addresses) ----
        private static float _fixInputUntil = -1f;
        private static string _typed;

        private static CoopMod Mod => CoopMod.Instance;

        /// <summary>Called after the game has built its main menu or pause menu: add our entry before the last one (Quit).</summary>
        internal static void AddEntry(BasicMenu m, Action reconfigure)
        {
            if (m == null) return;
            _inSub = false; _menu = m; _back = reconfigure;
            try
            {
                if (m.HasActiveItemByID(EntryId)) return;
                int n = m.GetItemCount();
                m.AddItem(EntryId, EntryValue, n, L.T("ORTAK OYUN", "CO-OP"), L.T("Arkadaşınla aynı dünyada oyna: sunucu aç ya da arkadaşına katıl.", "Play in the same world with your friend: host a server or join your friend."), null,
                    (Action)(() => Open(m, reconfigure)), Color.gray, Color.white);
                MoveBeforeLast(m);
                m.Refresh();
                EntriesAdded++;
            }
            catch (Exception e) { Fail("adding the menu entry", e); }
        }

        /// <summary>Put our entry just above the last item (Quit), and number the items again in their new order.</summary>
        private static void MoveBeforeLast(BasicMenu m)
        {
            try
            {
                var list = m.m_ItemModelList;
                if (list == null || list.Count < 3) return;
                int last = list.Count - 1;
                var ours = list[last];
                if (ours == null || ours.m_Id != EntryId) return;
                list.RemoveAt(last);
                list.Insert(last - 1, ours);
                for (int i = 0; i < list.Count; i++) if (list[i] != null) list[i].m_ItemIndex = i;
            }
            catch (Exception e) { Fail("moving the entry above Quit", e); }
        }

        private static void Open(BasicMenu m, Action reconfigure)
        {
            _menu = m; _back = reconfigure;
            SubOpened++;
            Build();
        }

        /// <summary>Our items, from what is going on now. Rebuilt after every choice and when the state changes.</summary>
        private static void Build()
        {
            var m = _menu; var mod = Mod;
            if (m == null || mod == null) return;
            try
            {
                m.Reset();
                int i = 0;
                var net = mod.Net;
                string status = StatusLine();
                Add(m, ref i, "status", status, L.T("Ortak oyunun şu anki durumu.", "What co-op is doing right now."), null, false);

                if (net.Mode == NetMode.Off)
                {
                    Add(m, ref i, "host", L.T("SUNUCU AÇ", "HOST"), WorldSync.InGame()
                        ? L.T("Bu dünyayı arkadaşınla paylaş. Sonra adresini kopyalayıp arkadaşına gönder.", "Share this world with your friend. Then copy your address and send it to them.")
                        : L.T("Sunucuyu aç, sonra kendi kaydını yükle. Arkadaşın bağlanınca dünyan ona gönderilir.", "Open the server, then load your own save. When your friend connects, your world is sent to them."), () => { mod.Host(); Done("sunucu açıldı"); });
                    string addr = mod.JoinAddress;
                    string clip = CoopMod.ClipboardAddress();
                    if (clip != null && clip != addr)
                        Add(m, ref i, "joinclip", L.T("KATIL (PANODAKİ): ", "JOIN (COPIED): ") + clip, L.T("Arkadaşının gönderdiği, kopyaladığın adrese bağlan.", "Connect to the address your friend sent you and you copied."), () => { mod.JoinFromMenu(clip); Done("panodaki adresle katılınıyor"); });
                    Add(m, ref i, "join", L.T("KATIL: ", "JOIN: ") + addr, L.T("Arkadaşının sunucusuna bu adresle bağlan.", "Connect to your friend's server at this address."), () => { mod.JoinFromMenu(addr); Done("katılınıyor"); });
                    Add(m, ref i, "type", L.T("ADRESİ YAZ", "TYPE ADDRESS"), L.T("Arkadaşının adresini elle yaz (ör. 203.0.113.5 ya da isim.duckdns.org).", "Type your friend's address (e.g. 203.0.113.5 or name.duckdns.org)."), AskAddress);
                }
                else if (net.Mode == NetMode.Host)
                {
                    string lan = CoopMod.LanAddress(), pub = PortOpener.PublicAddress;
                    if (pub != null)
                        Add(m, ref i, "copypub", L.T("İNTERNET: ", "INTERNET: ") + pub, PortOpener.Opened
                            ? L.T("Seçince kopyalanır. Başka evdeki arkadaşına gönder; o KATIL (PANODAKİ) seçer.", "Choose to copy it. Send it to a friend in another home; they choose JOIN (COPIED).")
                            : L.T("Seçince kopyalanır. Başka evdeki arkadaşına gönder. Modemde UDP " + mod.Port + " bu bilgisayara yönlendirilmiş olmalı.", "Choose to copy it. Send it to a friend in another home. Your router must forward UDP " + mod.Port + " to this PC."), () => Copy(pub));
                    if (lan != null)
                        Add(m, ref i, "copylan", L.T("EV AĞI: ", "HOME NETWORK: ") + lan, L.T("Seçince kopyalanır. Aynı evdeki (aynı modeme bağlı) arkadaşına gönder.", "Choose to copy it. Send it to a friend in the same home (on the same router)."), () => Copy(lan));
                    Add(m, ref i, "stop", L.T("SUNUCUYU KAPAT", "STOP SERVER"), L.T("Ortak oyunu bitir; arkadaşların bağlantısı kesilir.", "End co-op; your friends are disconnected."), () => { mod.Disconnect(); Done("sunucu kapatıldı"); });
                }
                else
                {
                    Add(m, ref i, "leave", L.T("AYRIL", "LEAVE"), L.T("Arkadaşının dünyasından ayrıl.", "Leave your friend's world."), () => { mod.Disconnect(); Done("ayrılındı"); });
                }
                Add(m, ref i, "feedback", L.T("GERİ BİLDİRİM: ", "FEEDBACK: ") + L.FeedbackMail, L.T("Seçince e-posta adresi kopyalanır. Hata ve önerilerini bu adrese yaz.", "Choose to copy the e-mail address. Send bugs and ideas there."), () => Copy(L.FeedbackMail, false));
                Add(m, ref i, "support", L.T("MODU DESTEKLE", "SUPPORT THE MOD"), L.T("Modun sayfasını tarayıcıda açar: indirme, eksikler ve isteğe bağlı bağış. Mod her zaman ücretsiz.", "Opens the mod's page in your browser: downloads, known issues and optional donations. The mod is always free."), OpenPage);
                Add(m, ref i, "back", L.T("GERİ", "BACK"), L.T("Önceki menüye dön.", "Back to the previous menu."), Back);
                try { m.SetBackAction((Action)Back); } catch { }
                m.Refresh();
                try { m.SetItemSelected(1); } catch { }
                _inSub = true;
                _shownKey = Key();
            }
            catch (Exception e) { Fail("building the menu", e); Back(); }
        }

        private static void Add(BasicMenu m, ref int i, string id, string label, string desc, Action act, bool selectable = true)
        {
            m.AddItem(EntryId + "_" + id, EntryValue + 1 + i, i, label, desc, null, act != null ? (Action)(() => Run(id, act)) : (Action)(() => { }), Color.gray, Color.white);
            if (!selectable) { try { m.SetItemSelectable(i, false); } catch { } }
            i++;
        }

        private static void Run(string id, Action act)
        {
            Actions++;
            try { act(); }
            catch (Exception e) { Fail("menu item " + id, e); }
            if (_inSub) Build();
        }

        private static void Done(string what) { Last = what; CoopMod.Log("Menu: " + what); }

        private static void Back()
        {
            _inSub = false;
            try { _back?.Invoke(); }
            catch (Exception e) { Fail("going back", e); }
        }

        private static string _flash; private static float _flashUntil;

        private static string StatusLine()
        {
            if (_flash != null && Time.realtimeSinceStartup < _flashUntil) return L.Pick(_flash);
            var net = Mod.Net;
            int n = 0; try { n = net.Remotes.Count; } catch { }
            if (net.Mode == NetMode.Host) return n > 0 ? L.T($"SUNUCU AÇIK: {n} ARKADAŞ BAĞLI", $"SERVER OPEN: {n} FRIEND(S) CONNECTED") : L.T("SUNUCU AÇIK: ARKADAŞ BEKLENİYOR", "SERVER OPEN: WAITING FOR A FRIEND");
            if (net.Mode == NetMode.Client) return net.IsConnected ? L.T("ARKADAŞININ DÜNYASINDASIN", "IN YOUR FRIEND'S WORLD") : (net.Status ?? "").ToUpperInvariant();
            return L.T("ORTAK OYUN KAPALI", "CO-OP IS OFF");
        }

        /// <summary>What decides the items: when this changes while our items are up, they are built again.</summary>
        private static string Key()
        {
            var net = Mod.Net; int n = 0; try { n = net.Remotes.Count; } catch { }
            bool flash = _flash != null && Time.realtimeSinceStartup < _flashUntil;
            return $"{net.Mode}|{net.IsConnected}|{n}|{PortOpener.PublicAddress}|{CoopMod.ClipboardAddress()}|{Mod.JoinAddress}|{flash}|{L.En}";
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private static bool _boxOpen, _vWasDown;

        /// <summary>The game's text box has no paste: Ctrl+V puts a copied address in while our address box is up.</summary>
        private static void PasteKey()
        {
            if (!_boxOpen) return;
            try
            {
                var conf = InterfaceManager.GetPanel<Panel_Confirmation>();
                var field = conf != null && conf.isActiveAndEnabled ? conf.m_CurrentGroup?.m_InputField : null;
                if (field == null) { _boxOpen = false; return; }
                bool v = (GetAsyncKeyState(0x56) & 0x8000) != 0, ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                if (v && ctrl && !_vWasDown && Application.isFocused)
                {
                    string clip = CoopMod.ClipboardAddress();
                    if (clip != null) { field.SetText(clip); try { if (field.m_Input != null) field.m_Input.value = clip; } catch { } Last = "pasted " + clip; }
                }
                _vWasDown = v;
            }
            catch { _boxOpen = false; }
        }

        /// <summary>Every frame: keep our items current, and make the address box accept addresses once it is up.</summary>
        internal static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (_fixInputUntil > 0f) FixInput(now);
            PasteKey();
            if (!_inSub || now < _nextCheck) return;
            _nextCheck = now + 0.5f;
            try
            {
                if (_menu == null || !_menu.gameObject.activeInHierarchy) { _inSub = false; return; }
                if (Key() != _shownKey) Build();
            }
            catch { _inSub = false; }
        }

        // ---------------- typing an address ----------------
        private static void AskAddress()
        {
            var conf = InterfaceManager.GetPanel<Panel_Confirmation>();
            if (conf == null) { Last = "no confirmation panel"; return; }
            _typed = null;
            conf.ShowRenamePanel(L.T("ARKADAŞININ ADRESİ (Ctrl+V yapıştırır)", "YOUR FRIEND'S ADDRESS (Ctrl+V pastes)"), CoopMod.ClipboardAddress() ?? Mod.JoinAddress, L.T("TAMAM", "OK"), L.T("İPTAL", "CANCEL"),
                (Action)(() =>
                {
                    _boxOpen = false;
                    string t = null;
                    try { t = conf.GetInputFieldText(); } catch { }
                    t = (t ?? "").Trim();
                    string ok = CoopMod.LooksLikeAddress(t);
                    if (ok == null) { CoopMod.Hud(L.T("Bu bir adres gibi görünmüyor: ", "That does not look like an address: ") + t); Last = "typed address refused: " + t; return; }
                    Mod.SetJoinAddress(ok);
                    Last = "address typed: " + ok;
                    CoopMod.Log("Menu: join address typed: " + ok);
                    if (_inSub) Build();
                }), null);
            _fixInputUntil = Time.realtimeSinceStartup + 3f;
            _boxOpen = true; _vWasDown = true;
            Last = "address box shown";
        }

        /// <summary>The rename window is made for save names: let it take dots, colons and dashes, and longer text.</summary>
        private static void FixInput(float now)
        {
            if (now > _fixInputUntil) { _fixInputUntil = -1f; return; }
            try
            {
                var conf = InterfaceManager.GetPanel<Panel_Confirmation>();
                var field = conf?.m_CurrentGroup?.m_InputField;
                if (field == null) return;
                field.SetMaxLength(64);
                field.m_AllowBlankText = false;
                var input = field.m_Input;
                if (input != null)
                {
                    input.validation = UIInput.Validation.None;
                    input.characterLimit = 64;
                }
                _fixInputUntil = -1f;
            }
            catch (Exception e) { _fixInputUntil = -1f; Fail("opening the address box up for addresses", e); }
        }

        // ---------------- clipboard ----------------
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);

        internal static bool SetClipboard(string text)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return false;
                try
                {
                    EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var h = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)bytes);
                    if (h == IntPtr.Zero) return false;
                    var p = GlobalLock(h);
                    var chars = (text + "\0").ToCharArray();
                    Marshal.Copy(chars, 0, p, chars.Length);
                    GlobalUnlock(h);
                    return SetClipboardData(13 /* CF_UNICODETEXT */, h) != IntPtr.Zero;
                }
                finally { CloseClipboard(); }
            }
            catch { return false; }
        }

        private static void Copy(string addr) => Copy(addr, true);

        private static void Copy(string addr, bool forFriend)
        {
            bool ok = SetClipboard(addr);
            _flash = ok ? L.Both("KOPYALANDI: " + addr, "COPIED: " + addr) : L.Both("KOPYALANAMADI", "COULD NOT COPY"); _flashUntil = Time.realtimeSinceStartup + 4f;
            if (forFriend) CoopMod.Hud(ok ? L.T($"Kopyalandı: {addr}. Mesajla arkadaşına gönder.", $"Copied: {addr}. Send it to your friend in a message.") : L.T("Kopyalanamadı: adresi elle yaz: ", "Could not copy it, type it yourself: ") + addr);
            else CoopMod.Hud(ok ? L.T($"Kopyalandı: {addr}. Hata ve önerilerini bu adrese yaz, teşekkürler!", $"Copied: {addr}. Send bugs and ideas there, thank you!") : addr);
            Done(ok ? "copied " + addr : "copy failed");
        }

        /// <summary>The mod's page (downloads, known issues, optional donations) in the player's browser.</summary>
        private static void OpenPage()
        {
            try { Application.OpenURL(L.ProjectPage); Done("page opened"); }
            catch (Exception e) { Fail("opening the page", e); }
            _flash = L.Both("SAYFA TARAYICIDA AÇILDI", "PAGE OPENED IN YOUR BROWSER"); _flashUntil = Time.realtimeSinceStartup + 4f;
            CoopMod.Hud(L.T("Modun sayfası tarayıcıda açıldı: ", "The mod's page opened in your browser: ") + L.ProjectPage);
        }

        private static void Fail(string what, Exception e)
        {
            LastError = what + ": " + e.GetType().Name + ": " + e.Message;
            CoopMod.Log("Menu: " + LastError);
        }

        internal static void WriteState(System.Text.StringBuilder sb)
        {
            sb.Append("menu_entries_added: ").Append(EntriesAdded).Append('\n');
            sb.Append("menu_in_sub: ").Append(_inSub).Append(" opened=").Append(SubOpened).Append(" actions=").Append(Actions).Append('\n');
            sb.Append("menu_last: ").Append(Last).Append('\n');
            sb.Append("menu_error: ").Append(LastError).Append('\n');
            try
            {
                if (_menu != null)
                {
                    var list = _menu.m_ItemModelList; var names = new List<string>();
                    if (list != null) for (int i = 0; i < list.Count; i++) names.Add(list[i]?.m_LabelText ?? "?");
                    sb.Append("menu_items: ").Append(string.Join(" | ", names)).Append('\n');
                }
            }
            catch { }
        }

        // ---------------- test harness ----------------
        /// <summary>menu open|click &lt;id&gt;|back: drive our items as a player would (the main or pause menu must be up).</summary>
        internal static string Test(string what, string arg)
        {
            if (what == "open")
            {
                var pause = InterfaceManager.GetPanel<Panel_PauseMenu>();
                var main = InterfaceManager.GetPanel<Panel_MainMenu>();
                BasicMenu m = null; Action back = null;
                if (pause != null && pause.isActiveAndEnabled) { m = pause.m_BasicMenu; back = () => pause.ConfigureMenu(); }
                else if (main != null && main.isActiveAndEnabled) { m = main.m_BasicMenu; back = () => main.ConfigureMenu(); }
                if (m == null) return "no main or pause menu is up";
                Open(m, back);
                return null;
            }
            if (what == "click")
            {
                if (_menu == null) return "no menu";
                var list = _menu.m_ItemModelList;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && (list[i].m_Id == EntryId + "_" + arg || list[i].m_Id == arg)) { list[i].m_Action?.Invoke(); return null; }
                return "no item " + arg;
            }
            if (what == "back") { Back(); return null; }
            return "usage: coopmenu open|click <id>|back";
        }
    }

    [HarmonyPatch(typeof(Panel_MainMenu), nameof(Panel_MainMenu.ConfigureMenu))]
    internal static class CoopMenuMainPatch
    {
        private static void Postfix(Panel_MainMenu __instance)
        {
            try { var p = __instance; CoopMenu.AddEntry(p.m_BasicMenu, () => p.ConfigureMenu()); }
            catch (Exception e) { CoopMod.Log("Menu (main): " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(Panel_PauseMenu), nameof(Panel_PauseMenu.ConfigureMenu))]
    internal static class CoopMenuPausePatch
    {
        private static void Postfix(Panel_PauseMenu __instance)
        {
            try { var p = __instance; CoopMenu.AddEntry(p.m_BasicMenu, () => p.ConfigureMenu()); }
            catch (Exception e) { CoopMod.Log("Menu (pause): " + e.Message); }
        }
    }
}
