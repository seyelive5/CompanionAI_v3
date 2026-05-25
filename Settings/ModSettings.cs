using System;
using System.Collections.Generic;
using System.IO;
using CompanionAI_v3.MachineSpirit;
using Newtonsoft.Json;
using UnityModManagerNet;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Settings
{
    /// <summary>
    /// Language option
    /// </summary>
    public enum Language
    {
        English,
        Korean,
        Russian,
        Japanese,
        Chinese
    }

    /// <summary>
    /// Role-based AI behavior profiles
    /// </summary>
    public enum AIRole
    {
        Auto,       // ★ v3.0.92: Automatically detect optimal role based on abilities
        Tank,       // Prioritize defense, draw enemy attention
        DPS,        // Prioritize damage output
        Support,    // Prioritize buffs and debuffs
        Overseer    // ★ v3.7.91: Familiar-centric combat (pet as primary damage source)
    }

    /// <summary>
    /// Range preference for combat
    /// </summary>
    public enum RangePreference
    {
        Adaptive,       // Use whatever is equipped
        PreferMelee,    // Stay close to enemies
        PreferRanged    // Keep distance from enemies
    }

    /// <summary>
    /// Settings for individual character
    /// </summary>
    public class CharacterSettings
    {
        public string CharacterId { get; set; } = "";
        public string CharacterName { get; set; } = "";
        public bool EnableCustomAI { get; set; } = false;
        public AIRole Role { get; set; } = AIRole.Auto;
        public RangePreference RangePreference { get; set; } = RangePreference.Adaptive;

        // Combat behavior
        public bool UseBuffsBeforeAttack { get; set; } = true;
        public bool FinishLowHPEnemies { get; set; } = true;
        public bool AvoidFriendlyFire { get; set; } = true;

        // ★ v3.110.11: MinEnemiesForAoE 삭제 — ClusterDetector.MIN_CLUSTER_SIZE (AIConfig)로 중앙집중.
        //   이전: per-character 설정 (기본 2). ClusterDetector의 전역 설정과 중복/충돌.
        //   현재: AIConfig.AoEConfig.MinClusterSize 단일 소스.

        // Movement behavior
        public bool AllowRetreat { get; set; } = true;
        public bool SeekCover { get; set; } = true;

        // ★ v3.110.11: MinSafeDistance 삭제 — WeaponRangeProfile에서 무기 특성 기반 자동 계산.
        //   이전: 기본값 7m가 Cone 무기 봉쇄, 단발 저격기엔 과도. 사용자 튜닝 불가능한 값.
        //   현재: EffectiveRange × 0.3 자동 계산 (근접 0, Cone r=7→2.1, 볼터 15→4.5).

        // Resource management
        public bool ConserveAmmo { get; set; } = false;
        public int HealAtHPPercent { get; set; } = 50;

        // ★ v3.2.30: 킬 시뮬레이터 토글 (다중 능력 조합으로 확정 킬 탐색)
        public bool UseKillSimulator { get; set; } = true;

        // ★ v3.3.00: AOE 클러스터 최적화 토글
        public bool UseAoEOptimization { get; set; } = true;

        // ★ v3.4.00: 예측적 이동 토글 (적 이동 예측하여 안전 위치 선택)
        public bool UsePredictiveMovement { get; set; } = true;

        // ★ v3.9.72: 무기 세트 로테이션 (한 턴에 양쪽 세트 공격)
        public bool EnableWeaponSetRotation { get; set; } = false;

        // ★ Phase 3: LLM-as-Judge — LLM으로 최적 플랜 선택
        public bool EnableLLMJudge { get; set; } = false;
    }

    /// <summary>
    /// ★ v3.5.96: 세이브 파일별 설정 (GameId 기반 파일 저장)
    /// Game.Instance.Player.GameId를 사용하여 settings_{gameId}.json 파일로 저장
    /// </summary>
    public class PerSaveSettings
    {
        private static PerSaveSettings _cached = null;
        private static string _currentGameId = null;
        private static string _modPath = null;

        // v3.117.60: Load 실패 후 Save 차단 flag (per gameId).
        //   파싱 에러 → 빈 설정으로 fallback. 그 빈 설정이 게임 저장 시점에 원본을 덮어쓰는 것 방지.
        //   파일 백업 (.corrupted-*) 도 같은 시점에 생성 — 사용자가 복구 가능.
        private static readonly HashSet<string> _failedGameIds = new HashSet<string>();

        /// <summary>캐릭터별 AI 설정</summary>
        [JsonProperty]
        public Dictionary<string, CharacterSettings> CharacterSettings { get; set; }
            = new Dictionary<string, CharacterSettings>();

        /// <summary>캐시된 인스턴스 가져오기 (없으면 파일에서 로드)</summary>
        public static PerSaveSettings Instance
        {
            get
            {
                // GameId가 변경되었으면 다시 로드
                var gameId = GetCurrentGameId();
                if (_cached != null && _currentGameId == gameId)
                    return _cached;

                Load();
                return _cached ?? (_cached = new PerSaveSettings());
            }
        }

        /// <summary>모드 경로 설정 (Main.Load에서 호출)</summary>
        public static void SetModPath(string path) => _modPath = path;

        /// <summary>캐시 클리어 (세이브 로드 시 호출)</summary>
        public static void ClearCache()
        {
            _cached = null;
            _currentGameId = null;
        }

        /// <summary>현재 GameId 가져오기</summary>
        private static string GetCurrentGameId()
        {
            try
            {
                return Kingmaker.Game.Instance?.Player?.GameId;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>설정 파일 경로 가져오기</summary>
        private static string GetSettingsFilePath(string gameId)
        {
            if (string.IsNullOrEmpty(_modPath) || string.IsNullOrEmpty(gameId))
                return null;
            return Path.Combine(_modPath, $"settings_{gameId}.json");
        }

        /// <summary>파일에서 설정 로드</summary>
        public static void Load()
        {
            try
            {
                var gameId = GetCurrentGameId();
                if (string.IsNullOrEmpty(gameId))
                {
                    Log.Persistence.Debug("[PerSaveSettings] GameId not available yet");
                    return;
                }

                _currentGameId = gameId;
                var filePath = GetSettingsFilePath(gameId);

                if (string.IsNullOrEmpty(filePath))
                {
                    Log.Persistence.Debug("[PerSaveSettings] Mod path not set");
                    _cached = new PerSaveSettings();
                    return;
                }

                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    _cached = JsonConvert.DeserializeObject<PerSaveSettings>(json);
                    if (_cached == null)
                    {
                        // 파일은 존재하지만 deserialize 결과가 null (빈 파일, "null" 리터럴 등) → corruption 으로 취급.
                        BackupCorruptedFile(filePath, "deserialize returned null");
                        _failedGameIds.Add(gameId);
                        _cached = new PerSaveSettings();
                    }
                    else
                    {
                        // 정상 로드 → 이전 실패 기록 제거 (사용자가 손상 파일 수동 복구한 케이스)
                        _failedGameIds.Remove(gameId);
                        Log.Persistence.Info($"[PerSaveSettings] Loaded {_cached.CharacterSettings?.Count ?? 0} settings from {Path.GetFileName(filePath)} (GameId={gameId})");
                    }
                }
                else
                {
                    _failedGameIds.Remove(gameId);
                    Log.Persistence.Info($"[PerSaveSettings] No settings file for GameId={gameId}, creating new");
                    _cached = new PerSaveSettings();
                }
            }
            catch (Exception ex)
            {
                // v3.117.60: 손상된 파일 백업 + Save 차단으로 데이터 손실 방지.
                BackupCorruptedFile(GetSettingsFilePath(_currentGameId), ex.Message);
                if (!string.IsNullOrEmpty(_currentGameId))
                    _failedGameIds.Add(_currentGameId);
                Log.Persistence.Error($"[PerSaveSettings] Load error (GameId={_currentGameId}): {ex.Message}. Save 차단 활성화 — 손상 파일은 .corrupted-* 로 백업됨.");
                _cached = new PerSaveSettings();
            }
        }

        /// <summary>v3.117.60: 손상 파일을 timestamp 접미사로 백업.</summary>
        private static void BackupCorruptedFile(string filePath, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var backupPath = $"{filePath}.corrupted-{stamp}";
                File.Copy(filePath, backupPath, overwrite: false);
                Log.Persistence.Warn($"[PerSaveSettings] Corrupted file backed up: {Path.GetFileName(backupPath)} (reason: {reason})");
            }
            catch (Exception ex)
            {
                Log.Persistence.Error($"[PerSaveSettings] Backup failed for {filePath}: {ex.Message}");
            }
        }

        /// <summary>파일에 설정 저장</summary>
        public static void Save()
        {
            try
            {
                var gameId = GetCurrentGameId();
                if (string.IsNullOrEmpty(gameId))
                {
                    Log.Persistence.Debug("[PerSaveSettings] Cannot save - GameId not available");
                    return;
                }

                // v3.117.60: 손상 파일 백업 후 빈 설정 fallback 상태에서는 Save 차단.
                //   사용자가 손상 파일 (.corrupted-*) 을 검토/복구할 시간 확보.
                if (_failedGameIds.Contains(gameId))
                {
                    Log.Persistence.Warn($"[PerSaveSettings] Save blocked for GameId={gameId} — load 실패 후 미해결. .corrupted-* 백업 확인 필요.");
                    return;
                }

                var filePath = GetSettingsFilePath(gameId);
                if (string.IsNullOrEmpty(filePath))
                {
                    Log.Persistence.Debug("[PerSaveSettings] Cannot save - mod path not set");
                    return;
                }

                if (_cached == null) return;

                // v3.117.60: read-only 속성 사전 체크 — 사용자 인지 가능한 명확한 에러.
                if (File.Exists(filePath))
                {
                    var attrs = File.GetAttributes(filePath);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        Log.Persistence.Error($"[PerSaveSettings] {Path.GetFileName(filePath)} 가 읽기 전용입니다. " +
                            "OneDrive/안티바이러스/백업 도구 또는 수동 속성 변경 확인 필요. 설정 변경 사항이 저장되지 않습니다.");
                        return;
                    }
                }

                var json = JsonConvert.SerializeObject(_cached, Formatting.Indented);
                File.WriteAllText(filePath, json);
                Log.Persistence.Debug($"[PerSaveSettings] Saved {_cached.CharacterSettings.Count} settings to {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Log.Persistence.Error($"[PerSaveSettings] Save error: {ex.Message} ({ex.GetType().Name})");
            }
        }
    }

    /// <summary>
    /// Global mod settings
    /// </summary>
    public class ModSettings
    {
        public static ModSettings Instance { get; private set; }
        private static UnityModManager.ModEntry _modEntry;

        // v3.117.60: Load 실패 시 Save 차단 — 빈 설정이 원본 파일을 덮어쓰는 것 방지.
        private static bool _loadFailed = false;

        public bool EnableDebugLogging { get; set; } = false;
        public bool ShowAIThoughts { get; set; } = false;

        /// <summary>★ v3.9.32: AI 대사 (BarkPlayer 말풍선) 표시 여부</summary>
        public bool EnableAISpeech { get; set; } = true;

        /// <summary>★ v3.9.80: 전투 승리 시 환호 말풍선 표시 여부</summary>
        public bool EnableVictoryBark { get; set; } = true;

        /// <summary>★ v3.20.0: 전투 리포트 JSON 자동 생성 여부 (combat_reports 폴더)</summary>
        public bool EnableCombatReport { get; set; } = true;

        /// <summary>★ v3.21.0: 비파티 아군 NPC AI 제어 (BodyGuard 등 타 모드 NPC 대상)</summary>
        public bool EnableAlliedNPCAI { get; set; } = false;

        /// <summary>★ v3.21.4: 함선전투에서 CompanionAI로 함선 제어 여부</summary>
        public bool EnableShipCombatAI { get; set; } = false;

        /// <summary>★ v3.46.0: 전략 지시 UI — 전투 중 유닛별 AI 행동 방향 지시 버튼 표시</summary>
        public bool EnableDecisionOverlay { get; set; } = false;

        /// <summary>★ v3.46.0: 전략 지시 UI 크기 배율 (0.8 ~ 2.0, 기본 1.0)</summary>
        public float DecisionOverlayScale { get; set; } = 1.0f;

        /// <summary>v3.117.62: 라벨 변경 "LLM Visual Overlay" → "AI Visual Overlay" + default false.
        ///   필드명은 settings.json 호환을 위해 유지. 위협 랭킹 + 액션 프리뷰는 LLM 무관 항상 작동.
        ///   Priority Target 마커만 LLM Judge 활성 시 추가.</summary>
        public bool EnableLLMVisualOverlay { get; set; } = false;

        /// <summary>★ LLM Combat AI: 전역 마스터 토글 (이것과 캐릭터별 EnableLLMJudge 모두 활성화 필요)</summary>
        public bool EnableLLMCombatAI { get; set; } = false;

        /// <summary>★ v3.84.0: Training data 수집 활성화 (Developer Only — Debug 탭에서 토글)</summary>
        public bool EnableTrainingDataCollection { get; set; } = false;

        /// <summary>★ Team Commander: 라운드 시작 시 팀 전략 LLM 호출 (EnableLLMCombatAI 하위)</summary>
        public bool EnableLLMCommander { get; set; } = true;

        /// <summary>★ Tactical Memory: 전투 간 전술 기억 (적 구성별 가중치 성과 기록/회상)</summary>
        public bool EnableTacticalMemory { get; set; } = true;

        /// <summary>★ LLM Combat AI: 오버레이 표시 여부</summary>
        public bool ShowLLMOverlay { get; set; } = true;

        /// <summary>★ Phase 3: LLM Judge 전용 모델 (빈 값이면 gemma4:e4b 사용)</summary>
        public string LLMJudgeModel { get; set; } = "";

        /// <summary>★ v3.20.0: 보관할 최대 전투 리포트 수 (초과 시 오래된 것부터 삭제)</summary>
        public int MaxCombatReports { get; set; } = 10;

        public Language UILanguage { get; set; } = Language.English;

        /// <summary>★ v3.50.0: UI 전체 크기 배율 (0.8 ~ 2.5, 기본 1.5)</summary>
        public float UIScale { get; set; } = 1.5f;

        /// <summary>Machine Spirit (LLM-powered voidship AI companion) settings</summary>
        public MachineSpiritConfig MachineSpirit { get; set; } = new MachineSpiritConfig();

        /// <summary>
        /// ★ v3.0.15: 주인공도 AI 제어 여부
        /// </summary>
        public bool ControlMainCharacter { get; set; } = true;

        #region ★ v3.5.20: Performance Settings (Global)

        /// <summary>
        /// 위협 예측 시 분석할 최대 적 수
        /// 높을수록 정확하지만 느림
        /// </summary>
        public int MaxEnemiesToAnalyze { get; set; } = 8;

        /// <summary>
        /// AOE 최적 위치 탐색 시 체크할 최대 위치 수
        /// 높을수록 AOE 타겟팅 정확, 느림
        /// </summary>
        public int MaxPositionsToEvaluate { get; set; } = 25;

        /// <summary>
        /// AOE 기회 탐색을 위해 추적할 최대 클러스터 수
        /// 높을수록 AOE 기회 많이 찾음, 느림
        /// </summary>
        public int MaxClusters { get; set; } = 5;

        /// <summary>
        /// 적 위협 예측을 위해 분석할 이동 타일 수
        /// 높을수록 위협 구역 정밀, 느림
        /// </summary>
        public int MaxTilesPerEnemy { get; set; } = 100;

        #endregion

        public CharacterSettings DefaultSettings { get; set; } = new CharacterSettings();

        /// <summary>
        /// ★ v3.5.89: 캐릭터 설정 가져오기 (PerSaveSettings 사용 - 세이브별 저장)
        /// </summary>
        public CharacterSettings GetOrCreateSettings(string characterId, string characterName = null)
        {
            if (string.IsNullOrEmpty(characterId))
                return DefaultSettings;

            // ★ v3.5.89: 세이브 파일에서 설정 로드
            var perSave = PerSaveSettings.Instance;
            if (!perSave.CharacterSettings.TryGetValue(characterId, out var settings))
            {
                settings = new CharacterSettings
                {
                    CharacterId = characterId,
                    CharacterName = characterName ?? characterId,
                    EnableCustomAI = DefaultSettings.EnableCustomAI,
                    Role = DefaultSettings.Role,
                    RangePreference = DefaultSettings.RangePreference,
                    UseBuffsBeforeAttack = DefaultSettings.UseBuffsBeforeAttack,
                    FinishLowHPEnemies = DefaultSettings.FinishLowHPEnemies,
                    AvoidFriendlyFire = DefaultSettings.AvoidFriendlyFire,
                    AllowRetreat = DefaultSettings.AllowRetreat,
                    SeekCover = DefaultSettings.SeekCover,
                    ConserveAmmo = DefaultSettings.ConserveAmmo,
                    HealAtHPPercent = DefaultSettings.HealAtHPPercent,
                    UseKillSimulator = DefaultSettings.UseKillSimulator,
                    UseAoEOptimization = DefaultSettings.UseAoEOptimization,
                    UsePredictiveMovement = DefaultSettings.UsePredictiveMovement,
                    EnableWeaponSetRotation = DefaultSettings.EnableWeaponSetRotation,
                    EnableLLMJudge = DefaultSettings.EnableLLMJudge
                };
                perSave.CharacterSettings[characterId] = settings;
                // ★ v3.6.23: 자동 저장 제거 - 매 턴 NPC 분석 시 파일 크기가 계속 증가하는 문제 해결
                // 저장은 UI에서 설정 변경 시 (SaveCharacterSettings) 또는 게임 저장 시 (SaveRoutine_Prefix)에만 수행
            }

            if (!string.IsNullOrEmpty(characterName))
                settings.CharacterName = characterName;

            return settings;
        }

        /// <summary>
        /// ★ v3.5.89: 캐릭터 설정 저장 (UI에서 설정 변경 시 호출)
        /// </summary>
        public void SaveCharacterSettings()
        {
            PerSaveSettings.Save();
        }

        #region Save/Load

        private static string GetSettingsPath()
        {
            return Path.Combine(_modEntry.Path, "settings.json");
        }

        public static void Load(UnityModManager.ModEntry modEntry)
        {
            _modEntry = modEntry;
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonConvert.DeserializeObject<ModSettings>(json);
                    if (settings != null)
                    {
                        Instance = settings;
                        _loadFailed = false;
                        Log.Persistence.Info("Settings loaded successfully");
                    }
                    else
                    {
                        // v3.117.60: deserialize 결과 null 도 corruption 으로 취급 → 백업 + Save 차단.
                        BackupCorruptedFile(path, "deserialize returned null");
                        _loadFailed = true;
                        Log.Persistence.Warn("settings.json deserialize returned null → 빈 설정 사용, Save 차단됨. .corrupted-* 백업 확인 필요.");
                        Instance = new ModSettings();
                    }
                }
                else
                {
                    // ★ v3.5.21: 설정 파일이 없으면 기본값으로 자동 생성
                    Log.Persistence.Info("Settings file not found, creating default settings.json");
                    Instance = new ModSettings();
                    Save();  // 기본 설정 파일 생성
                }
            }
            catch (Exception ex)
            {
                // v3.117.60: 손상 파일 백업 후 Save 차단.
                BackupCorruptedFile(GetSettingsPath(), ex.Message);
                _loadFailed = true;
                Log.Persistence.Error($"Failed to load settings: {ex.Message}. Save 차단 활성화 — .corrupted-* 백업 확인 필요.");
                Instance = new ModSettings();
            }

            // ★ v3.54.0: MaxTokens 마이그레이션 (구버전 150/300 → 신버전 500)
            //   v3.117.60: load 실패 상태에서는 마이그레이션 Save 도 차단 (Save 자체가 거부함 — 안전).
            if (Instance.MachineSpirit.MaxTokens < 500)
            {
                Instance.MachineSpirit.MaxTokens = 500;
                Save();
            }

            // ★ v3.1.30: AI 설정 로드 (Response Curves, Role 가중치 등)
            AIConfig.Load(modEntry.Path);
        }

        /// <summary>v3.117.60: 손상 파일을 timestamp 접미사로 백업.</summary>
        private static void BackupCorruptedFile(string filePath, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var backupPath = $"{filePath}.corrupted-{stamp}";
                File.Copy(filePath, backupPath, overwrite: false);
                Log.Persistence.Warn($"Corrupted file backed up: {Path.GetFileName(backupPath)} (reason: {reason})");
            }
            catch (Exception ex)
            {
                Log.Persistence.Error($"Backup failed for {filePath}: {ex.Message}");
            }
        }

        public static void Save()
        {
            if (Instance == null || _modEntry == null) return;

            // v3.117.60: Load 실패 후 미해결 상태에서는 Save 차단 — 빈 설정이 원본 덮어쓰는 것 방지.
            if (_loadFailed)
            {
                Log.Persistence.Warn("settings.json Save blocked — load 실패 후 미해결. .corrupted-* 백업 확인 후 수동 복구 필요.");
                return;
            }

            try
            {
                string path = GetSettingsPath();

                // v3.117.60: read-only 사전 체크.
                if (File.Exists(path))
                {
                    var attrs = File.GetAttributes(path);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        Log.Persistence.Error($"settings.json 가 읽기 전용입니다. " +
                            "OneDrive/안티바이러스/백업 도구 또는 수동 속성 변경 확인 필요. 설정 변경 사항이 저장되지 않습니다.");
                        return;
                    }
                }

                string json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(path, json);
                Log.Persistence.Debug("Settings saved");
            }
            catch (Exception ex)
            {
                Log.Persistence.Error($"Failed to save settings: {ex.Message} ({ex.GetType().Name})");
            }
        }

        #endregion
    }
}
