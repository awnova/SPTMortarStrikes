using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace MortarStrikes
{
    public class MortarStrikeManager : MonoBehaviour
    {
        private static ManualLogSource Log => Plugin.Log;
        private static MortarConfig Cfg => Plugin.Cfg;

        public static MortarStrikeManager Instance { get; private set; }

        private int _strikesThisRaid;
        private bool _isHost;
        private string _mapId = "";
        private AudioClip _sirenClip;
        private bool _sirenReady;
        private string _sirenInfo = "not set up";
        private bool _sirenPlaying;
        private Coroutine _sirenLoopCo;
        private bool _sirenPlayedForStrike;
        private object _betterAudio;
        private MethodInfo _playNonspatialMethod;
        private object _nonspatialGroup;
        private bool _discoveryDone;
        private object _shellingController;
        private MethodInfo _startShellingMethod;
        private string _artilleryInfo = "not discovered";
        private bool _clientPatchApplied;
        private string _clientPatchInfo = "not patched";
        private string _flareInfo = "not checked";

        private void Start()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            StartCoroutine(RaidLifecycle());
        }

        private void OnDestroy()
        {
            StopAllCoroutines();
            _sirenPlaying = false;
            FikaSync.ResetRaidState();
            if (Instance == this) Instance = null;
        }

        private void SetupSiren()
        {
            if (_sirenReady) return;
            _sirenReady = true;

            SetupBetterAudio();

            string wanted = Cfg.SirenClipName?.Trim() ?? "";
            if (!string.IsNullOrEmpty(wanted))
            {
                _sirenClip = FindClipByName(wanted);
                if (_sirenClip != null)
                    Log.LogInfo($"[Mortar] Siren clip: '{_sirenClip.name}' ({_sirenClip.length:F1}s)");
                else
                    Log.LogWarning($"[Mortar] Clip '{wanted}' not found — auto-searching...");
            }

            if (_sirenClip == null)
                _sirenClip = AutoFindBestClip();

            _sirenInfo = _sirenClip != null
                ? $"'{_sirenClip.name}' ({_sirenClip.length:F1}s), loop={Cfg.SirenLoop}"
                : "NO CLIP — check sirenClipName in config";

            Log.LogInfo($"[Mortar] Siren: {_sirenInfo}");
        }

        private AudioClip FindClipByName(string name)
        {
            var all = Resources.FindObjectsOfTypeAll<AudioClip>();
            return all.FirstOrDefault(c => c != null && c.name == name)
                ?? all.FirstOrDefault(c => c != null && c.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(c => c != null && c.name.ToLower().Contains(name.ToLower()));
        }

        private AudioClip AutoFindBestClip()
        {
            var all = Resources.FindObjectsOfTypeAll<AudioClip>();
            string[] preferred = {
                "firework_far_outdoor_01",
                "amb_scary_tone_outdoor_loop", "amb_scary_tone_indoor_loop",
                "amb_scary_tone_outdoor_9", "aircraft_loop_03", "amb_bunker"
            };
            foreach (var n in preferred)
            {
                var c = all.FirstOrDefault(x => x != null && x.name == n);
                if (c != null) { Log.LogInfo($"[Mortar] Auto-selected: '{c.name}'"); return c; }
            }
            return all.FirstOrDefault(c => c != null && c.name.Contains("scary_tone"));
        }

        private void LogAllClips()
        {
            if (!Cfg.DebugMode) return;
            var all = Resources.FindObjectsOfTypeAll<AudioClip>();
            Log.LogInfo($"[Mortar] === ALL AUDIOCLIPS ({all.Length}) ===");
            foreach (var c in all.Where(c => c != null && c.length >= 1f).OrderBy(c => c.name))
                Log.LogInfo($"[Mortar]   '{c.name}' ({c.length:F1}s, {c.frequency}Hz, {c.channels}ch)");
        }

        private void SetupBetterAudio()
        {
            try
            {
                var asm = typeof(GameWorld).Assembly;
                var baType = asm.GetTypes().FirstOrDefault(t => t.Name == "BetterAudio" && !t.Name.Contains("+"));
                if (baType == null) return;

                try { _betterAudio = typeof(Singleton<>).MakeGenericType(baType).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null); } catch { }
                if (_betterAudio == null) _betterAudio = FindObjectsOfType(baType).FirstOrDefault();
                if (_betterAudio == null) return;

                _playNonspatialMethod = baType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "PlayNonspatial");
                if (_playNonspatialMethod == null) return;

                var gp = _playNonspatialMethod.GetParameters().FirstOrDefault(p => p.ParameterType.IsEnum);
                if (gp != null)
                {
                    var enumType = gp.ParameterType;
                    foreach (var v in Enum.GetValues(enumType))
                    {
                        if (v.ToString() == "NonspatialBypass") { _nonspatialGroup = v; break; }
                        if (v.ToString() == "Nonspatial") _nonspatialGroup = v;
                    }
                    _nonspatialGroup ??= Enum.GetValues(enumType).GetValue(0);
                }
                Log.LogInfo($"[Mortar] BetterAudio ready, group={_nonspatialGroup}");
            }
            catch (Exception ex) { Log.LogError($"[Mortar] BetterAudio error: {ex.Message}"); }
        }

        private void PlayClip(AudioClip clip)
        {
            if (_betterAudio == null || _playNonspatialMethod == null || clip == null) return;
            try
            {
                var parms = _playNonspatialMethod.GetParameters();
                object[] args = new object[parms.Length];
                for (int i = 0; i < parms.Length; i++)
                {
                    var p = parms[i];
                    if (p.ParameterType == typeof(AudioClip)) args[i] = clip;
                    else if (p.ParameterType.IsEnum) args[i] = _nonspatialGroup;
                    else if (p.ParameterType == typeof(float) && p.Name.ToLower().Contains("volume")) args[i] = Cfg.SirenVolume;
                    else if (p.ParameterType == typeof(float)) args[i] = 0f;
                    else if (p.HasDefaultValue) args[i] = p.DefaultValue;
                    else args[i] = null;
                }
                _playNonspatialMethod.Invoke(_betterAudio, args);
            }
            catch (Exception ex) { Log.LogError($"[Mortar] PlayNonspatial: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        public void PlaySiren(bool force = false)
        {
            if (!Cfg.SirenEnabled) return;
            if (!_sirenReady) SetupSiren();
            if (_sirenClip == null) return;

            if (!force && _sirenPlayedForStrike)
            {
                if (Cfg.DebugMode) Log.LogInfo($"[Mortar] Siren BLOCKED (already played for this strike)");
                return;
            }
            _sirenPlayedForStrike = true;

            _sirenPlaying = true;
            Log.LogInfo($"[Mortar] Siren PLAY: '{_sirenClip.name}', loop={Cfg.SirenLoop}, forced={force}");
            PlayClip(_sirenClip);

            if (Cfg.SirenLoop && _sirenClip.length < Cfg.SirenDurationSeconds)
                _sirenLoopCo = StartCoroutine(SirenLoopCoroutine());
        }

        private IEnumerator SirenLoopCoroutine()
        {
            float clipLen = _sirenClip.length;
            yield return new WaitForSeconds(clipLen - 0.1f);
            while (_sirenPlaying && _sirenClip != null)
            {
                PlayClip(_sirenClip);
                yield return new WaitForSeconds(clipLen - 0.1f);
            }
        }

        private void StopSiren()
        {
            _sirenPlaying = false;
            if (_sirenLoopCo != null) { StopCoroutine(_sirenLoopCo); _sirenLoopCo = null; }
        }

        private void RunDiscovery()
        {
            if (_discoveryDone) return;
            _discoveryDone = true;

            var gw = Singleton<GameWorld>.Instance;
            if (gw == null) return;

            Log.LogInfo("[Mortar] === DISCOVERY ===");

            Type clientType = null;

            foreach (var prop in gw.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!prop.Name.Contains("Shelling")) continue;
                try
                {
                    var val = prop.GetValue(gw);
                    if (val == null) continue;
                    var tName = val.GetType().Name;
                    Log.LogInfo($"[Mortar] GW.{prop.Name} = {tName}");

                    if (tName.Contains("Server"))
                    {
                        var m = val.GetType().GetMethod("StartShellingPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (m != null && _startShellingMethod == null)
                        {
                            _shellingController = val;
                            _startShellingMethod = m;
                            _artilleryInfo = $"{tName}.StartShellingPosition(Vector3)";
                            Log.LogInfo($"[Mortar] *** Server: {_artilleryInfo} ***");
                        }
                    }

                    if (tName.Contains("Client"))
                    {
                        clientType = val.GetType();
                        Log.LogInfo($"[Mortar] *** Client: {tName} ***");
                    }
                }
                catch { }
            }

            if (clientType != null)
            {
                Log.LogInfo("[Mortar] === CLIENT CONTROLLER METHODS ===");
                var methods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var m in methods)
                {
                    var p = m.GetParameters();
                    Log.LogInfo($"[Mortar]   {m.Name}({string.Join(", ", p.Select(x => $"{x.ParameterType.Name} {x.Name}"))})");
                }

                ApplyClientPatches(clientType);
            }

            Log.LogInfo($"[Mortar] Artillery: {_artilleryInfo}");
            Log.LogInfo($"[Mortar] Client patch: {_clientPatchInfo}");

            DiscoverFlareSystem(gw);
            Log.LogInfo($"[Mortar] Flare: {_flareInfo}");
        }

        private void DiscoverFlareSystem(GameWorld gw)
        {
            _flareInfo = "READY (smoke particle system)";
        }

        private void ApplyClientPatches(Type clientType)
        {
            if (_clientPatchApplied) return;
            _clientPatchApplied = true;

            try
            {
                var harmony = new Harmony("com.mortarstrikes.clientpatch");
                var prefix = typeof(MortarStrikeManager).GetMethod(nameof(ClientShellingPrefix), BindingFlags.Static | BindingFlags.Public);
                int patchCount = 0;

                var methods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var method in methods)
                {
                    var parms = method.GetParameters();
                    bool hasVector3 = parms.Any(p => p.ParameterType == typeof(Vector3));
                    bool isShelling = method.Name.Contains("Shelling") || method.Name.Contains("shelling");

                    if (hasVector3 || isShelling)
                    {
                        try
                        {
                            harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                            Log.LogInfo($"[Mortar] PATCHED: {method.Name}({string.Join(", ", parms.Select(p => p.ParameterType.Name))})");
                            patchCount++;
                        }
                        catch (Exception ex) { Log.LogWarning($"[Mortar] Patch failed {method.Name}: {ex.Message}"); }
                    }
                }

                _clientPatchInfo = $"{patchCount} method(s) patched on {clientType.Name}";
                Log.LogInfo($"[Mortar] {_clientPatchInfo}");
            }
            catch (Exception ex)
            {
                _clientPatchInfo = $"FAILED: {ex.Message}";
                Log.LogError($"[Mortar] Harmony error: {ex.Message}");
            }
        }

        public static void ClientShellingPrefix()
        {
            try { Instance?.PlaySiren(); }
            catch { }
        }

        private bool SpawnBarrage(Vector3 position)
        {
            if (_startShellingMethod == null)
            {
                Log.LogError("[Mortar] SpawnBarrage FAILED: _startShellingMethod is null (artillery not discovered)");
                return false;
            }
            if (_shellingController == null)
            {
                Log.LogError("[Mortar] SpawnBarrage FAILED: _shellingController is null");
                return false;
            }
            try
            {
                _startShellingMethod.Invoke(_shellingController, new object[] { position });
                Log.LogInfo($"[Mortar] Artillery at {position} — OK");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"[Mortar] SpawnBarrage FAILED: {ex.InnerException?.Message ?? ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        public bool SpawnWarningFlare(Vector3 strikeCenter, float targetY = float.NaN)
        {
            try
            {
                Vector3 spawnPos = FindOutdoorGroundPosition(strikeCenter, targetY);
                float duration = Cfg.WarningDelaySeconds + 5f;
                float height = Cfg.SmokeHeight;
                Color smokeColor = new Color(Cfg.WarningSmokeColorR, Cfg.WarningSmokeColorG, Cfg.WarningSmokeColorB, 1f);

                var smokeMat = CreateSmokeMaterial(smokeColor);
                var go = new GameObject("[Mortar] WarningSmoke");

                go.transform.position = spawnPos;
                UnityEngine.Object.Destroy(go, duration + 10f);


                var mainPS = go.AddComponent<ParticleSystem>();
                var mainMain = mainPS.main;
                mainMain.duration = duration;
                mainMain.loop = false;
                mainMain.startLifetime = new ParticleSystem.MinMaxCurve(6f, 10f);
                mainMain.startSpeed = new ParticleSystem.MinMaxCurve(height * 0.1f, height * 0.15f);
                mainMain.startSize = new ParticleSystem.MinMaxCurve(4f, 8f);
                mainMain.startColor = smokeColor;
                mainMain.gravityModifier = -0.05f;
                mainMain.simulationSpace = ParticleSystemSimulationSpace.World;
                mainMain.maxParticles = 500;

                var mainEmission = mainPS.emission;
                mainEmission.rateOverTime = 25f;

                var mainShape = mainPS.shape;
                mainShape.shapeType = ParticleSystemShapeType.Cone;
                mainShape.angle = 6f;
                mainShape.radius = 1.5f;
                mainShape.rotation = new Vector3(-90f, 0f, 0f);

                var mainSizeOverLife = mainPS.sizeOverLifetime;
                mainSizeOverLife.enabled = true;
                mainSizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                    new Keyframe(0f, 0.5f),
                    new Keyframe(0.3f, 1f),
                    new Keyframe(1f, 2.5f)
                ));

                var mainColorOverLife = mainPS.colorOverLifetime;
                mainColorOverLife.enabled = true;
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[] {
                        new GradientColorKey(smokeColor, 0f),
                        new GradientColorKey(smokeColor, 0.6f),
                        new GradientColorKey(Color.gray, 1f)
                    },
                    new[] {
                        new GradientAlphaKey(0f, 0f),
                        new GradientAlphaKey(0.7f, 0.1f),
                        new GradientAlphaKey(0.6f, 0.7f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                mainColorOverLife.color = gradient;

                var mainRenderer = go.GetComponent<ParticleSystemRenderer>();
                mainRenderer.material = smokeMat;

                var burstGO = new GameObject("BurstPuff");
                burstGO.transform.SetParent(go.transform, false);
                var burstPS = burstGO.AddComponent<ParticleSystem>();
                var burstMain = burstPS.main;
                burstMain.duration = 1f;
                burstMain.loop = false;
                burstMain.startLifetime = new ParticleSystem.MinMaxCurve(3f, 5f);
                burstMain.startSpeed = new ParticleSystem.MinMaxCurve(2f, 6f);
                burstMain.startSize = new ParticleSystem.MinMaxCurve(3f, 6f);
                burstMain.startColor = smokeColor;
                burstMain.gravityModifier = -0.02f;
                burstMain.simulationSpace = ParticleSystemSimulationSpace.World;

                var burstEmission = burstPS.emission;
                burstEmission.rateOverTime = 0;
                burstEmission.SetBursts(new[] { new ParticleSystem.Burst(0f, 30) });

                var burstShape = burstPS.shape;
                burstShape.shapeType = ParticleSystemShapeType.Hemisphere;
                burstShape.radius = 3f;

                var burstRenderer = burstGO.GetComponent<ParticleSystemRenderer>();
                burstRenderer.material = smokeMat;

                mainPS.Play();
                burstPS.Play();

                try
                {
                    var clip = Resources.FindObjectsOfTypeAll<AudioClip>()
                        .FirstOrDefault(c => c != null && c.name == "grenade_smoke_loop");
                    if (clip != null)
                    {
                        var audio = go.AddComponent<AudioSource>();
                        audio.clip = clip;
                        audio.loop = true;
                        audio.spatialBlend = 1f;        // fully 3D
                        audio.rolloffMode = AudioRolloffMode.Linear;
                        audio.minDistance = 5f;
                        audio.maxDistance = Cfg.SmokeSoundRadius;
                        audio.volume = 0.8f;
                        audio.Play();
                        Log.LogInfo("[Mortar] Smoke sound: grenade_smoke_loop");
                    }
                    else
                        Log.LogWarning("[Mortar] Smoke sound 'grenade_smoke_loop' not found");
                }
                catch (Exception ex) { Log.LogWarning($"[Mortar] Smoke sound failed: {ex.Message}"); }

                Log.LogInfo($"[Mortar] Smoke signal at {spawnPos}, color=({smokeColor.r:F1},{smokeColor.g:F1},{smokeColor.b:F1}), height={height}m, duration={duration:F0}s");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"[Mortar] Smoke signal FAILED: {ex.Message}");
                return false;
            }
        }

        private static Material _cachedSmokeMaterial;
        private static bool _materialSearchDone;
        private Material CreateSmokeMaterial(Color tint)
        {
            if (!_materialSearchDone)
            {
                _materialSearchDone = true;
                try
                {
                    _cachedSmokeMaterial = FindBestSmokeMaterial();
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[Mortar] Smoke material search failed: {ex.Message}");
                }
            }

            if (_cachedSmokeMaterial != null)
            {
                var mat = new Material(_cachedSmokeMaterial);
                mat.color = tint;
                Log.LogInfo($"[Mortar] Using stolen smoke material: {_cachedSmokeMaterial.name}");
                return mat;
            }

            Log.LogWarning("[Mortar] No EFT smoke material found, using fallback");
            return CreateFallbackMaterial(tint);
        }

        private Material FindBestSmokeMaterial()
        {
            string[] knownNames = {
                "smoke", "Smoke", "SmokeGrenade", "smoke_grenade",
                "FX_Smoke", "fx_smoke", "Smoke_FX",
                "ParticleSmoke", "particle_smoke",
                "fog", "Fog", "dust", "Dust",
                "cloud", "Cloud", "puff", "Puff"
            };

            Texture2D bestTex = null;
            try
            {
                var allTextures = Resources.FindObjectsOfTypeAll<Texture2D>();
                Log.LogInfo($"[Mortar] Scanning {allTextures.Length} textures for smoke...");

                int bestScore = 0;
                int checked_ = 0;

                for (int i = 0; i < allTextures.Length; i++)
                {
                    try
                    {
                        var tex = allTextures[i];
                        if (tex == null) continue;

                        string texName = tex.name;
                        if (string.IsNullOrEmpty(texName)) continue;
                        if (tex.width < 32 || tex.width > 512) continue;

                        checked_++;
                        string lower = texName.ToLower();

                        if (lower.Contains("ui") || lower.Contains("icon") || lower.Contains("font")
                            || lower.Contains("scope") || lower.Contains("cursor")) continue;

                        int score = 0;
                        if (lower.Contains("smoke")) score += 10;
                        if (lower.Contains("fog")) score += 8;
                        if (lower.Contains("dust")) score += 7;
                        if (lower.Contains("cloud")) score += 7;
                        if (lower.Contains("puff")) score += 6;
                        if (lower.Contains("particle")) score += 4;
                        if (lower.Contains("soft")) score += 3;
                        if (lower.Contains("alpha")) score += 2;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTex = tex;
                            Log.LogInfo($"[Mortar]   Candidate: '{texName}' ({tex.width}x{tex.height}, score={score})");
                        }
                    }
                    catch { }
                }
                Log.LogInfo($"[Mortar] Checked {checked_} textures, best: {bestTex?.name ?? "none"} (score={bestScore})");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Mortar] Texture scan failed: {ex.Message}");
            }

            if (bestTex != null)
            {
                var shader = FindParticleShader();
                if (shader != null)
                {
                    var mat = new Material(shader);
                    mat.mainTexture = bestTex;
                    Log.LogInfo($"[Mortar] Smoke material built: tex='{bestTex.name}', shader='{shader.name}'");
                    return mat;
                }
            }

            Log.LogInfo("[Mortar] No smoke texture found, will use procedural fallback");
            return null;
        }

        private Shader FindParticleShader()
        {
            string[] shaderNames = {
                "Legacy Shaders/Particles/Alpha Blended",
                "Particles/Standard Unlit",
                "Mobile/Particles/Alpha Blended",
                "Legacy Shaders/Particles/Additive",
                "Unlit/Transparent"
            };

            foreach (var sn in shaderNames)
            {
                var s = Shader.Find(sn);
                if (s != null) return s;
            }
            return null;
        }

        private Material CreateFallbackMaterial(Color tint)
        {
            int res = 128;
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            float center = res * 0.5f;
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float dx = (x - center) / center;
                    float dy = (y - center) / center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(1f - dist);
                    alpha = alpha * alpha;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            var shader = FindParticleShader() ?? Shader.Find("Particles/Standard Unlit");
            var mat = new Material(shader);
            mat.mainTexture = tex;
            mat.color = tint;
            mat.renderQueue = 3000;
            return mat;
        }

        private IEnumerator ExecuteStrike(bool withSiren)
        {
            var gw = Singleton<GameWorld>.Instance;
            if (gw == null) yield break;
            var alive = gw.AllAlivePlayersList;
            if (alive == null) yield break;
            for (int waitRetry = 0; alive.Count == 0 && Plugin.IsInRaid() && waitRetry < 4; waitRetry++)
            {
                yield return new WaitForSeconds(30f);
                alive = gw.AllAlivePlayersList;
                if (alive == null) yield break;
            }
            if (alive.Count == 0) yield break;

            Player target = null;
            int weight = Math.Max(0, Math.Min(100, Cfg.PlayerTargetingWeight));
            var players = alive.Where(p => !p.IsAI).ToList();
            var bots = alive.Where(p => p.IsAI).ToList();

            if (players.Count == 0)
            {
                target = bots.Count > 0 ? bots[UnityEngine.Random.Range(0, bots.Count)] : alive[0];
            }
            else if (bots.Count == 0 || weight >= 100)
            {
                target = players[UnityEngine.Random.Range(0, players.Count)];
            }
            else if (weight <= 0)
            {
                target = alive[UnityEngine.Random.Range(0, alive.Count)];
            }
            else
            {
                float naturalRatio = (float)players.Count / alive.Count;
                float playerChance = Mathf.Lerp(naturalRatio, 1f, weight / 100f);

                if (UnityEngine.Random.value < playerChance)
                    target = players[UnityEngine.Random.Range(0, players.Count)];
                else
                    target = bots[UnityEngine.Random.Range(0, bots.Count)];
            }

            if (target == null) target = gw.MainPlayer;
            if (target == null) yield break;

            string targetType = target.IsAI ? "BOT" : "PLAYER";
            Log.LogInfo($"[Mortar] Target selection: weight={weight}, players={players.Count}, bots={bots.Count}, selected={targetType}");

            Vector3 tp = target.Transform.position;
            Vector3 sc = PickPositionNear(tp);
            string tName = "unknown";
            try { tName = target.Profile?.Nickname ?? "player"; } catch { }

            Log.LogInfo($"[Mortar] === STRIKE #{_strikesThisRaid + 1} === Target: {tName}, center: {sc}");

            if (withSiren)
            {
                bool warned = false;
                float warningTime = Cfg.WarningDelaySeconds;

                if (Cfg.WarningFlareEnabled)
                {
                    if (SpawnWarningFlare(sc, tp.y))
                    {
                        warned = true;
                        Log.LogInfo($"[Mortar] Warning flare launched! Barrage in {warningTime:F0}s...");
                    }
                }

                if (Cfg.SirenEnabled)
                {
                    _sirenPlayedForStrike = false;
                    PlaySiren(force: true);
                    FikaSync.BroadcastSirenPacket(sc.x, sc.y, sc.z);
                    warned = true;
                }

                if (warned)
                {
                    yield return new WaitForSeconds(warningTime);
                    StopSiren();
                    yield return new WaitForSeconds(UnityEngine.Random.Range(0.5f, 2f));
                }
            }

            if (!Plugin.IsInRaid()) yield break;

            int count = Mathf.Max(1, Cfg.BarrageCount);
            for (int i = 0; i < count; i++)
            {
                if (!Plugin.IsInRaid()) yield break;
                Vector3 pos = sc;
                if (i > 0 && Cfg.BarrageSpreadRadius > 0)
                {
                    var off = UnityEngine.Random.insideUnitCircle * Cfg.BarrageSpreadRadius;
                    pos = SnapToGround(sc + new Vector3(off.x, 0, off.y));
                }
                Log.LogInfo($"[Mortar] Barrage {i + 1}/{count} at {pos}");
                SpawnBarrage(pos);
                if (i < count - 1) yield return new WaitForSeconds(Cfg.BarrageSpacing);
            }
            _strikesThisRaid++;
        }

        public void TriggerStrikeInstant()
        {
            if (!_discoveryDone) RunDiscovery();
            if (!_sirenReady) SetupSiren();
            StartCoroutine(ExecuteStrike(withSiren: true));
        }

        private IEnumerator RaidLifecycle()
        {
            yield return new WaitForSeconds(8f);
            if (!Plugin.IsInRaid()) yield break;

            Plugin.ReloadConfig();
            _mapId = "";
            try { _mapId = Singleton<GameWorld>.Instance?.LocationId ?? ""; } catch { }

            if (Cfg.BlacklistedMaps.Any(s => _mapId.Equals(s, StringComparison.OrdinalIgnoreCase)))
            { Log.LogInfo($"[Mortar] Map '{_mapId}' blacklisted."); yield break; }

            _isHost = Plugin.IsHost();
            Log.LogInfo($"[Mortar] Map: {_mapId} | Host: {_isHost}");

            RunDiscovery();
            SetupSiren();
            LogAllClips();

            FikaSync.TryRegisterPacket();

            if (!_isHost)
            {
                Log.LogInfo("[Mortar] Not host — siren will play via FIKA packet (or fallback to Harmony patch).");
                yield break;
            }

            float roll = UnityEngine.Random.value;
            if (roll > Cfg.ChancePerRaid)
            { Log.LogInfo($"[Mortar] Roll {roll:F3} > {Cfg.ChancePerRaid:F3} — no strikes."); yield break; }

            float delay = UnityEngine.Random.Range(Cfg.MinDelayMinutes * 60f, Cfg.MaxDelayMinutes * 60f);
            Log.LogInfo($"[Mortar] Strikes enabled! First in {delay / 60f:F1} min.");
            yield return new WaitForSeconds(delay);

            if (!Plugin.IsInRaid()) yield break;

            yield return StartCoroutine(ExecuteStrike(withSiren: true));

            while (Cfg.AllowMultipleStrikes && _strikesThisRaid < Cfg.MaxStrikesPerRaid && Plugin.IsInRaid())
            {
                if (UnityEngine.Random.value > Cfg.AdditionalStrikeChance) break;
                float next = UnityEngine.Random.Range(120f, 480f);
                yield return new WaitForSeconds(next);
                if (!Plugin.IsInRaid()) yield break;
                yield return StartCoroutine(ExecuteStrike(withSiren: true));
            }
        }

        private Vector3 PickPositionNear(Vector3 c)
        {
            float mn = Cfg.MinDistanceFromTarget, mx = Cfg.MaxDistanceFromTarget;
            for (int i = 0; i < 20; i++)
            {
                float a = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float d = UnityEngine.Random.Range(mn, mx);
                var p = SnapToGround(c + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d));
                if (p.y > -50 && p.y < 500) return p;
            }
            return c + Vector3.right * UnityEngine.Random.Range(mn, mx);
        }

        private Vector3 FindOutdoorGroundPosition(Vector3 center, float targetY = float.NaN)
        {
            float useY = center.y;

            if (!float.IsNaN(targetY))
            {
                if (center.y > targetY + 20f)
                {
                    Log.LogWarning($"[Mortar] Smoke: center.y={center.y:F1} >> target.y={targetY:F1}, using target Y");
                    useY = targetY;
                }
            }
            else if (center.y > 100f)
            {
                if (Physics.Raycast(new Vector3(center.x, 100f, center.z), Vector3.down, out RaycastHit h, 200f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    Log.LogInfo($"[Mortar] Smoke corrected: y={h.point.y:F1} (was {center.y:F1})");
                    return h.point + Vector3.up * 0.3f;
                }
            }

            Log.LogInfo($"[Mortar] Smoke position: ({center.x:F0}, {useY:F1}, {center.z:F0})");
            return new Vector3(center.x, useY + 0.3f, center.z);
        }

        private Vector3 SnapToGround(Vector3 p) =>
            Physics.Raycast(new Vector3(p.x, p.y + 200, p.z), Vector3.down, out RaycastHit h, 500f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ? h.point : p;

        private void OnGUI()
        {
            if (!Cfg.DebugMode || !Plugin.IsInRaid()) return;
            GUILayout.BeginArea(new Rect(10, 10, 550, 350));
            GUILayout.BeginVertical("box");
            GUILayout.Label("<b>MORTAR STRIKES DEBUG</b>");
            GUILayout.Label($"Map: {_mapId} | Host: {_isHost} | Strikes: {_strikesThisRaid}");
            GUILayout.Label($"Artillery: {_artilleryInfo}");
            GUILayout.Label($"Warning: {_flareInfo}");
            GUILayout.Label($"Siren: {_sirenInfo}");
            GUILayout.Label($"Client patch: {_clientPatchInfo}");
            GUILayout.Label($"BetterAudio: {(_betterAudio != null)} | Group: {_nonspatialGroup}");
            GUILayout.Space(5);
            if (GUILayout.Button("TEST SMOKE"))
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw?.MainPlayer != null)
                    SpawnWarningFlare(gw.MainPlayer.Transform.position + gw.MainPlayer.Transform.forward * 20f);
            }
            if (GUILayout.Button("TEST SIREN")) PlaySiren(force: true);
            if (GUILayout.Button("TRIGGER STRIKE")) TriggerStrikeInstant();
            if (GUILayout.Button("ARTILLERY ONLY")) { if (!_discoveryDone) RunDiscovery(); StartCoroutine(ExecuteStrike(false)); }
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }
}
