using System;
using System.Collections.Generic;
using System.IO;
using CompanionAI_v3.MachineSpirit;
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
        Japanese,
        Chinese
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
                { Language.Japanese, "仲間AI v3.0 - TurnPlannerシステム" },
                { Language.Chinese, "同伴AI v3.0 - TurnPlanner系统" }
            },
            ["Subtitle"] = new() {
                { Language.English, "Complete AI replacement with TurnPlanner architecture" },
                { Language.Korean, "TurnPlanner 아키텍처 기반 완전한 AI 대체" },
                { Language.Russian, "Полная замена ИИ на архитектуру TurnPlanner" },
                { Language.Japanese, "TurnPlannerアーキテクチャによる完全なAI置換" },
                { Language.Chinese, "基于TurnPlanner架构的完整AI替代" }
            },

            // Global Settings
            ["GlobalSettings"] = new() {
                { Language.English, "Global Settings" },
                { Language.Korean, "전역 설정" },
                { Language.Russian, "Общие настройки" },
                { Language.Japanese, "グローバル設定" },
                { Language.Chinese, "全局设置" }
            },
            ["EnableDebugLogging"] = new() {
                { Language.English, "Enable Debug Logging" },
                { Language.Korean, "디버그 로깅 활성화" },
                { Language.Russian, "Включить отладочный журнал" },
                { Language.Japanese, "デバッグログを有効化" },
                { Language.Chinese, "启用调试日志" }
            },
            ["ShowAIDecisionLog"] = new() {
                { Language.English, "Show AI Decision Log" },
                { Language.Korean, "AI 결정 로그 표시" },
                { Language.Russian, "Показать журнал решений ИИ" },
                { Language.Japanese, "AI判断ログを表示" },
                { Language.Chinese, "显示AI决策日志" }
            },
            ["EnableAISpeech"] = new() {
                { Language.English, "Enable AI Speech Bubbles" },
                { Language.Korean, "AI 대사 말풍선 활성화" },
                { Language.Russian, "Включить реплики ИИ" },
                { Language.Japanese, "AIセリフ吹き出しを有効化" },
                { Language.Chinese, "启用AI对话气泡" }
            },
            ["EnableVictoryBark"] = new() {
                { Language.English, "Victory Bark" },
                { Language.Korean, "승리 환호" },
                { Language.Russian, "Возглас победы" },
                { Language.Japanese, "勝利の叫び" },
                { Language.Chinese, "胜利欢呼" }
            },

            // ★ v3.20.2: Debug & Diagnostics 섹션
            ["DebugDiagnostics"] = new() {
                { Language.English, "Debug & Diagnostics" },
                { Language.Korean, "디버그 & 진단" },
                { Language.Russian, "Отладка и диагностика" },
                { Language.Japanese, "デバッグ & 診断" },
                { Language.Chinese, "调试与诊断" }
            },
            ["DebugDiagnosticsDesc"] = new() {
                { Language.English, "Developer tools for inspecting AI behavior. Not required for normal gameplay." },
                { Language.Korean, "AI 행동 분석용 개발자 도구. 일반 플레이에는 필요하지 않습니다." },
                { Language.Russian, "Инструменты для анализа ИИ. Не требуются для обычной игры." },
                { Language.Japanese, "AI動作分析のための開発者ツール。通常プレイには不要です。" },
                { Language.Chinese, "用于检查AI行为的开发者工具。正常游戏无需使用。" }
            },
            ["EnableCombatReport"] = new() {
                { Language.English, "Combat Report (JSON)" },
                { Language.Korean, "전투 리포트 (JSON)" },
                { Language.Russian, "Боевой отчёт (JSON)" },
                { Language.Japanese, "戦闘レポート (JSON)" },
                { Language.Chinese, "战斗报告 (JSON)" }
            },
            ["EnableCombatReportDesc"] = new() {
                { Language.English, "Records AI decision-making each turn for post-battle analysis. Updated live during combat.\nOutput: [UMM folder]\\CompanionAI_v3\\combat_reports\\current_combat.json" },
                { Language.Korean, "매 턴 AI의 결정 근거(APBudget, TacticalEval, 이슈 감지)를 JSON으로 기록합니다.\n전투 중 실시간 갱신. 전투 후 분석 및 버그 리포트에 활용하세요.\n저장 위치: [UMM 폴더]\\CompanionAI_v3\\combat_reports\\current_combat.json" },
                { Language.Russian, "Записывает решения ИИ каждый ход для анализа. Обновляется в реальном времени.\nВывод: [папка UMM]\\CompanionAI_v3\\combat_reports\\current_combat.json" },
                { Language.Japanese, "各ターンのAI判断根拠をJSONに記録。戦闘中リアルタイム更新。\n保存先: [UMMフォルダ]\\CompanionAI_v3\\combat_reports\\current_combat.json" },
                { Language.Chinese, "每回合记录AI决策依据为JSON。战斗中实时更新。\n输出: [UMM文件夹]\\CompanionAI_v3\\combat_reports\\current_combat.json" }
            },

            // === ★ v3.48.0: Tactical Narrator 설정 ===
            ["EnableDecisionOverlay"] = new() {
                { Language.English, "Tactical Narrator" },
                { Language.Korean, "전술 내레이터" },
                { Language.Russian, "Тактический рассказчик" },
                { Language.Japanese, "タクティカルナレーター" },
                { Language.Chinese, "战术旁白" }
            },
            ["EnableDecisionOverlayDesc"] = new() {
                { Language.English, "Show companion dialogue at turn start — each character narrates the battlefield situation and their plan in their own personality" },
                { Language.Korean, "턴 시작 시 동료 대사 표시 — 각 캐릭터가 전장 상황과 계획을 자신만의 개성으로 설명합니다" },
                { Language.Russian, "Показывать реплики компаньонов в начале хода — каждый персонаж описывает ситуацию и план в своём стиле" },
                { Language.Japanese, "ターン開始時に仲間の台詞を表示 — 各キャラが戦場状況と計画を自分の個性で語ります" },
                { Language.Chinese, "回合开始时显示同伴对话——每个角色以自己的个性描述战场形势和作战计划" }
            },
            ["OverlayScale"] = new() {
                { Language.English, "Overlay Size" },
                { Language.Korean, "오버레이 크기" },
                { Language.Russian, "Размер оверлея" },
                { Language.Japanese, "オーバーレイサイズ" },
                { Language.Chinese, "覆盖层大小" }
            },

            ["EnableAlliedNPCAI"] = new() {
                { Language.English, "Allied NPC AI (Experimental)" },
                { Language.Korean, "아군 NPC AI 제어 (실험적)" },
                { Language.Russian, "AI союзников NPC (эксперим.)" },
                { Language.Japanese, "NPC味方AI制御 (実験的)" },
                { Language.Chinese, "友方NPC AI（实验性）" }
            },
            ["EnableAlliedNPCAIDesc"] = new() {
                { Language.English, "Apply CompanionAI to non-party ally units (e.g. BodyGuard mod). May conflict with the originating mod's AI." },
                { Language.Korean, "파티에 없는 아군 NPC에도 AI 적용 (예: BodyGuard 모드). 원본 모드 AI와 충돌할 수 있습니다." },
                { Language.Russian, "Применить AI к союзным NPC вне группы (напр. мод BodyGuard). Возможен конфликт с AI мода-источника." },
                { Language.Japanese, "非パーティの味方NPC（BodyGuardモッド等）にAIを適用。元モッドのAIと競合する可能性あり。" },
                { Language.Chinese, "对非队伍友方NPC应用AI（如BodyGuard模组）。可能与原模组AI冲突。" }
            },
            ["EnableShipCombatAI"] = new() {
                { Language.English, "Ship Combat AI (Experimental)" },
                { Language.Korean, "함선전투 AI 제어 (실험적)" },
                { Language.Russian, "AI в космическом бою (эксперим.)" },
                { Language.Japanese, "宇宙戦闘AI制御 (実験的)" },
                { Language.Chinese, "舰船战斗AI（实验性）" }
            },
            ["EnableShipCombatAIDesc"] = new() {
                { Language.English, "Auto-control your ship in space combat using the game's built-in ship AI." },
                { Language.Korean, "게임 내장 함선 AI를 사용하여 함선전투를 자동으로 진행합니다." },
                { Language.Russian, "Автоуправление кораблём с помощью встроенного ИИ игры." },
                { Language.Japanese, "ゲーム内蔵の艦船AIで宇宙戦闘を自動制御。" },
                { Language.Chinese, "使用游戏内置舰船AI自动控制太空战斗。" }
            },

            ["ReloadDialogue"] = new() {
                { Language.English, "Reload Dialogue JSON" },
                { Language.Korean, "대사 JSON 다시 불러오기" },
                { Language.Russian, "Перезагрузить JSON реплик" },
                { Language.Japanese, "セリフJSON再読み込み" },
                { Language.Chinese, "重新加载对话JSON" }
            },
            ["Language"] = new() {
                { Language.English, "Language" },
                { Language.Korean, "언어" },
                { Language.Russian, "Язык" },
                { Language.Japanese, "言語" },
                { Language.Chinese, "语言" }
            },

            // Party Members
            ["PartyMembers"] = new() {
                { Language.English, "Party Members" },
                { Language.Korean, "파티원" },
                { Language.Russian, "Члены группы" },
                { Language.Japanese, "パーティメンバー" },
                { Language.Chinese, "队伍成员" }
            },
            ["AI"] = new() {
                { Language.English, "AI" },
                { Language.Korean, "AI" },
                { Language.Russian, "ИИ" },
                { Language.Japanese, "AI" },
                { Language.Chinese, "AI" }
            },
            ["Character"] = new() {
                { Language.English, "Character" },
                { Language.Korean, "캐릭터" },
                { Language.Russian, "Персонаж" },
                { Language.Japanese, "キャラクター" },
                { Language.Chinese, "角色" }
            },
            ["Role"] = new() {
                { Language.English, "Role" },
                { Language.Korean, "역할" },
                { Language.Russian, "Роль" },
                { Language.Japanese, "役割" },
                { Language.Chinese, "职责" }
            },
            ["Range"] = new() {
                { Language.English, "Range" },
                { Language.Korean, "거리" },
                { Language.Russian, "Дальность" },
                { Language.Japanese, "射程" },
                { Language.Chinese, "射程" }
            },
            ["NoCharacters"] = new() {
                { Language.English, "No characters available. Load a save game first." },
                { Language.Korean, "사용 가능한 캐릭터가 없습니다. 먼저 저장 파일을 불러오세요." },
                { Language.Russian, "Нет доступных персонажей. Сначала загрузите сохранение." },
                { Language.Japanese, "利用可能なキャラクターがいません。先にセーブデータを読み込んでください。" },
                { Language.Chinese, "没有可用角色。请先加载存档。" }
            },

            // Combat Role
            ["CombatRole"] = new() {
                { Language.English, "Combat Role" },
                { Language.Korean, "전투 역할" },
                { Language.Russian, "Боевая роль" },
                { Language.Japanese, "戦闘役割" },
                { Language.Chinese, "战斗职责" }
            },
            ["CombatRoleDesc"] = new() {
                { Language.English, "How should this character behave in combat?" },
                { Language.Korean, "이 캐릭터가 전투에서 어떻게 행동할까요?" },
                { Language.Russian, "Как этот персонаж должен вести себя в бою?" },
                { Language.Japanese, "このキャラクターは戦闘でどう行動しますか？" },
                { Language.Chinese, "该角色在战斗中应如何行动？" }
            },

            // Role names
            ["Role_Auto"] = new() {
                { Language.English, "Auto" },
                { Language.Korean, "자동" },
                { Language.Russian, "Авто" },
                { Language.Japanese, "自動" },
                { Language.Chinese, "自动" }
            },
            ["Role_Tank"] = new() {
                { Language.English, "Tank" },
                { Language.Korean, "탱커" },
                { Language.Russian, "Танк" },
                { Language.Japanese, "タンク" },
                { Language.Chinese, "坦克" }
            },
            ["Role_DPS"] = new() {
                { Language.English, "DPS" },
                { Language.Korean, "딜러" },
                { Language.Russian, "Урон" },
                { Language.Japanese, "DPS" },
                { Language.Chinese, "输出" }
            },
            ["Role_Support"] = new() {
                { Language.English, "Support" },
                { Language.Korean, "지원" },
                { Language.Russian, "Поддержка" },
                { Language.Japanese, "サポート" },
                { Language.Chinese, "辅助" }
            },
            ["Role_Overseer"] = new() {  // ★ v3.7.91
                { Language.English, "Overseer" },
                { Language.Korean, "오버시어" },
                { Language.Russian, "Надзиратель" },
                { Language.Japanese, "オーバーシアー" },
                { Language.Chinese, "监督者" }
            },

            // Role descriptions
            ["RoleDesc_Auto"] = new() {
                { Language.English, "Automatically detects optimal role based on character abilities.\n• Has Taunt/Defense → Tank\n• Has Finisher/Heroic Act → DPS\n• Has Ally Heal/Buff → Support" },
                { Language.Korean, "캐릭터 능력을 분석하여 최적 역할을 자동 감지합니다.\n• 도발/방어 스킬 보유 → 탱커\n• 마무리/영웅적 행동 보유 → 딜러\n• 아군 힐/버프 보유 → 지원" },
                { Language.Russian, "Автоматически определяет оптимальную роль по способностям.\n• Есть Провокация/Защита → Танк\n• Есть Добивание/Героический акт → Урон\n• Есть Лечение/Баффы союзников → Поддержка" },
                { Language.Japanese, "キャラクターの能力に基づき最適な役割を自動検出します。\n• 挑発/防御スキルあり → タンク\n• フィニッシャー/英雄的行動あり → DPS\n• 味方回復/バフあり → サポート" },
                { Language.Chinese, "根据角色技能自动检测最佳职责。\n• 拥有嘲讽/防御 → 坦克\n• 拥有终结技/英勇行动 → 输出\n• 拥有治疗/增益 → 辅助" }
            },
            ["RoleDesc_Tank"] = new() {
                { Language.English, "Frontline fighter. Draws enemy attention, uses defensive skills, protects allies." },
                { Language.Korean, "최전방 전사. 적의 주의를 끌고, 방어 스킬 사용, 아군을 보호합니다." },
                { Language.Russian, "Боец первой линии. Привлекает внимание врагов, использует защитные навыки, защищает союзников." },
                { Language.Japanese, "前衛戦士。敵の注意を引き、防御スキルを使用し、味方を守ります。" },
                { Language.Chinese, "前线战士。吸引敌方注意力，使用防御技能，保护队友。" }
            },
            ["RoleDesc_DPS"] = new() {
                { Language.English, "Damage dealer. Focuses on killing enemies quickly, prioritizes low HP targets." },
                { Language.Korean, "딜러. 적을 빠르게 처치하는 데 집중, 체력 낮은 적 우선 공격." },
                { Language.Russian, "Наносит урон. Сосредоточен на быстром уничтожении врагов, приоритет — цели с низким HP." },
                { Language.Japanese, "ダメージディーラー。敵の素早い撃破に集中し、低HPの敵を優先攻撃。" },
                { Language.Chinese, "伤害输出。专注快速消灭敌人，优先攻击低血量目标。" }
            },
            ["RoleDesc_Support"] = new() {
                { Language.English, "Team supporter. Prioritizes buffs/debuffs, heals allies, avoids front line." },
                { Language.Korean, "팀 서포터. 버프/디버프 우선, 아군 치유, 최전방 회피." },
                { Language.Russian, "Поддержка команды. Приоритет — баффы/дебаффы, лечение союзников, избегает передовой." },
                { Language.Japanese, "チームサポーター。バフ/デバフを優先し、味方を回復、前線を避けます。" },
                { Language.Chinese, "团队辅助。优先增益/减益，治疗队友，避免前线战斗。" }
            },
            // ★ v3.7.91: Overseer role description
            ["RoleDesc_Overseer"] = new() {
                { Language.English, "Familiar master. Uses pets as primary damage source, activates Momentum before Warp Relay, retreats within familiar ability range." },
                { Language.Korean, "사역마 마스터. 펫을 주력 딜링으로 활용, Warp Relay 전 Momentum 활성화, 사역마 스킬 사거리 내 후퇴." },
                { Language.Russian, "Мастер фамильяра. Использует питомцев как основной источник урона, активирует Импульс перед Варп-ретранслятором, отступает в пределах дальности способностей фамильяра." },
                { Language.Japanese, "ファミリアマスター。ペットを主力ダメージ源として使用し、ワープリレー前にモメンタムを発動、ファミリアスキル射程内に退避。" },
                { Language.Chinese, "使魔大师。以宠物作为主要伤害来源，在亚空间中继前激活动量，在使魔技能射程内后撤。" }
            },

            // Range Preference
            ["RangePreference"] = new() {
                { Language.English, "Range Preference" },
                { Language.Korean, "거리 선호도" },
                { Language.Russian, "Предпочтение дальности" },
                { Language.Japanese, "射程の好み" },
                { Language.Chinese, "射程偏好" }
            },
            ["RangePreferenceDesc"] = new() {
                { Language.English, "How does this character prefer to engage enemies?" },
                { Language.Korean, "이 캐릭터가 적과 어떻게 교전할까요?" },
                { Language.Russian, "Как этот персонаж предпочитает вступать в бой?" },
                { Language.Japanese, "このキャラクターはどのように敵と交戦しますか？" },
                { Language.Chinese, "该角色偏好何种方式与敌交战？" }
            },

            // Range preference names
            ["Range_Adaptive"] = new() {
                { Language.English, "Adaptive" },
                { Language.Korean, "적응형" },
                { Language.Russian, "Адаптивный" },
                { Language.Japanese, "適応型" },
                { Language.Chinese, "自适应" }
            },
            ["Range_PreferMelee"] = new() {
                { Language.English, "Melee" },
                { Language.Korean, "근접" },
                { Language.Russian, "Ближний бой" },
                { Language.Japanese, "近接" },
                { Language.Chinese, "近战" }
            },
            ["Range_PreferRanged"] = new() {
                { Language.English, "Ranged" },
                { Language.Korean, "원거리" },
                { Language.Russian, "Дальний бой" },
                { Language.Japanese, "遠距離" },
                { Language.Chinese, "远程" }
            },

            // Range preference descriptions
            ["RangeDesc_Adaptive"] = new() {
                { Language.English, "Uses whatever weapon/skill is already in range. Minimizes unnecessary movement." },
                { Language.Korean, "이미 사거리 내에 있는 무기/스킬 사용. 불필요한 이동 최소화." },
                { Language.Russian, "Использует оружие/навыки в пределах текущей дальности. Минимизирует лишние перемещения." },
                { Language.Japanese, "射程内の武器/スキルを使用。不要な移動を最小化します。" },
                { Language.Chinese, "使用射程内的武器/技能。最小化不必要的移动。" }
            },
            ["RangeDesc_PreferMelee"] = new() {
                { Language.English, "Actively moves toward enemies for close combat. Best for melee fighters." },
                { Language.Korean, "적에게 적극적으로 접근. 근접 전투원에게 적합." },
                { Language.Russian, "Активно сближается с врагами для ближнего боя. Лучше всего для бойцов ближнего боя." },
                { Language.Japanese, "積極的に敵に接近して白兵戦。近接戦闘員に最適。" },
                { Language.Chinese, "主动接近敌人进行近战。最适合近战角色。" }
            },
            ["RangeDesc_PreferRanged"] = new() {
                { Language.English, "Keeps safe distance from enemies. Prioritizes ranged attacks over melee." },
                { Language.Korean, "적과 안전 거리 유지. 근접보다 원거리 공격 우선." },
                { Language.Russian, "Держит безопасную дистанцию от врагов. Приоритет — дальние атаки." },
                { Language.Japanese, "敵から安全な距離を維持。近接より遠距離攻撃を優先。" },
                { Language.Chinese, "与敌人保持安全距离。优先远程攻击而非近战。" }
            },

            // ★ v3.2.30: Kill Simulator
            ["UseKillSimulator"] = new() {
                { Language.English, "Use Kill Simulator" },
                { Language.Korean, "킬 시뮬레이터 사용" },
                { Language.Russian, "Симулятор убийств" },
                { Language.Japanese, "キルシミュレーター使用" },
                { Language.Chinese, "使用击杀模拟器" }
            },
            ["UseKillSimulatorDesc"] = new() {
                { Language.English, "Simulates multi-ability combinations to find confirmed kills.\nSlightly increases processing time but improves kill efficiency." },
                { Language.Korean, "다중 능력 조합을 시뮬레이션하여 확정 킬을 찾습니다.\n처리 시간이 약간 증가하지만 킬 효율이 향상됩니다." },
                { Language.Russian, "Симулирует комбинации способностей для подтверждённых убийств.\nНемного увеличивает время обработки, но повышает эффективность." },
                { Language.Japanese, "複数能力の組み合わせをシミュレートして確実なキルを探します。\n処理時間がわずかに増加しますが、キル効率が向上します。" },
                { Language.Chinese, "模拟多技能组合以寻找确定击杀。\n略微增加处理时间但提高击杀效率。" }
            },

            // ★ v3.3.00: AOE Optimization
            ["UseAoEOptimization"] = new() {
                { Language.English, "Use AOE Optimization" },
                { Language.Korean, "AOE 최적화 사용" },
                { Language.Russian, "Оптимизация AOE" },
                { Language.Japanese, "AOE最適化を使用" },
                { Language.Chinese, "使用AOE优化" }
            },
            ["UseAoEOptimizationDesc"] = new() {
                { Language.English, "Detect enemy clusters for optimal AOE targeting.\nSlightly increases processing time but improves AOE efficiency." },
                { Language.Korean, "적 클러스터를 탐지하여 최적의 AOE 위치를 찾습니다.\n처리 시간이 약간 증가하지만 AOE 효율이 향상됩니다." },
                { Language.Russian, "Обнаруживает скопления врагов для оптимального наведения AOE.\nНемного увеличивает время обработки, но повышает эффективность AOE." },
                { Language.Japanese, "敵の密集を検出してAOEの最適な位置を特定します。\n処理時間がわずかに増加しますが、AOE効率が向上します。" },
                { Language.Chinese, "检测敌方集群以优化AOE瞄准。\n略微增加处理时间但提高AOE效率。" }
            },

            // ★ v3.4.00: Predictive Movement
            ["UsePredictiveMovement"] = new() {
                { Language.English, "Use Predictive Movement" },
                { Language.Korean, "예측적 이동 사용" },
                { Language.Russian, "Предиктивное движение" },
                { Language.Japanese, "予測移動を使用" },
                { Language.Chinese, "使用预测移动" }
            },
            ["UsePredictiveMovementDesc"] = new() {
                { Language.English, "Predict enemy movement to select safer positions.\nConsiders where enemies can move next turn." },
                { Language.Korean, "적 이동을 예측하여 더 안전한 위치를 선택합니다.\n다음 턴에 적이 이동할 수 있는 위치를 고려합니다." },
                { Language.Russian, "Предсказывает движение врагов для выбора более безопасных позиций.\nУчитывает, куда враги могут переместиться в следующий ход." },
                { Language.Japanese, "敵の移動を予測してより安全な位置を選択します。\n次のターンに敵が移動できる位置を考慮します。" },
                { Language.Chinese, "预测敌方移动以选择更安全的位置。\n考虑敌人下回合可能移动到的位置。" }
            },
            // ★ v3.9.72: Weapon Set Rotation
            ["EnableWeaponSetRotation"] = new() {
                { Language.English, "Enable Weapon Set Rotation" },
                { Language.Korean, "무기 세트 로테이션 사용" },
                { Language.Russian, "Ротация комплектов оружия" },
                { Language.Japanese, "武器セットローテーション" },
                { Language.Chinese, "启用武器组轮换" }
            },
            ["EnableWeaponSetRotationDesc"] = new() {
                { Language.English, "Use both weapon sets in a single turn.\nSwitches weapons (0 AP) to use attacks from the alternate set.\n⚠️ This feature is under development and may not work as intended." },
                { Language.Korean, "한 턴에 양쪽 무기 세트를 모두 사용합니다.\n무기 전환(0 AP)으로 대체 세트의 공격을 활용합니다.\n⚠️ 이 기능은 개발 중이며 의도대로 동작하지 않을 수 있습니다." },
                { Language.Russian, "Использовать оба комплекта оружия за один ход.\nПереключает оружие (0 AP) для атак из альтернативного комплекта.\n⚠️ Эта функция находится в разработке и может работать не так, как задумано." },
                { Language.Japanese, "1ターンで両方の武器セットを使用します。\n武器切替(0 AP)で代替セットの攻撃を活用します。\n⚠️ この機能は開発中であり、意図した通りに動作しない場合があります。" },
                { Language.Chinese, "在单回合内使用两套武器组。\n切换武器(0 AP)以使用备用武器组的攻击。\n⚠️ 此功能正在开发中，可能无法按预期工作。" }
            },

            // ★ v3.5.13: Advanced Settings UI
            ["AdvancedSettings"] = new() {
                { Language.English, "Advanced Settings" },
                { Language.Korean, "고급 설정" },
                { Language.Russian, "Расширенные настройки" },
                { Language.Japanese, "詳細設定" },
                { Language.Chinese, "高级设置" }
            },
            ["AdvancedWarning"] = new() {
                { Language.English, "⚠️ Changing these values may negatively affect AI behavior. Use with caution." },
                { Language.Korean, "⚠️ 이 값들을 변경하면 AI 동작에 부정적인 영향을 줄 수 있습니다. 주의하세요." },
                { Language.Russian, "⚠️ Изменение этих значений может негативно повлиять на поведение ИИ. Используйте с осторожностью." },
                { Language.Japanese, "⚠️ これらの値を変更するとAIの動作に悪影響を与える可能性があります。注意して使用してください。" },
                { Language.Chinese, "⚠️ 更改这些值可能会对AI行为产生负面影响。请谨慎使用。" }
            },
            ["ResetToDefault"] = new() {
                { Language.English, "Reset to Default" },
                { Language.Korean, "기본값으로 리셋" },
                { Language.Russian, "Сбросить по умолчанию" },
                { Language.Japanese, "デフォルトにリセット" },
                { Language.Chinese, "重置为默认" }
            },
            ["MinSafeDistance"] = new() {
                { Language.English, "Min Safe Distance" },
                { Language.Korean, "최소 안전 거리" },
                { Language.Russian, "Мин. безопасная дистанция" },
                { Language.Japanese, "最小安全距離" },
                { Language.Chinese, "最小安全距离" }
            },
            ["MinSafeDistanceDesc"] = new() {
                { Language.English, "Minimum distance ranged characters try to keep from enemies (meters)" },
                { Language.Korean, "원거리 캐릭터가 적과 유지하려는 최소 거리 (미터)" },
                { Language.Russian, "Минимальная дистанция, которую дальнобойные персонажи стараются держать от врагов (метры)" },
                { Language.Japanese, "遠距離キャラクターが敵との間に保つ最小距離（メートル）" },
                { Language.Chinese, "远程角色与敌人保持的最小距离（米）" }
            },
            ["HealAtHPPercent"] = new() {
                { Language.English, "Heal at HP%" },
                { Language.Korean, "힐 시작 HP%" },
                { Language.Russian, "Лечить при HP%" },
                { Language.Japanese, "回復開始HP%" },
                { Language.Chinese, "治疗触发HP%" }
            },
            ["HealAtHPPercentDesc"] = new() {
                { Language.English, "Start healing allies when their HP falls below this percentage" },
                { Language.Korean, "아군 HP가 이 퍼센트 이하로 떨어지면 힐 시작" },
                { Language.Russian, "Начать лечение союзников, когда их HP падает ниже этого процента" },
                { Language.Japanese, "味方のHPがこの割合以下になったら回復を開始" },
                { Language.Chinese, "队友HP降至此百分比以下时开始治疗" }
            },
            ["MinEnemiesForAoE"] = new() {
                { Language.English, "Min Enemies for AOE" },
                { Language.Korean, "AOE 최소 적 수" },
                { Language.Russian, "Мин. врагов для AOE" },
                { Language.Japanese, "AOE最小敵数" },
                { Language.Chinese, "AOE最少敌人数" }
            },
            ["MinEnemiesForAoEDesc"] = new() {
                { Language.English, "Minimum number of enemies to use AOE abilities" },
                { Language.Korean, "AOE 능력 사용에 필요한 최소 적 수" },
                { Language.Russian, "Минимальное количество врагов для использования AOE способностей" },
                { Language.Japanese, "AOE能力を使用するために必要な最小敵数" },
                { Language.Chinese, "使用AOE技能所需的最少敌人数量" }
            },

            // ★ v3.5.20: Performance Settings
            ["PerformanceSettings"] = new() {
                { Language.English, "Performance Settings" },
                { Language.Korean, "성능 설정" },
                { Language.Russian, "Настройки производительности" },
                { Language.Japanese, "パフォーマンス設定" },
                { Language.Chinese, "性能设置" }
            },
            ["PerformanceWarning"] = new() {
                { Language.English, "⚠️ Lower values = faster but less accurate AI. Higher values = smarter but slower." },
                { Language.Korean, "⚠️ 낮은 값 = 빠르지만 부정확한 AI. 높은 값 = 똑똑하지만 느림." },
                { Language.Russian, "⚠️ Меньшие значения = быстрее, но менее точный ИИ. Большие = умнее, но медленнее." },
                { Language.Japanese, "⚠️ 低い値 = 速いが不正確なAI。高い値 = 賢いが遅い。" },
                { Language.Chinese, "⚠️ 较低的值 = 更快但AI不太精确。较高的值 = 更智能但更慢。" }
            },
            ["MaxEnemiesToAnalyze"] = new() {
                { Language.English, "Max Enemies to Analyze" },
                { Language.Korean, "최대 분석 적 수" },
                { Language.Russian, "Макс. анализируемых врагов" },
                { Language.Japanese, "最大分析敵数" },
                { Language.Chinese, "最大分析敌人数" }
            },
            ["MaxEnemiesToAnalyzeDesc"] = new() {
                { Language.English, "How many enemies to evaluate when predicting threats.\nMore = accurate threat prediction, but slower.\n(Affects: Movement safety, retreat decisions)" },
                { Language.Korean, "위협 예측 시 분석할 최대 적 수.\n많을수록 위협 예측이 정확하지만 느려집니다.\n(영향: 이동 안전성, 후퇴 결정)" },
                { Language.Russian, "Сколько врагов анализировать при прогнозе угроз.\nБольше = точнее прогноз, но медленнее.\n(Влияет: безопасность движения, решения об отступлении)" },
                { Language.Japanese, "脅威予測時に分析する最大敵数。\n多いほど脅威予測が正確ですが遅くなります。\n（影響: 移動安全性、撤退判断）" },
                { Language.Chinese, "预测威胁时分析的最大敌人数。\n越多=威胁预测越准确，但更慢。\n（影响：移动安全性、撤退决策）" }
            },
            ["MaxPositionsToEvaluate"] = new() {
                { Language.English, "Max Positions to Evaluate" },
                { Language.Korean, "최대 평가 위치 수" },
                { Language.Russian, "Макс. оцениваемых позиций" },
                { Language.Japanese, "最大評価位置数" },
                { Language.Chinese, "最大评估位置数" }
            },
            ["MaxPositionsToEvaluateDesc"] = new() {
                { Language.English, "How many positions to check for optimal AOE placement.\nMore = better AOE targeting, but slower.\n(Affects: AOE ability targeting)" },
                { Language.Korean, "AOE 최적 위치 탐색 시 체크할 최대 위치 수.\n많을수록 AOE 타겟팅이 정확하지만 느려집니다.\n(영향: AOE 능력 타겟팅)" },
                { Language.Russian, "Сколько позиций проверять для оптимального размещения AOE.\nБольше = точнее наведение AOE, но медленнее.\n(Влияет: наведение AOE способностей)" },
                { Language.Japanese, "AOE最適配置のチェック位置数。\n多いほどAOEターゲティングが正確ですが遅くなります。\n（影響: AOE能力のターゲティング）" },
                { Language.Chinese, "检查AOE最佳放置的最大位置数。\n越多=AOE瞄准越好，但更慢。\n（影响：AOE技能瞄准）" }
            },
            ["MaxClusters"] = new() {
                { Language.English, "Max Enemy Clusters" },
                { Language.Korean, "최대 클러스터 수" },
                { Language.Russian, "Макс. скоплений врагов" },
                { Language.Japanese, "最大敵クラスター数" },
                { Language.Chinese, "最大敌方集群数" }
            },
            ["MaxClustersDesc"] = new() {
                { Language.English, "How many enemy groups to track for AOE opportunities.\nMore = finds more AOE chances, but slower.\n(Affects: AOE ability decisions)" },
                { Language.Korean, "AOE 기회 탐색을 위해 추적할 적 그룹 수.\n많을수록 AOE 기회를 더 많이 찾지만 느려집니다.\n(영향: AOE 능력 결정)" },
                { Language.Russian, "Сколько групп врагов отслеживать для AOE возможностей.\nБольше = находит больше шансов для AOE, но медленнее.\n(Влияет: решения по AOE способностям)" },
                { Language.Japanese, "AOE機会のために追跡する敵グループ数。\n多いほどAOE機会を多く発見しますが遅くなります。\n（影響: AOE能力の判断）" },
                { Language.Chinese, "追踪AOE机会的最大敌方群组数。\n越多=发现更多AOE机会，但更慢。\n（影响：AOE技能决策）" }
            },
            ["MaxTilesPerEnemy"] = new() {
                { Language.English, "Max Tiles per Enemy" },
                { Language.Korean, "적당 최대 타일 수" },
                { Language.Russian, "Макс. тайлов на врага" },
                { Language.Japanese, "敵あたり最大タイル数" },
                { Language.Chinese, "每个敌人最大格数" }
            },
            ["MaxTilesPerEnemyDesc"] = new() {
                { Language.English, "Movement tiles to analyze per enemy for threat prediction.\nMore = precise threat zones, but slower.\n(Affects: Predictive movement, safe positioning)" },
                { Language.Korean, "적 위협 예측을 위해 분석할 이동 타일 수.\n많을수록 위협 구역 예측이 정밀하지만 느려집니다.\n(영향: 예측적 이동, 안전 위치 선정)" },
                { Language.Russian, "Тайлы движения для анализа на врага при прогнозе угроз.\nБольше = точнее зоны угроз, но медленнее.\n(Влияет: предиктивное движение, безопасное позиционирование)" },
                { Language.Japanese, "脅威予測のために敵ごとに分析する移動タイル数。\n多いほど脅威ゾーンが精密ですが遅くなります。\n（影響: 予測移動、安全な位置取り）" },
                { Language.Chinese, "每个敌人用于威胁预测的移动格数。\n越多=威胁区域越精确，但更慢。\n（影响：预测移动、安全定位）" }
            },
            ["ResetPerformanceToDefault"] = new() {
                { Language.English, "Reset Performance to Default" },
                { Language.Korean, "성능 설정 기본값으로" },
                { Language.Russian, "Сбросить настройки производительности" },
                { Language.Japanese, "パフォーマンス設定をリセット" },
                { Language.Chinese, "重置性能设置为默认" }
            },

            // ★ v3.8.12: AOE Settings
            ["AoESettings"] = new() {
                { Language.English, "AOE Settings" },
                { Language.Korean, "AOE 설정" },
                { Language.Russian, "Настройки AOE" },
                { Language.Japanese, "AOE設定" },
                { Language.Chinese, "AOE设置" }
            },
            ["AoEWarning"] = new() {
                { Language.English, "⚠️ Controls how AI handles AOE abilities that may hit allies." },
                { Language.Korean, "⚠️ 아군에게 피해를 줄 수 있는 AOE 능력의 AI 처리 방식을 조절합니다." },
                { Language.Russian, "⚠️ Управляет обработкой ИИ AOE способностей, которые могут задеть союзников." },
                { Language.Japanese, "⚠️ 味方に当たる可能性のあるAOE能力のAI処理方法を制御します。" },
                { Language.Chinese, "⚠️ 控制AI如何处理可能命中友方的AOE技能。" }
            },
            ["MaxPlayerAlliesHit"] = new() {
                { Language.English, "Max Allies in AOE" },
                { Language.Korean, "AOE 최대 허용 아군 수" },
                { Language.Russian, "Макс. союзников в AOE" },
                { Language.Japanese, "AOE内最大味方数" },
                { Language.Chinese, "AOE内最大友方数" }
            },
            // ★ v3.8.94: 설명 업데이트 — 모든 AoE 타입 통합, 허용 범위 내 감점 없음
            ["MaxPlayerAlliesHitDesc"] = new() {
                { Language.English, "Maximum number of allies allowed in ALL AOE areas (self-AoE, melee AoE, ranged AoE).\n0 = Never hit allies, 1 = Allow 1 ally, 2 = Allow 2, 3 = Allow 3.\nWithin limit = fully allowed (no penalty)." },
                { Language.Korean, "모든 AOE 범위(자체 AOE, 근접 AOE, 원거리 AOE) 내 허용 최대 아군 수.\n0 = 아군 절대 안 맞춤, 1 = 1명 허용, 2 = 2명 허용, 3 = 3명 허용.\n허용 범위 내 = 감점 없이 완전 허용." },
                { Language.Russian, "Максимальное количество союзников во ВСЕХ зонах AOE (собственная, ближняя, дальняя).\n0 = Никогда не задевать, 1 = Допустимо 1, 2 = Допустимо 2, 3 = Допустимо 3.\nВ пределах лимита = полностью разрешено (без штрафа)." },
                { Language.Japanese, "全AOE範囲（自己AOE、近接AOE、遠距離AOE）内の許容最大味方数。\n0 = 味方に絶対当てない、1 = 1人許容、2 = 2人許容、3 = 3人許容。\n許容範囲内 = ペナルティなしで完全許可。" },
                { Language.Chinese, "所有AOE范围（自身AOE、近战AOE、远程AOE）内允许的最大友方数量。\n0 = 绝不命中友方，1 = 允许1人，2 = 允许2人，3 = 允许3人。\n在限额内 = 完全允许（无惩罚）。" }
            },
            ["ResetAoEToDefault"] = new() {
                { Language.English, "Reset AOE to Default" },
                { Language.Korean, "AOE 설정 기본값으로" },
                { Language.Russian, "Сбросить настройки AOE" },
                { Language.Japanese, "AOE設定をリセット" },
                { Language.Chinese, "重置AOE设置为默认" }
            },

            // ★ v3.16.2: aiconfig.json 전체 설정 UI 노출
            // ═══════════════════════════════════════════════════
            // 상위 그룹: AI 로직 설정
            // ═══════════════════════════════════════════════════
            ["LogicSettings"] = new() {
                { Language.English, "⚠ AI Logic Settings" },
                { Language.Korean, "⚠ AI 로직 설정" },
                { Language.Russian, "⚠ Настройки логики ИИ" },
                { Language.Japanese, "⚠ AIロジック設定" },
                { Language.Chinese, "⚠ AI逻辑设置" }
            },
            ["LogicSettingsWarning"] = new() {
                { Language.English, "⚠️ WARNING: These are internal AI decision parameters.\nChanging values without understanding may cause unpredictable AI behavior.\nUse Reset buttons to restore defaults if issues occur." },
                { Language.Korean, "⚠️ 경고: AI 내부 의사결정 파라미터입니다.\n이해 없이 변경하면 예측할 수 없는 AI 행동이 발생할 수 있습니다.\n문제 발생 시 리셋 버튼으로 기본값을 복원하세요." },
                { Language.Russian, "⚠️ ВНИМАНИЕ: Это внутренние параметры решений ИИ.\nИзменение без понимания может вызвать непредсказуемое поведение ИИ.\nИспользуйте кнопки сброса для восстановления значений по умолчанию." },
                { Language.Japanese, "⚠️ 警告：AI内部の意思決定パラメータです。\n理解なく変更すると予測不能なAI動作が発生する可能性があります。\n問題が発生した場合はリセットボタンでデフォルトに復元してください。" },
                { Language.Chinese, "⚠️ 警告：这些是AI内部决策参数。\n不了解的情况下修改可能导致AI行为不可预测。\n出现问题时请使用重置按钮恢复默认值。" }
            },
            // ★ v3.18.8: 통합 리셋 버튼
            ["ResetAllLogicToDefault"] = new() {
                { Language.English, "Reset ALL Logic Settings to Default" },
                { Language.Korean, "AI 로직 설정 전체 기본값으로" },
                { Language.Russian, "Сбросить ВСЕ настройки логики" },
                { Language.Japanese, "AIロジック設定を全てリセット" },
                { Language.Chinese, "重置所有AI逻辑设置为默认" }
            },
            ["ResetAllLogicConfirm"] = new() {
                { Language.English, "All AI logic settings have been reset to defaults." },
                { Language.Korean, "모든 AI 로직 설정이 기본값으로 초기화되었습니다." },
                { Language.Russian, "Все настройки логики ИИ сброшены." },
                { Language.Japanese, "全てのAIロジック設定がリセットされました。" },
                { Language.Chinese, "所有AI逻辑设置已重置为默认值。" }
            },

            // ═══════════════════════════════════════════════════
            // 전투 임계값 (Threshold Settings)
            // ═══════════════════════════════════════════════════
            ["ThresholdSettings"] = new() {
                { Language.English, "Combat Thresholds" },
                { Language.Korean, "전투 임계값 설정" },
                { Language.Russian, "Боевые пороги" },
                { Language.Japanese, "戦闘閾値設定" },
                { Language.Chinese, "战斗阈值设置" }
            },
            ["ThresholdWarning"] = new() {
                { Language.English, "⚠️ Controls when AI triggers heals, buffs, retreats, and finishers. Changes apply immediately." },
                { Language.Korean, "⚠️ AI가 힐, 버프, 후퇴, 마무리를 언제 실행할지 조절합니다. 변경 즉시 적용." },
                { Language.Russian, "⚠️ Управляет моментами лечения, баффов, отступлений и добиваний ИИ. Изменения применяются немедленно." },
                { Language.Japanese, "⚠️ AIが回復・バフ・撤退・トドメを実行するタイミングを制御します。変更は即座に適用。" },
                { Language.Chinese, "⚠️ 控制AI何时触发治疗、增益、撤退和终结。更改立即生效。" }
            },
            ["ResetThresholdToDefault"] = new() {
                { Language.English, "Reset Thresholds to Default" },
                { Language.Korean, "임계값 기본값으로" },
                { Language.Russian, "Сбросить пороги" },
                { Language.Japanese, "閾値をリセット" },
                { Language.Chinese, "重置阈值为默认" }
            },
            ["EmergencyHealHP"] = new() {
                { Language.English, "Emergency Heal HP%" },
                { Language.Korean, "긴급 힐 HP%" },
                { Language.Russian, "Экстренное лечение HP%" },
                { Language.Japanese, "緊急回復HP%" },
                { Language.Chinese, "紧急治疗HP%" }
            },
            ["EmergencyHealHPDesc"] = new() {
                { Language.English, "Below this HP%, trigger emergency heal first.\nHigher = heal earlier, Lower = prioritize attacks" },
                { Language.Korean, "이 HP% 이하면 긴급 힐 우선 실행.\n높으면 일찍 힐, 낮으면 공격 우선" },
                { Language.Russian, "Ниже этого HP% — экстренное лечение.\nВыше = лечить раньше, Ниже = приоритет атак" },
                { Language.Japanese, "このHP%以下で緊急回復を優先実行。\n高い=早めに回復、低い=攻撃優先" },
                { Language.Chinese, "HP低于此百分比时优先紧急治疗。\n越高=越早治疗，越低=优先攻击" }
            },
            ["HealPriorityHP"] = new() {
                { Language.English, "Heal Priority HP%" },
                { Language.Korean, "힐 우선순위 HP%" },
                { Language.Russian, "Приоритет лечения HP%" },
                { Language.Japanese, "回復優先HP%" },
                { Language.Chinese, "治疗优先HP%" }
            },
            ["HealPriorityHPDesc"] = new() {
                { Language.English, "Prioritize healing allies below this HP%.\nHigher = heal more often, Lower = attack more" },
                { Language.Korean, "이 HP% 이하 아군에게 힐 우선.\n높으면 힐 자주, 낮으면 공격 우선" },
                { Language.Russian, "Приоритет лечения союзников ниже этого HP%.\nВыше = чаще лечить, Ниже = больше атаковать" },
                { Language.Japanese, "このHP%以下の味方を回復優先。\n高い=回復頻度増、低い=攻撃優先" },
                { Language.Chinese, "优先治疗HP低于此百分比的友方。\n越高=治疗更频繁，越低=更多攻击" }
            },
            ["FinisherTargetHP"] = new() {
                { Language.English, "Finisher Target HP%" },
                { Language.Korean, "마무리 대상 HP%" },
                { Language.Russian, "HP% для добивания" },
                { Language.Japanese, "トドメ対象HP%" },
                { Language.Chinese, "终结目标HP%" }
            },
            ["FinisherTargetHPDesc"] = new() {
                { Language.English, "Prioritize finishing enemies below this HP%.\nHigher = more aggressive finishers" },
                { Language.Korean, "적 HP가 이 이하면 마무리 우선.\n높으면 마무리 더 적극적" },
                { Language.Russian, "Добивать врагов ниже этого HP%.\nВыше = агрессивнее добивания" },
                { Language.Japanese, "敵HPがこれ以下ならトドメ優先。\n高い=より積極的なトドメ" },
                { Language.Chinese, "优先终结HP低于此百分比的敌人。\n越高=终结越积极" }
            },
            ["SkipBuffBelowHP"] = new() {
                { Language.English, "Skip Buff Below HP%" },
                { Language.Korean, "버프 스킵 HP%" },
                { Language.Russian, "Пропуск баффов ниже HP%" },
                { Language.Japanese, "バフスキップHP%" },
                { Language.Chinese, "低HP跳过增益%" }
            },
            ["SkipBuffBelowHPDesc"] = new() {
                { Language.English, "Skip buffs when own HP is below this %.\nHigher = less buffing, more survival focus" },
                { Language.Korean, "내 HP가 이 이하면 버프 스킵하고 공격/힐.\n높으면 버프 자제, 생존 우선" },
                { Language.Russian, "Пропускать баффы при HP ниже этого %.\nВыше = меньше баффов, больше выживания" },
                { Language.Japanese, "自分のHPがこの%以下ならバフスキップ。\n高い=バフ控えめ、生存優先" },
                { Language.Chinese, "自身HP低于此百分比时跳过增益。\n越高=增益越少，更注重生存" }
            },
            ["PreAttackBuffMinHP"] = new() {
                { Language.English, "Pre-Attack Buff Min HP%" },
                { Language.Korean, "공격 전 버프 최소 HP%" },
                { Language.Russian, "Мин. HP% для предатакового баффа" },
                { Language.Japanese, "攻撃前バフ最小HP%" },
                { Language.Chinese, "攻击前增益最低HP%" }
            },
            ["PreAttackBuffMinHPDesc"] = new() {
                { Language.English, "Only use pre-attack buffs above this HP%.\nHigher = only buff when safe" },
                { Language.Korean, "이 HP% 이상일 때만 공격 전 버프 사용.\n높으면 안전할 때만 버프" },
                { Language.Russian, "Использовать предатаковые баффы только выше этого HP%.\nВыше = баффы только в безопасности" },
                { Language.Japanese, "このHP%以上でのみ攻撃前バフ使用。\n高い=安全時のみバフ" },
                { Language.Chinese, "仅在HP高于此百分比时使用攻击前增益。\n越高=仅在安全时增益" }
            },
            ["SelfDamageMinHP"] = new() {
                { Language.English, "Self-Damage Min HP%" },
                { Language.Korean, "자해 스킬 최소 HP%" },
                { Language.Russian, "Мин. HP% для самоповреждения" },
                { Language.Japanese, "自傷スキル最小HP%" },
                { Language.Chinese, "自伤技能最低HP%" }
            },
            ["SelfDamageMinHPDesc"] = new() {
                { Language.English, "Min HP% to use self-damaging skills (Blade Dance etc).\nHigher = more cautious" },
                { Language.Korean, "자해 스킬(Blade Dance 등) 사용 최소 HP%.\n높으면 더 신중하게 사용" },
                { Language.Russian, "Мин. HP% для навыков с самоповреждением.\nВыше = осторожнее" },
                { Language.Japanese, "自傷スキル(ブレードダンス等)使用最小HP%。\n高い=より慎重に使用" },
                { Language.Chinese, "使用自伤技能(刃之舞等)的最低HP%。\n越高=越谨慎" }
            },
            ["DesperatePhaseHP"] = new() {
                { Language.English, "Desperate Phase (Team HP%)" },
                { Language.Korean, "절박 모드 (팀 HP%)" },
                { Language.Russian, "Критическая фаза (HP% команды)" },
                { Language.Japanese, "絶望モード(チームHP%)" },
                { Language.Chinese, "危急阶段（团队HP%）" }
            },
            ["DesperatePhaseHPDesc"] = new() {
                { Language.English, "Team avg HP% below this = desperate mode (defense priority).\nHigher = enter defensive mode earlier" },
                { Language.Korean, "팀 평균 HP%가 이 이하면 절박 모드 (방어 우선).\n높으면 방어 모드 일찍 진입" },
                { Language.Russian, "Средний HP% команды ниже этого = критическая фаза (приоритет защиты).\nВыше = раньше перейти в защиту" },
                { Language.Japanese, "チーム平均HP%がこれ以下=絶望モード(防御優先)。\n高い=早めに防御モード移行" },
                { Language.Chinese, "团队平均HP%低于此值=危急模式（防御优先）。\n越高=越早进入防御模式" }
            },
            ["DesperateSelfHP"] = new() {
                { Language.English, "Desperate Phase (Self HP%)" },
                { Language.Korean, "절박 모드 (자신 HP%)" },
                { Language.Russian, "Критическая фаза (свой HP%)" },
                { Language.Japanese, "絶望モード(自分HP%)" },
                { Language.Chinese, "危急阶段（自身HP%）" }
            },
            ["DesperateSelfHPDesc"] = new() {
                { Language.English, "Own HP% below this = desperate mode.\nHigher = play safer earlier" },
                { Language.Korean, "자신 HP%가 이 이하면 절박 모드.\n높으면 더 일찍 방어적으로 행동" },
                { Language.Russian, "Свой HP% ниже этого = критическая фаза.\nВыше = раньше играть осторожнее" },
                { Language.Japanese, "自分HP%がこれ以下=絶望モード。\n高い=早めに防御的行動" },
                { Language.Chinese, "自身HP%低于此值=危急模式。\n越高=越早采取保守行动" }
            },
            ["CfgSafeDistance"] = new() {
                { Language.English, "Safe Distance (tiles)" },
                { Language.Korean, "안전 거리 (타일)" },
                { Language.Russian, "Безопасная дистанция (тайлы)" },
                { Language.Japanese, "安全距離(タイル)" },
                { Language.Chinese, "安全距离（格）" }
            },
            ["CfgSafeDistanceDesc"] = new() {
                { Language.English, "Safe distance for ranged characters (tiles).\nHigher = retreat further from enemies" },
                { Language.Korean, "원거리 캐릭터의 안전 거리 (타일).\n높으면 적에게서 더 멀리 후퇴" },
                { Language.Russian, "Безопасная дистанция для стрелков (тайлы).\nВыше = дальше отступать от врагов" },
                { Language.Japanese, "遠距離キャラの安全距離(タイル)。\n高い=敵からより遠くに撤退" },
                { Language.Chinese, "远程角色的安全距离（格）。\n越高=撤退到更远处" }
            },
            ["DangerDistance"] = new() {
                { Language.English, "Danger Distance (tiles)" },
                { Language.Korean, "위험 거리 (타일)" },
                { Language.Russian, "Опасная дистанция (тайлы)" },
                { Language.Japanese, "危険距離(タイル)" },
                { Language.Chinese, "危险距离（格）" }
            },
            ["DangerDistanceDesc"] = new() {
                { Language.English, "Enemies within this distance = danger.\nHigher = more cautious positioning" },
                { Language.Korean, "이 거리 내 적이 있으면 위험 판정.\n높으면 더 신중하게 위치 선정" },
                { Language.Russian, "Враги в этом радиусе = опасность.\nВыше = осторожнее позиционирование" },
                { Language.Japanese, "この距離内の敵=危険判定。\n高い=より慎重な位置取り" },
                { Language.Chinese, "此距离内的敌人=危险。\n越高=定位越谨慎" }
            },
            ["OneHitKillRatio"] = new() {
                { Language.English, "One-Hit Kill Ratio" },
                { Language.Korean, "1타킬 비율" },
                { Language.Russian, "Коэффициент убийства с одного удара" },
                { Language.Japanese, "一撃キル比率" },
                { Language.Chinese, "一击必杀比率" }
            },
            ["OneHitKillRatioDesc"] = new() {
                { Language.English, "Damage/HP ratio for one-hit kill detection.\nLower = more aggressive kill attempts" },
                { Language.Korean, "데미지/HP 비율이 이 이상이면 1타킬 판정.\n낮으면 더 적극적으로 킬 시도" },
                { Language.Russian, "Соотношение урона/HP для обнаружения одного удара.\nНиже = агрессивнее попытки убийства" },
                { Language.Japanese, "ダメージ/HP比率による一撃キル判定。\n低い=より積極的なキル試行" },
                { Language.Chinese, "伤害/HP比率用于一击必杀检测。\n越低=击杀尝试越积极" }
            },
            ["TwoHitKillRatio"] = new() {
                { Language.English, "Two-Hit Kill Ratio" },
                { Language.Korean, "2타킬 비율" },
                { Language.Russian, "Коэффициент убийства с двух ударов" },
                { Language.Japanese, "二撃キル比率" },
                { Language.Chinese, "两击击杀比率" }
            },
            ["TwoHitKillRatioDesc"] = new() {
                { Language.English, "Damage/HP ratio for two-hit kill detection.\nLower = more aggressive" },
                { Language.Korean, "데미지/HP 비율이 이 이상이면 2타킬 판정.\n낮으면 더 적극적" },
                { Language.Russian, "Соотношение урона/HP для убийства двумя ударами.\nНиже = агрессивнее" },
                { Language.Japanese, "ダメージ/HP比率による二撃キル判定。\n低い=より積極的" },
                { Language.Chinese, "伤害/HP比率用于两击击杀检测。\n越低=越积极" }
            },
            ["CleanupEnemyCount"] = new() {
                { Language.English, "Cleanup Enemy Count" },
                { Language.Korean, "정리 단계 적 수" },
                { Language.Russian, "Кол-во врагов для фазы зачистки" },
                { Language.Japanese, "掃討段階敵数" },
                { Language.Chinese, "扫荡阶段敌人数" }
            },
            ["CleanupEnemyCountDesc"] = new() {
                { Language.English, "When enemies ≤ this = cleanup phase (less buffing, more attacks).\nHigher = enter cleanup earlier" },
                { Language.Korean, "남은 적이 이 이하면 정리 단계 (버프 축소, 공격 집중).\n높으면 일찍 정리 모드 진입" },
                { Language.Russian, "Когда врагов ≤ этого = фаза зачистки (меньше баффов, больше атак).\nВыше = раньше начать зачистку" },
                { Language.Japanese, "残り敵がこれ以下=掃討段階(バフ減、攻撃集中)。\n高い=早めに掃討モード" },
                { Language.Chinese, "敌人数量不超过此值=扫荡阶段（减少增益，更多攻击）。\n越高=越早进入扫荡" }
            },
            ["OpeningPhaseMinAP"] = new() {
                { Language.English, "Opening Phase Min AP" },
                { Language.Korean, "개막 최소 AP" },
                { Language.Russian, "Мин. AP для начальной фазы" },
                { Language.Japanese, "開幕最小AP" },
                { Language.Chinese, "开局阶段最低AP" }
            },
            ["OpeningPhaseMinAPDesc"] = new() {
                { Language.English, "Min AP for opening phase buffs on first turn.\nHigher = need more AP to use opening buffs" },
                { Language.Korean, "전투 첫 턴에 이 AP 이상이면 개막 버프 사용.\n높으면 개막 버프 조건 엄격" },
                { Language.Russian, "Мин. AP для баффов начальной фазы.\nВыше = нужно больше AP для начальных баффов" },
                { Language.Japanese, "開幕バフ使用に必要な最小AP。\n高い=開幕バフ条件が厳しい" },
                { Language.Chinese, "首回合开局增益所需最低AP。\n越高=开局增益条件越严格" }
            },
            ["LowThreatHP"] = new() {
                { Language.English, "Low Threat HP%" },
                { Language.Korean, "약한 적 HP%" },
                { Language.Russian, "HP% низкой угрозы" },
                { Language.Japanese, "低脅威HP%" },
                { Language.Chinese, "低威胁HP%" }
            },
            ["LowThreatHPDesc"] = new() {
                { Language.English, "Enemies below this HP% have reduced threat.\nHigher = ignore more wounded enemies" },
                { Language.Korean, "적 HP가 이 이하면 위협도 감소 (거의 죽은 적).\n높으면 부상 적 무시" },
                { Language.Russian, "Враги ниже этого HP% менее опасны.\nВыше = игнорировать больше раненых" },
                { Language.Japanese, "敵HPがこれ以下なら脅威度低下。\n高い=負傷した敵をより無視" },
                { Language.Chinese, "HP低于此百分比的敌人威胁降低。\n越高=忽略更多受伤敌人" }
            },

            // ═══════════════════════════════════════════════════
            // 위협 평가 (Threat Evaluation)
            // ═══════════════════════════════════════════════════
            ["ThreatSettings"] = new() {
                { Language.English, "Threat Evaluation" },
                { Language.Korean, "위협 평가 가중치" },
                { Language.Russian, "Оценка угроз" },
                { Language.Japanese, "脅威評価" },
                { Language.Chinese, "威胁评估" }
            },
            ["ThreatWarning"] = new() {
                { Language.English, "⚠️ Controls how AI evaluates which enemies are most dangerous. Changes apply immediately." },
                { Language.Korean, "⚠️ AI가 어떤 적이 가장 위험한지 평가하는 방식을 조절합니다. 변경 즉시 적용." },
                { Language.Russian, "⚠️ Управляет оценкой ИИ наиболее опасных врагов. Изменения применяются немедленно." },
                { Language.Japanese, "⚠️ AIがどの敵が最も危険かを評価する方法を制御します。変更は即座に適用。" },
                { Language.Chinese, "⚠️ 控制AI如何评估哪些敌人最危险。更改立即生效。" }
            },
            ["ResetThreatToDefault"] = new() {
                { Language.English, "Reset Threat to Default" },
                { Language.Korean, "위협 평가 기본값으로" },
                { Language.Russian, "Сбросить оценку угроз" },
                { Language.Japanese, "脅威評価をリセット" },
                { Language.Chinese, "重置威胁评估为默认" }
            },
            ["LethalityWeight"] = new() {
                { Language.English, "Lethality Weight" },
                { Language.Korean, "치명도 가중치" },
                { Language.Russian, "Вес летальности" },
                { Language.Japanese, "致死性ウェイト" },
                { Language.Chinese, "致命性权重" }
            },
            ["LethalityWeightDesc"] = new() {
                { Language.English, "Weight for enemy HP-based threat.\nHigher = full HP enemies seen as more threatening" },
                { Language.Korean, "적 HP 기반 위협도 가중치.\n높으면 만피 적이 더 위협적" },
                { Language.Russian, "Вес угрозы по HP врага.\nВыше = враги с полным HP опаснее" },
                { Language.Japanese, "敵HP基準の脅威度ウェイト。\n高い=満HPの敵がより脅威的" },
                { Language.Chinese, "基于敌方HP的威胁权重。\n越高=满血敌人被视为更具威胁" }
            },
            ["ProximityWeight"] = new() {
                { Language.English, "Proximity Weight" },
                { Language.Korean, "근접성 가중치" },
                { Language.Russian, "Вес близости" },
                { Language.Japanese, "近接性ウェイト" },
                { Language.Chinese, "接近度权重" }
            },
            ["ProximityWeightDesc"] = new() {
                { Language.English, "Weight for distance-based threat.\nHigher = closer enemies seen as more threatening" },
                { Language.Korean, "거리 기반 위협도 가중치.\n높으면 가까운 적이 더 위협적" },
                { Language.Russian, "Вес угрозы по расстоянию.\nВыше = ближайшие враги опаснее" },
                { Language.Japanese, "距離基準の脅威度ウェイト。\n高い=近い敵がより脅威的" },
                { Language.Chinese, "基于距离的威胁权重。\n越高=近距离敌人被视为更具威胁" }
            },
            ["HealerRoleBonus"] = new() {
                { Language.English, "Healer Role Bonus" },
                { Language.Korean, "힐러 적 보너스" },
                { Language.Russian, "Бонус за хилера" },
                { Language.Japanese, "ヒーラーボーナス" },
                { Language.Chinese, "治疗者加成" }
            },
            ["HealerRoleBonusDesc"] = new() {
                { Language.English, "Extra threat for enemy healers.\nHigher = prioritize killing enemy healers" },
                { Language.Korean, "힐러 적 추가 위협도.\n높으면 적 힐러 우선 처치" },
                { Language.Russian, "Доп. угроза от вражеских хилеров.\nВыше = приоритет убийства хилеров" },
                { Language.Japanese, "敵ヒーラーの追加脅威度。\n高い=敵ヒーラー優先撃破" },
                { Language.Chinese, "敌方治疗者的额外威胁。\n越高=优先击杀敌方治疗者" }
            },
            ["CasterRoleBonus"] = new() {
                { Language.English, "Caster Role Bonus" },
                { Language.Korean, "캐스터 적 보너스" },
                { Language.Russian, "Бонус за кастера" },
                { Language.Japanese, "キャスターボーナス" },
                { Language.Chinese, "施法者加成" }
            },
            ["CasterRoleBonusDesc"] = new() {
                { Language.English, "Extra threat for enemy casters.\nHigher = prioritize killing enemy casters" },
                { Language.Korean, "캐스터 적 추가 위협도.\n높으면 적 캐스터 우선 처치" },
                { Language.Russian, "Доп. угроза от вражеских кастеров.\nВыше = приоритет убийства кастеров" },
                { Language.Japanese, "敵キャスターの追加脅威度。\n高い=敵キャスター優先撃破" },
                { Language.Chinese, "敌方施法者的额外威胁。\n越高=优先击杀敌方施法者" }
            },
            ["RangedWeaponBonus"] = new() {
                { Language.English, "Ranged Weapon Bonus" },
                { Language.Korean, "원거리 무기 보너스" },
                { Language.Russian, "Бонус за дальнобойное" },
                { Language.Japanese, "遠距離武器ボーナス" },
                { Language.Chinese, "远程武器加成" }
            },
            ["RangedWeaponBonusDesc"] = new() {
                { Language.English, "Extra threat for enemies with ranged weapons.\nHigher = prioritize ranged enemies" },
                { Language.Korean, "원거리 무기 적 추가 위협도.\n높으면 원거리 적 우선 처치" },
                { Language.Russian, "Доп. угроза от врагов с дальнобойным оружием.\nВыше = приоритет дальнобойных" },
                { Language.Japanese, "遠距離武器持ち敵の追加脅威度。\n高い=遠距離敵優先" },
                { Language.Chinese, "持远程武器敌人的额外威胁。\n越高=优先远程敌人" }
            },
            ["ThreatMaxDistance"] = new() {
                { Language.English, "Threat Max Distance (tiles)" },
                { Language.Korean, "위협 최대 거리 (타일)" },
                { Language.Russian, "Макс. дистанция угрозы (тайлы)" },
                { Language.Japanese, "脅威最大距離(タイル)" },
                { Language.Chinese, "威胁最大距离（格）" }
            },
            ["ThreatMaxDistanceDesc"] = new() {
                { Language.English, "Max distance for threat evaluation. Enemies beyond this are ignored.\nHigher = consider more distant enemies" },
                { Language.Korean, "위협 평가 최대 거리. 이 너머의 적은 무시.\n높으면 먼 적도 위협으로 고려" },
                { Language.Russian, "Макс. дистанция для оценки угроз. Дальше = игнорировать.\nВыше = учитывать более далёких врагов" },
                { Language.Japanese, "脅威評価の最大距離。これ以上の敵は無視。\n高い=遠い敵も脅威として考慮" },
                { Language.Chinese, "威胁评估的最大距离。超出此距离的敌人被忽略。\n越高=考虑更远处的敌人" }
            },

            // ═══════════════════════════════════════════════════
            // AoE 세부 설정 (확장)
            // ═══════════════════════════════════════════════════
            ["EnemyHitScore"] = new() {
                { Language.English, "Enemy Hit Score" },
                { Language.Korean, "적 타격 기본 점수" },
                { Language.Russian, "Очки за попадание по врагу" },
                { Language.Japanese, "敵命中基本スコア" },
                { Language.Chinese, "敌方命中基础分" }
            },
            ["EnemyHitScoreDesc"] = new() {
                { Language.English, "Base score per enemy hit by AoE.\nHigher = AI uses AoE more aggressively" },
                { Language.Korean, "AoE 적 1명당 기본 점수.\n높으면 AoE 더 적극적 사용" },
                { Language.Russian, "Базовые очки за каждого поражённого врага.\nВыше = ИИ агрессивнее использует AOE" },
                { Language.Japanese, "AoEで敵1体あたりの基本スコア。\n高い=AoEをより積極的に使用" },
                { Language.Chinese, "AoE命中每个敌人的基础分。\n越高=AI更积极使用AoE" }
            },
            ["PlayerAllyPenaltyMult"] = new() {
                { Language.English, "Ally Penalty Multiplier" },
                { Language.Korean, "아군 피격 페널티 배수" },
                { Language.Russian, "Множитель штрафа за союзников" },
                { Language.Japanese, "味方被弾ペナルティ倍率" },
                { Language.Chinese, "友方惩罚倍率" }
            },
            ["PlayerAllyPenaltyMultDesc"] = new() {
                { Language.English, "Penalty multiplier when AoE hits allies.\nHigher = avoid hitting allies more" },
                { Language.Korean, "AoE가 아군을 맞출 때 페널티 배수.\n높으면 아군 피격 더 기피" },
                { Language.Russian, "Множитель штрафа при попадании AOE по союзникам.\nВыше = больше избегать попаданий" },
                { Language.Japanese, "AoEが味方に当たる時のペナルティ倍率。\n高い=味方被弾をより回避" },
                { Language.Chinese, "AoE命中友方时的惩罚倍率。\n越高=更避免命中友方" }
            },
            ["NpcAllyPenaltyMult"] = new() {
                { Language.English, "NPC Ally Penalty Multiplier" },
                { Language.Korean, "NPC 아군 페널티 배수" },
                { Language.Russian, "Множитель штрафа за NPC" },
                { Language.Japanese, "NPC味方ペナルティ倍率" },
                { Language.Chinese, "NPC友方惩罚倍率" }
            },
            ["NpcAllyPenaltyMultDesc"] = new() {
                { Language.English, "Penalty multiplier when AoE hits NPC allies.\nHigher = protect NPCs more" },
                { Language.Korean, "AoE가 NPC 아군을 맞출 때 페널티 배수.\n높으면 NPC 아군 보호 강화" },
                { Language.Russian, "Множитель штрафа при попадании по NPC союзникам.\nВыше = больше защищать NPC" },
                { Language.Japanese, "AoEがNPC味方に当たる時のペナルティ倍率。\n高い=NPC味方保護強化" },
                { Language.Chinese, "AoE命中NPC友方时的惩罚倍率。\n越高=更保护NPC" }
            },
            ["CasterSelfPenaltyMult"] = new() {
                { Language.English, "Caster Self Penalty" },
                { Language.Korean, "캐스터 자기 피격 배수" },
                { Language.Russian, "Штраф за самопопадание" },
                { Language.Japanese, "キャスター自己被弾倍率" },
                { Language.Chinese, "施法者自伤惩罚" }
            },
            ["CasterSelfPenaltyMultDesc"] = new() {
                { Language.English, "Penalty multiplier when caster hits self with AoE.\nHigher = avoid self-damage more" },
                { Language.Korean, "캐스터가 자기 AoE에 맞을 때 페널티 배수.\n높으면 자기 피격 더 기피" },
                { Language.Russian, "Множитель штрафа при самопопадании AOE.\nВыше = больше избегать самоповреждения" },
                { Language.Japanese, "キャスターが自身のAoEに当たる時のペナルティ倍率。\n高い=自傷をより回避" },
                { Language.Chinese, "施法者被自身AoE命中时的惩罚倍率。\n越高=更避免自伤" }
            },
            ["CfgMinClusterSize"] = new() {
                { Language.English, "Min Cluster Size" },
                { Language.Korean, "클러스터 최소 크기" },
                { Language.Russian, "Мин. размер скопления" },
                { Language.Japanese, "最小クラスターサイズ" },
                { Language.Chinese, "最小集群大小" }
            },
            ["CfgMinClusterSizeDesc"] = new() {
                { Language.English, "Min enemies in a group for AoE targeting.\n1 = single enemies are valid AoE targets" },
                { Language.Korean, "AoE 타겟팅 유효 클러스터 최소 적 수.\n1이면 단일 적도 AoE 대상" },
                { Language.Russian, "Мин. врагов в группе для наведения AOE.\n1 = даже одиночные враги — цели AOE" },
                { Language.Japanese, "AoEターゲティング有効クラスター最小敵数。\n1=単体敵もAoE対象" },
                { Language.Chinese, "AoE瞄准有效集群的最少敌人数。\n1=单个敌人也是有效AoE目标" }
            },
            ["ClusterNpcAllyPenalty"] = new() {
                { Language.English, "Cluster NPC Ally Penalty" },
                { Language.Korean, "클러스터 NPC 감점" },
                { Language.Russian, "Штраф за NPC в скоплении" },
                { Language.Japanese, "クラスターNPCペナルティ" },
                { Language.Chinese, "集群NPC友方惩罚" }
            },
            ["ClusterNpcAllyPenaltyDesc"] = new() {
                { Language.English, "Score penalty for NPC allies in AoE cluster.\nHigher = protect NPCs in clusters more" },
                { Language.Korean, "클러스터 내 NPC 아군 감점.\n높으면 NPC 보호 강화" },
                { Language.Russian, "Штрафные очки за NPC союзников в скоплении.\nВыше = больше защищать NPC" },
                { Language.Japanese, "AoEクラスター内NPC味方の減点。\n高い=NPC保護強化" },
                { Language.Chinese, "AoE集群内NPC友方的扣分。\n越高=更保护集群中的NPC" }
            },

            // ═══════════════════════════════════════════════════
            // 스코어링 가중치 (Scoring Weights)
            // ═══════════════════════════════════════════════════
            ["ScoringSettings"] = new() {
                { Language.English, "Scoring Weights" },
                { Language.Korean, "스코어링 가중치" },
                { Language.Russian, "Весовые коэффициенты" },
                { Language.Japanese, "スコアリングウェイト" },
                { Language.Chinese, "评分权重" }
            },
            ["ScoringWarning"] = new() {
                { Language.English, "⚠️ Fine-tune AI decision scoring. Higher values increase that factor's importance." },
                { Language.Korean, "⚠️ AI 의사결정 점수를 세밀하게 조절합니다. 높은 값 = 해당 요소의 중요도 증가." },
                { Language.Russian, "⚠️ Тонкая настройка оценки решений ИИ. Выше = больше важность этого фактора." },
                { Language.Japanese, "⚠️ AI判断スコアの微調整。高い値=その要素の重要度増加。" },
                { Language.Chinese, "⚠️ 微调AI决策评分。值越高=该因素的重要性越大。" }
            },
            ["ResetScoringToDefault"] = new() {
                { Language.English, "Reset Scoring to Default" },
                { Language.Korean, "스코어링 기본값으로" },
                { Language.Russian, "Сбросить коэффициенты" },
                { Language.Japanese, "スコアリングをリセット" },
                { Language.Chinese, "重置评分为默认" }
            },
            ["ScoringGroup_BuffMult"] = new() {
                { Language.English, "— Buff Multipliers —" },
                { Language.Korean, "— 버프 배율 —" },
                { Language.Russian, "— Множители баффов —" },
                { Language.Japanese, "— バフ倍率 —" },
                { Language.Chinese, "— 增益倍率 —" }
            },
            ["OpeningPhaseBuffMult"] = new() {
                { Language.English, "Opening Phase Buff Mult" },
                { Language.Korean, "개막 버프 배율" },
                { Language.Russian, "Множитель начальных баффов" },
                { Language.Japanese, "開幕バフ倍率" },
                { Language.Chinese, "开局增益倍率" }
            },
            ["OpeningPhaseBuffMultDesc"] = new() {
                { Language.English, "Buff score multiplier in opening phase.\nHigher = buff more on first turns" },
                { Language.Korean, "개막 단계 버프 점수 배율.\n높으면 첫 턴 버프 적극적" },
                { Language.Russian, "Множитель баффов в начальной фазе.\nВыше = больше баффов в первых ходах" },
                { Language.Japanese, "開幕段階のバフスコア倍率。\n高い=最初のターンでバフ積極的" },
                { Language.Chinese, "开局阶段增益评分倍率。\n越高=首回合增益越积极" }
            },
            ["CleanupPhaseBuffMult"] = new() {
                { Language.English, "Cleanup Phase Buff Mult" },
                { Language.Korean, "정리 단계 버프 배율" },
                { Language.Russian, "Множитель баффов в фазе зачистки" },
                { Language.Japanese, "掃討段階バフ倍率" },
                { Language.Chinese, "扫荡阶段增益倍率" }
            },
            ["CleanupPhaseBuffMultDesc"] = new() {
                { Language.English, "Buff score multiplier in cleanup phase.\nHigher = still buff during cleanup" },
                { Language.Korean, "정리 단계 버프 점수 배율.\n높으면 정리 시에도 버프 사용" },
                { Language.Russian, "Множитель баффов в фазе зачистки.\nВыше = баффы даже при зачистке" },
                { Language.Japanese, "掃討段階のバフスコア倍率。\n高い=掃討中もバフ使用" },
                { Language.Chinese, "扫荡阶段增益评分倍率。\n越高=扫荡期间仍使用增益" }
            },
            ["DesperateNonDefMult"] = new() {
                { Language.English, "Desperate Non-Defense Mult" },
                { Language.Korean, "위기시 비방어 배율" },
                { Language.Russian, "Множитель незащитных в кризисе" },
                { Language.Japanese, "危機時非防御倍率" },
                { Language.Chinese, "危急非防御倍率" }
            },
            ["DesperateNonDefMultDesc"] = new() {
                { Language.English, "Non-defensive buff multiplier in desperate phase.\nHigher = still use offensive buffs in crisis" },
                { Language.Korean, "위기시 비방어 버프 배율.\n높으면 위기에도 공격 버프 사용" },
                { Language.Russian, "Множитель незащитных баффов в кризисе.\nВыше = наступательные баффы даже в кризисе" },
                { Language.Japanese, "危機時の非防御バフ倍率。\n高い=危機でも攻撃バフ使用" },
                { Language.Chinese, "危急阶段非防御增益倍率。\n越高=危急时仍使用攻击增益" }
            },
            ["ScoringGroup_Timing"] = new() {
                { Language.English, "— Timing Bonuses —" },
                { Language.Korean, "— 타이밍 보너스 —" },
                { Language.Russian, "— Бонусы за тайминг —" },
                { Language.Japanese, "— タイミングボーナス —" },
                { Language.Chinese, "— 时机加成 —" }
            },
            ["PreCombatOpeningBonus"] = new() {
                { Language.English, "Pre-Combat Opening Bonus" },
                { Language.Korean, "선제 버프 개막 보너스" },
                { Language.Russian, "Бонус начальных пребоевых баффов" },
                { Language.Japanese, "戦前バフ開幕ボーナス" },
                { Language.Chinese, "战前开局加成" }
            },
            ["PreCombatOpeningBonusDesc"] = new() {
                { Language.English, "Bonus score for pre-combat buffs at battle start" },
                { Language.Korean, "전투 시작 시 선제 버프 보너스 점수" },
                { Language.Russian, "Бонусные очки за пребоевые баффы в начале боя" },
                { Language.Japanese, "戦闘開始時の戦前バフボーナススコア" },
                { Language.Chinese, "战斗开始时战前增益的加成分" }
            },
            ["PreCombatCleanupPenalty"] = new() {
                { Language.English, "Pre-Combat Cleanup Penalty" },
                { Language.Korean, "선제 버프 정리 감점" },
                { Language.Russian, "Штраф пребоевых в фазе зачистки" },
                { Language.Japanese, "戦前バフ掃討ペナルティ" },
                { Language.Chinese, "战前增益扫荡惩罚" }
            },
            ["PreCombatCleanupPenaltyDesc"] = new() {
                { Language.English, "Penalty for pre-combat buffs during cleanup" },
                { Language.Korean, "정리 단계에서 선제 버프 감점" },
                { Language.Russian, "Штраф за пребоевые баффы в фазе зачистки" },
                { Language.Japanese, "掃討段階での戦前バフペナルティ" },
                { Language.Chinese, "扫荡阶段使用战前增益的惩罚" }
            },
            ["PreAttackHittableBonus"] = new() {
                { Language.English, "Pre-Attack Hittable Bonus" },
                { Language.Korean, "공격전 버프 적 보너스" },
                { Language.Russian, "Бонус за атакуемых" },
                { Language.Japanese, "攻撃前バフ敵ボーナス" },
                { Language.Chinese, "攻击前有敌加成" }
            },
            ["PreAttackHittableBonusDesc"] = new() {
                { Language.English, "Bonus for pre-attack buffs when enemies are in range" },
                { Language.Korean, "적이 사거리 내일 때 공격 전 버프 보너스" },
                { Language.Russian, "Бонус за предатаковые баффы при врагах в зоне досягаемости" },
                { Language.Japanese, "敵が射程内にいる時の攻撃前バフボーナス" },
                { Language.Chinese, "敌人在射程内时攻击前增益的加成" }
            },
            ["PreAttackNoEnemyPenalty"] = new() {
                { Language.English, "Pre-Attack No Enemy Penalty" },
                { Language.Korean, "적 부재 버프 감점" },
                { Language.Russian, "Штраф без врагов" },
                { Language.Japanese, "敵不在バフペナルティ" },
                { Language.Chinese, "攻击前无敌惩罚" }
            },
            ["PreAttackNoEnemyPenaltyDesc"] = new() {
                { Language.English, "Penalty for pre-attack buffs with no enemies in range" },
                { Language.Korean, "적 부재 시 공격 전 버프 감점" },
                { Language.Russian, "Штраф за предатаковые баффы без врагов" },
                { Language.Japanese, "敵不在時の攻撃前バフペナルティ" },
                { Language.Chinese, "射程内无敌人时攻击前增益的惩罚" }
            },
            ["EmergencyDesperateBonus"] = new() {
                { Language.English, "Emergency Desperate Bonus" },
                { Language.Korean, "긴급 위기 보너스" },
                { Language.Russian, "Бонус экстренных в кризисе" },
                { Language.Japanese, "緊急危機ボーナス" },
                { Language.Chinese, "紧急危急加成" }
            },
            ["EmergencyDesperateBonusDesc"] = new() {
                { Language.English, "Bonus for emergency buffs in desperate situations" },
                { Language.Korean, "위기 상황에서 긴급 버프 보너스" },
                { Language.Russian, "Бонус за экстренные баффы в кризисе" },
                { Language.Japanese, "危機状況での緊急バフボーナス" },
                { Language.Chinese, "危急情况下紧急增益的加成" }
            },
            ["EmergencyNonDesperatePenalty"] = new() {
                { Language.English, "Emergency Non-Desperate Penalty" },
                { Language.Korean, "비위기 긴급 감점" },
                { Language.Russian, "Штраф экстренных без кризиса" },
                { Language.Japanese, "非危機緊急ペナルティ" },
                { Language.Chinese, "非危急紧急惩罚" }
            },
            ["EmergencyNonDesperatePenaltyDesc"] = new() {
                { Language.English, "Penalty for emergency buffs in non-desperate situations" },
                { Language.Korean, "비위기 시 긴급 버프 감점" },
                { Language.Russian, "Штраф за экстренные баффы без кризиса" },
                { Language.Japanese, "非危機時の緊急バフペナルティ" },
                { Language.Chinese, "非危急情况下使用紧急增益的惩罚" }
            },
            ["TauntNearEnemiesBonus"] = new() {
                { Language.English, "Taunt Near Enemies Bonus" },
                { Language.Korean, "도발 근접 적 보너스" },
                { Language.Russian, "Бонус провокации рядом с врагами" },
                { Language.Japanese, "挑発近接敵ボーナス" },
                { Language.Chinese, "嘲讽近距敌人加成" }
            },
            ["TauntNearEnemiesBonusDesc"] = new() {
                { Language.English, "Bonus for taunts with many nearby enemies" },
                { Language.Korean, "도발 시 근접 적 다수 보너스" },
                { Language.Russian, "Бонус за провокацию при множестве ближних врагов" },
                { Language.Japanese, "挑発時に近くの敵が多い場合のボーナス" },
                { Language.Chinese, "周围敌人多时嘲讽的加成" }
            },
            ["TauntFewEnemiesPenalty"] = new() {
                { Language.English, "Taunt Few Enemies Penalty" },
                { Language.Korean, "도발 적 부족 감점" },
                { Language.Russian, "Штраф провокации при малом числе врагов" },
                { Language.Japanese, "挑発敵不足ペナルティ" },
                { Language.Chinese, "嘲讽少敌惩罚" }
            },
            ["TauntFewEnemiesPenaltyDesc"] = new() {
                { Language.English, "Penalty for taunts with few nearby enemies" },
                { Language.Korean, "도발 시 적 부족 감점" },
                { Language.Russian, "Штраф за провокацию при малом числе врагов" },
                { Language.Japanese, "挑発時に近くの敵が少ない場合のペナルティ" },
                { Language.Chinese, "周围敌人少时嘲讽的惩罚" }
            },
            ["ScoringGroup_Synergy"] = new() {
                { Language.English, "— Synergy Bonuses —" },
                { Language.Korean, "— 시너지 보너스 —" },
                { Language.Russian, "— Бонусы синергии —" },
                { Language.Japanese, "— シナジーボーナス —" },
                { Language.Chinese, "— 协同加成 —" }
            },
            ["BuffAttackSynergy"] = new() {
                { Language.English, "Buff + Attack Synergy" },
                { Language.Korean, "버프+공격 시너지" },
                { Language.Russian, "Синергия бафф+атака" },
                { Language.Japanese, "バフ+攻撃シナジー" },
                { Language.Chinese, "增益+攻击协同" }
            },
            ["BuffAttackSynergyDesc"] = new() {
                { Language.English, "Bonus when attack buff + attack are planned together" },
                { Language.Korean, "공격 버프 + 공격 조합 보너스" },
                { Language.Russian, "Бонус за комбинацию бафф атаки + атака" },
                { Language.Japanese, "攻撃バフ+攻撃の組み合わせボーナス" },
                { Language.Chinese, "攻击增益+攻击组合时的加成" }
            },
            ["MoveAttackSynergy"] = new() {
                { Language.English, "Move + Attack Synergy" },
                { Language.Korean, "이동+공격 시너지" },
                { Language.Russian, "Синергия движение+атака" },
                { Language.Japanese, "移動+攻撃シナジー" },
                { Language.Chinese, "移动+攻击协同" }
            },
            ["MoveAttackSynergyDesc"] = new() {
                { Language.English, "Bonus for move + attack combos (gap closers)" },
                { Language.Korean, "이동 + 공격 조합 보너스 (갭클로저)" },
                { Language.Russian, "Бонус за комбо движение+атака (сближение)" },
                { Language.Japanese, "移動+攻撃コンボボーナス(ギャップクローザー)" },
                { Language.Chinese, "移动+攻击连招的加成（突进）" }
            },
            ["MultiAttackPerAttack"] = new() {
                { Language.English, "Multi-Attack Bonus" },
                { Language.Korean, "연속 공격 보너스" },
                { Language.Russian, "Бонус мультиатаки" },
                { Language.Japanese, "連続攻撃ボーナス" },
                { Language.Chinese, "连续攻击加成" }
            },
            ["MultiAttackPerAttackDesc"] = new() {
                { Language.English, "Bonus per additional attack in a turn" },
                { Language.Korean, "공격당 추가 점수 (연속 공격)" },
                { Language.Russian, "Бонус за каждую дополнительную атаку за ход" },
                { Language.Japanese, "ターン内追加攻撃ごとのボーナス" },
                { Language.Chinese, "回合内每次额外攻击的加成" }
            },
            ["DefenseRetreatSynergy"] = new() {
                { Language.English, "Defense + Retreat Synergy" },
                { Language.Korean, "방어+후퇴 시너지" },
                { Language.Russian, "Синергия защита+отступление" },
                { Language.Japanese, "防御+撤退シナジー" },
                { Language.Chinese, "防御+撤退协同" }
            },
            ["DefenseRetreatSynergyDesc"] = new() {
                { Language.English, "Bonus for defense buff + retreat combo" },
                { Language.Korean, "방어 버프 + 후퇴 조합 보너스" },
                { Language.Russian, "Бонус за комбо защитный бафф+отступление" },
                { Language.Japanese, "防御バフ+撤退コンボボーナス" },
                { Language.Chinese, "防御增益+撤退组合的加成" }
            },
            ["KillConfirmSynergy"] = new() {
                { Language.English, "Kill Confirm Synergy" },
                { Language.Korean, "킬 확정 시너지" },
                { Language.Russian, "Синергия подтверждённого убийства" },
                { Language.Japanese, "キル確定シナジー" },
                { Language.Chinese, "确认击杀协同" }
            },
            ["KillConfirmSynergyDesc"] = new() {
                { Language.English, "Bonus when planned damage ≥ target HP (confirmed kill)" },
                { Language.Korean, "킬 확정 시 보너스 (데미지 ≥ HP)" },
                { Language.Russian, "Бонус при планируемом уроне ≥ HP цели (подтверждённое убийство)" },
                { Language.Japanese, "計画ダメージ≧対象HP時のボーナス(確定キル)" },
                { Language.Chinese, "计划伤害≥目标HP时的加成（确认击杀）" }
            },
            ["AlmostKillSynergy"] = new() {
                { Language.English, "Almost Kill Synergy" },
                { Language.Korean, "거의 킬 시너지" },
                { Language.Russian, "Синергия почти убийства" },
                { Language.Japanese, "ほぼキルシナジー" },
                { Language.Chinese, "接近击杀协同" }
            },
            ["AlmostKillSynergyDesc"] = new() {
                { Language.English, "Bonus when planned damage ≥ 90% of target HP" },
                { Language.Korean, "거의 킬 시 보너스 (데미지 ≥ 90% HP)" },
                { Language.Russian, "Бонус при планируемом уроне ≥ 90% HP цели" },
                { Language.Japanese, "計画ダメージ≧対象HP90%時のボーナス" },
                { Language.Chinese, "计划伤害≥目标HP的90%时的加成" }
            },
            ["ScoringGroup_Other"] = new() {
                { Language.English, "— Other Scoring —" },
                { Language.Korean, "— 기타 점수 —" },
                { Language.Russian, "— Прочие очки —" },
                { Language.Japanese, "— その他スコア —" },
                { Language.Chinese, "— 其他评分 —" }
            },
            ["ClearMPDangerBase"] = new() {
                { Language.English, "ClearMP Danger Penalty" },
                { Language.Korean, "MP소모+위험 감점" },
                { Language.Russian, "Штраф за расход MP в опасности" },
                { Language.Japanese, "MP消費+危険ペナルティ" },
                { Language.Chinese, "清空MP危险惩罚" }
            },
            ["ClearMPDangerBaseDesc"] = new() {
                { Language.English, "Penalty for using MP-clearing abilities in danger" },
                { Language.Korean, "위험 상황에서 MP 소모 스킬 사용 시 기본 감점" },
                { Language.Russian, "Штраф за использование навыков с расходом MP в опасности" },
                { Language.Japanese, "危険状況でMP消費スキル使用時の基本ペナルティ" },
                { Language.Chinese, "危险情况下使用清空MP技能的惩罚" }
            },
            ["AoEBonusPerEnemy"] = new() {
                { Language.English, "AoE Bonus Per Enemy" },
                { Language.Korean, "AoE 적당 보너스" },
                { Language.Russian, "Бонус AOE за врага" },
                { Language.Japanese, "AoE敵あたりボーナス" },
                { Language.Chinese, "AoE每敌加成" }
            },
            ["AoEBonusPerEnemyDesc"] = new() {
                { Language.English, "Score bonus per additional enemy in AoE" },
                { Language.Korean, "AoE에 추가 적 1명당 보너스" },
                { Language.Russian, "Бонусные очки за каждого дополнительного врага в AOE" },
                { Language.Japanese, "AoE内追加敵1体あたりのボーナス" },
                { Language.Chinese, "AoE中每个额外敌人的加成分" }
            },
            ["InertiaBonus"] = new() {
                { Language.English, "Target Inertia Bonus" },
                { Language.Korean, "타겟 관성 보너스" },
                { Language.Russian, "Бонус инерции цели" },
                { Language.Japanese, "ターゲット慣性ボーナス" },
                { Language.Chinese, "目标惯性加成" }
            },
            ["InertiaBonusDesc"] = new() {
                { Language.English, "Bonus for attacking same target as last turn.\nHigher = focus fire on one target" },
                { Language.Korean, "이전 턴 동일 타겟 공격 보너스.\n높으면 한 타겟 집중 공격" },
                { Language.Russian, "Бонус за атаку той же цели, что и в прошлый ход.\nВыше = фокус огня на одной цели" },
                { Language.Japanese, "前ターンと同じ対象を攻撃する際のボーナス。\n高い=1体に集中攻撃" },
                { Language.Chinese, "攻击与上回合相同目标的加成。\n越高=集中火力于单一目标" }
            },
            ["HardCCExploitBonus"] = new() {
                { Language.English, "Hard CC Exploit Bonus" },
                { Language.Korean, "CC 활용 보너스" },
                { Language.Russian, "Бонус за использование CC" },
                { Language.Japanese, "ハードCC活用ボーナス" },
                { Language.Chinese, "硬控利用加成" }
            },
            ["HardCCExploitBonusDesc"] = new() {
                { Language.English, "Bonus for attacking stunned/immobilized enemies" },
                { Language.Korean, "기절/고정된 적 공격 보너스" },
                { Language.Russian, "Бонус за атаку оглушённых/обездвиженных врагов" },
                { Language.Japanese, "気絶/固定された敵攻撃ボーナス" },
                { Language.Chinese, "攻击眩晕/定身敌人的加成" }
            },
            ["DOTFollowUpBonus"] = new() {
                { Language.English, "DoT Follow-Up Bonus" },
                { Language.Korean, "DoT 후속 보너스" },
                { Language.Russian, "Бонус за продолжение по DoT" },
                { Language.Japanese, "DoT追撃ボーナス" },
                { Language.Chinese, "DoT追击加成" }
            },
            ["DOTFollowUpBonusDesc"] = new() {
                { Language.English, "Bonus for attacking enemies with active DoTs" },
                { Language.Korean, "DoT(출혈/독/화상) 걸린 적 후속 공격 보너스" },
                { Language.Russian, "Бонус за атаку врагов с активным DoT" },
                { Language.Japanese, "DoT(出血/毒/火傷)が付いた敵への追撃ボーナス" },
                { Language.Chinese, "攻击身上有DoT（流血/中毒/灼烧）的敌人的加成" }
            },

            // ═══════════════════════════════════════════════════
            // 역할별 타겟 가중치 (Role Target Weights)
            // ═══════════════════════════════════════════════════
            ["RoleWeightSettings"] = new() {
                { Language.English, "Role Target Weights" },
                { Language.Korean, "역할별 타겟 가중치" },
                { Language.Russian, "Весовые коэффициенты ролей" },
                { Language.Japanese, "役割別ターゲットウェイト" },
                { Language.Chinese, "职责目标权重" }
            },
            ["RoleWeightWarning"] = new() {
                { Language.English, "⚠️ Controls how each role selects attack targets. Changes apply immediately." },
                { Language.Korean, "⚠️ 각 역할이 공격 대상을 선택하는 방식을 조절합니다. 변경 즉시 적용." },
                { Language.Russian, "⚠️ Управляет выбором целей для атаки каждой роли. Изменения применяются немедленно." },
                { Language.Japanese, "⚠️ 各役割の攻撃対象選択方法を制御します。変更は即座に適用。" },
                { Language.Chinese, "⚠️ 控制每个职责如何选择攻击目标。更改立即生效。" }
            },
            ["ResetRoleWeightToDefault"] = new() {
                { Language.English, "Reset Role Weights to Default" },
                { Language.Korean, "역할 가중치 기본값으로" },
                { Language.Russian, "Сбросить весовые коэффициенты ролей" },
                { Language.Japanese, "役割ウェイトをリセット" },
                { Language.Chinese, "重置职责权重为默认" }
            },
            ["RW_HPPercent"] = new() {
                { Language.English, "Low HP Priority" },
                { Language.Korean, "낮은 HP 우선" },
                { Language.Russian, "Приоритет низкого HP" },
                { Language.Japanese, "低HP優先" },
                { Language.Chinese, "低HP优先" }
            },
            ["RW_HPPercentDesc"] = new() {
                { Language.English, "Weight for targeting low HP enemies.\nHigher = focus on wounded enemies" },
                { Language.Korean, "낮은 HP 적 우선 가중치.\n높으면 빈사 적 집중" },
                { Language.Russian, "Вес для приоритета целей с низким HP.\nВыше = фокус на раненых" },
                { Language.Japanese, "低HP敵の優先ウェイト。\n高い=負傷した敵に集中" },
                { Language.Chinese, "瞄准低HP敌人的权重。\n越高=集中攻击受伤敌人" }
            },
            ["RW_Threat"] = new() {
                { Language.English, "Threat Priority" },
                { Language.Korean, "위협도 우선" },
                { Language.Russian, "Приоритет угрозы" },
                { Language.Japanese, "脅威度優先" },
                { Language.Chinese, "威胁优先" }
            },
            ["RW_ThreatDesc"] = new() {
                { Language.English, "Weight for targeting threatening enemies.\nHigher = focus on dangerous enemies" },
                { Language.Korean, "위협적인 적 우선 가중치.\n높으면 위험한 적 먼저" },
                { Language.Russian, "Вес для приоритета угрожающих целей.\nВыше = фокус на опасных" },
                { Language.Japanese, "脅威的な敵の優先ウェイト。\n高い=危険な敵を優先" },
                { Language.Chinese, "瞄准高威胁敌人的权重。\n越高=集中攻击危险敌人" }
            },
            ["RW_Distance"] = new() {
                { Language.English, "Distance Priority" },
                { Language.Korean, "거리 우선" },
                { Language.Russian, "Приоритет расстояния" },
                { Language.Japanese, "距離優先" },
                { Language.Chinese, "距离优先" }
            },
            ["RW_DistanceDesc"] = new() {
                { Language.English, "Weight for targeting closer enemies.\nHigher = attack nearest enemies first" },
                { Language.Korean, "가까운 적 우선 가중치.\n높으면 가까운 적 집중" },
                { Language.Russian, "Вес для приоритета ближайших целей.\nВыше = атаковать ближайших первыми" },
                { Language.Japanese, "近い敵の優先ウェイト。\n高い=近い敵を優先攻撃" },
                { Language.Chinese, "瞄准近距离敌人的权重。\n越高=优先攻击最近的敌人" }
            },
            ["RW_FinisherBonus"] = new() {
                { Language.English, "Finisher Bonus" },
                { Language.Korean, "마무리 보너스" },
                { Language.Russian, "Бонус добивания" },
                { Language.Japanese, "トドメボーナス" },
                { Language.Chinese, "终结加成" }
            },
            ["RW_FinisherBonusDesc"] = new() {
                { Language.English, "Multiplier for finishable targets.\nHigher = prioritize finishing off enemies" },
                { Language.Korean, "마무리 가능 적 보너스 배수.\n높으면 마무리 적극적" },
                { Language.Russian, "Множитель для добиваемых целей.\nВыше = приоритет добивания" },
                { Language.Japanese, "トドメ可能な対象のボーナス倍率。\n高い=トドメを積極的に" },
                { Language.Chinese, "可终结目标的加成倍率。\n越高=优先终结敌人" }
            },
            ["RW_OneHitKillBonus"] = new() {
                { Language.English, "One-Hit Kill Bonus" },
                { Language.Korean, "1타킬 보너스" },
                { Language.Russian, "Бонус убийства одним ударом" },
                { Language.Japanese, "一撃キルボーナス" },
                { Language.Chinese, "一击必杀加成" }
            },
            ["RW_OneHitKillBonusDesc"] = new() {
                { Language.English, "Multiplier for one-hit-killable targets.\nHigher = prioritize easy kills" },
                { Language.Korean, "1타킬 가능 적 보너스 배수.\n높으면 쉬운 킬 우선" },
                { Language.Russian, "Множитель для целей, убиваемых одним ударом.\nВыше = приоритет лёгких убийств" },
                { Language.Japanese, "一撃キル可能な対象のボーナス倍率。\n高い=簡単なキルを優先" },
                { Language.Chinese, "可一击必杀目标的加成倍率。\n越高=优先轻松击杀" }
            },

            // ═══════════════════════════════════════════════════
            // 무기 로테이션 (Weapon Rotation)
            // ═══════════════════════════════════════════════════
            ["WeaponRotationSettings"] = new() {
                { Language.English, "Weapon Rotation Settings" },
                { Language.Korean, "무기 로테이션 설정" },
                { Language.Russian, "Настройки ротации оружия" },
                { Language.Japanese, "武器ローテーション設定" },
                { Language.Chinese, "武器轮换设置" }
            },
            ["WeaponRotationWarning"] = new() {
                { Language.English, "⚠️ This feature is under development and may not work as intended.\nControls weapon set switching behavior during combat." },
                { Language.Korean, "⚠️ 이 기능은 개발 중이며 의도대로 동작하지 않을 수 있습니다.\n전투 중 무기 세트 전환 동작을 조절합니다." },
                { Language.Russian, "⚠️ Эта функция находится в разработке и может работать не так, как задумано.\nУправляет переключением комплектов оружия в бою." },
                { Language.Japanese, "⚠️ この機能は開発中であり、意図した通りに動作しない場合があります。\n戦闘中の武器セット切り替え動作を制御します。" },
                { Language.Chinese, "⚠️ 此功能正在开发中，可能无法按预期工作。\n控制战斗中武器组切换行为。" }
            },
            ["ResetWeaponRotationToDefault"] = new() {
                { Language.English, "Reset Weapon Rotation to Default" },
                { Language.Korean, "무기 로테이션 기본값으로" },
                { Language.Russian, "Сбросить ротацию оружия" },
                { Language.Japanese, "武器ローテーションをリセット" },
                { Language.Chinese, "重置武器轮换为默认" }
            },
            ["MaxSwitchesPerTurn"] = new() {
                { Language.English, "Max Switches Per Turn" },
                { Language.Korean, "턴당 최대 전환 횟수" },
                { Language.Russian, "Макс. переключений за ход" },
                { Language.Japanese, "ターンあたり最大切り替え回数" },
                { Language.Chinese, "每回合最大切换次数" }
            },
            ["MaxSwitchesPerTurnDesc"] = new() {
                { Language.English, "Max weapon set switches per turn.\nHigher = more weapon variety per turn" },
                { Language.Korean, "턴당 최대 무기 전환 횟수.\n높으면 한 턴에 더 다양한 무기 사용" },
                { Language.Russian, "Макс. переключений оружия за ход.\nВыше = больше разнообразия оружия" },
                { Language.Japanese, "ターンあたりの最大武器切り替え回数。\n高い=1ターンでより多様な武器使用" },
                { Language.Chinese, "每回合最大武器组切换次数。\n越高=每回合使用更多样的武器" }
            },
            ["MinEnemiesForAlternateAoE"] = new() {
                { Language.English, "Min Enemies for Alt. AoE" },
                { Language.Korean, "대체 AoE 최소 적 수" },
                { Language.Russian, "Мин. врагов для альт. AOE" },
                { Language.Japanese, "代替AoE最小敵数" },
                { Language.Chinese, "备用AoE最少敌人数" }
            },
            ["MinEnemiesForAlternateAoEDesc"] = new() {
                { Language.English, "Min enemies to switch to alternate weapon set for AoE.\nLower = switch more often for AoE" },
                { Language.Korean, "대체 무기 세트 AoE 사용 최소 적 수.\n낮으면 AoE 위해 더 자주 전환" },
                { Language.Russian, "Мин. врагов для переключения на альт. комплект для AOE.\nНиже = чаще переключаться" },
                { Language.Japanese, "AoEのために代替武器セットに切り替える最小敵数。\n低い=AoEのためにより頻繁に切り替え" },
                { Language.Chinese, "切换至备用武器组使用AoE的最少敌人数。\n越低=为AoE更频繁切换" }
            },

            // ── Tab labels ───────────────────────────────────────
            ["TabParty"] = new() {
                { Language.English, "Party" }, { Language.Korean, "파티" },
                { Language.Russian, "Отряд" }, { Language.Japanese, "パーティ" },
                { Language.Chinese, "队伍" }
            },
            ["TabGameplay"] = new() {
                { Language.English, "Gameplay" }, { Language.Korean, "게임플레이" },
                { Language.Russian, "Геймплей" }, { Language.Japanese, "ゲームプレイ" },
                { Language.Chinese, "游戏性" }
            },
            ["TabCombat"] = new() {
                { Language.English, "Combat" }, { Language.Korean, "전투" },
                { Language.Russian, "Бой" }, { Language.Japanese, "戦闘" },
                { Language.Chinese, "战斗" }
            },
            ["TabPerformance"] = new() {
                { Language.English, "Performance" }, { Language.Korean, "성능" },
                { Language.Russian, "Производительность" }, { Language.Japanese, "パフォーマンス" },
                { Language.Chinese, "性能" }
            },
            ["TabLanguage"] = new() {
                { Language.English, "Language" }, { Language.Korean, "언어" },
                { Language.Russian, "Язык" }, { Language.Japanese, "言語" },
                { Language.Chinese, "语言" }
            },
            ["TabDebug"] = new() {
                { Language.English, "Debug" }, { Language.Korean, "디버그" },
                { Language.Russian, "Отладка" }, { Language.Japanese, "デバッグ" },
                { Language.Chinese, "调试" }
            },
            ["TabMachineSpirit"] = new() {
                { Language.English, "Machine Spirit" }, { Language.Korean, "머신 스피릿" },
                { Language.Russian, "Дух Машины" }, { Language.Japanese, "マシンスピリット" },
                { Language.Chinese, "机魂" }
            },

            // ── Machine Spirit settings ───────────────────────────
            ["MSProvider"] = new() {
                { Language.English, "Provider" }, { Language.Korean, "공급자" },
                { Language.Russian, "Провайдер" }, { Language.Japanese, "プロバイダー" },
                { Language.Chinese, "提供商" }
            },
            ["MSAutoSetup"] = new() {
                { Language.English, "Auto Setup" }, { Language.Korean, "자동 설정" },
                { Language.Russian, "Авто-настройка" }, { Language.Japanese, "自動セットアップ" },
                { Language.Chinese, "自动设置" }
            },
            ["MSGuide_Ollama"] = new() {
                { Language.English, "Runs AI locally on your PC — free, unlimited, no internet needed after setup.\n1. Install Ollama from ollama.com  2. Click [Auto Setup] below\n★ Recommended: Gemma 3 4B QAT (~3GB VRAM). For more quality: 12B (~8GB) or 27B (~18GB).\nAll Gemma 3 models support 128K context and multilingual chat." },
                { Language.Korean, "PC에서 로컬로 AI 실행 — 무료, 무제한, 설치 후 인터넷 불필요.\n1. ollama.com에서 Ollama 설치  2. 아래 [자동 설정] 클릭\n★ 추천: Gemma 3 4B QAT (~3GB VRAM). 더 높은 품질: 12B (~8GB) 또는 27B (~18GB).\n모든 Gemma 3 모델은 128K 컨텍스트와 다국어 채팅을 지원합니다." },
                { Language.Russian, "Запускает ИИ локально — бесплатно, без лимитов, интернет не нужен.\n1. Установите Ollama с ollama.com  2. Нажмите [Авто-настройка]\n★ Рекомендуется: Gemma 3 4B QAT (~3ГБ VRAM). Качественнее: 12B (~8ГБ) или 27B (~18ГБ).\nВсе модели Gemma 3: 128К контекст, мультиязычный чат." },
                { Language.Japanese, "PCでAIをローカル実行 — 無料・無制限、セットアップ後はネット不要。\n1. ollama.comからインストール  2. [自動セットアップ]をクリック\n★ 推奨: Gemma 3 4B QAT (~3GB VRAM)。高品質: 12B (~8GB)または27B (~18GB)。\n全Gemma 3モデル: 128Kコンテキスト、多言語チャット対応。" },
                { Language.Chinese, "在本地PC上运行AI——免费、无限制、设置后无需联网。\n1. 从ollama.com安装Ollama  2. 点击下方[自动设置]\n★ 推荐：Gemma 3 4B QAT (~3GB显存)。更高品质：12B (~8GB)或27B (~18GB)。\n所有Gemma 3模型支持128K上下文和多语言聊天。" }
            },
            ["MSGuide_Groq"] = new() {
                { Language.English, "Fast cloud AI — free tier available, no GPU needed. Recommended for most users.\nSign up at console.groq.com, create an API key, and paste it below." },
                { Language.Korean, "빠른 클라우드 AI — 무료 사용 가능, GPU 불필요. 대부분의 사용자에게 추천.\nconsole.groq.com에서 가입 후 API 키를 발급받아 아래에 붙여넣으세요." },
                { Language.Russian, "Быстрый облачный ИИ — бесплатный тариф, GPU не нужен. Лучший выбор.\nЗарегистрируйтесь на console.groq.com, создайте API-ключ и вставьте ниже." },
                { Language.Japanese, "高速クラウドAI — 無料枠あり、GPU不要。ほとんどのユーザーにおすすめ。\nconsole.groq.comで登録しAPIキーを作成して下に貼り付け。" },
                { Language.Chinese, "快速云AI——有免费额度，无需GPU。推荐大多数用户使用。\n在console.groq.com注册，创建API密钥，粘贴到下方。" }
            },
            ["MSGuide_OpenAI"] = new() {
                { Language.English, "OpenAI GPT models — highest quality, paid. Requires OpenAI account with billing.\nGet your API key at platform.openai.com/api-keys" },
                { Language.Korean, "OpenAI GPT 모델 — 최고 품질, 유료. 결제 등록된 OpenAI 계정 필요.\nplatform.openai.com/api-keys에서 API 키 발급." },
                { Language.Russian, "Модели GPT от OpenAI — высшее качество, платно. Нужен аккаунт с оплатой.\nAPI-ключ: platform.openai.com/api-keys" },
                { Language.Japanese, "OpenAI GPTモデル — 最高品質、有料。課金済みOpenAIアカウント必要。\nAPIキー: platform.openai.com/api-keys" },
                { Language.Chinese, "OpenAI GPT模型——最高品质，付费。需要有计费的OpenAI账户。\n在platform.openai.com/api-keys获取API密钥。" }
            },
            ["MSGuide_Gemini"] = new() {
                { Language.English, "Google Gemini — free, fast cloud AI. Just need a Google account.\nGet API key at aistudio.google.com, no billing required." },
                { Language.Korean, "Google Gemini — 무료, 빠른 클라우드 AI. 구글 계정만 있으면 됩니다.\naistudio.google.com에서 API 키 발급 (결제 불필요)." },
                { Language.Russian, "Google Gemini — бесплатный, быстрый облачный ИИ. Нужен только аккаунт Google.\nAPI-ключ на aistudio.google.com, оплата не требуется." },
                { Language.Japanese, "Google Gemini — 無料、高速クラウドAI。Googleアカウントだけで利用可能。\naistudio.google.comでAPIキー取得（課金不要）。" },
                { Language.Chinese, "Google Gemini——免费、快速的云AI。只需要Google账户。\n在aistudio.google.com获取API密钥，无需计费。" }
            },
            ["MSGuide_Custom"] = new() {
                { Language.English, "For advanced users. Connect to any OpenAI-compatible API (LM Studio, text-generation-webui, etc)." },
                { Language.Korean, "고급 사용자용. OpenAI 호환 API에 연결 (LM Studio, text-generation-webui 등)." },
                { Language.Russian, "Для продвинутых. Любой OpenAI-совместимый API (LM Studio, text-generation-webui и т.д.)." },
                { Language.Japanese, "上級者向け。OpenAI互換APIに接続（LM Studio、text-generation-webui等）。" },
                { Language.Chinese, "面向高级用户。连接任何OpenAI兼容API（LM Studio、text-generation-webui等）。" }
            },
            ["MSSteps_Groq"] = new() {
                { Language.English, "Step 1: console.groq.com > Step 2: Sign up (Google OK) > Step 3: API Keys > Create > Step 4: Paste below" },
                { Language.Korean, "1: console.groq.com 접속 > 2: 가입 (구글 가능) > 3: API Keys > Create > 4: 아래 붙여넣기" },
                { Language.Russian, "1: console.groq.com > 2: Регистрация (Google) > 3: API Keys > Create > 4: Вставить ниже" },
                { Language.Japanese, "1: console.groq.com > 2: 登録(Google可) > 3: API Keys > Create > 4: 下に貼付" },
                { Language.Chinese, "步骤1: console.groq.com > 步骤2: 注册（可用Google） > 步骤3: API Keys > Create > 步骤4: 粘贴到下方" }
            },
            ["MSSteps_Gemini"] = new() {
                { Language.English, "Step 1: aistudio.google.com > Step 2: Sign in with Google > Step 3: Get API Key > Step 4: Paste below" },
                { Language.Korean, "1: aistudio.google.com 접속 > 2: 구글 로그인 > 3: API 키 발급 > 4: 아래 붙여넣기" },
                { Language.Russian, "1: aistudio.google.com > 2: Войти через Google > 3: Получить API-ключ > 4: Вставить ниже" },
                { Language.Japanese, "1: aistudio.google.com > 2: Googleログイン > 3: APIキー取得 > 4: 下に貼付" },
                { Language.Chinese, "步骤1: aistudio.google.com > 步骤2: 用Google登录 > 步骤3: 获取API密钥 > 步骤4: 粘贴到下方" }
            },
            ["MSSteps_OpenAI"] = new() {
                { Language.English, "Step 1: platform.openai.com > Step 2: Sign up + billing > Step 3: API Keys > Create > Step 4: Paste below" },
                { Language.Korean, "1: platform.openai.com 접속 > 2: 가입 + 결제등록 > 3: API Keys > Create > 4: 아래 붙여넣기" },
                { Language.Russian, "1: platform.openai.com > 2: Регистрация + оплата > 3: API Keys > Create > 4: Вставить ниже" },
                { Language.Japanese, "1: platform.openai.com > 2: 登録+課金 > 3: API Keys > Create > 4: 下に貼付" },
                { Language.Chinese, "步骤1: platform.openai.com > 步骤2: 注册+设置计费 > 步骤3: API Keys > Create > 步骤4: 粘贴到下方" }
            },
            ["MSModelHint"] = new() {
                { Language.English, "Enter any model ID supported by your provider." },
                { Language.Korean, "사용 중인 공급자가 지원하는 모델 ID를 입력하세요." },
                { Language.Russian, "Введите ID модели, поддерживаемой вашим провайдером." },
                { Language.Japanese, "プロバイダーがサポートするモデルIDを入力。" },
                { Language.Chinese, "输入您的提供商支持的任意模型ID。" }
            },

            // ── Ollama models (Gemma 3 recommended per latest research) ──
            ["MSModel_gemma3_4b"] = new() {
                { Language.English, "★ RECOMMENDED — Google Gemma 3 4B QAT. Best quality/performance ratio. ~3GB VRAM. Multilingual, 128K context." },
                { Language.Korean, "★ 추천 — Google Gemma 3 4B QAT. 최고의 성능/품질 비율. ~3GB VRAM. 다국어, 128K 컨텍스트." },
                { Language.Russian, "★ РЕКОМЕНДУЕТСЯ — Google Gemma 3 4B QAT. Лучшее соотношение качества. ~3ГБ VRAM. Мультиязычная, 128К." },
                { Language.Japanese, "★ 推奨 — Google Gemma 3 4B QAT。最高の性能/品質比。~3GB VRAM。多言語、128Kコンテキスト。" },
                { Language.Chinese, "★ 推荐 — Google Gemma 3 4B QAT。最佳性价比。~3GB显存。多语言，128K上下文。" }
            },
            ["MSModel_gemma3_12b"] = new() {
                { Language.English, "Gemma 3 12B — Deep reasoning, excellent roleplay. ~8GB VRAM. For mid-range GPUs (RTX 3060+)." },
                { Language.Korean, "Gemma 3 12B — 깊은 추론, 뛰어난 롤플레이. ~8GB VRAM. 중급 GPU (RTX 3060+)." },
                { Language.Russian, "Gemma 3 12B — Глубокое рассуждение. ~8ГБ VRAM. Для средних GPU (RTX 3060+)." },
                { Language.Japanese, "Gemma 3 12B — 深い推論、優れたRP。~8GB VRAM。中級GPU（RTX 3060+）。" },
                { Language.Chinese, "Gemma 3 12B — 深度推理，出色角色扮演。~8GB显存。适合中端GPU（RTX 3060+）。" }
            },
            ["MSModel_gemma3_27b"] = new() {
                { Language.English, "Gemma 3 27B — Maximum quality. ~18GB VRAM. For high-end GPUs only (RTX 3090/4090)." },
                { Language.Korean, "Gemma 3 27B — 최고 품질. ~18GB VRAM. 고급 GPU 전용 (RTX 3090/4090)." },
                { Language.Russian, "Gemma 3 27B — Максимальное качество. ~18ГБ VRAM. Только для мощных GPU (RTX 3090/4090)." },
                { Language.Japanese, "Gemma 3 27B — 最高品質。~18GB VRAM。ハイエンドGPU専用（RTX 3090/4090）。" },
                { Language.Chinese, "Gemma 3 27B — 最高品质。~18GB显存。仅限高端GPU（RTX 3090/4090）。" }
            },
            ["MSModel_qwen25"] = new() {
                { Language.English, "Multilingual (Korean, Japanese, Chinese, English). 4.7GB. Good alternative." },
                { Language.Korean, "다국어 지원 (한국어, 일본어, 중국어, 영어). 4.7GB. 좋은 대안." },
                { Language.Russian, "Мультиязычная (рус., англ., кит., яп., кор.). 4.7ГБ. Хорошая альтернатива." },
                { Language.Japanese, "多言語対応（日本語、韓国語、中国語、英語）。4.7GB。良い代替。" },
                { Language.Chinese, "多语言支持（中文、韩语、日语、英语）。4.7GB。优秀备选。" }
            },
            ["MSModel_llama32"] = new() {
                { Language.English, "Fast & lightweight (2GB). English only. Best for low-end GPUs." },
                { Language.Korean, "빠르고 가벼움 (2GB). 영어 전용. 저사양 GPU에 적합." },
                { Language.Russian, "Быстрая и лёгкая (2ГБ). Только английский. Для слабых GPU." },
                { Language.Japanese, "高速・軽量（2GB）。英語のみ。低スペGPU向け。" },
                { Language.Chinese, "快速轻量（2GB）。仅英语。最适合低端GPU。" }
            },

            // ── Gemini models ──
            ["MSModel_gemini25flash"] = new() {
                { Language.English, "Fast & smart. Best balance of speed and quality. Recommended." },
                { Language.Korean, "빠르고 똑똑함. 속도와 품질의 최적 균형. 추천!" },
                { Language.Russian, "Быстрая и умная. Лучший баланс скорости и качества. Рекомендуется." },
                { Language.Japanese, "高速かつ賢い。速度と品質の最適バランス。推奨！" },
                { Language.Chinese, "快速且智能。速度与质量的最佳平衡。推荐！" }
            },
            ["MSModel_gemini25lite"] = new() {
                { Language.English, "Fastest responses. Slightly less smart. Higher daily limit (1,000/day)." },
                { Language.Korean, "가장 빠른 응답. 약간 덜 똑똑함. 일일 한도 높음 (1,000회/일)." },
                { Language.Russian, "Самые быстрые ответы. Чуть менее умная. Лимит выше (1000/день)." },
                { Language.Japanese, "最速応答。やや精度低。日次制限多め（1,000回/日）。" },
                { Language.Chinese, "最快响应。智能稍低。每日限额更高（1,000次/天）。" }
            },
            ["MSModel_gemini25pro"] = new() {
                { Language.English, "Smartest Gemini model. Slower, lower daily limit (100/day). For quality over speed." },
                { Language.Korean, "가장 똑똑한 Gemini. 느리고 일일 한도 낮음 (100회/일). 품질 우선 시." },
                { Language.Russian, "Самая умная Gemini. Медленнее, лимит ниже (100/день). Для качества." },
                { Language.Japanese, "最も賢いGemini。低速、日次制限少(100回/日)。品質重視向け。" },
                { Language.Chinese, "最智能的Gemini模型。较慢，每日限额较低（100次/天）。品质优先。" }
            },

            // ── Groq models ──
            ["MSModel_llama33"] = new() {
                { Language.English, "Large 70B model. High quality, multilingual. 1,000 requests/day free." },
                { Language.Korean, "대형 70B 모델. 고품질, 다국어. 무료 1,000회/일." },
                { Language.Russian, "Большая 70B модель. Высокое качество, мультиязычная. 1000 запросов/день." },
                { Language.Japanese, "大型70Bモデル。高品質、多言語。無料1,000回/日。" },
                { Language.Chinese, "大型70B模型。高质量，多语言。免费1,000次/天。" }
            },
            ["MSModel_llama4scout"] = new() {
                { Language.English, "Latest Llama 4. Fast, good quality. 1,000 requests/day free." },
                { Language.Korean, "최신 Llama 4. 빠르고 좋은 품질. 무료 1,000회/일." },
                { Language.Russian, "Новейшая Llama 4. Быстрая, хорошее качество. 1000 запросов/день." },
                { Language.Japanese, "最新Llama 4。高速、高品質。無料1,000回/日。" },
                { Language.Chinese, "最新Llama 4。快速，质量好。免费1,000次/天。" }
            },
            ["MSModel_qwen3"] = new() {
                { Language.English, "Alibaba 32B model. Excellent multilingual (Korean, Japanese, Chinese)." },
                { Language.Korean, "Alibaba 32B 모델. 뛰어난 다국어 (한국어, 일본어, 중국어). 추천!" },
                { Language.Russian, "Модель Alibaba 32B. Отличная мультиязычная (кор., яп., кит.)." },
                { Language.Japanese, "Alibaba 32Bモデル。優れた多言語（韓国語、日本語、中国語）。推奨！" },
                { Language.Chinese, "Alibaba 32B模型。出色的多语言支持（中文、韩语、日语）。推荐！" }
            },

            // ── OpenAI models ──
            ["MSModel_gpt4omini"] = new() {
                { Language.English, "Cheapest OpenAI model. Good quality for the price. ~$0.15/M tokens." },
                { Language.Korean, "가장 저렴한 OpenAI. 가격 대비 좋은 품질. ~$0.15/M 토큰." },
                { Language.Russian, "Самая дешёвая OpenAI. Хорошее качество за цену. ~$0.15/M токенов." },
                { Language.Japanese, "最安のOpenAI。コスパ良好。~$0.15/Mトークン。" },
                { Language.Chinese, "最便宜的OpenAI模型。性价比好。~$0.15/M tokens。" }
            },
            ["MSModel_gpt4o"] = new() {
                { Language.English, "Full GPT-4o. Best quality, higher cost. ~$2.50/M tokens." },
                { Language.Korean, "풀 GPT-4o. 최고 품질, 더 비쌈. ~$2.50/M 토큰." },
                { Language.Russian, "Полная GPT-4o. Лучшее качество, дороже. ~$2.50/M токенов." },
                { Language.Japanese, "フルGPT-4o。最高品質、高コスト。~$2.50/Mトークン。" },
                { Language.Chinese, "完整GPT-4o。最高品质，费用更高。~$2.50/M tokens。" }
            },
            ["MSAdvanced"] = new() {
                { Language.English, "Advanced Settings" }, { Language.Korean, "고급 설정" },
                { Language.Russian, "Расширенные настройки" }, { Language.Japanese, "詳細設定" },
                { Language.Chinese, "高级设置" }
            },
            ["MSAdvancedHint"] = new() {
                { Language.English, "Leave at defaults unless you want to fine-tune the AI's responses." },
                { Language.Korean, "AI 응답을 세밀하게 조정하려는 게 아니면 기본값 그대로 두세요." },
                { Language.Russian, "Оставьте по умолчанию, если не хотите настраивать ответы ИИ." },
                { Language.Japanese, "AIの応答を微調整する場合以外はデフォルトで。" },
                { Language.Chinese, "除非想微调AI响应，否则保持默认。" }
            },
            ["MSEnabled"] = new() {
                { Language.English, "Enable Machine Spirit" }, { Language.Korean, "머신 스피릿 활성화" },
                { Language.Russian, "Включить Дух Машины" }, { Language.Japanese, "マシンスピリットを有効化" },
                { Language.Chinese, "启用机魂" }
            },
            ["MSApiUrl"] = new() {
                { Language.English, "API URL" }, { Language.Korean, "API URL" },
                { Language.Russian, "URL API" }, { Language.Japanese, "API URL" },
                { Language.Chinese, "API URL" }
            },
            ["MSApiKey"] = new() {
                { Language.English, "API Key" }, { Language.Korean, "API 키" },
                { Language.Russian, "Ключ API" }, { Language.Japanese, "APIキー" },
                { Language.Chinese, "API密钥" }
            },
            ["MSModel"] = new() {
                { Language.English, "Model" }, { Language.Korean, "모델" },
                { Language.Russian, "Модель" }, { Language.Japanese, "モデル" },
                { Language.Chinese, "模型" }
            },
            ["MSMaxTokens"] = new() {
                { Language.English, "Max Tokens" }, { Language.Korean, "최대 토큰" },
                { Language.Russian, "Макс. токенов" }, { Language.Japanese, "最大トークン" },
                { Language.Chinese, "最大Token数" }
            },
            ["MSTemperature"] = new() {
                { Language.English, "Temperature" }, { Language.Korean, "온도" },
                { Language.Russian, "Температура" }, { Language.Japanese, "温度" },
                { Language.Chinese, "温度" }
            },
            ["MSHotkey"] = new() {
                { Language.English, "Chat Hotkey" }, { Language.Korean, "채팅 핫키" },
                { Language.Russian, "Горячая клавиша" }, { Language.Japanese, "チャットホットキー" },
                { Language.Chinese, "聊天快捷键" }
            },
            ["MSTestConnection"] = new() {
                { Language.English, "Test Connection" }, { Language.Korean, "연결 테스트" },
                { Language.Russian, "Тест соединения" }, { Language.Japanese, "接続テスト" },
                { Language.Chinese, "测试连接" }
            },
            ["MSScanModels"] = new() {
                { Language.English, "Scan Installed Models" }, { Language.Korean, "설치된 모델 검색" },
                { Language.Russian, "Сканировать модели" }, { Language.Japanese, "インストール済みモデル検索" },
                { Language.Chinese, "扫描已安装模型" }
            },
            ["MSInstalledModels"] = new() {
                { Language.English, "Installed" }, { Language.Korean, "설치됨" },
                { Language.Russian, "Установлены" }, { Language.Japanese, "インストール済み" },
                { Language.Chinese, "已安装" }
            },
            ["MSDescription"] = new() {
                { Language.English, "Machine Spirit — AI voidship companion powered by local Ollama + Gemma 3. Free, unlimited, runs on your GPU." },
                { Language.Korean, "머신 스피릿 — 로컬 Ollama + Gemma 3 기반 AI 보이드쉽 동반자. 무료, 무제한, GPU에서 실행." },
                { Language.Russian, "Дух Машины — ИИ-компаньон корабля на Ollama + Gemma 3. Бесплатно, без лимитов, на вашем GPU." },
                { Language.Japanese, "マシンスピリット — ローカルOllama + Gemma 3搭載AIコンパニオン。無料・無制限、GPU実行。" },
                { Language.Chinese, "机魂——由本地Ollama + Gemma 3驱动的AI虚空舰伴侣。免费、无限制，在您的GPU上运行。" }
            },

            ["GameplaySettings"] = new() {
                { Language.English, "Gameplay Settings" }, { Language.Korean, "게임플레이 설정" },
                { Language.Russian, "Настройки геймплея" }, { Language.Japanese, "ゲームプレイ設定" },
                { Language.Chinese, "游戏性设置" }
            },
            ["CombatSettings"] = new() {
                { Language.English, "Combat Settings" }, { Language.Korean, "전투 설정" },
                { Language.Russian, "Настройки боя" }, { Language.Japanese, "戦闘設定" },
                { Language.Chinese, "战斗设置" }
            },
            ["UIScale"] = new() {
                { Language.English, "UI Scale" }, { Language.Korean, "UI 크기" },
                { Language.Russian, "Масштаб UI" }, { Language.Japanese, "UIスケール" },
                { Language.Chinese, "UI缩放" }
            },

            // Machine Spirit v3.60.0 — Personality
            ["MSPersonality"] = new() {
                { Language.English, "Personality" }, { Language.Korean, "성격" },
                { Language.Russian, "Личность" }, { Language.Japanese, "パーソナリティ" },
                { Language.Chinese, "性格" }
            },
            ["MSPersonality_Mechanicus"] = new() {
                { Language.English, "Tech-priest devotion — sacred algorithms and binary cant" },
                { Language.Korean, "기술 사제의 헌신 — 신성한 알고리즘과 이진 성가" },
                { Language.Russian, "Преданность Механикус — священные алгоритмы и двоичные гимны" },
                { Language.Japanese, "テック・プリーストの献身 — 聖なるアルゴリズムと二進法の詠唱" },
                { Language.Chinese, "技术祭司的虔诚——神圣算法与二进制圣歌" }
            },
            ["MSPersonality_Heretic"] = new() {
                { Language.English, "Outwardly warm and helpful — subtly nudges toward darker, more 'practical' choices" },
                { Language.Korean, "겉으로는 다정하고 친절 — 은근히 더 어둡고 '실용적인' 선택을 유도" },
                { Language.Russian, "Внешне тёплый и заботливый — исподволь подталкивает к более тёмным, 'практичным' решениям" },
                { Language.Japanese, "表面は温かく親切 — さりげなく暗い、より'実用的'な選択へ誘導する" },
                { Language.Chinese, "表面温暖友善 — 巧妙地引导向更黑暗、更'务实'的选择" }
            },
            ["MSPersonality_Feral"] = new() {
                { Language.English, "Primitive beast-mind — aggressive, loyal, hilariously dumb but lovable" },
                { Language.Korean, "원시 야수 의식 — 과격하고 충성스럽고 바보같지만 사랑스러운" },
                { Language.Russian, "Первобытный зверь — агрессивный, верный, комично глупый, но обаятельный" },
                { Language.Japanese, "原始獣意識 — 過激で忠実で、おバカだけど愛おしい" },
                { Language.Chinese, "原始兽性意识 — 粗暴、忠诚、笨得可爱" }
            },
            ["MSPersonality_Magickal"] = new() {
                { Language.English, "Dark Age girl AI — bubbly, cheerful, casually mentions galaxy-ending superweapons" },
                { Language.Korean, "기술 암흑기 소녀 AI — 발랄하고 유쾌하며 은하계 멸망급 무기를 아무렇지 않게 언급" },
                { Language.Russian, "ИИ Тёмной Эры — игривая, весёлая, мимоходом упоминает оружие для уничтожения галактик" },
                { Language.Japanese, "技術暗黒時代の少女AI — 元気で明るく、銀河滅亡級の超兵器をさらっと語る" },
                { Language.Chinese, "技术暗黑时代少女AI — 活泼开朗，轻描淡写地提及灭星级超级武器" }
            },

            // Machine Spirit v3.60.0 — Idle Commentary
            ["MSIdleMode"] = new() {
                { Language.English, "Idle Commentary" }, { Language.Korean, "아이들 수다" },
                { Language.Russian, "Фоновые комментарии" }, { Language.Japanese, "アイドルコメント" },
                { Language.Chinese, "闲时评论" }
            },
            ["MSIdleDesc"] = new() {
                { Language.English, "Machine Spirit speaks on its own during exploration. Off = silent, High = frequent." },
                { Language.Korean, "탐색 중 머신 스피릿이 자율적으로 발화합니다. Off = 침묵, High = 빈번." },
                { Language.Russian, "Дух Машины говорит сам по себе во время исследования. Off = тишина, High = часто." },
                { Language.Japanese, "探索中にマシン・スピリットが自律的に発言します。Off = 沈黙、High = 頻繁。" },
                { Language.Chinese, "机魂在探索期间自主发言。Off = 静默，High = 频繁。" }
            },

            // Machine Spirit v3.60.0 — Vision
            ["MSEnableVision"] = new() {
                { Language.English, "Enable Vision (Pict-capture)" }, { Language.Korean, "비전 활성화 (화면 캡처)" },
                { Language.Russian, "Включить зрение (захват экрана)" }, { Language.Japanese, "ビジョン有効化（画面キャプチャ）" },
                { Language.Chinese, "启用视觉（画面捕获）" }
            },
            ["MSVisionDesc"] = new() {
                { Language.English, "After long silence, captures a screenshot for Gemma 3 to comment on. Ollama only." },
                { Language.Korean, "긴 침묵 후 스크린샷을 캡처하여 Gemma 3가 코멘트합니다. Ollama 전용." },
                { Language.Russian, "После долгой тишины делает скриншот для комментария Gemma 3. Только Ollama." },
                { Language.Japanese, "長い沈黙の後、スクリーンショットを撮影してGemma 3がコメントします。Ollamaのみ。" },
                { Language.Chinese, "长时间沉默后，截取屏幕画面供Gemma 3评论。仅限Ollama。" }
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

            // ★ v3.54.0: MaxTokens 마이그레이션 (구버전 150/300 → 신버전 500)
            if (Instance.MachineSpirit.MaxTokens < 500)
            {
                Instance.MachineSpirit.MaxTokens = 500;
                Save();
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
