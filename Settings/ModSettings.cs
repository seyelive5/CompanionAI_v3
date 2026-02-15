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
                    UsePredictiveMovement = DefaultSettings.UsePredictiveMovement
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
