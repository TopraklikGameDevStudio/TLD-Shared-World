using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Friendly fire: a shot from this game (rifle, revolver, flare gun, bow) that goes through a friend's drawn body
    /// hurts the friend. The friend's figure has no colliders (it must never be in anybody's way), so the shot is
    /// checked here against a simple body shape along the line of fire, and a wall or tree in between stops it.
    /// The hit itself is decided by the shooter and applied by the friend's game (condition loss, bleeding on a hard hit;
    /// a deadly hit goes through the friend's downed rules like any other).
    /// </summary>
    internal sealed class FriendlyFire
    {
        internal static FriendlyFire Instance;
        internal const byte Rifle = 1, Revolver = 2, Flare = 3, Arrow = 4;

        private readonly Network _net;
        private readonly RemotePlayers _players;
        private readonly Func<bool> _on;
        private readonly Func<bool> _shared;

        internal int Shots, HitsGiven, HitsTaken;
        internal string Last = "-";

        public FriendlyFire(Network net, RemotePlayers players, Func<bool> on, Func<bool> shared)
        {
            Instance = this; _net = net; _players = players; _on = on; _shared = shared;
            _net.OnMessage += OnMessage;
        }

        private bool On() { try { return _on(); } catch { return true; } }
        private bool Shared() { try { return _shared(); } catch { return false; } }
        private string Name(byte id) => _net.Remotes.TryGetValue(id, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name : L.T($"Oyuncu {id}", $"Player {id}");
        private static void Line(string s, float sec = 3f) { try { HUDMessage.AddMessage(s, sec, true, true); } catch { } }

        internal static float Range(byte w) => w switch { Rifle => 250f, Revolver => 80f, Flare => 60f, _ => 60f };
        internal static float Damage(byte w) => w switch { Rifle => 70f, Revolver => 45f, Flare => 20f, _ => 40f };
        internal static string WeaponTr(byte w) => w switch { Rifle => "tüfek", Revolver => "tabanca", Flare => "işaret fişeği tabancası", _ => "ok" };
        internal static string WeaponEn(byte w) => w switch { Rifle => "rifle", Revolver => "revolver", Flare => "flare gun", _ => "arrow" };
        private static string Weapon(byte w) => L.En ? WeaponEn(w) : WeaponTr(w);

        internal static byte KindOf(GunItem g)
        {
            try
            {
                string t = g.m_GunType.ToString();
                if (t.IndexOf("Revolver", StringComparison.OrdinalIgnoreCase) >= 0) return Revolver;
                if (t.IndexOf("Flare", StringComparison.OrdinalIgnoreCase) >= 0) return Flare;
            }
            catch { }
            return Rifle;
        }

        /// <summary>A shot left this game now along the camera's view. Returns what happened (for the log and the harness).</summary>
        internal string Shot(byte weapon)
        {
            Shots++;
            if (!On()) return Last = "friendly fire is off";
            if (!_net.IsConnected || !Shared()) return Last = "not in a shared world";
            var cam = GameManager.GetMainCamera();
            if (cam == null) return Last = "no camera";
            Vector3 o = cam.transform.position, d = cam.transform.forward;
            string scene = CoopMod.CurrentScene() ?? "";
            float range = Range(weapon);
            byte best = 255; float bestT = float.MaxValue; bool head = false;
            foreach (var b in _players.Bodies(scene))
            {
                if (!HitBody(o, d, range, b.Pos, b.Yaw, b.Crouch, b.Lying, out float t, out bool h)) continue;
                if (t < bestT) { bestT = t; best = b.Id; head = h; }
            }
            if (best == 255) return Last = $"{WeaponEn(weapon)}: missed everybody";
            // a wall, a tree, the ground in between takes the shot
            try
            {
                if (bestT > 0.6f && Physics.Raycast(o, d, out var hit, bestT - 0.3f, ~0, QueryTriggerInteraction.Ignore))
                    return Last = FormattableString.Invariant($"{WeaponEn(weapon)}: player {best} at {bestT:0.0} m is behind {hit.collider?.gameObject?.name} ({hit.distance:0.0} m)");
            }
            catch { }
            float dmg = Damage(weapon) * (head ? 1.6f : 1f);
            var w = _net.Begin(Msg.PlayerHit);
            w.Put(best); w.Put(_net.LocalId); w.Put(scene); w.Put(dmg); w.Put(weapon); w.Put(head);
            if (_net.Mode == NetMode.Host) _net.SendTo(best, w, DeliveryMethod.ReliableOrdered);
            else _net.Send(w, DeliveryMethod.ReliableOrdered);
            HitsGiven++;
            Line(L.T($"{Name(best)} oyuncusunu vurdun!", $"You shot {Name(best)}!"), 3f);
            Last = FormattableString.Invariant($"hit player {best} with the {WeaponEn(weapon)} at {bestT:0.0} m{(head ? " in the head" : "")}: {dmg:0} % sent");
            CoopMod.Log("Friendly fire: " + Last);
            return Last;
        }

        /// <summary>Does the line o + t*d (0..range) pass through the body? A standing body is an upright capsule, a lying one lies along its facing.</summary>
        internal static bool HitBody(Vector3 o, Vector3 d, float range, Vector3 feet, float yaw, bool crouch, bool lying, out float t, out bool head)
        {
            const float r = 0.32f;
            Vector3 a, b;
            if (lying)
            {
                float yr = yaw * 0.017453292f; var along = new Vector3(MathF.Sin(yr), 0f, MathF.Cos(yr));
                a = feet + Vector3.up * 0.25f - along * 0.8f; b = feet + Vector3.up * 0.25f + along * 0.8f;
            }
            else { a = feet + Vector3.up * 0.3f; b = feet + Vector3.up * (crouch ? 1.0f : 1.5f); }
            ClosestRaySegment(o, d, a, b, out t, out float s, out float dist);
            head = false;
            if (t < 0f || t > range || dist > r) return false;
            if (!lying) { var p = o + d * t; head = p.y > feet.y + (crouch ? 0.85f : 1.45f); }
            return true;
        }

        private static void ClosestRaySegment(Vector3 o, Vector3 d, Vector3 a, Vector3 b, out float t, out float s, out float dist)
        {
            Vector3 u = d, v = b - a, w0 = o - a;
            float A = Vector3.Dot(u, u), B = Vector3.Dot(u, v), C = Vector3.Dot(v, v), D = Vector3.Dot(u, w0), E = Vector3.Dot(v, w0);
            float den = A * C - B * B;
            s = den > 1e-6f ? Mathf.Clamp01((A * E - B * D) / den) : 0f;
            t = (B * s - D) / A;
            if (t < 0f) t = 0f;
            // with t fixed, the nearest point on the segment again
            s = C > 1e-6f ? Mathf.Clamp01(Vector3.Dot(o + u * t - a, v) / C) : 0f;
            dist = Vector3.Distance(o + u * t, a + v * s);
        }

        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.PlayerHit) return;
            try
            {
                byte target = r.GetByte(); byte shooter = r.GetByte(); string scene = r.GetString();
                float dmg = r.GetFloat(); byte weapon = r.GetByte(); bool head = r.GetBool();
                if (_net.Mode == NetMode.Host)
                {
                    shooter = from; // a client speaks only for itself
                    if (target != _net.LocalId)
                    {
                        var w = _net.Begin(Msg.PlayerHit);
                        w.Put(target); w.Put(shooter); w.Put(scene); w.Put(dmg); w.Put(weapon); w.Put(head);
                        _net.SendTo(target, w, DeliveryMethod.ReliableOrdered);
                        return;
                    }
                }
                if (target != _net.LocalId) return;
                Apply(shooter, scene, dmg, weapon, head);
            }
            catch (Exception e) { CoopMod.Log("Friendly fire message: " + e.Message); }
        }

        private void Apply(byte shooter, string scene, float dmg, byte weapon, bool head)
        {
            if (!On()) { Last = $"hit by player {shooter}, but friendly fire is off here"; return; }
            if (!WorldSync.InGame() || scene != CoopMod.CurrentScene()) { Last = $"hit by player {shooter} in {scene}: not here any more"; return; }
            var dn = CoopMod.Instance?.Downed;
            if (dn != null && dn.LocalDowned) { Last = $"hit by player {shooter} while down: nothing more happens"; return; }
            var cond = GameManager.GetConditionComponent();
            if (cond == null) return;
            dmg = Mathf.Clamp(dmg, 0f, 200f);
            float before = DownedSync.ConditionPercent();
            cond.AddHealth(-dmg / 100f * cond.m_MaxHP, DamageSource.BulletWound);
            bool bleed = false;
            if (dmg >= 30f)
            {
                try { var b = GameManager.GetBloodLossComponent(); if (b != null && !b.HasBloodLoss()) { b.BloodLossStart("TLDCoop friendly fire", true, AfflictionOptions.None); bleed = true; } } catch { }
            }
            HitsTaken++;
            Last = FormattableString.Invariant($"hit by player {shooter} with the {WeaponEn(weapon)}{(head ? " in the head" : "")}: condition {before:0.0} -> {DownedSync.ConditionPercent():0.0} %, bleeding started={bleed}");
            CoopMod.Log("Friendly fire: " + Last);
            Line(L.T($"{Name(shooter)} seni vurdu ({Weapon(weapon)})!", $"{Name(shooter)} shot you ({Weapon(weapon)})!"), 4f);
        }

        internal void WriteState(System.Text.StringBuilder sb)
        {
            sb.Append("ff_setting: ").Append(On() ? "on" : "off").Append('\n');
            sb.Append("ff_shots: ").Append(Shots).Append('\n');
            sb.Append("ff_hits_given: ").Append(HitsGiven).Append('\n');
            sb.Append("ff_hits_taken: ").Append(HitsTaken).Append('\n');
            sb.Append("ff_last: ").Append(Last).Append('\n');
        }
    }

    [HarmonyPatch(typeof(GunItem), nameof(GunItem.Fired))]
    internal static class FriendlyFireGunPatch
    {
        private static void Postfix(GunItem __instance)
        {
            try
            {
                var ff = FriendlyFire.Instance; if (ff == null) return;
                // only this player's own gun (an animal-held or remote one never fires through here, but be sure)
                var pm = GameManager.GetPlayerManagerComponent();
                var held = pm != null ? pm.m_ItemInHands : null;
                if (held == null || held.m_GunItem == null || held.m_GunItem.Pointer != __instance.Pointer) return;
                ff.Shot(FriendlyFire.KindOf(__instance));
            }
            catch (Exception e) { CoopMod.Log("Friendly fire (gun): " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(BowItem), nameof(BowItem.ShootArrow))]
    internal static class FriendlyFireBowPatch
    {
        private static void Postfix()
        {
            try { FriendlyFire.Instance?.Shot(FriendlyFire.Arrow); }
            catch (Exception e) { CoopMod.Log("Friendly fire (bow): " + e.Message); }
        }
    }
}
