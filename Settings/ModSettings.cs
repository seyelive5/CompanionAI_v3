using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityModManagerNet;

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
        Japanese
    }

    /// <summary>
    /// Localization system
    /// </summary>
    public static class Localization
    {
        public static Language CurrentLanguage { get; set; } = Language.English;

        private static readonly Dictionary<string, Dictionary<Language, string>> Strings = new()
        {
            // Header
            ["Title"] = new() {
                { Language.English, "Companion AI v3.0 - TurnPlanner System" },
                { Language.Korean, "동료 AI v3.0 - TurnPlanner 시스템" },
                { Language.Russian, "ИИ Компаньонов v3.0 - Система TurnPlanner" },
                { Language.Japanese, "仲間AI v3.0 - TurnPlannerシステム" }
            },
            ["Subtitle"] = new() {
                { Language.English, "Complete AI replacement with TurnPlanner architecture" },
                { Language.Korean, "TurnPlanner 아키텍처 기반 완전한 AI 대체" },
                { Language.Russian, "Полная замена ИИ на архитектуру TurnPlanner" },
                { Language.Japanese, "TurnPlannerアーキテクチャによる完全なAI置換" }
            },

            // Global Settings
            ["GlobalSettings"] = new() {
                { Language.English, "Global Settings" },
                { Language.Korean, "전역 설정" },
                { Language.Russian, "Общие настройки" },
                { Language.Japanese, "グローバル設定" }
            },
            ["EnableDebugLogging"] = new() {
                { Language.English, "Enable Debug Logging" },
                { Language.Korean, "디버그 로깅 활성화" },
                { Language.Russian, "Включить отладочный журнал" },
                { Language.Japanese, "デバッグログを有効化" }
            },
            ["ShowAIDecisionLog"] = new() {
                { Language.English, "Show AI Decision Log" },
                { Language.Korean, "AI 결정 로그 표시" },
                { Language.Russian, "Показать журнал решений ИИ" },
                { Language.Japanese, "AI判断ログを表示" }
            },
            ["EnableAISpeech"] = new() {
                { Language.English, "Enable AI Speech Bubbles" },
                { Language.Korean, "AI 대사 말풍선 활성화" },
                { Language.Russian, "Включить реплики ИИ" },
                { Language.Japanese, "AIセリフ吹き出しを有効化" }
            },
            ["EnableVictoryBark"] = new() {
                { Language.English, "Victory Bark" },
                { Language.Korean, "승리 환호" },
                { Language.Russian, "Возглас победы" },
                { Language.Japanese, "勝利の叫び" }
            },
            ["ReloadDialogue"] = new() {
                { Language.English, "Reload Dialogue JSON" },
                { Language.Korean, "대사 JSON 다시 불러오기" },
                { Language.Russian, "Перезагрузить JSON реплик" },
                { Language.Japanese, "セリフJSON再読み込み" }
            },
            ["Language"] = new() {
                { Language.English, "Language" },
                { Language.Korean, "언어" },
                { Language.Russian, "Язык" },
                { Language.Japanese, "言語" }
            },

            // Party Members
            ["PartyMembers"] = new() {
                { Language.English, "Party Members" },
                { Language.Korean, "파티원" },
                { Language.Russian, "Члены группы" },
                { Language.Japanese, "パーティメンバー" }
            },
            ["AI"] = new() {
                { Language.English, "AI" },
                { Language.Korean, "AI" },
                { Language.Russian, "ИИ" },
                { Language.Japanese, "AI" }
            },
            ["Character"] = new() {
                { Language.English, "Character" },
                { Language.Korean, "캐릭터" },
                { Language.Russian, "Персонаж" },
                { Language.Japanese, "キャラクター" }
            },
            ["Role"] = new() {
                { Language.English, "Role" },
                { Language.Korean, "역할" },
                { Language.Russian, "Роль" },
                { Language.Japanese, "役割" }
            },
            ["Range"] = new() {
                { Language.English, "Range" },
                { Language.Korean, "거리" },
                { Language.Russian, "Дальность" },
                { Language.Japanese, "射程" }
            },
            ["NoCharacters"] = new() {
                { Language.English, "No characters available. Load a save game first." },
                { Language.Korean, "사용 가능한 캐릭터가 없습니다. 먼저 저장 파일을 불러오세요." },
                { Language.Russian, "Нет доступных персонажей. Сначала загрузите сохранение." },
                { Language.Japanese, "利用可能なキャラクターがいません。先にセーブデータを読み込んでください。" }
            },

            // Combat Role
            ["CombatRole"] = new() {
                { Language.English, "Combat Role" },
                { Language.Korean, "전투 역할" },
                { Language.Russian, "Боевая роль" },
                { Language.Japanese, "戦闘役割" }
            },
            ["CombatRoleDesc"] = new() {
                { Language.English, "How should this character behave in combat?" },
                { Language.Korean, "이 캐릭터가 전투에서 어떻게 행동할까요?" },
                { Language.Russian, "Как этот персонаж должен вести себя в бою?" },
                { Language.Japanese, "このキャラクターは戦闘でどう行動しますか？" }
            },

            // Role names
            ["Role_Auto"] = new() {
                { Language.English, "Auto" },
                { Language.Korean, "자동" },
                { Language.Russian, "Авто" },
                { Language.Japanese, "自動" }
            },
            ["Role_Tank"] = new() {
                { Language.English, "Tank" },
                { Language.Korean, "탱커" },
                { Language.Russian, "Танк" },
                { Language.Japanese, "タンク" }
            },
            ["Role_DPS"] = new() {
                { Language.English, "DPS" },
                { Language.Korean, "딜러" },
                { Language.Russian, "Урон" },
                { Language.Japanese, "DPS" }
            },
            ["Role_Support"] = new() {
                { Language.English, "Support" },
                { Language.Korean, "지원" },
                { Language.Russian, "Поддержка" },
                { Language.Japanese, "サポート" }
            },
            ["Role_Overseer"] = new() {  // ★ v3.7.91
                { Language.English, "Overseer" },
                { Language.Korean, "오버시어" },
                { Language.Russian, "Надзиратель" },
                { Language.Japanese, "オーバーシアー" }
            },

            // Role descriptions
            ["RoleDesc_Auto"] = new() {
                { Language.English, "Automatically detects optimal role based on character abilities.\n• Has Taunt/Defense → Tank\n• Has Finisher/Heroic Act → DPS\n• Has Ally Heal/Buff → Support" },
                { Language.Korean, "캐릭터 능력을 분석하여 최적 역할을 자동 감지합니다.\n• 도발/방어 스킬 보유 → 탱커\n• 마무리/영웅적 행동 보유 → 딜러\n• 아군 힐/버프 보유 → 지원" },
                { Language.Russian, "Автоматически определяет оптимальную роль по способностям.\n• Есть Провокация/Защита → Танк\n• Есть Добивание/Героический акт → Урон\n• Есть Лечение/Баффы союзников → Поддержка" },
                { Language.Japanese, "キャラクターの能力に基づき最適な役割を自動検出します。\n• 挑発/防御スキルあり → タンク\n• フィニッシャー/英雄的行動あり → DPS\n• 味方回復/バフあり → サポート" }
            },
            ["RoleDesc_Tank"] = new() {
                { Language.English, "Frontline fighter. Draws enemy attention, uses defensive skills, protects allies." },
                { Language.Korean, "최전방 전사. 적의 주의를 끌고, 방어 스킬 사용, 아군을 보호합니다." },
                { Language.Russian, "Боец первой линии. Привлекает внимание врагов, использует защитные навыки, защищает союзников." },
                { Language.Japanese, "前衛戦士。敵の注意を引き、防御スキルを使用し、味方を守ります。" }
            },
            ["RoleDesc_DPS"] = new() {
                { Language.English, "Damage dealer. Focuses on killing enemies quickly, prioritizes low HP targets." },
                { Language.Korean, "딜러. 적을 빠르게 처치하는 데 집중, 체력 낮은 적 우선 공격." },
                { Language.Russian, "Наносит урон. Сосредоточен на быстром уничтожении врагов, приоритет — цели с низким HP." },
                { Language.Japanese, "ダメージディーラー。敵の素早い撃破に集中し、低HPの敵を優先攻撃。" }
            },
            ["RoleDesc_Support"] = new() {
                { Language.English, "Team supporter. Prioritizes buffs/debuffs, heals allies, avoids front line." },
                { Language.Korean, "팀 서포터. 버프/디버프 우선, 아군 치유, 최전방 회피." },
                { Language.Russian, "Поддержка команды. Приоритет — баффы/дебаффы, лечение союзников, избегает передовой." },
                { Language.Japanese, "チームサポーター。バフ/デバフを優先し、味方を回復、前線を避けます。" }
            },
            // ★ v3.7.91: Overseer role description
            ["RoleDesc_Overseer"] = new() {
                { Language.English, "Familiar master. Uses pets as primary damage source, activates Momentum before Warp Relay, retreats within familiar ability range." },
                { Language.Korean, "사역마 마스터. 펫을 주력 딜링으로 활용, Warp Relay 전 Momentum 활성화, 사역마 스킬 사거리 내 후퇴." },
                { Language.Russian, "Мастер фамильяра. Использует питомцев как основной источник урона, активирует Импульс перед Варп-ретранслятором, отступает в пределах дальности способностей фамильяра." },
                { Language.Japanese, "ファミリアマスター。ペットを主力ダメージ源として使用し、ワープリレー前にモメンタムを発動、ファミリアスキル射程内に退避。" }
            },

            // Range Preference
            ["RangePreference"] = new() {
                { Language.English, "Range Preference" },
                { Language.Korean, "거리 선호도" },
                { Language.Russian, "Предпочтение дальности" },
                { Language.Japanese, "射程の好み" }
            },
            ["RangePreferenceDesc"] = new() {
                { Language.English, "How does this character prefer to engage enemies?" },
                { Language.Korean, "이 캐릭터가 적과 어떻게 교전할까요?" },
                { Language.Russian, "Как этот персонаж предпочитает вступать в бой?" },
                { Language.Japanese, "このキャラクターはどのように敵と交戦しますか？" }
            },

            // Range preference names
            ["Range_Adaptive"] = new() {
                { Language.English, "Adaptive" },
                { Language.Korean, "적응형" },
                { Language.Russian, "Адаптивный" },
                { Language.Japanese, "適応型" }
            },
            ["Range_PreferMelee"] = new() {
                { Language.English, "Melee" },
                { Language.Korean, "근접" },
                { Language.Russian, "Ближний бой" },
                { Language.Japanese, "近接" }
            },
            ["Range_PreferRanged"] = new() {
                { Language.English, "Ranged" },
                { Language.Korean, "원거리" },
                { Language.Russian, "Дальний бой" },
                { Language.Japanese, "遠距離" }
            },

            // Range preference descriptions
            ["RangeDesc_Adaptive"] = new() {
                { Language.English, "Uses whatever weapon/skill is already in range. Minimizes unnecessary movement." },
                { Language.Korean, "이미 사거리 내에 있는 무기/스킬 사용. 불필요한 이동 최소화." },
                { Language.Russian, "Использует оружие/навыки в пределах текущей дальности. Минимизирует лишние перемещения." },
                { Language.Japanese, "射程内の武器/スキルを使用。不要な移動を最小化します。" }
            },
            ["RangeDesc_PreferMelee"] = new() {
                { Language.English, "Actively moves toward enemies for close combat. Best for melee fighters." },
                { Language.Korean, "적에게 적극적으로 접근. 근접 전투원에게 적합." },
                { Language.Russian, "Активно сближается с врагами для ближнего боя. Лучше всего для бойцов ближнего боя." },
                { Language.Japanese, "積極的に敵に接近して白兵戦。近接戦闘員に最適。" }
            },
            ["RangeDesc_PreferRanged"] = new() {
                { Language.English, "Keeps safe distance from enemies. Prioritizes ranged attacks over melee." },
                { Language.Korean, "적과 안전 거리 유지. 근접보다 원거리 공격 우선." },
                { Language.Russian, "Держит безопасную дистанцию от врагов. Приоритет — дальние атаки." },
                { Language.Japanese, "敵から安全な距離を維持。近接より遠距離攻撃を優先。" }
            },

            // ★ v3.2.30: Kill Simulator
            ["UseKillSimulator"] = new() {
                { Language.English, "Use Kill Simulator" },
                { Language.Korean, "킬 시뮬레이터 사용" },
                { Language.Russian, "Симулятор убийств" },
                { Language.Japanese, "キルシミュレーター使用" }
            },
            ["UseKillSimulatorDesc"] = new() {
                { Language.English, "Simulates multi-ability combinations to find confirmed kills.\nSlightly increases processing time but improves kill efficiency." },
                { Language.Korean, "다중 능력 조합을 시뮬레이션하여 확정 킬을 찾습니다.\n처리 시간이 약간 증가하지만 킬 효율이 향상됩니다." },
                { Language.Russian, "Симулирует комбинации способностей для подтверждённых убийств.\nНемного увеличивает время обработки, но повышает эффективность." },
                { Language.Japanese, "複数能力の組み合わせをシミュレートして確実なキルを探します。\n処理時間がわずかに増加しますが、キル効率が向上します。" }
            },

            // ★ v3.3.00: AOE Optimization
            ["UseAoEOptimization"] = new() {
                { Language.English, "Use AOE Optimization" },
                { Language.Korean, "AOE 최적화 사용" },
                { Language.Russian, "Оптимизация AOE" },
                { Language.Japanese, "AOE最適化を使用" }
            },
            ["UseAoEOptimizationDesc"] = new() {
                { Language.English, "Detect enemy clusters for optimal AOE targeting.\nSlightly increases processing time but improves AOE efficiency." },
                { Language.Korean, "적 클러스터를 탐지하여 최적의 AOE 위치를 찾습니다.\n처리 시간이 약간 증가하지만 AOE 효율이 향상됩니다." },
                { Language.Russian, "Обнаруживает скопления врагов для оптимального наведения AOE.\nНемного увеличивает время обработки, но повышает эффективность AOE." },
                { Language.Japanese, "敵の密集を検出してAOEの最適な位置を特定します。\n処理時間がわずかに増加しますが、AOE効率が向上します。" }
            },

            // ★ v3.4.00: Predictive Movement
            ["UsePredictiveMovement"] = new() {
                { Language.English, "Use Predictive Movement" },
                { Language.Korean, "예측적 이동 사용" },
                { Language.Russian, "Предиктивное движение" },
                { Language.Japanese, "予測移動を使用" }
            },
            ["UsePredictiveMovementDesc"] = new() {
                { Language.English, "Predict enemy movement to select safer positions.\nConsiders where enemies can move next turn." },
                { Language.Korean, "적 이동을 예측하여 더 안전한 위치를 선택합니다.\n다음 턴에 적이 이동할 수 있는 위치를 고려합니다." },
                { Language.Russian, "Предсказывает движение врагов для выбора более безопасных позиций.\nУчитывает, куда враги могут переместиться в следующий ход." },
                { Language.Japanese, "敵の移動を予測してより安全な位置を選択します。\n次のターンに敵が移動できる位置を考慮します。" }
            },
            // ★ v3.9.72: Weapon Set Rotation
            ["EnableWeaponSetRotation"] = new() {
                { Language.English, "Enable Weapon Set Rotation" },
                { Language.Korean, "무기 세트 로테이션 사용" },
                { Language.Russian, "Ротация комплектов оружия" },
                { Language.Japanese, "武器セットローテーション" }
            },
            ["EnableWeaponSetRotationDesc"] = new() {
                { Language.English, "Use both weapon sets in a single turn.\nSwitches weapons (0 AP) to use attacks from the alternate set.\n⚠️ This feature is under development and may not work as intended." },
                { Language.Korean, "한 턴에 양쪽 무기 세트를 모두 사용합니다.\n무기 전환(0 AP)으로 대체 세트의 공격을 활용합니다.\n⚠️ 이 기능은 개발 중이며 의도대로 동작하지 않을 수 있습니다." },
                { Language.Russian, "Использовать оба комплекта оружия за один ход.\nПереключает оружие (0 AP) для атак из альтернативного комплекта.\n⚠️ Эта функция находится в разработке и может работать не так, как задумано." },
                { Language.Japanese, "1ターンで両方の武器セットを使用します。\n武器切替(0 AP)で代替セットの攻撃を活用します。\n⚠️ この機能は開発中であり、意図した通りに動作しない場合があります。" }
            },

            // ★ v3.5.13: Advanced Settings UI
            ["AdvancedSettings"] = new() {
                { Language.English, "Advanced Settings" },
                { Language.Korean, "고급 설정" },
                { Language.Russian, "Расширенные настройки" },
                { Language.Japanese, "詳細設定" }
            },
            ["AdvancedWarning"] = new() {
                { Language.English, "⚠️ Changing these values may negatively affect AI behavior. Use with caution." },
                { Language.Korean, "⚠️ 이 값들을 변경하면 AI 동작에 부정적인 영향을 줄 수 있습니다. 주의하세요." },
                { Language.Russian, "⚠️ Изменение этих значений может негативно повлиять на поведение ИИ. Используйте с осторожностью." },
                { Language.Japanese, "⚠️ これらの値を変更するとAIの動作に悪影響を与える可能性があります。注意して使用してください。" }
            },
            ["ResetToDefault"] = new() {
                { Language.English, "Reset to Default" },
                { Language.Korean, "기본값으로 리셋" },
                { Language.Russian, "Сбросить по умолчанию" },
                { Language.Japanese, "デフォルトにリセット" }
            },
            ["MinSafeDistance"] = new() {
                { Language.English, "Min Safe Distance" },
                { Language.Korean, "최소 안전 거리" },
                { Language.Russian, "Мин. безопасная дистанция" },
                { Language.Japanese, "最小安全距離" }
            },
            ["MinSafeDistanceDesc"] = new() {
                { Language.English, "Minimum distance ranged characters try to keep from enemies (meters)" },
                { Language.Korean, "원거리 캐릭터가 적과 유지하려는 최소 거리 (미터)" },
                { Language.Russian, "Минимальная дистанция, которую дальнобойные персонажи стараются держать от врагов (метры)" },
                { Language.Japanese, "遠距離キャラクターが敵との間に保つ最小距離（メートル）" }
            },
            ["HealAtHPPercent"] = new() {
                { Language.English, "Heal at HP%" },
                { Language.Korean, "힐 시작 HP%" },
                { Language.Russian, "Лечить при HP%" },
                { Language.Japanese, "回復開始HP%" }
            },
            ["HealAtHPPercentDesc"] = new() {
                { Language.English, "Start healing allies when their HP falls below this percentage" },
                { Language.Korean, "아군 HP가 이 퍼센트 이하로 떨어지면 힐 시작" },
                { Language.Russian, "Начать лечение союзников, когда их HP падает ниже этого процента" },
                { Language.Japanese, "味方のHPがこの割合以下になったら回復を開始" }
            },
            ["MinEnemiesForAoE"] = new() {
                { Language.English, "Min Enemies for AOE" },
                { Language.Korean, "AOE 최소 적 수" },
                { Language.Russian, "Мин. врагов для AOE" },
                { Language.Japanese, "AOE最小敵数" }
            },
            ["MinEnemiesForAoEDesc"] = new() {
                { Language.English, "Minimum number of enemies to use AOE abilities" },
                { Language.Korean, "AOE 능력 사용에 필요한 최소 적 수" },
                { Language.Russian, "Минимальное количество врагов для использования AOE способностей" },
                { Language.Japanese, "AOE能力を使用するために必要な最小敵数" }
            },

            // ★ v3.5.20: Performance Settings
            ["PerformanceSettings"] = new() {
                { Language.English, "Performance Settings" },
                { Language.Korean, "성능 설정" },
                { Language.Russian, "Настройки производительности" },
                { Language.Japanese, "パフォーマンス設定" }
            },
            ["PerformanceWarning"] = new() {
                { Language.English, "⚠️ Lower values = faster but less accurate AI. Higher values = smarter but slower." },
                { Language.Korean, "⚠️ 낮은 값 = 빠르지만 부정확한 AI. 높은 값 = 똑똑하지만 느림." },
                { Language.Russian, "⚠️ Меньшие значения = быстрее, но менее точный ИИ. Большие = умнее, но медленнее." },
                { Language.Japanese, "⚠️ 低い値 = 速いが不正確なAI。高い値 = 賢いが遅い。" }
            },
            ["MaxEnemiesToAnalyze"] = new() {
                { Language.English, "Max Enemies to Analyze" },
                { Language.Korean, "최대 분석 적 수" },
                { Language.Russian, "Макс. анализируемых врагов" },
                { Language.Japanese, "最大分析敵数" }
            },
            ["MaxEnemiesToAnalyzeDesc"] = new() {
                { Language.English, "How many enemies to evaluate when predicting threats.\nMore = accurate threat prediction, but slower.\n(Affects: Movement safety, retreat decisions)" },
                { Language.Korean, "위협 예측 시 분석할 최대 적 수.\n많을수록 위협 예측이 정확하지만 느려집니다.\n(영향: 이동 안전성, 후퇴 결정)" },
                { Language.Russian, "Сколько врагов анализировать при прогнозе угроз.\nБольше = точнее прогноз, но медленнее.\n(Влияет: безопасность движения, решения об отступлении)" },
                { Language.Japanese, "脅威予測時に分析する最大敵数。\n多いほど脅威予測が正確ですが遅くなります。\n（影響: 移動安全性、撤退判断）" }
            },
            ["MaxPositionsToEvaluate"] = new() {
                { Language.English, "Max Positions to Evaluate" },
                { Language.Korean, "최대 평가 위치 수" },
                { Language.Russian, "Макс. оцениваемых позиций" },
                { Language.Japanese, "最大評価位置数" }
            },
            ["MaxPositionsToEvaluateDesc"] = new() {
                { Language.English, "How many positions to check for optimal AOE placement.\nMore = better AOE targeting, but slower.\n(Affects: AOE ability targeting)" },
                { Language.Korean, "AOE 최적 위치 탐색 시 체크할 최대 위치 수.\n많을수록 AOE 타겟팅이 정확하지만 느려집니다.\n(영향: AOE 능력 타겟팅)" },
                { Language.Russian, "Сколько позиций проверять для оптимального размещения AOE.\nБольше = точнее наведение AOE, но медленнее.\n(Влияет: наведение AOE способностей)" },
                { Language.Japanese, "AOE最適配置のチェック位置数。\n多いほどAOEターゲティングが正確ですが遅くなります。\n（影響: AOE能力のターゲティング）" }
            },
            ["MaxClusters"] = new() {
                { Language.English, "Max Enemy Clusters" },
                { Language.Korean, "최대 클러스터 수" },
                { Language.Russian, "Макс. скоплений врагов" },
                { Language.Japanese, "最大敵クラスター数" }
            },
            ["MaxClustersDesc"] = new() {
                { Language.English, "How many enemy groups to track for AOE opportunities.\nMore = finds more AOE chances, but slower.\n(Affects: AOE ability decisions)" },
                { Language.Korean, "AOE 기회 탐색을 위해 추적할 적 그룹 수.\n많을수록 AOE 기회를 더 많이 찾지만 느려집니다.\n(영향: AOE 능력 결정)" },
                { Language.Russian, "Сколько групп врагов отслеживать для AOE возможностей.\nБольше = находит больше шансов для AOE, но медленнее.\n(Влияет: решения по AOE способностям)" },
                { Language.Japanese, "AOE機会のために追跡する敵グループ数。\n多いほどAOE機会を多く発見しますが遅くなります。\n（影響: AOE能力の判断）" }
            },
            ["MaxTilesPerEnemy"] = new() {
                { Language.English, "Max Tiles per Enemy" },
                { Language.Korean, "적당 최대 타일 수" },
                { Language.Russian, "Макс. тайлов на врага" },
                { Language.Japanese, "敵あたり最大タイル数" }
            },
            ["MaxTilesPerEnemyDesc"] = new() {
                { Language.English, "Movement tiles to analyze per enemy for threat prediction.\nMore = precise threat zones, but slower.\n(Affects: Predictive movement, safe positioning)" },
                { Language.Korean, "적 위협 예측을 위해 분석할 이동 타일 수.\n많을수록 위협 구역 예측이 정밀하지만 느려집니다.\n(영향: 예측적 이동, 안전 위치 선정)" },
                { Language.Russian, "Тайлы движения для анализа на врага при прогнозе угроз.\nБольше = точнее зоны угроз, но медленнее.\n(Влияет: предиктивное движение, безопасное позиционирование)" },
                { Language.Japanese, "脅威予測のために敵ごとに分析する移動タイル数。\n多いほど脅威ゾーンが精密ですが遅くなります。\n（影響: 予測移動、安全な位置取り）" }
            },
            ["ResetPerformanceToDefault"] = new() {
                { Language.English, "Reset Performance to Default" },
                { Language.Korean, "성능 설정 기본값으로" },
                { Language.Russian, "Сбросить настройки производительности" },
                { Language.Japanese, "パフォーマンス設定をリセット" }
            },

            // ★ v3.8.12: AOE Settings
            ["AoESettings"] = new() {
                { Language.English, "AOE Settings" },
                { Language.Korean, "AOE 설정" },
                { Language.Russian, "Настройки AOE" },
                { Language.Japanese, "AOE設定" }
            },
            ["AoEWarning"] = new() {
                { Language.English, "⚠️ Controls how AI handles AOE abilities that may hit allies." },
                { Language.Korean, "⚠️ 아군에게 피해를 줄 수 있는 AOE 능력의 AI 처리 방식을 조절합니다." },
                { Language.Russian, "⚠️ Управляет обработкой ИИ AOE способностей, которые могут задеть союзников." },
                { Language.Japanese, "⚠️ 味方に当たる可能性のあるAOE能力のAI処理方法を制御します。" }
            },
            ["MaxPlayerAlliesHit"] = new() {
                { Language.English, "Max Allies in AOE" },
                { Language.Korean, "AOE 최대 허용 아군 수" },
                { Language.Russian, "Макс. союзников в AOE" },
                { Language.Japanese, "AOE内最大味方数" }
            },
            // ★ v3.8.94: 설명 업데이트 — 모든 AoE 타입 통합, 허용 범위 내 감점 없음
            ["MaxPlayerAlliesHitDesc"] = new() {
                { Language.English, "Maximum number of allies allowed in ALL AOE areas (self-AoE, melee AoE, ranged AoE).\n0 = Never hit allies, 1 = Allow 1 ally, 2 = Allow 2, 3 = Allow 3.\nWithin limit = fully allowed (no penalty)." },
                { Language.Korean, "모든 AOE 범위(자체 AOE, 근접 AOE, 원거리 AOE) 내 허용 최대 아군 수.\n0 = 아군 절대 안 맞춤, 1 = 1명 허용, 2 = 2명 허용, 3 = 3명 허용.\n허용 범위 내 = 감점 없이 완전 허용." },
                { Language.Russian, "Максимальное количество союзников во ВСЕХ зонах AOE (собственная, ближняя, дальняя).\n0 = Никогда не задевать, 1 = Допустимо 1, 2 = Допустимо 2, 3 = Допустимо 3.\nВ пределах лимита = полностью разрешено (без штрафа)." },
                { Language.Japanese, "全AOE範囲（自己AOE、近接AOE、遠距離AOE）内の許容最大味方数。\n0 = 味方に絶対当てない、1 = 1人許容、2 = 2人許容、3 = 3人許容。\n許容範囲内 = ペナルティなしで完全許可。" }
            },
            ["ResetAoEToDefault"] = new() {
                { Language.English, "Reset AOE to Default" },
                { Language.Korean, "AOE 설정 기본값으로" },
                { Language.Russian, "Сбросить настройки AOE" },
                { Language.Japanese, "AOE設定をリセット" }
            },

            // ★ v3.16.2: aiconfig.json 전체 설정 UI 노출
            // ═══════════════════════════════════════════════════
            // 상위 그룹: AI 로직 설정
            // ═══════════════════════════════════════════════════
            ["LogicSettings"] = new() {
                { Language.English, "⚠ AI Logic Settings" },
                { Language.Korean, "⚠ AI 로직 설정" },
                { Language.Russian, "⚠ Настройки логики ИИ" },
                { Language.Japanese, "⚠ AIロジック設定" }
            },
            ["LogicSettingsWarning"] = new() {
                { Language.English, "⚠️ WARNING: These are internal AI decision parameters.\nChanging values without understanding may cause unpredictable AI behavior.\nUse Reset buttons to restore defaults if issues occur." },
                { Language.Korean, "⚠️ 경고: AI 내부 의사결정 파라미터입니다.\n이해 없이 변경하면 예측할 수 없는 AI 행동이 발생할 수 있습니다.\n문제 발생 시 리셋 버튼으로 기본값을 복원하세요." },
                { Language.Russian, "⚠️ ВНИМАНИЕ: Это внутренние параметры решений ИИ.\nИзменение без понимания может вызвать непредсказуемое поведение ИИ.\nИспользуйте кнопки сброса для восстановления значений по умолчанию." },
                { Language.Japanese, "⚠️ 警告：AI内部の意思決定パラメータです。\n理解なく変更すると予測不能なAI動作が発生する可能性があります。\n問題が発生した場合はリセットボタンでデフォルトに復元してください。" }
            },
            // ★ v3.18.8: 통합 리셋 버튼
            ["ResetAllLogicToDefault"] = new() {
                { Language.English, "Reset ALL Logic Settings to Default" },
                { Language.Korean, "AI 로직 설정 전체 기본값으로" },
                { Language.Russian, "Сбросить ВСЕ настройки логики" },
                { Language.Japanese, "AIロジック設定を全てリセット" }
            },
            ["ResetAllLogicConfirm"] = new() {
                { Language.English, "All AI logic settings have been reset to defaults." },
                { Language.Korean, "모든 AI 로직 설정이 기본값으로 초기화되었습니다." },
                { Language.Russian, "Все настройки логики ИИ сброшены." },
                { Language.Japanese, "全てのAIロジック設定がリセットされました。" }
            },

            // ═══════════════════════════════════════════════════
            // 전투 임계값 (Threshold Settings)
            // ═══════════════════════════════════════════════════
            ["ThresholdSettings"] = new() {
                { Language.English, "Combat Thresholds" },
                { Language.Korean, "전투 임계값 설정" },
                { Language.Russian, "Боевые пороги" },
                { Language.Japanese, "戦闘閾値設定" }
            },
            ["ThresholdWarning"] = new() {
                { Language.English, "⚠️ Controls when AI triggers heals, buffs, retreats, and finishers. Changes apply immediately." },
                { Language.Korean, "⚠️ AI가 힐, 버프, 후퇴, 마무리를 언제 실행할지 조절합니다. 변경 즉시 적용." },
                { Language.Russian, "⚠️ Управляет моментами лечения, баффов, отступлений и добиваний ИИ. Изменения применяются немедленно." },
                { Language.Japanese, "⚠️ AIが回復・バフ・撤退・トドメを実行するタイミングを制御します。変更は即座に適用。" }
            },
            ["ResetThresholdToDefault"] = new() {
                { Language.English, "Reset Thresholds to Default" },
                { Language.Korean, "임계값 기본값으로" },
                { Language.Russian, "Сбросить пороги" },
                { Language.Japanese, "閾値をリセット" }
            },
            ["EmergencyHealHP"] = new() {
                { Language.English, "Emergency Heal HP%" },
                { Language.Korean, "긴급 힐 HP%" },
                { Language.Russian, "Экстренное лечение HP%" },
                { Language.Japanese, "緊急回復HP%" }
            },
            ["EmergencyHealHPDesc"] = new() {
                { Language.English, "Below this HP%, trigger emergency heal first.\nHigher = heal earlier, Lower = prioritize attacks" },
                { Language.Korean, "이 HP% 이하면 긴급 힐 우선 실행.\n높으면 일찍 힐, 낮으면 공격 우선" },
                { Language.Russian, "Ниже этого HP% — экстренное лечение.\nВыше = лечить раньше, Ниже = приоритет атак" },
                { Language.Japanese, "このHP%以下で緊急回復を優先実行。\n高い=早めに回復、低い=攻撃優先" }
            },
            ["HealPriorityHP"] = new() {
                { Language.English, "Heal Priority HP%" },
                { Language.Korean, "힐 우선순위 HP%" },
                { Language.Russian, "Приоритет лечения HP%" },
                { Language.Japanese, "回復優先HP%" }
            },
            ["HealPriorityHPDesc"] = new() {
                { Language.English, "Prioritize healing allies below this HP%.\nHigher = heal more often, Lower = attack more" },
                { Language.Korean, "이 HP% 이하 아군에게 힐 우선.\n높으면 힐 자주, 낮으면 공격 우선" },
                { Language.Russian, "Приоритет лечения союзников ниже этого HP%.\nВыше = чаще лечить, Ниже = больше атаковать" },
                { Language.Japanese, "このHP%以下の味方を回復優先。\n高い=回復頻度増、低い=攻撃優先" }
            },
            ["FinisherTargetHP"] = new() {
                { Language.English, "Finisher Target HP%" },
                { Language.Korean, "마무리 대상 HP%" },
                { Language.Russian, "HP% для добивания" },
                { Language.Japanese, "トドメ対象HP%" }
            },
            ["FinisherTargetHPDesc"] = new() {
                { Language.English, "Prioritize finishing enemies below this HP%.\nHigher = more aggressive finishers" },
                { Language.Korean, "적 HP가 이 이하면 마무리 우선.\n높으면 마무리 더 적극적" },
                { Language.Russian, "Добивать врагов ниже этого HP%.\nВыше = агрессивнее добивания" },
                { Language.Japanese, "敵HPがこれ以下ならトドメ優先。\n高い=より積極的なトドメ" }
            },
            ["SkipBuffBelowHP"] = new() {
                { Language.English, "Skip Buff Below HP%" },
                { Language.Korean, "버프 스킵 HP%" },
                { Language.Russian, "Пропуск баффов ниже HP%" },
                { Language.Japanese, "バフスキップHP%" }
            },
            ["SkipBuffBelowHPDesc"] = new() {
                { Language.English, "Skip buffs when own HP is below this %.\nHigher = less buffing, more survival focus" },
                { Language.Korean, "내 HP가 이 이하면 버프 스킵하고 공격/힐.\n높으면 버프 자제, 생존 우선" },
                { Language.Russian, "Пропускать баффы при HP ниже этого %.\nВыше = меньше баффов, больше выживания" },
                { Language.Japanese, "自分のHPがこの%以下ならバフスキップ。\n高い=バフ控えめ、生存優先" }
            },
            ["PreAttackBuffMinHP"] = new() {
                { Language.English, "Pre-Attack Buff Min HP%" },
                { Language.Korean, "공격 전 버프 최소 HP%" },
                { Language.Russian, "Мин. HP% для предатакового баффа" },
                { Language.Japanese, "攻撃前バフ最小HP%" }
            },
            ["PreAttackBuffMinHPDesc"] = new() {
                { Language.English, "Only use pre-attack buffs above this HP%.\nHigher = only buff when safe" },
                { Language.Korean, "이 HP% 이상일 때만 공격 전 버프 사용.\n높으면 안전할 때만 버프" },
                { Language.Russian, "Использовать предатаковые баффы только выше этого HP%.\nВыше = баффы только в безопасности" },
                { Language.Japanese, "このHP%以上でのみ攻撃前バフ使用。\n高い=安全時のみバフ" }
            },
            ["SelfDamageMinHP"] = new() {
                { Language.English, "Self-Damage Min HP%" },
                { Language.Korean, "자해 스킬 최소 HP%" },
                { Language.Russian, "Мин. HP% для самоповреждения" },
                { Language.Japanese, "自傷スキル最小HP%" }
            },
            ["SelfDamageMinHPDesc"] = new() {
                { Language.English, "Min HP% to use self-damaging skills (Blade Dance etc).\nHigher = more cautious" },
                { Language.Korean, "자해 스킬(Blade Dance 등) 사용 최소 HP%.\n높으면 더 신중하게 사용" },
                { Language.Russian, "Мин. HP% для навыков с самоповреждением.\nВыше = осторожнее" },
                { Language.Japanese, "自傷スキル(ブレードダンス等)使用最小HP%。\n高い=より慎重に使用" }
            },
            ["DesperatePhaseHP"] = new() {
                { Language.English, "Desperate Phase (Team HP%)" },
                { Language.Korean, "절박 모드 (팀 HP%)" },
                { Language.Russian, "Критическая фаза (HP% команды)" },
                { Language.Japanese, "絶望モード(チームHP%)" }
            },
            ["DesperatePhaseHPDesc"] = new() {
                { Language.English, "Team avg HP% below this = desperate mode (defense priority).\nHigher = enter defensive mode earlier" },
                { Language.Korean, "팀 평균 HP%가 이 이하면 절박 모드 (방어 우선).\n높으면 방어 모드 일찍 진입" },
                { Language.Russian, "Средний HP% команды ниже этого = критическая фаза (приоритет защиты).\nВыше = раньше перейти в защиту" },
                { Language.Japanese, "チーム平均HP%がこれ以下=絶望モード(防御優先)。\n高い=早めに防御モード移行" }
            },
            ["DesperateSelfHP"] = new() {
                { Language.English, "Desperate Phase (Self HP%)" },
                { Language.Korean, "절박 모드 (자신 HP%)" },
                { Language.Russian, "Критическая фаза (свой HP%)" },
                { Language.Japanese, "絶望モード(自分HP%)" }
            },
            ["DesperateSelfHPDesc"] = new() {
                { Language.English, "Own HP% below this = desperate mode.\nHigher = play safer earlier" },
                { Language.Korean, "자신 HP%가 이 이하면 절박 모드.\n높으면 더 일찍 방어적으로 행동" },
                { Language.Russian, "Свой HP% ниже этого = критическая фаза.\nВыше = раньше играть осторожнее" },
                { Language.Japanese, "自分HP%がこれ以下=絶望モード。\n高い=早めに防御的行動" }
            },
            ["CfgSafeDistance"] = new() {
                { Language.English, "Safe Distance (tiles)" },
                { Language.Korean, "안전 거리 (타일)" },
                { Language.Russian, "Безопасная дистанция (тайлы)" },
                { Language.Japanese, "安全距離(タイル)" }
            },
            ["CfgSafeDistanceDesc"] = new() {
                { Language.English, "Safe distance for ranged characters (tiles).\nHigher = retreat further from enemies" },
                { Language.Korean, "원거리 캐릭터의 안전 거리 (타일).\n높으면 적에게서 더 멀리 후퇴" },
                { Language.Russian, "Безопасная дистанция для стрелков (тайлы).\nВыше = дальше отступать от врагов" },
                { Language.Japanese, "遠距離キャラの安全距離(タイル)。\n高い=敵からより遠くに撤退" }
            },
            ["DangerDistance"] = new() {
                { Language.English, "Danger Distance (tiles)" },
                { Language.Korean, "위험 거리 (타일)" },
                { Language.Russian, "Опасная дистанция (тайлы)" },
                { Language.Japanese, "危険距離(タイル)" }
            },
            ["DangerDistanceDesc"] = new() {
                { Language.English, "Enemies within this distance = danger.\nHigher = more cautious positioning" },
                { Language.Korean, "이 거리 내 적이 있으면 위험 판정.\n높으면 더 신중하게 위치 선정" },
                { Language.Russian, "Враги в этом радиусе = опасность.\nВыше = осторожнее позиционирование" },
                { Language.Japanese, "この距離内の敵=危険判定。\n高い=より慎重な位置取り" }
            },
            ["OneHitKillRatio"] = new() {
                { Language.English, "One-Hit Kill Ratio" },
                { Language.Korean, "1타킬 비율" },
                { Language.Russian, "Коэффициент убийства с одного удара" },
                { Language.Japanese, "一撃キル比率" }
            },
            ["OneHitKillRatioDesc"] = new() {
                { Language.English, "Damage/HP ratio for one-hit kill detection.\nLower = more aggressive kill attempts" },
                { Language.Korean, "데미지/HP 비율이 이 이상이면 1타킬 판정.\n낮으면 더 적극적으로 킬 시도" },
                { Language.Russian, "Соотношение урона/HP для обнаружения одного удара.\nНиже = агрессивнее попытки убийства" },
                { Language.Japanese, "ダメージ/HP比率による一撃キル判定。\n低い=より積極的なキル試行" }
            },
            ["TwoHitKillRatio"] = new() {
                { Language.English, "Two-Hit Kill Ratio" },
                { Language.Korean, "2타킬 비율" },
                { Language.Russian, "Коэффициент убийства с двух ударов" },
                { Language.Japanese, "二撃キル比率" }
            },
            ["TwoHitKillRatioDesc"] = new() {
                { Language.English, "Damage/HP ratio for two-hit kill detection.\nLower = more aggressive" },
                { Language.Korean, "데미지/HP 비율이 이 이상이면 2타킬 판정.\n낮으면 더 적극적" },
                { Language.Russian, "Соотношение урона/HP для убийства двумя ударами.\nНиже = агрессивнее" },
                { Language.Japanese, "ダメージ/HP比率による二撃キル判定。\n低い=より積極的" }
            },
            ["CleanupEnemyCount"] = new() {
                { Language.English, "Cleanup Enemy Count" },
                { Language.Korean, "정리 단계 적 수" },
                { Language.Russian, "Кол-во врагов для фазы зачистки" },
                { Language.Japanese, "掃討段階敵数" }
            },
            ["CleanupEnemyCountDesc"] = new() {
                { Language.English, "When enemies ≤ this = cleanup phase (less buffing, more attacks).\nHigher = enter cleanup earlier" },
                { Language.Korean, "남은 적이 이 이하면 정리 단계 (버프 축소, 공격 집중).\n높으면 일찍 정리 모드 진입" },
                { Language.Russian, "Когда врагов ≤ этого = фаза зачистки (меньше баффов, больше атак).\nВыше = раньше начать зачистку" },
                { Language.Japanese, "残り敵がこれ以下=掃討段階(バフ減、攻撃集中)。\n高い=早めに掃討モード" }
            },
            ["OpeningPhaseMinAP"] = new() {
                { Language.English, "Opening Phase Min AP" },
                { Language.Korean, "개막 최소 AP" },
                { Language.Russian, "Мин. AP для начальной фазы" },
                { Language.Japanese, "開幕最小AP" }
            },
            ["OpeningPhaseMinAPDesc"] = new() {
                { Language.English, "Min AP for opening phase buffs on first turn.\nHigher = need more AP to use opening buffs" },
                { Language.Korean, "전투 첫 턴에 이 AP 이상이면 개막 버프 사용.\n높으면 개막 버프 조건 엄격" },
                { Language.Russian, "Мин. AP для баффов начальной фазы.\nВыше = нужно больше AP для начальных баффов" },
                { Language.Japanese, "開幕バフ使用に必要な最小AP。\n高い=開幕バフ条件が厳しい" }
            },
            ["LowThreatHP"] = new() {
                { Language.English, "Low Threat HP%" },
                { Language.Korean, "약한 적 HP%" },
                { Language.Russian, "HP% низкой угрозы" },
                { Language.Japanese, "低脅威HP%" }
            },
            ["LowThreatHPDesc"] = new() {
                { Language.English, "Enemies below this HP% have reduced threat.\nHigher = ignore more wounded enemies" },
                { Language.Korean, "적 HP가 이 이하면 위협도 감소 (거의 죽은 적).\n높으면 부상 적 무시" },
                { Language.Russian, "Враги ниже этого HP% менее опасны.\nВыше = игнорировать больше раненых" },
                { Language.Japanese, "敵HPがこれ以下なら脅威度低下。\n高い=負傷した敵をより無視" }
            },

            // ═══════════════════════════════════════════════════
            // 위협 평가 (Threat Evaluation)
            // ═══════════════════════════════════════════════════
            ["ThreatSettings"] = new() {
                { Language.English, "Threat Evaluation" },
                { Language.Korean, "위협 평가 가중치" },
                { Language.Russian, "Оценка угроз" },
                { Language.Japanese, "脅威評価" }
            },
            ["ThreatWarning"] = new() {
                { Language.English, "⚠️ Controls how AI evaluates which enemies are most dangerous. Changes apply immediately." },
                { Language.Korean, "⚠️ AI가 어떤 적이 가장 위험한지 평가하는 방식을 조절합니다. 변경 즉시 적용." },
                { Language.Russian, "⚠️ Управляет оценкой ИИ наиболее опасных врагов. Изменения применяются немедленно." },
                { Language.Japanese, "⚠️ AIがどの敵が最も危険かを評価する方法を制御します。変更は即座に適用。" }
            },
            ["ResetThreatToDefault"] = new() {
                { Language.English, "Reset Threat to Default" },
                { Language.Korean, "위협 평가 기본값으로" },
                { Language.Russian, "Сбросить оценку угроз" },
                { Language.Japanese, "脅威評価をリセット" }
            },
            ["LethalityWeight"] = new() {
                { Language.English, "Lethality Weight" },
                { Language.Korean, "치명도 가중치" },
                { Language.Russian, "Вес летальности" },
                { Language.Japanese, "致死性ウェイト" }
            },
            ["LethalityWeightDesc"] = new() {
                { Language.English, "Weight for enemy HP-based threat.\nHigher = full HP enemies seen as more threatening" },
                { Language.Korean, "적 HP 기반 위협도 가중치.\n높으면 만피 적이 더 위협적" },
                { Language.Russian, "Вес угрозы по HP врага.\nВыше = враги с полным HP опаснее" },
                { Language.Japanese, "敵HP基準の脅威度ウェイト。\n高い=満HPの敵がより脅威的" }
            },
            ["ProximityWeight"] = new() {
                { Language.English, "Proximity Weight" },
                { Language.Korean, "근접성 가중치" },
                { Language.Russian, "Вес близости" },
                { Language.Japanese, "近接性ウェイト" }
            },
            ["ProximityWeightDesc"] = new() {
                { Language.English, "Weight for distance-based threat.\nHigher = closer enemies seen as more threatening" },
                { Language.Korean, "거리 기반 위협도 가중치.\n높으면 가까운 적이 더 위협적" },
                { Language.Russian, "Вес угрозы по расстоянию.\nВыше = ближайшие враги опаснее" },
                { Language.Japanese, "距離基準の脅威度ウェイト。\n高い=近い敵がより脅威的" }
            },
            ["HealerRoleBonus"] = new() {
                { Language.English, "Healer Role Bonus" },
                { Language.Korean, "힐러 적 보너스" },
                { Language.Russian, "Бонус за хилера" },
                { Language.Japanese, "ヒーラーボーナス" }
            },
            ["HealerRoleBonusDesc"] = new() {
                { Language.English, "Extra threat for enemy healers.\nHigher = prioritize killing enemy healers" },
                { Language.Korean, "힐러 적 추가 위협도.\n높으면 적 힐러 우선 처치" },
                { Language.Russian, "Доп. угроза от вражеских хилеров.\nВыше = приоритет убийства хилеров" },
                { Language.Japanese, "敵ヒーラーの追加脅威度。\n高い=敵ヒーラー優先撃破" }
            },
            ["CasterRoleBonus"] = new() {
                { Language.English, "Caster Role Bonus" },
                { Language.Korean, "캐스터 적 보너스" },
                { Language.Russian, "Бонус за кастера" },
                { Language.Japanese, "キャスターボーナス" }
            },
            ["CasterRoleBonusDesc"] = new() {
                { Language.English, "Extra threat for enemy casters.\nHigher = prioritize killing enemy casters" },
                { Language.Korean, "캐스터 적 추가 위협도.\n높으면 적 캐스터 우선 처치" },
                { Language.Russian, "Доп. угроза от вражеских кастеров.\nВыше = приоритет убийства кастеров" },
                { Language.Japanese, "敵キャスターの追加脅威度。\n高い=敵キャスター優先撃破" }
            },
            ["RangedWeaponBonus"] = new() {
                { Language.English, "Ranged Weapon Bonus" },
                { Language.Korean, "원거리 무기 보너스" },
                { Language.Russian, "Бонус за дальнобойное" },
                { Language.Japanese, "遠距離武器ボーナス" }
            },
            ["RangedWeaponBonusDesc"] = new() {
                { Language.English, "Extra threat for enemies with ranged weapons.\nHigher = prioritize ranged enemies" },
                { Language.Korean, "원거리 무기 적 추가 위협도.\n높으면 원거리 적 우선 처치" },
                { Language.Russian, "Доп. угроза от врагов с дальнобойным оружием.\nВыше = приоритет дальнобойных" },
                { Language.Japanese, "遠距離武器持ち敵の追加脅威度。\n高い=遠距離敵優先" }
            },
            ["ThreatMaxDistance"] = new() {
                { Language.English, "Threat Max Distance (tiles)" },
                { Language.Korean, "위협 최대 거리 (타일)" },
                { Language.Russian, "Макс. дистанция угрозы (тайлы)" },
                { Language.Japanese, "脅威最大距離(タイル)" }
            },
            ["ThreatMaxDistanceDesc"] = new() {
                { Language.English, "Max distance for threat evaluation. Enemies beyond this are ignored.\nHigher = consider more distant enemies" },
                { Language.Korean, "위협 평가 최대 거리. 이 너머의 적은 무시.\n높으면 먼 적도 위협으로 고려" },
                { Language.Russian, "Макс. дистанция для оценки угроз. Дальше = игнорировать.\nВыше = учитывать более далёких врагов" },
                { Language.Japanese, "脅威評価の最大距離。これ以上の敵は無視。\n高い=遠い敵も脅威として考慮" }
            },

            // ═══════════════════════════════════════════════════
            // AoE 세부 설정 (확장)
            // ═══════════════════════════════════════════════════
            ["EnemyHitScore"] = new() {
                { Language.English, "Enemy Hit Score" },
                { Language.Korean, "적 타격 기본 점수" },
                { Language.Russian, "Очки за попадание по врагу" },
                { Language.Japanese, "敵命中基本スコア" }
            },
            ["EnemyHitScoreDesc"] = new() {
                { Language.English, "Base score per enemy hit by AoE.\nHigher = AI uses AoE more aggressively" },
                { Language.Korean, "AoE 적 1명당 기본 점수.\n높으면 AoE 더 적극적 사용" },
                { Language.Russian, "Базовые очки за каждого поражённого врага.\nВыше = ИИ агрессивнее использует AOE" },
                { Language.Japanese, "AoEで敵1体あたりの基本スコア。\n高い=AoEをより積極的に使用" }
            },
            ["PlayerAllyPenaltyMult"] = new() {
                { Language.English, "Ally Penalty Multiplier" },
                { Language.Korean, "아군 피격 페널티 배수" },
                { Language.Russian, "Множитель штрафа за союзников" },
                { Language.Japanese, "味方被弾ペナルティ倍率" }
            },
            ["PlayerAllyPenaltyMultDesc"] = new() {
                { Language.English, "Penalty multiplier when AoE hits allies.\nHigher = avoid hitting allies more" },
                { Language.Korean, "AoE가 아군을 맞출 때 페널티 배수.\n높으면 아군 피격 더 기피" },
                { Language.Russian, "Множитель штрафа при попадании AOE по союзникам.\nВыше = больше избегать попаданий" },
                { Language.Japanese, "AoEが味方に当たる時のペナルティ倍率。\n高い=味方被弾をより回避" }
            },
            ["NpcAllyPenaltyMult"] = new() {
                { Language.English, "NPC Ally Penalty Multiplier" },
                { Language.Korean, "NPC 아군 페널티 배수" },
                { Language.Russian, "Множитель штрафа за NPC" },
                { Language.Japanese, "NPC味方ペナルティ倍率" }
            },
            ["NpcAllyPenaltyMultDesc"] = new() {
                { Language.English, "Penalty multiplier when AoE hits NPC allies.\nHigher = protect NPCs more" },
                { Language.Korean, "AoE가 NPC 아군을 맞출 때 페널티 배수.\n높으면 NPC 아군 보호 강화" },
                { Language.Russian, "Множитель штрафа при попадании по NPC союзникам.\nВыше = больше защищать NPC" },
                { Language.Japanese, "AoEがNPC味方に当たる時のペナルティ倍率。\n高い=NPC味方保護強化" }
            },
            ["CasterSelfPenaltyMult"] = new() {
                { Language.English, "Caster Self Penalty" },
                { Language.Korean, "캐스터 자기 피격 배수" },
                { Language.Russian, "Штраф за самопопадание" },
                { Language.Japanese, "キャスター自己被弾倍率" }
            },
            ["CasterSelfPenaltyMultDesc"] = new() {
                { Language.English, "Penalty multiplier when caster hits self with AoE.\nHigher = avoid self-damage more" },
                { Language.Korean, "캐스터가 자기 AoE에 맞을 때 페널티 배수.\n높으면 자기 피격 더 기피" },
                { Language.Russian, "Множитель штрафа при самопопадании AOE.\nВыше = больше избегать самоповреждения" },
                { Language.Japanese, "キャスターが自身のAoEに当たる時のペナルティ倍率。\n高い=自傷をより回避" }
            },
            ["CfgMinClusterSize"] = new() {
                { Language.English, "Min Cluster Size" },
                { Language.Korean, "클러스터 최소 크기" },
                { Language.Russian, "Мин. размер скопления" },
                { Language.Japanese, "最小クラスターサイズ" }
            },
            ["CfgMinClusterSizeDesc"] = new() {
                { Language.English, "Min enemies in a group for AoE targeting.\n1 = single enemies are valid AoE targets" },
                { Language.Korean, "AoE 타겟팅 유효 클러스터 최소 적 수.\n1이면 단일 적도 AoE 대상" },
                { Language.Russian, "Мин. врагов в группе для наведения AOE.\n1 = даже одиночные враги — цели AOE" },
                { Language.Japanese, "AoEターゲティング有効クラスター最小敵数。\n1=単体敵もAoE対象" }
            },
            ["ClusterNpcAllyPenalty"] = new() {
                { Language.English, "Cluster NPC Ally Penalty" },
                { Language.Korean, "클러스터 NPC 감점" },
                { Language.Russian, "Штраф за NPC в скоплении" },
                { Language.Japanese, "クラスターNPCペナルティ" }
            },
            ["ClusterNpcAllyPenaltyDesc"] = new() {
                { Language.English, "Score penalty for NPC allies in AoE cluster.\nHigher = protect NPCs in clusters more" },
                { Language.Korean, "클러스터 내 NPC 아군 감점.\n높으면 NPC 보호 강화" },
                { Language.Russian, "Штрафные очки за NPC союзников в скоплении.\nВыше = больше защищать NPC" },
                { Language.Japanese, "AoEクラスター内NPC味方の減点。\n高い=NPC保護強化" }
            },

            // ═══════════════════════════════════════════════════
            // 스코어링 가중치 (Scoring Weights)
            // ═══════════════════════════════════════════════════
            ["ScoringSettings"] = new() {
                { Language.English, "Scoring Weights" },
                { Language.Korean, "스코어링 가중치" },
                { Language.Russian, "Весовые коэффициенты" },
                { Language.Japanese, "スコアリングウェイト" }
            },
            ["ScoringWarning"] = new() {
                { Language.English, "⚠️ Fine-tune AI decision scoring. Higher values increase that factor's importance." },
                { Language.Korean, "⚠️ AI 의사결정 점수를 세밀하게 조절합니다. 높은 값 = 해당 요소의 중요도 증가." },
                { Language.Russian, "⚠️ Тонкая настройка оценки решений ИИ. Выше = больше важность этого фактора." },
                { Language.Japanese, "⚠️ AI判断スコアの微調整。高い値=その要素の重要度増加。" }
            },
            ["ResetScoringToDefault"] = new() {
                { Language.English, "Reset Scoring to Default" },
                { Language.Korean, "스코어링 기본값으로" },
                { Language.Russian, "Сбросить коэффициенты" },
                { Language.Japanese, "スコアリングをリセット" }
            },
            ["ScoringGroup_BuffMult"] = new() {
                { Language.English, "— Buff Multipliers —" },
                { Language.Korean, "— 버프 배율 —" },
                { Language.Russian, "— Множители баффов —" },
                { Language.Japanese, "— バフ倍率 —" }
            },
            ["OpeningPhaseBuffMult"] = new() {
                { Language.English, "Opening Phase Buff Mult" },
                { Language.Korean, "개막 버프 배율" },
                { Language.Russian, "Множитель начальных баффов" },
                { Language.Japanese, "開幕バフ倍率" }
            },
            ["OpeningPhaseBuffMultDesc"] = new() {
                { Language.English, "Buff score multiplier in opening phase.\nHigher = buff more on first turns" },
                { Language.Korean, "개막 단계 버프 점수 배율.\n높으면 첫 턴 버프 적극적" },
                { Language.Russian, "Множитель баффов в начальной фазе.\nВыше = больше баффов в первых ходах" },
                { Language.Japanese, "開幕段階のバフスコア倍率。\n高い=最初のターンでバフ積極的" }
            },
            ["CleanupPhaseBuffMult"] = new() {
                { Language.English, "Cleanup Phase Buff Mult" },
                { Language.Korean, "정리 단계 버프 배율" },
                { Language.Russian, "Множитель баффов в фазе зачистки" },
                { Language.Japanese, "掃討段階バフ倍率" }
            },
            ["CleanupPhaseBuffMultDesc"] = new() {
                { Language.English, "Buff score multiplier in cleanup phase.\nHigher = still buff during cleanup" },
                { Language.Korean, "정리 단계 버프 점수 배율.\n높으면 정리 시에도 버프 사용" },
                { Language.Russian, "Множитель баффов в фазе зачистки.\nВыше = баффы даже при зачистке" },
                { Language.Japanese, "掃討段階のバフスコア倍率。\n高い=掃討中もバフ使用" }
            },
            ["DesperateNonDefMult"] = new() {
                { Language.English, "Desperate Non-Defense Mult" },
                { Language.Korean, "위기시 비방어 배율" },
                { Language.Russian, "Множитель незащитных в кризисе" },
                { Language.Japanese, "危機時非防御倍率" }
            },
            ["DesperateNonDefMultDesc"] = new() {
                { Language.English, "Non-defensive buff multiplier in desperate phase.\nHigher = still use offensive buffs in crisis" },
                { Language.Korean, "위기시 비방어 버프 배율.\n높으면 위기에도 공격 버프 사용" },
                { Language.Russian, "Множитель незащитных баффов в кризисе.\nВыше = наступательные баффы даже в кризисе" },
                { Language.Japanese, "危機時の非防御バフ倍率。\n高い=危機でも攻撃バフ使用" }
            },
            ["ScoringGroup_Timing"] = new() {
                { Language.English, "— Timing Bonuses —" },
                { Language.Korean, "— 타이밍 보너스 —" },
                { Language.Russian, "— Бонусы за тайминг —" },
                { Language.Japanese, "— タイミングボーナス —" }
            },
            ["PreCombatOpeningBonus"] = new() {
                { Language.English, "Pre-Combat Opening Bonus" },
                { Language.Korean, "선제 버프 개막 보너스" },
                { Language.Russian, "Бонус начальных пребоевых баффов" },
                { Language.Japanese, "戦前バフ開幕ボーナス" }
            },
            ["PreCombatOpeningBonusDesc"] = new() {
                { Language.English, "Bonus score for pre-combat buffs at battle start" },
                { Language.Korean, "전투 시작 시 선제 버프 보너스 점수" },
                { Language.Russian, "Бонусные очки за пребоевые баффы в начале боя" },
                { Language.Japanese, "戦闘開始時の戦前バフボーナススコア" }
            },
            ["PreCombatCleanupPenalty"] = new() {
                { Language.English, "Pre-Combat Cleanup Penalty" },
                { Language.Korean, "선제 버프 정리 감점" },
                { Language.Russian, "Штраф пребоевых в фазе зачистки" },
                { Language.Japanese, "戦前バフ掃討ペナルティ" }
            },
            ["PreCombatCleanupPenaltyDesc"] = new() {
                { Language.English, "Penalty for pre-combat buffs during cleanup" },
                { Language.Korean, "정리 단계에서 선제 버프 감점" },
                { Language.Russian, "Штраф за пребоевые баффы в фазе зачистки" },
                { Language.Japanese, "掃討段階での戦前バフペナルティ" }
            },
            ["PreAttackHittableBonus"] = new() {
                { Language.English, "Pre-Attack Hittable Bonus" },
                { Language.Korean, "공격전 버프 적 보너스" },
                { Language.Russian, "Бонус за атакуемых" },
                { Language.Japanese, "攻撃前バフ敵ボーナス" }
            },
            ["PreAttackHittableBonusDesc"] = new() {
                { Language.English, "Bonus for pre-attack buffs when enemies are in range" },
                { Language.Korean, "적이 사거리 내일 때 공격 전 버프 보너스" },
                { Language.Russian, "Бонус за предатаковые баффы при врагах в зоне досягаемости" },
                { Language.Japanese, "敵が射程内にいる時の攻撃前バフボーナス" }
            },
            ["PreAttackNoEnemyPenalty"] = new() {
                { Language.English, "Pre-Attack No Enemy Penalty" },
                { Language.Korean, "적 부재 버프 감점" },
                { Language.Russian, "Штраф без врагов" },
                { Language.Japanese, "敵不在バフペナルティ" }
            },
            ["PreAttackNoEnemyPenaltyDesc"] = new() {
                { Language.English, "Penalty for pre-attack buffs with no enemies in range" },
                { Language.Korean, "적 부재 시 공격 전 버프 감점" },
                { Language.Russian, "Штраф за предатаковые баффы без врагов" },
                { Language.Japanese, "敵不在時の攻撃前バフペナルティ" }
            },
            ["EmergencyDesperateBonus"] = new() {
                { Language.English, "Emergency Desperate Bonus" },
                { Language.Korean, "긴급 위기 보너스" },
                { Language.Russian, "Бонус экстренных в кризисе" },
                { Language.Japanese, "緊急危機ボーナス" }
            },
            ["EmergencyDesperateBonusDesc"] = new() {
                { Language.English, "Bonus for emergency buffs in desperate situations" },
                { Language.Korean, "위기 상황에서 긴급 버프 보너스" },
                { Language.Russian, "Бонус за экстренные баффы в кризисе" },
                { Language.Japanese, "危機状況での緊急バフボーナス" }
            },
            ["EmergencyNonDesperatePenalty"] = new() {
                { Language.English, "Emergency Non-Desperate Penalty" },
                { Language.Korean, "비위기 긴급 감점" },
                { Language.Russian, "Штраф экстренных без кризиса" },
                { Language.Japanese, "非危機緊急ペナルティ" }
            },
            ["EmergencyNonDesperatePenaltyDesc"] = new() {
                { Language.English, "Penalty for emergency buffs in non-desperate situations" },
                { Language.Korean, "비위기 시 긴급 버프 감점" },
                { Language.Russian, "Штраф за экстренные баффы без кризиса" },
                { Language.Japanese, "非危機時の緊急バフペナルティ" }
            },
            ["TauntNearEnemiesBonus"] = new() {
                { Language.English, "Taunt Near Enemies Bonus" },
                { Language.Korean, "도발 근접 적 보너스" },
                { Language.Russian, "Бонус провокации рядом с врагами" },
                { Language.Japanese, "挑発近接敵ボーナス" }
            },
            ["TauntNearEnemiesBonusDesc"] = new() {
                { Language.English, "Bonus for taunts with many nearby enemies" },
                { Language.Korean, "도발 시 근접 적 다수 보너스" },
                { Language.Russian, "Бонус за провокацию при множестве ближних врагов" },
                { Language.Japanese, "挑発時に近くの敵が多い場合のボーナス" }
            },
            ["TauntFewEnemiesPenalty"] = new() {
                { Language.English, "Taunt Few Enemies Penalty" },
                { Language.Korean, "도발 적 부족 감점" },
                { Language.Russian, "Штраф провокации при малом числе врагов" },
                { Language.Japanese, "挑発敵不足ペナルティ" }
            },
            ["TauntFewEnemiesPenaltyDesc"] = new() {
                { Language.English, "Penalty for taunts with few nearby enemies" },
                { Language.Korean, "도발 시 적 부족 감점" },
                { Language.Russian, "Штраф за провокацию при малом числе врагов" },
                { Language.Japanese, "挑発時に近くの敵が少ない場合のペナルティ" }
            },
            ["ScoringGroup_Synergy"] = new() {
                { Language.English, "— Synergy Bonuses —" },
                { Language.Korean, "— 시너지 보너스 —" },
                { Language.Russian, "— Бонусы синергии —" },
                { Language.Japanese, "— シナジーボーナス —" }
            },
            ["BuffAttackSynergy"] = new() {
                { Language.English, "Buff + Attack Synergy" },
                { Language.Korean, "버프+공격 시너지" },
                { Language.Russian, "Синергия бафф+атака" },
                { Language.Japanese, "バフ+攻撃シナジー" }
            },
            ["BuffAttackSynergyDesc"] = new() {
                { Language.English, "Bonus when attack buff + attack are planned together" },
                { Language.Korean, "공격 버프 + 공격 조합 보너스" },
                { Language.Russian, "Бонус за комбинацию бафф атаки + атака" },
                { Language.Japanese, "攻撃バフ+攻撃の組み合わせボーナス" }
            },
            ["MoveAttackSynergy"] = new() {
                { Language.English, "Move + Attack Synergy" },
                { Language.Korean, "이동+공격 시너지" },
                { Language.Russian, "Синергия движение+атака" },
                { Language.Japanese, "移動+攻撃シナジー" }
            },
            ["MoveAttackSynergyDesc"] = new() {
                { Language.English, "Bonus for move + attack combos (gap closers)" },
                { Language.Korean, "이동 + 공격 조합 보너스 (갭클로저)" },
                { Language.Russian, "Бонус за комбо движение+атака (сближение)" },
                { Language.Japanese, "移動+攻撃コンボボーナス(ギャップクローザー)" }
            },
            ["MultiAttackPerAttack"] = new() {
                { Language.English, "Multi-Attack Bonus" },
                { Language.Korean, "연속 공격 보너스" },
                { Language.Russian, "Бонус мультиатаки" },
                { Language.Japanese, "連続攻撃ボーナス" }
            },
            ["MultiAttackPerAttackDesc"] = new() {
                { Language.English, "Bonus per additional attack in a turn" },
                { Language.Korean, "공격당 추가 점수 (연속 공격)" },
                { Language.Russian, "Бонус за каждую дополнительную атаку за ход" },
                { Language.Japanese, "ターン内追加攻撃ごとのボーナス" }
            },
            ["DefenseRetreatSynergy"] = new() {
                { Language.English, "Defense + Retreat Synergy" },
                { Language.Korean, "방어+후퇴 시너지" },
                { Language.Russian, "Синергия защита+отступление" },
                { Language.Japanese, "防御+撤退シナジー" }
            },
            ["DefenseRetreatSynergyDesc"] = new() {
                { Language.English, "Bonus for defense buff + retreat combo" },
                { Language.Korean, "방어 버프 + 후퇴 조합 보너스" },
                { Language.Russian, "Бонус за комбо защитный бафф+отступление" },
                { Language.Japanese, "防御バフ+撤退コンボボーナス" }
            },
            ["KillConfirmSynergy"] = new() {
                { Language.English, "Kill Confirm Synergy" },
                { Language.Korean, "킬 확정 시너지" },
                { Language.Russian, "Синергия подтверждённого убийства" },
                { Language.Japanese, "キル確定シナジー" }
            },
            ["KillConfirmSynergyDesc"] = new() {
                { Language.English, "Bonus when planned damage ≥ target HP (confirmed kill)" },
                { Language.Korean, "킬 확정 시 보너스 (데미지 ≥ HP)" },
                { Language.Russian, "Бонус при планируемом уроне ≥ HP цели (подтверждённое убийство)" },
                { Language.Japanese, "計画ダメージ≧対象HP時のボーナス(確定キル)" }
            },
            ["AlmostKillSynergy"] = new() {
                { Language.English, "Almost Kill Synergy" },
                { Language.Korean, "거의 킬 시너지" },
                { Language.Russian, "Синергия почти убийства" },
                { Language.Japanese, "ほぼキルシナジー" }
            },
            ["AlmostKillSynergyDesc"] = new() {
                { Language.English, "Bonus when planned damage ≥ 90% of target HP" },
                { Language.Korean, "거의 킬 시 보너스 (데미지 ≥ 90% HP)" },
                { Language.Russian, "Бонус при планируемом уроне ≥ 90% HP цели" },
                { Language.Japanese, "計画ダメージ≧対象HP90%時のボーナス" }
            },
            ["ScoringGroup_Other"] = new() {
                { Language.English, "— Other Scoring —" },
                { Language.Korean, "— 기타 점수 —" },
                { Language.Russian, "— Прочие очки —" },
                { Language.Japanese, "— その他スコア —" }
            },
            ["ClearMPDangerBase"] = new() {
                { Language.English, "ClearMP Danger Penalty" },
                { Language.Korean, "MP소모+위험 감점" },
                { Language.Russian, "Штраф за расход MP в опасности" },
                { Language.Japanese, "MP消費+危険ペナルティ" }
            },
            ["ClearMPDangerBaseDesc"] = new() {
                { Language.English, "Penalty for using MP-clearing abilities in danger" },
                { Language.Korean, "위험 상황에서 MP 소모 스킬 사용 시 기본 감점" },
                { Language.Russian, "Штраф за использование навыков с расходом MP в опасности" },
                { Language.Japanese, "危険状況でMP消費スキル使用時の基本ペナルティ" }
            },
            ["AoEBonusPerEnemy"] = new() {
                { Language.English, "AoE Bonus Per Enemy" },
                { Language.Korean, "AoE 적당 보너스" },
                { Language.Russian, "Бонус AOE за врага" },
                { Language.Japanese, "AoE敵あたりボーナス" }
            },
            ["AoEBonusPerEnemyDesc"] = new() {
                { Language.English, "Score bonus per additional enemy in AoE" },
                { Language.Korean, "AoE에 추가 적 1명당 보너스" },
                { Language.Russian, "Бонусные очки за каждого дополнительного врага в AOE" },
                { Language.Japanese, "AoE内追加敵1体あたりのボーナス" }
            },
            ["InertiaBonus"] = new() {
                { Language.English, "Target Inertia Bonus" },
                { Language.Korean, "타겟 관성 보너스" },
                { Language.Russian, "Бонус инерции цели" },
                { Language.Japanese, "ターゲット慣性ボーナス" }
            },
            ["InertiaBonusDesc"] = new() {
                { Language.English, "Bonus for attacking same target as last turn.\nHigher = focus fire on one target" },
                { Language.Korean, "이전 턴 동일 타겟 공격 보너스.\n높으면 한 타겟 집중 공격" },
                { Language.Russian, "Бонус за атаку той же цели, что и в прошлый ход.\nВыше = фокус огня на одной цели" },
                { Language.Japanese, "前ターンと同じ対象を攻撃する際のボーナス。\n高い=1体に集中攻撃" }
            },
            ["HardCCExploitBonus"] = new() {
                { Language.English, "Hard CC Exploit Bonus" },
                { Language.Korean, "CC 활용 보너스" },
                { Language.Russian, "Бонус за использование CC" },
                { Language.Japanese, "ハードCC活用ボーナス" }
            },
            ["HardCCExploitBonusDesc"] = new() {
                { Language.English, "Bonus for attacking stunned/immobilized enemies" },
                { Language.Korean, "기절/고정된 적 공격 보너스" },
                { Language.Russian, "Бонус за атаку оглушённых/обездвиженных врагов" },
                { Language.Japanese, "気絶/固定された敵攻撃ボーナス" }
            },
            ["DOTFollowUpBonus"] = new() {
                { Language.English, "DoT Follow-Up Bonus" },
                { Language.Korean, "DoT 후속 보너스" },
                { Language.Russian, "Бонус за продолжение по DoT" },
                { Language.Japanese, "DoT追撃ボーナス" }
            },
            ["DOTFollowUpBonusDesc"] = new() {
                { Language.English, "Bonus for attacking enemies with active DoTs" },
                { Language.Korean, "DoT(출혈/독/화상) 걸린 적 후속 공격 보너스" },
                { Language.Russian, "Бонус за атаку врагов с активным DoT" },
                { Language.Japanese, "DoT(出血/毒/火傷)が付いた敵への追撃ボーナス" }
            },

            // ═══════════════════════════════════════════════════
            // 역할별 타겟 가중치 (Role Target Weights)
            // ═══════════════════════════════════════════════════
            ["RoleWeightSettings"] = new() {
                { Language.English, "Role Target Weights" },
                { Language.Korean, "역할별 타겟 가중치" },
                { Language.Russian, "Весовые коэффициенты ролей" },
                { Language.Japanese, "役割別ターゲットウェイト" }
            },
            ["RoleWeightWarning"] = new() {
                { Language.English, "⚠️ Controls how each role selects attack targets. Changes apply immediately." },
                { Language.Korean, "⚠️ 각 역할이 공격 대상을 선택하는 방식을 조절합니다. 변경 즉시 적용." },
                { Language.Russian, "⚠️ Управляет выбором целей для атаки каждой роли. Изменения применяются немедленно." },
                { Language.Japanese, "⚠️ 各役割の攻撃対象選択方法を制御します。変更は即座に適用。" }
            },
            ["ResetRoleWeightToDefault"] = new() {
                { Language.English, "Reset Role Weights to Default" },
                { Language.Korean, "역할 가중치 기본값으로" },
                { Language.Russian, "Сбросить весовые коэффициенты ролей" },
                { Language.Japanese, "役割ウェイトをリセット" }
            },
            ["RW_HPPercent"] = new() {
                { Language.English, "Low HP Priority" },
                { Language.Korean, "낮은 HP 우선" },
                { Language.Russian, "Приоритет низкого HP" },
                { Language.Japanese, "低HP優先" }
            },
            ["RW_HPPercentDesc"] = new() {
                { Language.English, "Weight for targeting low HP enemies.\nHigher = focus on wounded enemies" },
                { Language.Korean, "낮은 HP 적 우선 가중치.\n높으면 빈사 적 집중" },
                { Language.Russian, "Вес для приоритета целей с низким HP.\nВыше = фокус на раненых" },
                { Language.Japanese, "低HP敵の優先ウェイト。\n高い=負傷した敵に集中" }
            },
            ["RW_Threat"] = new() {
                { Language.English, "Threat Priority" },
                { Language.Korean, "위협도 우선" },
                { Language.Russian, "Приоритет угрозы" },
                { Language.Japanese, "脅威度優先" }
            },
            ["RW_ThreatDesc"] = new() {
                { Language.English, "Weight for targeting threatening enemies.\nHigher = focus on dangerous enemies" },
                { Language.Korean, "위협적인 적 우선 가중치.\n높으면 위험한 적 먼저" },
                { Language.Russian, "Вес для приоритета угрожающих целей.\nВыше = фокус на опасных" },
                { Language.Japanese, "脅威的な敵の優先ウェイト。\n高い=危険な敵を優先" }
            },
            ["RW_Distance"] = new() {
                { Language.English, "Distance Priority" },
                { Language.Korean, "거리 우선" },
                { Language.Russian, "Приоритет расстояния" },
                { Language.Japanese, "距離優先" }
            },
            ["RW_DistanceDesc"] = new() {
                { Language.English, "Weight for targeting closer enemies.\nHigher = attack nearest enemies first" },
                { Language.Korean, "가까운 적 우선 가중치.\n높으면 가까운 적 집중" },
                { Language.Russian, "Вес для приоритета ближайших целей.\nВыше = атаковать ближайших первыми" },
                { Language.Japanese, "近い敵の優先ウェイト。\n高い=近い敵を優先攻撃" }
            },
            ["RW_FinisherBonus"] = new() {
                { Language.English, "Finisher Bonus" },
                { Language.Korean, "마무리 보너스" },
                { Language.Russian, "Бонус добивания" },
                { Language.Japanese, "トドメボーナス" }
            },
            ["RW_FinisherBonusDesc"] = new() {
                { Language.English, "Multiplier for finishable targets.\nHigher = prioritize finishing off enemies" },
                { Language.Korean, "마무리 가능 적 보너스 배수.\n높으면 마무리 적극적" },
                { Language.Russian, "Множитель для добиваемых целей.\nВыше = приоритет добивания" },
                { Language.Japanese, "トドメ可能な対象のボーナス倍率。\n高い=トドメを積極的に" }
            },
            ["RW_OneHitKillBonus"] = new() {
                { Language.English, "One-Hit Kill Bonus" },
                { Language.Korean, "1타킬 보너스" },
                { Language.Russian, "Бонус убийства одним ударом" },
                { Language.Japanese, "一撃キルボーナス" }
            },
            ["RW_OneHitKillBonusDesc"] = new() {
                { Language.English, "Multiplier for one-hit-killable targets.\nHigher = prioritize easy kills" },
                { Language.Korean, "1타킬 가능 적 보너스 배수.\n높으면 쉬운 킬 우선" },
                { Language.Russian, "Множитель для целей, убиваемых одним ударом.\nВыше = приоритет лёгких убийств" },
                { Language.Japanese, "一撃キル可能な対象のボーナス倍率。\n高い=簡単なキルを優先" }
            },

            // ═══════════════════════════════════════════════════
            // 무기 로테이션 (Weapon Rotation)
            // ═══════════════════════════════════════════════════
            ["WeaponRotationSettings"] = new() {
                { Language.English, "Weapon Rotation Settings" },
                { Language.Korean, "무기 로테이션 설정" },
                { Language.Russian, "Настройки ротации оружия" },
                { Language.Japanese, "武器ローテーション設定" }
            },
            ["WeaponRotationWarning"] = new() {
                { Language.English, "⚠️ This feature is under development and may not work as intended.\nControls weapon set switching behavior during combat." },
                { Language.Korean, "⚠️ 이 기능은 개발 중이며 의도대로 동작하지 않을 수 있습니다.\n전투 중 무기 세트 전환 동작을 조절합니다." },
                { Language.Russian, "⚠️ Эта функция находится в разработке и может работать не так, как задумано.\nУправляет переключением комплектов оружия в бою." },
                { Language.Japanese, "⚠️ この機能は開発中であり、意図した通りに動作しない場合があります。\n戦闘中の武器セット切り替え動作を制御します。" }
            },
            ["ResetWeaponRotationToDefault"] = new() {
                { Language.English, "Reset Weapon Rotation to Default" },
                { Language.Korean, "무기 로테이션 기본값으로" },
                { Language.Russian, "Сбросить ротацию оружия" },
                { Language.Japanese, "武器ローテーションをリセット" }
            },
            ["MaxSwitchesPerTurn"] = new() {
                { Language.English, "Max Switches Per Turn" },
                { Language.Korean, "턴당 최대 전환 횟수" },
                { Language.Russian, "Макс. переключений за ход" },
                { Language.Japanese, "ターンあたり最大切り替え回数" }
            },
            ["MaxSwitchesPerTurnDesc"] = new() {
                { Language.English, "Max weapon set switches per turn.\nHigher = more weapon variety per turn" },
                { Language.Korean, "턴당 최대 무기 전환 횟수.\n높으면 한 턴에 더 다양한 무기 사용" },
                { Language.Russian, "Макс. переключений оружия за ход.\nВыше = больше разнообразия оружия" },
                { Language.Japanese, "ターンあたりの最大武器切り替え回数。\n高い=1ターンでより多様な武器使用" }
            },
            ["MinEnemiesForAlternateAoE"] = new() {
                { Language.English, "Min Enemies for Alt. AoE" },
                { Language.Korean, "대체 AoE 최소 적 수" },
                { Language.Russian, "Мин. врагов для альт. AOE" },
                { Language.Japanese, "代替AoE最小敵数" }
            },
            ["MinEnemiesForAlternateAoEDesc"] = new() {
                { Language.English, "Min enemies to switch to alternate weapon set for AoE.\nLower = switch more often for AoE" },
                { Language.Korean, "대체 무기 세트 AoE 사용 최소 적 수.\n낮으면 AoE 위해 더 자주 전환" },
                { Language.Russian, "Мин. врагов для переключения на альт. комплект для AOE.\nНиже = чаще переключаться" },
                { Language.Japanese, "AoEのために代替武器セットに切り替える最小敵数。\n低い=AoEのためにより頻繁に切り替え" }
            },
        };

        public static string Get(string key)
        {
            if (Strings.TryGetValue(key, out var translations))
            {
                if (translations.TryGetValue(CurrentLanguage, out var text))
                    return text;
                if (translations.TryGetValue(Language.English, out var fallback))
                    return fallback;
            }
            return key;
        }

        public static string GetRoleName(AIRole role) => Get($"Role_{role}");
        public static string GetRoleDescription(AIRole role) => Get($"RoleDesc_{role}");
        public static string GetRangeName(RangePreference pref) => Get($"Range_{pref}");
        public static string GetRangeDescription(RangePreference pref) => Get($"RangeDesc_{pref}");
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
        public int MinEnemiesForAoE { get; set; } = 2;

        // Movement behavior
        public bool AllowRetreat { get; set; } = true;
        public bool SeekCover { get; set; } = true;
        // ★ v3.1.29: 기본값 7m로 증가 (근접 무기 사거리 3-5m 고려, 여유 확보)
        public float MinSafeDistance { get; set; } = 7.0f;

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
                    Main.LogDebug("[PerSaveSettings] GameId not available yet");
                    return;
                }

                _currentGameId = gameId;
                var filePath = GetSettingsFilePath(gameId);

                if (string.IsNullOrEmpty(filePath))
                {
                    Main.LogDebug("[PerSaveSettings] Mod path not set");
                    _cached = new PerSaveSettings();
                    return;
                }

                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    _cached = JsonConvert.DeserializeObject<PerSaveSettings>(json);
                    Main.Log($"[PerSaveSettings] Loaded {_cached?.CharacterSettings?.Count ?? 0} settings from {Path.GetFileName(filePath)}");
                }
                else
                {
                    Main.Log($"[PerSaveSettings] No settings file for GameId={gameId}, creating new");
                    _cached = new PerSaveSettings();
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[PerSaveSettings] Load error: {ex.Message}");
                _cached = new PerSaveSettings();
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
                    Main.LogDebug("[PerSaveSettings] Cannot save - GameId not available");
                    return;
                }

                var filePath = GetSettingsFilePath(gameId);
                if (string.IsNullOrEmpty(filePath))
                {
                    Main.LogDebug("[PerSaveSettings] Cannot save - mod path not set");
                    return;
                }

                if (_cached == null) return;

                var json = JsonConvert.SerializeObject(_cached, Formatting.Indented);
                File.WriteAllText(filePath, json);
                Main.LogDebug($"[PerSaveSettings] Saved {_cached.CharacterSettings.Count} settings to {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Main.LogError($"[PerSaveSettings] Save error: {ex.Message}");
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

        public bool EnableDebugLogging { get; set; } = false;
        public bool ShowAIThoughts { get; set; } = false;

        /// <summary>★ v3.9.32: AI 대사 (BarkPlayer 말풍선) 표시 여부</summary>
        public bool EnableAISpeech { get; set; } = true;

        /// <summary>★ v3.9.80: 전투 승리 시 환호 말풍선 표시 여부</summary>
        public bool EnableVictoryBark { get; set; } = true;

        public Language UILanguage { get; set; } = Language.English;

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
                    MinEnemiesForAoE = DefaultSettings.MinEnemiesForAoE,
                    AllowRetreat = DefaultSettings.AllowRetreat,
                    SeekCover = DefaultSettings.SeekCover,
                    MinSafeDistance = DefaultSettings.MinSafeDistance,
                    ConserveAmmo = DefaultSettings.ConserveAmmo,
                    HealAtHPPercent = DefaultSettings.HealAtHPPercent,
                    UseKillSimulator = DefaultSettings.UseKillSimulator,
                    UseAoEOptimization = DefaultSettings.UseAoEOptimization,
                    UsePredictiveMovement = DefaultSettings.UsePredictiveMovement,
                    EnableWeaponSetRotation = DefaultSettings.EnableWeaponSetRotation
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
                        Main.Log("Settings loaded successfully");
                    }
                    else
                    {
                        Main.Log("Using default settings");
                        Instance = new ModSettings();
                    }
                }
                else
                {
                    // ★ v3.5.21: 설정 파일이 없으면 기본값으로 자동 생성
                    Main.Log("Settings file not found, creating default settings.json");
                    Instance = new ModSettings();
                    Save();  // 기본 설정 파일 생성
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"Failed to load settings: {ex.Message}");
                Instance = new ModSettings();
            }

            // ★ v3.1.30: AI 설정 로드 (Response Curves, Role 가중치 등)
            AIConfig.Load(modEntry.Path);
        }

        public static void Save()
        {
            if (Instance == null || _modEntry == null) return;

            try
            {
                string path = GetSettingsPath();
                string json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(path, json);
                Main.LogDebug("Settings saved");
            }
            catch (Exception ex)
            {
                Main.LogError($"Failed to save settings: {ex.Message}");
            }
        }

        #endregion
    }
}
