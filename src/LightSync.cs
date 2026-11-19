using System;
using Il2Cpp;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Light carried by players: flares, torches, lanterns and flashlights.
    /// The holder only sends a number (which light is in their hand and lit);
    /// the other side puts a matching light on that player's stand-in.
    /// </summary>
    internal static class LightSync
    {
        public const byte None = 0, Flare = 1, Torch = 2, Lamp = 3, Flashlight = 4;

        /// <summary>What lit light, if any, this item is.</summary>
        public static byte LitKind(GearItem gi)
        {
            if (gi == null) return None;
            try { var f = gi.m_FlareItem; if (f != null && f.IsBurning()) return Flare; } catch { }
            try { var t = gi.m_TorchItem; if (t != null && t.IsBurning()) return Torch; } catch { }
            try { var l = gi.m_KeroseneLampItem; if (l != null && l.IsOn()) return Lamp; } catch { }
            try { var fl = gi.m_FlashlightItem; if (fl != null && fl.IsOn()) return Flashlight; } catch { }
            return None;
        }

        private static GearItem _lastHeld;

        public static byte HeldLight(PlayerManager pm)
        {
            if (pm == null) return None;
            GearItem gi = null;
            try { gi = pm.m_ItemInHands; } catch { }
            // whatever just left the hand and is now lying (or flying) in the world was thrown or dropped
            try
            {
                if (_lastHeld != null && (gi == null || gi.Pointer != _lastHeld.Pointer))
                {
                    var last = _lastHeld; _lastHeld = null;
                    bool inBag = false;
                    try { var inv = GameManager.GetInventoryComponent(); inBag = inv != null && inv.FindByInstanceID(last.m_InstanceID) != null; } catch { }
                    if (!inBag && last != null && last.gameObject != null && last.gameObject.activeInHierarchy)
                    {
                        CoopMod.Log($"{ItemSync.PrefabName(last.gameObject)} left the hand into the world");
                        ItemSync.Instance?.Track(last);
                    }
                }
            }
            catch { _lastHeld = null; }
            _lastHeld = gi;
            return LitKind(gi);
        }

        /// <summary>A lit item someone dropped or threw: light ours too.</summary>
        public static void Relight(GearItem gi, byte kind, string state = "")
        {
            if (gi == null) return;
            switch (kind)
            {
                case Flare:
                {
                    var f = gi.m_FlareItem; if (f == null) break;
                    if (!string.IsNullOrEmpty(state)) { try { f.Deserialize(state); } catch (Exception e) { CoopMod.Log("Flare load: " + e.Message); } }
                    if (f.IsBurning()) break;
                    try { f.SetState(FlareState.Burning); } catch (Exception e) { CoopMod.Log("Flare state: " + e.Message); }
                    try { f.OnIgniteComplete(); } catch { }
                    try { f.HideEffects(false); } catch { }
                    try { f.ResumeLight(); } catch { }
                    break;
                }
                case Torch:
                {
                    var t = gi.m_TorchItem; if (t == null) break;
                    if (!string.IsNullOrEmpty(state)) { try { t.Deserialize(state); } catch (Exception e) { CoopMod.Log("Torch load: " + e.Message); } }
                    if (t.IsBurning()) break;
                    try { t.SetState(TorchState.Burning); } catch (Exception e) { CoopMod.Log("Torch state: " + e.Message); }
                    try { t.OnIgniteComplete(); } catch { }
                    try { t.HideEffects(false); } catch { }
                    break;
                }
                case Lamp: gi.m_KeroseneLampItem?.TurnOn(true); break;
                case Flashlight: gi.m_FlashlightItem?.TurnOn(); break;
            }
            CoopMod.Log($"Lit the dropped {ItemSync.PrefabName(gi.gameObject)} here too (light {kind}, now {LitKind(gi)})");
        }

        // ---------------- light on a remote player's stand-in ----------------
        internal sealed class Carried
        {
            private GameObject _root;
            private Light _light;
            private Renderer _glow;
            private Transform _stick;
            private Renderer _stickR;
            private byte _kind;
            private float _seed = UnityEngine.Random.value * 100f;

            public void Set(Transform parent, byte kind)
            {
                if (kind == _kind && (_root != null || kind == None)) return;
                _kind = kind;
                if (kind == None) { if (_root != null) _root.SetActive(false); return; }
                if (_root == null) Build(parent);
                _root.SetActive(true);
                switch (kind)
                {
                    case Flare:
                        Setup(LightType.Point, new Color(1f, 0.22f, 0.15f), 3.0f, 14f, 0f, new Color(1f, 0.3f, 0.2f));
                        Shape(new Vector3(0.035f, 0.13f, 0.035f), new Vector3(0f, -0.12f, 0f), new Vector3(15f, 0f, 0f), new Color(0.75f, 0.1f, 0.08f));
                        break;
                    case Torch:
                        Setup(LightType.Point, new Color(1f, 0.55f, 0.2f), 2.2f, 10f, 0f, new Color(1f, 0.6f, 0.25f));
                        Shape(new Vector3(0.04f, 0.25f, 0.04f), new Vector3(0f, -0.24f, 0f), new Vector3(15f, 0f, 0f), new Color(0.35f, 0.22f, 0.12f));
                        break;
                    case Lamp:
                        Setup(LightType.Point, new Color(1f, 0.8f, 0.5f), 1.8f, 9f, 0f, new Color(1f, 0.85f, 0.6f));
                        Shape(new Vector3(0.12f, 0.08f, 0.12f), new Vector3(0f, -0.02f, 0f), Vector3.zero, new Color(0.45f, 0.12f, 0.1f));
                        break;
                    case Flashlight:
                        Setup(LightType.Spot, new Color(0.9f, 0.95f, 1f), 3.5f, 22f, 55f, new Color(0.9f, 0.95f, 1f));
                        Shape(new Vector3(0.045f, 0.1f, 0.045f), new Vector3(0f, 0f, -0.1f), new Vector3(90f, 0f, 0f), new Color(0.15f, 0.15f, 0.17f));
                        break;
                }
            }

            private void Build(Transform parent)
            {
                _root = new GameObject("TLDCoop_CarriedLight");
                _root.transform.SetParent(parent, false);
                // held out in front, a bit to the right, at chest height
                _root.transform.localPosition = new Vector3(0.3f, 1.35f, 0.35f);
                _light = _root.AddComponent<Light>();
                _light.shadows = LightShadows.None;

                var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = g.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
                g.transform.SetParent(_root.transform, false);
                g.transform.localScale = new Vector3(0.12f, 0.12f, 0.12f);
                _glow = g.GetComponent<Renderer>();

                // the thing in the hand (stick, lantern body, flashlight barrel)
                var st = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                var sc = st.GetComponent<Collider>(); if (sc != null) UnityEngine.Object.Destroy(sc);
                st.transform.SetParent(_root.transform, false);
                _stick = st.transform;
                _stickR = st.GetComponent<Renderer>();
            }

            /// <summary>
            /// The human figure holds the light in its raised hand: put it there (the old capsule never calls this
            /// and keeps the fixed spot in front of the chest).
            /// </summary>
            public void Place(Vector3 worldPos, Quaternion worldRot)
            {
                if (_root == null || !_root.activeSelf) return;
                var t = _root.transform;
                t.position = worldPos;
                t.rotation = worldRot;
            }

            private void Shape(Vector3 scale, Vector3 pos, Vector3 euler, Color c)
            {
                if (_stick == null) return;
                _stick.localScale = scale;
                _stick.localPosition = pos;
                _stick.localEulerAngles = euler;
                try { if (_stickR != null && _stickR.material != null) _stickR.material.color = c; } catch { }
            }

            private void Setup(LightType type, Color c, float intensity, float range, float spot, Color glow)
            {
                _light.type = type;
                _light.color = c;
                _light.intensity = intensity;
                _light.range = range;
                if (type == LightType.Spot) _light.spotAngle = spot;
                _baseIntensity = intensity;
                try
                {
                    if (_glow != null && _glow.material != null)
                    {
                        _glow.material.color = glow;
                        _glow.material.EnableKeyword("_EMISSION");
                        _glow.material.SetColor("_EmissionColor", glow * 3f);
                    }
                }
                catch { }
            }

            private float _baseIntensity;

            /// <summary>Flames flicker; electric light doesn't.</summary>
            public void Tick()
            {
                if (_root == null || !_root.activeSelf || _light == null) return;
                if (_kind == Flare || _kind == Torch)
                {
                    float t = Time.time * 9f + _seed;
                    float n = Mathf.PerlinNoise(t, _seed) * 0.35f + 0.8f;
                    _light.intensity = _baseIntensity * n;
                }
            }
        }
    }
}
