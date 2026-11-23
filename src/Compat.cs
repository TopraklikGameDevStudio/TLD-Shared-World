using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2Cpp;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// The few game members that differ between game versions (2.55 on Epic and older Steam builds, 2.60 on Steam).
    /// Anything listed here is reached by reflection, so one DLL runs on both. tools/refcheck lists such members:
    /// a member that one version lacks must never be named directly in code, or the whole method that names it
    /// fails to load ("Method not found") on that version.
    /// </summary>
    internal static class Compat
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static bool? _newSpawn;
        /// <summary>2.60 and later: spawn regions hand out AssetReferenceBaseAi. 2.55: an animal table of AssetReferenceAnimalPrefab.</summary>
        internal static bool NewAnimalSpawn
        {
            get
            {
                if (_newSpawn == null)
                {
                    try { _newSpawn = typeof(SpawnRegion).GetProperty("m_SpawnList", Inst) != null; }
                    catch { _newSpawn = true; }
                    CoopMod.Log("Game build: animal spawning " + (_newSpawn == true ? "2.60 style (spawn list)" : "2.55 style (animal table)"));
                }
                return _newSpawn.Value;
            }
        }

        /// <summary>Empty a container's "still to be made" list (strings in 2.60, prefabs in 2.55).</summary>
        internal static void ClearGearToInstantiate(Container c)
        {
            if (c == null) return;
            try
            {
                var list = typeof(Container).GetProperty("m_GearToInstantiate", Inst)?.GetValue(c);
                list?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(list, null);
            }
            catch { }
        }

        private static PropertyInfo _prevMode; private static bool _prevModeLooked;
        /// <summary>
        /// The control mode the game returns to after the current one. 2.60: m_PreviousControlMode;
        /// 2.55 has no such field (it keeps m_InteractionRestoreControlMode for the same job).
        /// If it is our lock, make it Normal so the game never "goes back" to being locked.
        /// </summary>
        internal static void UnlockReturnMode(PlayerManager pm)
        {
            if (pm == null) return;
            try
            {
                if (!_prevModeLooked)
                {
                    _prevModeLooked = true;
                    _prevMode = typeof(PlayerManager).GetProperty("m_PreviousControlMode", Inst)
                             ?? typeof(PlayerManager).GetProperty("m_InteractionRestoreControlMode", Inst);
                }
                if (_prevMode == null) return;
                if ((PlayerControlMode)_prevMode.GetValue(pm) == PlayerControlMode.Locked) _prevMode.SetValue(pm, PlayerControlMode.Normal);
            }
            catch { }
        }

        // ---------------- 2.55 animal spawning ----------------

        /// <summary>
        /// 2.55: make one animal of each kind this region's table can make, until one matches.
        /// The table gives asset references; the prefab is loaded (once, synchronously) and handed to InstantiateSpawn.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static BaseAi SpawnFromTable255(SpawnRegion region, WildlifeMode wm, Vector3 pos, float yaw, Func<BaseAi, bool> keep)
        {
            if (region == null) return null;
            var table = typeof(SpawnRegion).GetProperty("m_SpawnRegionAnimalTableSO", Inst)?.GetValue(region);
            if (table == null) return SpawnFromPrefab255(region, wm, pos, yaw, keep);
            var tType = table.GetType();
            var list = tType.GetProperty("m_SpawnAnimalsList", Inst)?.GetValue(table);
            var getFor = tType.GetMethod("GetEntryAnimalFromMode", Inst);
            var inst = FindInstantiate255();
            if (list == null || inst == null) { Diag($"table list {(list != null)}, InstantiateSpawn {(inst != null)}"); return null; }
            int n = (int)list.GetType().GetProperty("Count").GetValue(list);
            var tried = new System.Text.StringBuilder();
            var item = list.GetType().GetProperty("Item");
            for (int i = 0; i < n; i++)
            {
                var entry = item.GetValue(list, new object[] { i });
                if (entry == null) continue;
                object asset = null;
                try { asset = getFor?.Invoke(table, new object[] { entry, wm }); } catch { }
                if (asset == null) { try { asset = entry.GetType().GetProperty("m_AnimalPrefab", Inst)?.GetValue(entry); } catch { } }
                // in aurora mode an entry may have no aurora animal: then its normal one
                if (asset == null || !Valid(asset)) { try { var normal = entry.GetType().GetProperty("m_AnimalPrefab", Inst)?.GetValue(entry); if (normal != null) asset = normal; } catch { } }
                var ai = Make255(region, inst, asset, wm, pos, yaw, out string why);
                tried.Append(i).Append(':').Append(why).Append(' ');
                if (ai == null) continue;
                if (keep(ai)) return ai;
                try { ai.Despawn(); } catch { UnityEngine.Object.Destroy(ai.gameObject); }
            }
            Diag($"{n} table entries, none matched ({wm}): {tried}");
            return null;
        }

        /// <summary>2.55 regions without an animal table have one prefab of their own (m_SpawnablePrefab); the game's mode (aurora) picks the variant.</summary>
        private static BaseAi SpawnFromPrefab255(SpawnRegion region, WildlifeMode wm, Vector3 pos, float yaw, Func<BaseAi, bool> keep)
        {
            GameObject prefab = null;
            try { prefab = typeof(SpawnRegion).GetProperty("m_SpawnablePrefab", Inst)?.GetValue(region) as GameObject; } catch { }
            if (prefab == null) { Diag("region has neither an animal table nor a prefab"); return null; }
            MethodInfo internalSpawn = null, spawn = FindInstantiate255();
            foreach (var m in typeof(SpawnRegion).GetMethods(Inst))
                if (m.Name == "InstantiateSpawnInternal" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(GameObject)) { internalSpawn = m; break; }
            var rot = Quaternion.Euler(0f, yaw, 0f);
            BaseAi ai = null; string why = "";
            try
            {
                if (internalSpawn != null) ai = internalSpawn.Invoke(region, new object[] { prefab, wm, pos, rot }) as BaseAi;
                else if (spawn != null) ai = spawn.Invoke(region, new object[] { prefab, null, pos, rot, AiMode.Wander, wm }) as BaseAi;
                why = ai != null ? "made " + ai.gameObject.name : "nothing made";
            }
            catch (Exception e) { why = "spawn: " + (e.InnerException ?? e).Message; }
            if (ai == null) { Diag($"prefab {prefab.name} ({wm}): {why}"); return null; }
            if (keep(ai)) return ai;
            Diag($"prefab {prefab.name} ({wm}) made {ai.gameObject.name}, not the host's kind");
            try { ai.Despawn(); } catch { UnityEngine.Object.Destroy(ai.gameObject); }
            return null;
        }

        private static readonly System.Collections.Generic.HashSet<string> _diagSaid = new();
        private static void Diag(string s) { if (_diagSaid.Count < 40 && _diagSaid.Add(s)) CoopMod.Log("Animal spawn (2.55): " + s); }

        private static bool Valid(object asset)
        {
            try { return new UnityEngine.AddressableAssets.AssetReference(((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)asset).Pointer).RuntimeKeyIsValid(); }
            catch { return false; }
        }

        private static MethodInfo _inst255;
        private static MethodInfo FindInstantiate255()
        {
            if (_inst255 != null) return _inst255;
            foreach (var m in typeof(SpawnRegion).GetMethods(Inst))
                if (m.Name == "InstantiateSpawn" && m.GetParameters().Length == 6 && m.GetParameters()[0].ParameterType == typeof(GameObject))
                    return _inst255 = m;
            return null;
        }

        private static BaseAi Make255(SpawnRegion region, MethodInfo inst, object asset, WildlifeMode wm, Vector3 pos, float yaw, out string why)
        {
            why = "no asset";
            if (asset == null) return null;
            GameObject prefab = null;
            try
            {
                var ptr = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)asset).Pointer;
                var aref = new UnityEngine.AddressableAssets.AssetReference(ptr);
                if (!aref.RuntimeKeyIsValid()) { why = "invalid key"; return null; }
                prefab = aref.Asset?.TryCast<GameObject>();
                if (prefab == null)
                {
                    var h = aref.LoadAssetAsync<GameObject>();
                    prefab = h.WaitForCompletion();
                }
            }
            catch (Exception e) { why = "load: " + e.Message; return null; }
            if (prefab == null) { why = "no prefab"; return null; }
            try
            {
                var ai = inst.Invoke(region, new object[] { prefab, asset, pos, Quaternion.Euler(0f, yaw, 0f), AiMode.Wander, wm }) as BaseAi;
                why = ai != null ? "made " + ai.gameObject.name : "InstantiateSpawn gave nothing (" + prefab.name + ")";
                return ai;
            }
            catch (Exception e) { why = "spawn: " + (e.InnerException ?? e).Message; return null; }
        }
    }
}
