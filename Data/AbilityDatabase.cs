using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.ElementsSystem;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics.Actions;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// 통합 능력 데이터베이스
    /// v2.2에서 포팅 - GUID 기반 정확한 스킬 식별
    /// </summary>
    public static class AbilityDatabase
    {
        #region Data Structure

        /// <summary>
        /// 능력 정보 구조체
        /// </summary>
        public class AbilityInfo
        {
            public string Guid { get; }
            public string Name { get; }
            public AbilityTiming Timing { get; }
            public float HPThreshold { get; }
            public float TargetHPThreshold { get; }
            public AbilityFlags Flags { get; }

            public AbilityInfo(string guid, string name, AbilityTiming timing,
                float hpThreshold = 0f, float targetHP = 0f, AbilityFlags flags = AbilityFlags.None)
            {
                Guid = guid;
                Name = name;
                Timing = timing;
                HPThreshold = hpThreshold;
                TargetHPThreshold = targetHP;
                Flags = flags;
            }

            public bool IsSingleUse => (Flags & AbilityFlags.SingleUse) != 0;
            public bool IsDangerous => (Flags & AbilityFlags.Dangerous) != 0;
        }

        #endregion

        #region GUID Database

        private static readonly Dictionary<string, AbilityInfo> Database = new()
        {
            // ========================================
            // PostFirstAction - 첫 행동 후 사용
            // ========================================

            // Run and Gun
            { "22a25a3e418246ccbe95f2cc81c17473", new AbilityInfo("22a25a3e418246ccbe95f2cc81c17473", "RunAndGun_HeavyBolter", AbilityTiming.PostFirstAction) },
            { "cfc7943b71f04a1c9be6465946fc9ee2", new AbilityInfo("cfc7943b71f04a1c9be6465946fc9ee2", "RunAndGun_Mob", AbilityTiming.PostFirstAction) },
            { "5e60764f84c94277ae6a78b63a1fd2aa", new AbilityInfo("5e60764f84c94277ae6a78b63a1fd2aa", "RunAndGun_Soldier", AbilityTiming.PostFirstAction) },
            { "4d36d2b6d17e41348a21dd4fc0f4f8fd", new AbilityInfo("4d36d2b6d17e41348a21dd4fc0f4f8fd", "RunAndGun", AbilityTiming.PostFirstAction, flags: AbilityFlags.SelfTargetOnly) },
            { "83de00a47cb74f518e127978d3049a6e", new AbilityInfo("83de00a47cb74f518e127978d3049a6e", "RunAndGun2", AbilityTiming.PostFirstAction, flags: AbilityFlags.SelfTargetOnly) },

            // Daring Breach
            { "51366be5481b4ca7b348d9ac69a79f46", new AbilityInfo("51366be5481b4ca7b348d9ac69a79f46", "DaringBreach", AbilityTiming.PostFirstAction, hpThreshold: 30f) },
            { "845a1ed417f2489489eab670b00b773a", new AbilityInfo("845a1ed417f2489489eab670b00b773a", "DaringBreach_Fighter", AbilityTiming.PostFirstAction, hpThreshold: 30f) },
            { "ed21642647a14ead9a09183cd5318d11", new AbilityInfo("ed21642647a14ead9a09183cd5318d11", "DaringBreach_Ultimate", AbilityTiming.PostFirstAction, hpThreshold: 30f) },

            // ========================================
            // PreCombatBuff - 전투 시작 시 버프
            // ========================================

            // Defensive Stance
            { "cd42292391e74ba7809d0600ddb43a8d", new AbilityInfo("cd42292391e74ba7809d0600ddb43a8d", "DefensiveStance", AbilityTiming.PreCombatBuff) },
            { "dfda4e8761d44549b0e70b10a71947fc", new AbilityInfo("dfda4e8761d44549b0e70b10a71947fc", "DefensiveStance_Recover", AbilityTiming.PreCombatBuff) },
            { "39247f7f6f024676a693a7e04fcc631d", new AbilityInfo("39247f7f6f024676a693a7e04fcc631d", "DefensiveStance_Vanguard", AbilityTiming.PreCombatBuff) },

            // Bulwark
            { "0b693a158fed42a387d5f61ff6f0ae4c", new AbilityInfo("0b693a158fed42a387d5f61ff6f0ae4c", "Bulwark", AbilityTiming.PreCombatBuff) },
            { "b064fd994f804996afc43725ffb75f7c", new AbilityInfo("b064fd994f804996afc43725ffb75f7c", "Bulwark_Strategy", AbilityTiming.PreCombatBuff) },

            // Other Buffs
            { "3e34d7ddd1dc4cc580dc8a578cc09beb", new AbilityInfo("3e34d7ddd1dc4cc580dc8a578cc09beb", "AuraOfFaith", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },
            { "4eda9e3c69b24a8f866a21ba0f935a09", new AbilityInfo("4eda9e3c69b24a8f866a21ba0f935a09", "FinestHour", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },
            { "51fe0021d50d4e3b8ce96c4bd7fe6b56", new AbilityInfo("51fe0021d50d4e3b8ce96c4bd7fe6b56", "BlessedBullets", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // TurnEnding - 턴 종료 스킬
            // ========================================

            { "f6a60b4556214528b0ce295c4f69306e", new AbilityInfo("f6a60b4556214528b0ce295c4f69306e", "StalwartDefense", AbilityTiming.TurnEnding) },
            { "a77c8eee6d684e9587f6be5b10f93bb7", new AbilityInfo("a77c8eee6d684e9587f6be5b10f93bb7", "Overwatch", AbilityTiming.TurnEnding, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // Reload - 재장전
            // ========================================

            { "98f4a31b68e446ad9c63411c7b349146", new AbilityInfo("98f4a31b68e446ad9c63411c7b349146", "Reload", AbilityTiming.Reload) },
            { "b1704fc05eeb406ba23158061e765cac", new AbilityInfo("b1704fc05eeb406ba23158061e765cac", "Reload_NoAoO", AbilityTiming.Reload) },
            { "121068f8b70641458b24b3edc31f9132", new AbilityInfo("121068f8b70641458b24b3edc31f9132", "Reload_Plasma", AbilityTiming.Reload) },
            { "1e3a9caa44f04f7696ad5bd4ec4056a3", new AbilityInfo("1e3a9caa44f04f7696ad5bd4ec4056a3", "Reload_Kellermoph", AbilityTiming.Reload) },
            { "1cedb5f0cf104f57a88f91168e4c0df8", new AbilityInfo("1cedb5f0cf104f57a88f91168e4c0df8", "Reload_PostCombat", AbilityTiming.Reload) },
            { "afb34784b5f742b980cb0a3d46c9abe3", new AbilityInfo("afb34784b5f742b980cb0a3d46c9abe3", "WeaponReload", AbilityTiming.Reload) },

            // ========================================
            // Taunt - 도발
            // ========================================

            { "742ab23861c544b38f26e17175d17183", new AbilityInfo("742ab23861c544b38f26e17175d17183", "Taunt", AbilityTiming.Taunt) },
            { "46e7a840c3d04703b154660efb45538b", new AbilityInfo("46e7a840c3d04703b154660efb45538b", "Taunt_Vanguard", AbilityTiming.Taunt) },
            { "a8c7d8404d104d4dad2d460ec2b470ee", new AbilityInfo("a8c7d8404d104d4dad2d460ec2b470ee", "Taunt_Servoskull", AbilityTiming.Taunt) },
            { "13e41af1d54c458da81050336ce8e0fc", new AbilityInfo("13e41af1d54c458da81050336ce8e0fc", "MockingCry", AbilityTiming.Taunt) },
            { "383d89aaa52f4c3f8e19a02659ce19e7", new AbilityInfo("383d89aaa52f4c3f8e19a02659ce19e7", "ProvocatorHelm", AbilityTiming.Taunt) },
            { "beef5bd0b6724e5c8373fb9fbcd34084", new AbilityInfo("beef5bd0b6724e5c8373fb9fbcd34084", "TauntTargetAbility", AbilityTiming.Taunt, flags: AbilityFlags.EnemyTarget) },

            // ========================================
            // Finisher - 마무리
            // ========================================

            { "6a4c3b65dff840e0aab5966ffe8aa7ba", new AbilityInfo("6a4c3b65dff840e0aab5966ffe8aa7ba", "DeathSentence", AbilityTiming.Finisher, targetHP: 30f) },
            { "5b8545bc7a90491d865410a585071efe", new AbilityInfo("5b8545bc7a90491d865410a585071efe", "MissionComplete", AbilityTiming.Finisher, targetHP: 30f) },
            { "cf6c3356a9b44dd7badea16a625687be", new AbilityInfo("cf6c3356a9b44dd7badea16a625687be", "MissionComplete_Sub", AbilityTiming.Finisher, targetHP: 30f) },
            { "ed10346264414140936abd17d6c5b445", new AbilityInfo("ed10346264414140936abd17d6c5b445", "TwilightDismantle", AbilityTiming.Finisher, targetHP: 25f) },
            { "614fe492067d4b50b03695782af00f00", new AbilityInfo("614fe492067d4b50b03695782af00f00", "PowerMaulFinish", AbilityTiming.Finisher, targetHP: 30f) },
            { "70b2fcb4c67544da847ea8e0792191d5", new AbilityInfo("70b2fcb4c67544da847ea8e0792191d5", "InstantExecution_Desperate", AbilityTiming.Finisher, targetHP: 30f) },
            { "ed15e7b6f9cc4d2f9d0f3c6e3c3a4b1c", new AbilityInfo("ed15e7b6f9cc4d2f9d0f3c6e3c3a4b1c", "BringItDown_Execute", AbilityTiming.Finisher, targetHP: 30f) },

            // ========================================
            // HeroicAct - Momentum 175+
            // ========================================

            { "635161f3087c4294bf39c5fefe3d01af", new AbilityInfo("635161f3087c4294bf39c5fefe3d01af", "ChainLightning", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "fda0e6fc865d4712a8dd48a63bce326e", new AbilityInfo("fda0e6fc865d4712a8dd48a63bce326e", "NurglesGift", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            // ★ v3.0.43: AllyTarget 플래그 추가 (Friend=True, Self=False)
            { "234425ce980548588fc9bb0fbd08497b", new AbilityInfo("234425ce980548588fc9bb0fbd08497b", "DataBlessing", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.AllyTarget) },
            { "ac688f9b6e8443da8380431780785eb8", new AbilityInfo("ac688f9b6e8443da8380431780785eb8", "PrecisionTuning", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.AllyTarget) },
            { "1497623133f74bcabd797aecdab2bb05", new AbilityInfo("1497623133f74bcabd797aecdab2bb05", "InstantExecution_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "8dfbb8da5a3b4a5b83e45934661fdd82", new AbilityInfo("8dfbb8da5a3b4a5b83e45934661fdd82", "Cannibalize", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "bba2c59af522402f8a6c2690256b1f8e", new AbilityInfo("bba2c59af522402f8a6c2690256b1f8e", "EndlessSlaughter", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "1a0e3b0471da4d61be248f36eac5fdaa", new AbilityInfo("1a0e3b0471da4d61be248f36eac5fdaa", "Frenzy", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "7a05cb34622f47fb8e704cebbfab3df8", new AbilityInfo("7a05cb34622f47fb8e704cebbfab3df8", "IncendiaryRounds", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "189ed32cd3c746078d63bd98c58ef05f", new AbilityInfo("189ed32cd3c746078d63bd98c58ef05f", "PainRetribution", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "8f34b8e6b92e48e09d411e34de5e5462", new AbilityInfo("8f34b8e6b92e48e09d411e34de5e5462", "SwiftAttack", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "49977f6a6b414182bb6af1f130073981", new AbilityInfo("49977f6a6b414182bb6af1f130073981", "TzeentchTerror", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "741837887a4f429193ba44ad3948a71a", new AbilityInfo("741837887a4f429193ba44ad3948a71a", "Overdose", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "f8629a743eaf414eb8bf79edee9b02d0", new AbilityInfo("f8629a743eaf414eb8bf79edee9b02d0", "LifeDrain", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "14c15a3b56d54b30addcc1df8f4a6420", new AbilityInfo("14c15a3b56d54b30addcc1df8f4a6420", "LifeDrain_Sculptor", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "1598fb26e644442098e72562537ae660", new AbilityInfo("1598fb26e644442098e72562537ae660", "DeathSentence_Divination", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "84ddefd28f224d5fb3f5e176375c1f05", new AbilityInfo("84ddefd28f224d5fb3f5e176375c1f05", "Inferno", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "bdde427505b14cd68b20ab0d915d5fe3", new AbilityInfo("bdde427505b14cd68b20ab0d915d5fe3", "EmperorsWrath", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "9edd0e95bfea4532b764920a7b7f67bf", new AbilityInfo("9edd0e95bfea4532b764920a7b7f67bf", "Binding", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },
            { "5b19d80b3d694f77b84c2b38a04efe8f", new AbilityInfo("5b19d80b3d694f77b84c2b38a04efe8f", "DeathPhantom", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },

            // ========================================
            // Healing - 치료
            // ========================================

            { "083d5280759b4ed3a2d0b61254653273", new AbilityInfo("083d5280759b4ed3a2d0b61254653273", "Medikit", AbilityTiming.Healing) },
            { "b6e3c9398ea94c75afdbf61633ce2f85", new AbilityInfo("b6e3c9398ea94c75afdbf61633ce2f85", "Medikit_BattleMedic", AbilityTiming.Healing) },
            { "dd2e9a6170b448d4b2ec5a7fe0321e65", new AbilityInfo("dd2e9a6170b448d4b2ec5a7fe0321e65", "CombatStimMedikit", AbilityTiming.Healing) },
            { "ededbc48a7f24738a0fdb708fc48bb4c", new AbilityInfo("ededbc48a7f24738a0fdb708fc48bb4c", "MedicMedikit", AbilityTiming.Healing) },
            { "1359b77cf6714555895e2d3577f6f9b9", new AbilityInfo("1359b77cf6714555895e2d3577f6f9b9", "GladiatorHealKit", AbilityTiming.Healing) },
            { "48ac9afb9b6d4caf8d488ea85d3d60ac", new AbilityInfo("48ac9afb9b6d4caf8d488ea85d3d60ac", "SkinPatch", AbilityTiming.Healing) },
            { "2e9a23383b574408b4acdf6b62f6ed9b", new AbilityInfo("2e9a23383b574408b4acdf6b62f6ed9b", "LabourerMedikit", AbilityTiming.Healing) },
            { "2081944e0fd8481e84c30ec03cfdc04e", new AbilityInfo("2081944e0fd8481e84c30ec03cfdc04e", "ProperCare", AbilityTiming.Healing) },
            { "d722bfac662c40f9b2a47dc6ea70d00a", new AbilityInfo("d722bfac662c40f9b2a47dc6ea70d00a", "LargeMedikit", AbilityTiming.Healing) },
            { "0a88bfaa16ff41ea847ea14f58b384da", new AbilityInfo("0a88bfaa16ff41ea847ea14f58b384da", "TraumaCare", AbilityTiming.Healing) },
            { "6aa84dbf3a3d4ae4a50db8425dc9e62e", new AbilityInfo("6aa84dbf3a3d4ae4a50db8425dc9e62e", "BasicMedikit", AbilityTiming.Healing, flags: AbilityFlags.SelfTargetOnly | AbilityFlags.IsConsumable) },
            { "12e98698daeb44748efed608fedc4645", new AbilityInfo("12e98698daeb44748efed608fedc4645", "ExtendedMedikit", AbilityTiming.Healing, flags: AbilityFlags.SelfTargetOnly | AbilityFlags.IsConsumable) },
            { "c888846cba094af0a56f7036e3fc0854", new AbilityInfo("c888846cba094af0a56f7036e3fc0854", "BlastMedikit", AbilityTiming.Healing, flags: AbilityFlags.SelfTargetOnly | AbilityFlags.IsConsumable) },
            { "7218ef4b24b04fe89ab460f960c0b769", new AbilityInfo("7218ef4b24b04fe89ab460f960c0b769", "Medikit_Argenta", AbilityTiming.Healing, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // SelfDamage - 자해 스킬
            // ========================================

            // ★ v3.0.42: BloodOath는 적을 마킹하는 스킬 (SelfDamage → Marker)
            // 적에게 사용 → 공격 시 보너스 획득, HP 비용 있음
            { "590c990c1d684fd09ae883754d28a8ac", new AbilityInfo("590c990c1d684fd09ae883754d28a8ac", "BloodOath", AbilityTiming.Marker, hpThreshold: 60f, flags: AbilityFlags.EnemyTarget | AbilityFlags.SingleUse) },
            { "858e841542554025bc3ecdb6336b87ea", new AbilityInfo("858e841542554025bc3ecdb6336b87ea", "Bloodletting", AbilityTiming.SelfDamage, hpThreshold: 50f) },
            { "566b140329b3441aafa971d729124947", new AbilityInfo("566b140329b3441aafa971d729124947", "RecklessDecision", AbilityTiming.SelfDamage, hpThreshold: 70f) },
            // ★ v3.0.43: HyperMetabolism는 아군에게 추가 턴을 주는 버프 (SelfDamage → PreCombatBuff)
            // 아군 타겟 + 단일 사용, HP 임계값은 아군에게 적용되지 않으므로 제거
            { "29b7ab2d3e2640f3ad20a5c44c300346", new AbilityInfo("29b7ab2d3e2640f3ad20a5c44c300346", "HyperMetabolism", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SingleUse | AbilityFlags.AllyTarget) },
            // ★ v3.0.43: OathOfEndurance 제거 - 가짜 GUID (게임에 존재하지 않는 능력)

            // ========================================
            // DangerousAoE - 위험한 AoE
            // ========================================

            { "1a508a8b705e427aa00dcae2bdba407e", new AbilityInfo("1a508a8b705e427aa00dcae2bdba407e", "LidlessGaze_Mob", AbilityTiming.DangerousAoE, flags: AbilityFlags.Dangerous) },
            { "b932b545f5d8460ab562b0003294e775", new AbilityInfo("b932b545f5d8460ab562b0003294e775", "LidlessGaze", AbilityTiming.DangerousAoE, flags: AbilityFlags.Dangerous) },
            { "b79546d74c044b2e936362536656ab6f", new AbilityInfo("b79546d74c044b2e936362536656ab6f", "BladeDance_Master", AbilityTiming.DangerousAoE, flags: AbilityFlags.Dangerous) },
            { "e955823f54d24088ae1fdefe88d3684d", new AbilityInfo("e955823f54d24088ae1fdefe88d3684d", "BladeDance_Reaper", AbilityTiming.DangerousAoE, flags: AbilityFlags.Dangerous) },

            // ========================================
            // Debuff - 디버프
            // ========================================

            { "197b8a8a12b0442db7ffee1067cf3d97", new AbilityInfo("197b8a8a12b0442db7ffee1067cf3d97", "ExposeWeakness", AbilityTiming.Debuff) },
            { "c8b1b420e52c46699781bf2789e9905c", new AbilityInfo("c8b1b420e52c46699781bf2789e9905c", "ExposeWeakness_Sub", AbilityTiming.Debuff) },
            // ★ v3.0.34: Master Tactician - 목표 지정은 Marker로 재분류
            { "91d40472299d48ffa675c249c4226d64", new AbilityInfo("91d40472299d48ffa675c249c4226d64", "AssignObjective", AbilityTiming.Marker, flags: AbilityFlags.EnemyTarget) },

            // ★ v3.0.29: Operative 적 분석 (Analyze Enemies) - exploit 추가, 공격 아님!
            { "502b9195c84c42c5b180f3f63ed7955c", new AbilityInfo("502b9195c84c42c5b180f3f63ed7955c", "AnalyzeEnemies", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },
            { "18f968c992344aa08d873bab8ae6a7af", new AbilityInfo("18f968c992344aa08d873bab8ae6a7af", "AnalyzeEnemies_Mob", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },
            { "aa56d3dd1b744673ba16672a1ea1cfb1", new AbilityInfo("aa56d3dd1b744673ba16672a1ea1cfb1", "AnalyzeEnemies_OffbeatSight", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },
            { "50dd16833a7a4ffcaab972fe3fca3980", new AbilityInfo("50dd16833a7a4ffcaab972fe3fca3980", "AnalyzeEnemies_Starport", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },

            // ★ v3.0.30: 기계령 추방 (Machine Spirit Banishment) - 적 공격력 감소 디버프
            { "0b8058ef3f33406faebea94ee7967d66", new AbilityInfo("0b8058ef3f33406faebea94ee7967d66", "MachineSpiritBanishment", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.31: Operative 유틸리티 스킬 (공격 아님!)
            // ========================================

            // Intimidation (위협) - 다음 공격에 협박 효과 부여
            { "aedb4d2a2fd049db9c3f61b6737043a4", new AbilityInfo("aedb4d2a2fd049db9c3f61b6737043a4", "Intimidation", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Joint Analysis (합동 분석) - 아군 공격도 exploit 제거 효과 부여
            { "6f6d2e22b4e744dd9e777b769c1aa167", new AbilityInfo("6f6d2e22b4e744dd9e777b769c1aa167", "JointAnalysis", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Precise Attack (정밀 공격) - 다음 공격 명중률/엄폐 무시+
            { "7b00b5ea0dc246e1a304619d5678ca5b", new AbilityInfo("7b00b5ea0dc246e1a304619d5678ca5b", "PreciseAttack", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Tactical Knowledge (전술 지식) - exploit 제거 후 아군 방어 버프
            { "2a417cde46674ae0a5da8dcfd9ec621d", new AbilityInfo("2a417cde46674ae0a5da8dcfd9ec621d", "TacticalKnowledge", AbilityTiming.PreCombatBuff, flags: AbilityFlags.PointTarget) },

            // ========================================
            // GapCloser - 갭 클로저
            // ========================================

            { "c78506dd0e14f7c45a599990e4e65038", new AbilityInfo("c78506dd0e14f7c45a599990e4e65038", "Charge", AbilityTiming.GapCloser) },
            { "40800d54d3d64c7cb2d746cc2cce9a1b", new AbilityInfo("40800d54d3d64c7cb2d746cc2cce9a1b", "Charge_CSM", AbilityTiming.GapCloser) },
            { "67f785ba0562480697aa3735bdd9e0c2", new AbilityInfo("67f785ba0562480697aa3735bdd9e0c2", "Charge_Uralon", AbilityTiming.GapCloser) },
            { "4955b43454f6488f82892e166c76c995", new AbilityInfo("4955b43454f6488f82892e166c76c995", "Charge_Fighter", AbilityTiming.GapCloser) },
            { "d9f20b396eb64a4293c9e3bd3270e0dc", new AbilityInfo("d9f20b396eb64a4293c9e3bd3270e0dc", "UnstoppableOnslaught", AbilityTiming.GapCloser) },
            { "8fed5098066b48efa1e09a14f7b8f6c6", new AbilityInfo("8fed5098066b48efa1e09a14f7b8f6c6", "Ambush_Ambull", AbilityTiming.GapCloser) },
            { "c3e407372e02483e87b350235fc409f0", new AbilityInfo("c3e407372e02483e87b350235fc409f0", "AmbushTeleport_Ambull", AbilityTiming.GapCloser) },

            // ========================================
            // DOTIntensify - DoT 강화 스킬
            // ========================================

            { "7720d74e51f94184bb43b97ce9c9e53f", new AbilityInfo("7720d74e51f94184bb43b97ce9c9e53f", "ShapeFlames", AbilityTiming.DOTIntensify) },
            { "24f1e49a2294434da2dc17edb6808517", new AbilityInfo("24f1e49a2294434da2dc17edb6808517", "FanTheFlames", AbilityTiming.DOTIntensify) },
            { "cb3a7a2b865d424183d290b4ff8d3f34", new AbilityInfo("cb3a7a2b865d424183d290b4ff8d3f34", "FanTheFlames_EnemiesOnly", AbilityTiming.DOTIntensify) },

            // ========================================
            // ChainEffect - 연쇄 효과 스킬
            // ========================================

            { "7b68b4aa3c024f348a20dce3ef172e40", new AbilityInfo("7b68b4aa3c024f348a20dce3ef172e40", "ChainLightning", AbilityTiming.ChainEffect, flags: AbilityFlags.SingleUse) },
            { "3c48374cbe244fc2bb8b6293230a6829", new AbilityInfo("3c48374cbe244fc2bb8b6293230a6829", "ChainLightning_Desperate", AbilityTiming.ChainEffect, flags: AbilityFlags.SingleUse) },

            // ========================================
            // ★ v3.0.28: Marker - 마킹 스킬 (데미지 없음, 적 표시만)
            // ========================================

            // Bounty Hunter - 사냥감 표시 스킬 (공격 아님!)
            { "b97c9e76f6ca46d3bb8ccd86baa9d7c9", new AbilityInfo("b97c9e76f6ca46d3bb8ccd86baa9d7c9", "HuntDownThePrey", AbilityTiming.Marker, flags: AbilityFlags.EnemyTarget) },
            { "56bd0b3c54784098ae7044ceae173c7c", new AbilityInfo("56bd0b3c54784098ae7044ceae173c7c", "PaveTheTrail", AbilityTiming.Marker, flags: AbilityFlags.EnemyTarget) },
            { "43ee13d74e824d07a0fa2a651c23df40", new AbilityInfo("43ee13d74e824d07a0fa2a651c23df40", "ChoosePrey_Noble", AbilityTiming.Marker, flags: AbilityFlags.EnemyTarget) },

            // ★ v3.0.32: Warrior - 숙적 (Sworn Enemy) - 마킹 스킬 (데미지 없음, 보너스 부여만)
            { "4696f02da13b4596b941bb950d945a05", new AbilityInfo("4696f02da13b4596b941bb950d945a05", "SwornEnemy", AbilityTiming.Marker, flags: AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.33: Warrior 추가 스킬
            // ========================================

            // Endure - 방어 버프 (Deflection + Temporary Wounds)
            { "4d2f2a839d2340388d45cf4cf66c947b", new AbilityInfo("4d2f2a839d2340388d45cf4cf66c947b", "Endure", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Break Through - Slash 능력 활성화 (첫 행동 후 추가 공격 활성화)
            { "54f73c2523524544a8d7ffc3807056f0", new AbilityInfo("54f73c2523524544a8d7ffc3807056f0", "BreakThrough", AbilityTiming.PostFirstAction, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // ★ v3.0.34: Bladedancer 스킬
            // ========================================

            // ★ v3.0.81: Death from Above - 핵심 갭클로저 (3칸 점프 + 착지 피해)
            // 충전 기반, 적 처치 시 충전 회복, 적극 활용 권장
            // PointTarget: 셀 타겟 능력 → MovementPlanner에서 착지 위치 계산
            { "6f1b7cfb48a0450cb85ce8a8879502de", new AbilityInfo("6f1b7cfb48a0450cb85ce8a8879502de", "DeathFromAbove", AbilityTiming.GapCloser, flags: AbilityFlags.PointTarget) },

            // ★ v3.1.24: 죽음 강림 (Death Descending) 변형들 - 모두 GapCloser + PointTarget
            { "780c0fb08c2e4205a8f6df9b9fc2ed3b", new AbilityInfo("780c0fb08c2e4205a8f6df9b9fc2ed3b", "DeathFromAbove_Free", AbilityTiming.GapCloser, flags: AbilityFlags.PointTarget) },
            { "bdb0bd56d86a45dc914226ea4704f5f6", new AbilityInfo("bdb0bd56d86a45dc914226ea4704f5f6", "DeathFromAbove_Spring", AbilityTiming.GapCloser, flags: AbilityFlags.PointTarget) },
            { "2fe0f07d130c4af2ab23a9ce75efad5a", new AbilityInfo("2fe0f07d130c4af2ab23a9ce75efad5a", "DeathFromAbove_Targeted", AbilityTiming.GapCloser, flags: AbilityFlags.PointTarget) },
            { "4c8e11fc01cc4923895e0246be178aea", new AbilityInfo("4c8e11fc01cc4923895e0246be178aea", "DeathFromAbove_Ultimate", AbilityTiming.GapCloser, flags: AbilityFlags.PointTarget) },

            // Death Waltz (Heroic Act) - Momentum 175+ 필요
            { "d52b8f3b44434f2798cd3a01c97fd1ed", new AbilityInfo("d52b8f3b44434f2798cd3a01c97fd1ed", "DeathWaltz_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // Death Waltz (Desperate Measures) - 긴급 상황, 트라우마 유발
            { "e90bd23750dc4390a9bff3d8c3abab0b", new AbilityInfo("e90bd23750dc4390a9bff3d8c3abab0b", "DeathWaltz_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // ★ v3.0.43: Oath of Vengeance - 아군 지정 버프 (SelfDamage → PreCombatBuff)
            // wound 소모하지만 타겟은 아군, 해당 아군 공격한 적에게 크리 보너스
            { "3774147440ac412a876725b9b2b24682", new AbilityInfo("3774147440ac412a876725b9b2b24682", "OathOfVengeance", AbilityTiming.PreCombatBuff, hpThreshold: 60f, flags: AbilityFlags.AllyTarget) },

            // Captive Audience - 다음 공격에 출혈+고정 효과 부여
            { "ce30e102719a4671b660ffe5bff7c43d", new AbilityInfo("ce30e102719a4671b660ffe5bff7c43d", "CaptiveAudience", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Acrobatic Artistry - 턴 시작 위치로 복귀 (탈출 스킬)
            { "1798e1237504457db15655280481d549", new AbilityInfo("1798e1237504457db15655280481d549", "AcrobaticArtistry", AbilityTiming.TurnEnding, flags: AbilityFlags.SelfTargetOnly) },

            // Veil of Blades - wound 소모 + 턴 종료 + 방어 자세
            { "8b7bcaa093224422ac66c80ffcf69f6d", new AbilityInfo("8b7bcaa093224422ac66c80ffcf69f6d", "VeilOfBlades", AbilityTiming.TurnEnding, hpThreshold: 60f, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // ★ v3.0.34: Assassin 스킬
            // ========================================

            // Killing Edge - 적 HP 낮을수록 데미지 증가 (Finisher)
            { "c9dba0331212410dabe7dd2e19c52cd3", new AbilityInfo("c9dba0331212410dabe7dd2e19c52cd3", "KillingEdge", AbilityTiming.Finisher, targetHP: 50f, flags: AbilityFlags.EnemyTarget) },

            // Elusive Shadow - 은신 + 회피 버프
            { "c62c629dde5843a99a511997646a95f3", new AbilityInfo("c62c629dde5843a99a511997646a95f3", "ElusiveShadow", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Feinting Attack - 다음 공격 회피/패리 무시
            { "e80e1c231c0e4d658ff9731d91dc2294", new AbilityInfo("e80e1c231c0e4d658ff9731d91dc2294", "FeintingAttack", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Poised to Strike - 적 방어구/편향 감소 디버프
            { "d8dd5aa174fb49a4b493ba266dece9af", new AbilityInfo("d8dd5aa174fb49a4b493ba266dece9af", "PoisedToStrike", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },

            // Death Whisper - 0 AP 공격 + 출혈 (공격으로 분류됨)
            { "33214b7215b442519f4303bcd4a8ab34", new AbilityInfo("33214b7215b442519f4303bcd4a8ab34", "DeathWhisper", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget | AbilityFlags.IsWeaponAttack) },

            // Danse Macabre - 이동 + 회피 버프 (갭클로저/이동)
            { "af4b536f77d14e12907337ff0efb7f76", new AbilityInfo("af4b536f77d14e12907337ff0efb7f76", "DanseMacabre", AbilityTiming.GapCloser, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // ★ v3.0.34: Arch-Militant 스킬
            // ========================================

            // Cautious Approach - 방어적 스탠스 (회피/패리 보너스)
            { "4b247404899148edb98585496b072dde", new AbilityInfo("4b247404899148edb98585496b072dde", "CautiousApproach", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Confident Approach - 공격적 스탠스 (회피 관통/크리티컬)
            { "142b89c8df7f4b2795c45243fde0a169", new AbilityInfo("142b89c8df7f4b2795c45243fde0a169", "ConfidentApproach", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Devastating Attack - 다음 공격 CC 부여
            { "fe85384efebb4d38b71738594746e413", new AbilityInfo("fe85384efebb4d38b71738594746e413", "DevastatingAttack", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Wildfire - 다음 공격 0 AP
            { "abc65b0bb19641228b32bf007f1aaaa0", new AbilityInfo("abc65b0bb19641228b32bf007f1aaaa0", "Wildfire", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Heroic Sacrifice (Desperate) - 추가 공격 획득, 출혈
            { "f4dfd57e9b204e1e8d7eb2bb61a9ac11", new AbilityInfo("f4dfd57e9b204e1e8d7eb2bb61a9ac11", "HeroicSacrifice_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.SelfTargetOnly) },

            // Steady Superiority (Heroic) - 추가 공격 획득
            { "ee309bc21c4a48b1ab8a825de8e447ff", new AbilityInfo("ee309bc21c4a48b1ab8a825de8e447ff", "SteadySuperiority_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.SelfTargetOnly) },

            // Kick - 0 AP 공격 + 밀어내기
            { "22d6c1b32a6d47f4899a370e28cf16f9", new AbilityInfo("22d6c1b32a6d47f4899a370e28cf16f9", "Kick", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.34: Officer 스킬
            // ========================================

            // Voice of Command - 아군 특성 증가 버프
            { "9c78e44bf8ff44a9afff8370c673c9ad", new AbilityInfo("9c78e44bf8ff44a9afff8370c673c9ad", "VoiceOfCommand", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Air of Authority - 아군 Resolve 증가
            { "32c0e0539f8548b08780089f8006f54a", new AbilityInfo("32c0e0539f8548b08780089f8006f54a", "AirOfAuthority", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Take Aim! - 아군 엄폐 무시, 사거리 2배
            { "3a4d9ccff9cf40bd88fa018e05ec550d", new AbilityInfo("3a4d9ccff9cf40bd88fa018e05ec550d", "TakeAim", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Break Their Ranks! - 아군 근접 데미지 증가
            { "b509a3ee5ab44253b4b859dd5f3dbffb", new AbilityInfo("b509a3ee5ab44253b4b859dd5f3dbffb", "BreakTheirRanks", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Move, Move, Move! - 아군 MP 증가
            { "cb64551ce0234a85880bd0d8da91637f", new AbilityInfo("cb64551ce0234a85880bd0d8da91637f", "MoveMoveMove", AbilityTiming.Normal, flags: AbilityFlags.AllyTarget) },

            // Get Back in the Fight! - 아군 상태이상 해제
            { "083057fc1a6b47f29ea0409337135030", new AbilityInfo("083057fc1a6b47f29ea0409337135030", "GetBackInTheFight", AbilityTiming.Healing, flags: AbilityFlags.AllyTarget) },

            // Finest Hour (Heroic) - 아군 추가 턴 부여
            { "33c8c9db91694ca6ad3eff26e36dd0af", new AbilityInfo("33c8c9db91694ca6ad3eff26e36dd0af", "FinestHour_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.AllyTarget) },

            // Finest Hour (Desperate) - 아군 추가 턴 부여 (FEL 감소)
            { "3f78bec2aea340a780e47f0a2a8dfb5f", new AbilityInfo("3f78bec2aea340a780e47f0a2a8dfb5f", "FinestHour_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.AllyTarget) },

            // Bring It Down! - 아군 추가 턴 (2 AP)
            { "f6998735d21d42f7b23afe1485735f73", new AbilityInfo("f6998735d21d42f7b23afe1485735f73", "BringItDown", AbilityTiming.PostFirstAction, flags: AbilityFlags.AllyTarget) },

            // ========================================
            // ★ v3.0.34: Grand Strategist 스킬
            // ========================================

            // Combat Tactics 구역 설정 (전방/보조/후방)
            { "7cdf3a1dd3b5477096e008492e738587", new AbilityInfo("7cdf3a1dd3b5477096e008492e738587", "CombatTactics_Frontline", AbilityTiming.PositionalBuff, flags: AbilityFlags.PointTarget) },
            { "bc99e6ba256f45368bc6bc529e59effd", new AbilityInfo("bc99e6ba256f45368bc6bc529e59effd", "CombatTactics_Backline", AbilityTiming.PositionalBuff, flags: AbilityFlags.PointTarget) },
            { "b1e24d15bb6841d89b9873829f7b3ba5", new AbilityInfo("b1e24d15bb6841d89b9873829f7b3ba5", "CombatTactics_Rear", AbilityTiming.PositionalBuff, flags: AbilityFlags.PointTarget) },

            // Stratagem 스킬들 (구역 강화)
            { "7005fbf810a64264893cd18fc0187b39", new AbilityInfo("7005fbf810a64264893cd18fc0187b39", "BlitzStratagem", AbilityTiming.Stratagem, flags: AbilityFlags.PointTarget) },
            { "b6fa6a9130a64255933ca0144f28dd03", new AbilityInfo("b6fa6a9130a64255933ca0144f28dd03", "CombatLocusStratagem", AbilityTiming.Stratagem, flags: AbilityFlags.PointTarget) },
            { "ab86bcee2036424c90dd12c2ad3fab39", new AbilityInfo("ab86bcee2036424c90dd12c2ad3fab39", "KillzoneStratagem", AbilityTiming.Stratagem, flags: AbilityFlags.PointTarget) },
            { "7a5637714948456686eeaafa37f51813", new AbilityInfo("7a5637714948456686eeaafa37f51813", "OverwhelmingStratagem", AbilityTiming.Stratagem, flags: AbilityFlags.PointTarget) },
            { "111f6e8111ae4d30a9d5d6d06027281d", new AbilityInfo("111f6e8111ae4d30a9d5d6d06027281d", "StrongholdStratagem", AbilityTiming.Stratagem, flags: AbilityFlags.PointTarget) },
            { "0e89f6eda1ae4960aeebfed0737289a3", new AbilityInfo("0e89f6eda1ae4960aeebfed0737289a3", "TrenchlineStratagem", AbilityTiming.Stratagem, flags: AbilityFlags.PointTarget) },

            // Take and Hold (Heroic/Desperate)
            { "7684e69f9b404160af35e83ffa349d66", new AbilityInfo("7684e69f9b404160af35e83ffa349d66", "TakeAndHold_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.PointTarget) },
            { "a17db52789a74e85b24600c5081eb2e3", new AbilityInfo("a17db52789a74e85b24600c5081eb2e3", "TakeAndHold_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.PointTarget) },

            // ========================================
            // ★ v3.0.34: Master Tactician 스킬
            // ========================================

            // Fervour - 열의, 자기 버프
            { "305858b91e6e4d89bff75431fa6030e6", new AbilityInfo("305858b91e6e4d89bff75431fa6030e6", "Fervour", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Inspire - 고무, 아군 버프
            { "b34dd87c65af46c69c35502e3e1cfe93", new AbilityInfo("b34dd87c65af46c69c35502e3e1cfe93", "Inspire", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Linchpin - 핵심축, 아군 버프
            { "ca8040427b6d4a3d859991b62609b318", new AbilityInfo("ca8040427b6d4a3d859991b62609b318", "Linchpin", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Press the Advantage - 이점 활용, 자기 버프
            { "60a2011e90b949f2a38fe4a6cefb5100", new AbilityInfo("60a2011e90b949f2a38fe4a6cefb5100", "PressTheAdvantage", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Strongpoint - 거점, 아군 버프
            { "70ab234e53764c85a6f3d19de7bb182d", new AbilityInfo("70ab234e53764c85a6f3d19de7bb182d", "Strongpoint", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Desperate - 최후의 일제사격
            { "7a6c78a5bc854a2b95aa9b3cc3e2a094", new AbilityInfo("7a6c78a5bc854a2b95aa9b3cc3e2a094", "FinalSalvo_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // Ultimate - 지휘 포화
            { "4d45e7405f2a40e8899c48c0c2634f66", new AbilityInfo("4d45e7405f2a40e8899c48c0c2634f66", "CommandingBarrage_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.34: Executioner 스킬
            // ========================================

            // Carnival of Pain (Heroic) - 고통의 카니발
            { "650c67473864408e95c835e25f640c8d", new AbilityInfo("650c67473864408e95c835e25f640c8d", "CarnivalOfPain_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // Carnival of Pain (Desperate) - 고통의 카니발
            { "3bd371f9808649088757dbf8f0f1f577", new AbilityInfo("3bd371f9808649088757dbf8f0f1f577", "CarnivalOfPain_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // Aggravate - 고통 공명
            { "fd305e7458de42b5a28fa8b80437cf03", new AbilityInfo("fd305e7458de42b5a28fa8b80437cf03", "Aggravate", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Debilitating Shot - 무자비한 판결
            { "914b9182e9634dd1a3139c16485d091f", new AbilityInfo("914b9182e9634dd1a3139c16485d091f", "DebilitatingShot", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Vital Strike - 급소 찌르기 (Keystone)
            { "cfdb72a6653c49ffa792fa99abfed128", new AbilityInfo("cfdb72a6653c49ffa792fa99abfed128", "VitalStrike", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Gift of Agony - 고뇌의 선물
            { "401f97698d794e058adf5bee28aa2f1e", new AbilityInfo("401f97698d794e058adf5bee28aa2f1e", "GiftOfAgony", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Terrifying Strike - 공포의 일격
            { "d0c7cc80880242b1ba1ded8192c53ea4", new AbilityInfo("d0c7cc80880242b1ba1ded8192c53ea4", "TerrifyingStrike", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // ★ v3.0.34: Navigator 스킬
            // ========================================

            // Warp Curse Unleashed - 풀려난 워프의 저주
            { "3705e0ad78154ee0bd6436fc15dac784", new AbilityInfo("3705e0ad78154ee0bd6436fc15dac784", "WarpCurseUnleashed", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },

            // Reveal the Light - 빛을 드러내라
            { "8b97251d56204c40904d06c927930572", new AbilityInfo("8b97251d56204c40904d06c927930572", "RevealTheLight", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Mend Reality - 현실 수복 (Veil 감소)
            { "fdce618ea795493cac808e02871b429a", new AbilityInfo("fdce618ea795493cac808e02871b429a", "MendReality", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Held in My Gaze - 내 시선에 갇히다 (CC)
            { "ec639338f11e49b1a75a7b9757a77076", new AbilityInfo("ec639338f11e49b1a75a7b9757a77076", "HeldInMyGaze", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },

            // Zone of Fear - 공포 지대 (CC)
            { "d2a3ffe8100b4e7983ca87b2b76a6cc0", new AbilityInfo("d2a3ffe8100b4e7983ca87b2b76a6cc0", "ZoneOfFear", AbilityTiming.Debuff, flags: AbilityFlags.PointTarget) },

            // Notch of Purpose - 목표의 홈 (적 강제 이동)
            { "17077e37decf427db1a049ea25bb2b66", new AbilityInfo("17077e37decf427db1a049ea25bb2b66", "NotchOfPurpose", AbilityTiming.Debuff, flags: AbilityFlags.PointTarget) },

            // Point of Curiosity - 호기심의 장소 (적 강제 이동)
            { "b0d4e6b6b3534497bb21590826c34d13", new AbilityInfo("b0d4e6b6b3534497bb21590826c34d13", "PointOfCuriosity", AbilityTiming.Debuff, flags: AbilityFlags.PointTarget) },

            // Scourge of the Red Tide - 붉은 조수의 스커지 (범위 지속 피해)
            { "9043cccc4e3a4aef82d86f4c46052dc6", new AbilityInfo("9043cccc4e3a4aef82d86f4c46052dc6", "ScourgeOfTheRedTide", AbilityTiming.Normal, flags: AbilityFlags.PointTarget) },

            // ========================================
            // ★ v3.0.34: Arbitrator 스킬
            // ========================================

            // On the Ground! - 엎드려! (다음 공격 knockdown)
            { "b113924f2ec847e5ae0ccbc76809b704", new AbilityInfo("b113924f2ec847e5ae0ccbc76809b704", "OnTheGround", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Shield of the Lex - 법의 방패 (방어 버프)
            { "d606790f85ec4a02867fcc3c8ef62236", new AbilityInfo("d606790f85ec4a02867fcc3c8ef62236", "ShieldOfTheLex", AbilityTiming.PreCombatBuff, flags: AbilityFlags.EnemyTarget) },

            // Death Sentence (Desperate) - 사형 선고
            { "4e6482c67f0548668107514fd187dadd", new AbilityInfo("4e6482c67f0548668107514fd187dadd", "DeathSentence_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse) },

            // Double Slug - 이중 슬러그 (2연발 공격)
            { "def7164545244af28d52cd38d0502525", new AbilityInfo("def7164545244af28d52cd38d0502525", "DoubleSlug", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget | AbilityFlags.IsWeaponAttack) },

            // That's an Order! - 명령이다! (적 강제 이동)
            { "b6e6d67ba6cb4fae9bd717b1b05b15fa", new AbilityInfo("b6e6d67ba6cb4fae9bd717b1b05b15fa", "ThatsAnOrder", AbilityTiming.Debuff, flags: AbilityFlags.PointTarget) },

            // ========================================
            // ★ v3.0.34: Bounty Hunter 스킬
            // ========================================

            // Wild Hunt (Desperate) - 와일드 헌트
            { "ea17b46c1c2b42518605df74849c2e5e", new AbilityInfo("ea17b46c1c2b42518605df74849c2e5e", "WildHunt_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // Wild Hunt (Heroic) - 와일드 헌트
            { "39809f7335f542c587678086db6ce338", new AbilityInfo("39809f7335f542c587678086db6ce338", "WildHunt_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // Claim the Bounty - 현상금 청구
            { "a78f3ecf5c024fac860fc2beaa63a8f9", new AbilityInfo("a78f3ecf5c024fac860fc2beaa63a8f9", "ClaimTheBounty", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget | AbilityFlags.IsWeaponAttack) },

            // Piercing Shot - 관통 사격
            { "0d8923eff3f94a5faf71bfe36ca19d70", new AbilityInfo("0d8923eff3f94a5faf71bfe36ca19d70", "PiercingShot", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // ★ v3.0.36: Biomancy 스킬
            // ========================================

            // Invigorate - 활력 주입 (아군 회복)
            { "f51d5d8fdc8e49c4828c0180868e0be9", new AbilityInfo("f51d5d8fdc8e49c4828c0180868e0be9", "Invigorate", AbilityTiming.Healing, flags: AbilityFlags.AllyTarget) },

            // Iron Arm - 강철 팔 (자기/아군 버프 - 방어력)
            { "d4d5ca2a8f2d42ad82029a102ca0504a", new AbilityInfo("d4d5ca2a8f2d42ad82029a102ca0504a", "IronArm", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Regeneration - 재생 (아군 버프 - 지속 회복)
            { "1161a6715442449da47157aa60274a42", new AbilityInfo("1161a6715442449da47157aa60274a42", "Regeneration", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Warp Speed - 워프 속도 (아군 버프 - 이동/회피)
            { "b83829b067534eb0aaadab90b4d86452", new AbilityInfo("b83829b067534eb0aaadab90b4d86452", "WarpSpeed", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Enfeeble - 허약 (적 디버프 - 특성 감소)
            { "0fd6606266b245d19127ff2bf44e0d52", new AbilityInfo("0fd6606266b245d19127ff2bf44e0d52", "Enfeeble", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },

            // Syphon Life - 생명력 흡수 (적 공격 + 회복)
            { "fb5efed90c6442acb6f9c756905072eb", new AbilityInfo("fb5efed90c6442acb6f9c756905072eb", "SyphonLife", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // Syphon Life (Desperate) - 생명력 흡수 (필사적인 수단)
            { "65791f6355c04864b890c9e964b9c372", new AbilityInfo("65791f6355c04864b890c9e964b9c372", "SyphonLife_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.36: Pyromancy 스킬
            // ========================================

            // Molten Man - 용융 인간 (자기 버프 - 화염 반사)
            { "109578dd4983405a8b1261db279f3ada", new AbilityInfo("109578dd4983405a8b1261db279f3ada", "MoltenMan", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Fire Storm - 화염 폭풍 (범위 공격)
            { "321a9274e3454d69ada142f4ce540b12", new AbilityInfo("321a9274e3454d69ada142f4ce540b12", "FireStorm", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // Ignite - 점화 (적 공격 - 화상)
            { "a2cca43669184eaa9f0da981f204e1c9", new AbilityInfo("a2cca43669184eaa9f0da981f204e1c9", "Ignite", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // Incinerate - 소각 (적 공격 - 화상)
            { "8e54302d79784976a0fb95308d7f2fef", new AbilityInfo("8e54302d79784976a0fb95308d7f2fef", "Incinerate", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // Molten Beam - 용해 광선 (직선 공격)
            { "f31807528a974b86aa0910e46b844e9b", new AbilityInfo("f31807528a974b86aa0910e46b844e9b", "MoltenBeam", AbilityTiming.Normal, flags: AbilityFlags.PointTarget) },

            // Inferno - 인페르노 (무기 사이킥 공격)
            { "8a759cdc2b754309b1fb75397798fbf1", new AbilityInfo("8a759cdc2b754309b1fb75397798fbf1", "Inferno", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget | AbilityFlags.IsWeaponAttack) },

            // Inferno (Desperate) - 인페르노 (필사적인 수단)
            { "c4ea2ad9fe1e4509916cb5f1787b1530", new AbilityInfo("c4ea2ad9fe1e4509916cb5f1787b1530", "Inferno_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.36: Divination 스킬
            // ========================================

            // Forewarning - 조짐 (아군 버프 - 방어)
            { "4a7f14d3eed040fca5d96b51b4d070b1", new AbilityInfo("4a7f14d3eed040fca5d96b51b4d070b1", "Forewarning", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Perfect Timing - 절호의 기회 (자기 버프 - AP 회복)
            { "133771a21537411d989c1954e1fd7b23", new AbilityInfo("133771a21537411d989c1954e1fd7b23", "PerfectTiming", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Precognition - 예지 (자기 버프 - 전체 보너스)
            { "8ebd436eedfe4f9fb8b705b043eeaf67", new AbilityInfo("8ebd436eedfe4f9fb8b705b043eeaf67", "Precognition", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Prescience - 선견지명 (아군 버프 - 전체 보너스)
            { "2a8341087cbb458b95bbe882a366642f", new AbilityInfo("2a8341087cbb458b95bbe882a366642f", "Prescience", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Prophetic Intervention - 예언자의 개입 (아군 버프 - 무한 사거리)
            { "03d350e9f0a048e393cb8665934a72ec", new AbilityInfo("03d350e9f0a048e393cb8665934a72ec", "PropheticIntervention", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Foreboding - 불길한 예감 (범위 버프)
            { "f03c1b80631e412490316e47528c211f", new AbilityInfo("f03c1b80631e412490316e47528c211f", "Foreboding", AbilityTiming.PositionalBuff, flags: AbilityFlags.PointTarget) },

            // Consign - 사형 선고 (적 공격)
            { "26787468defa42598581625e2164ffd8", new AbilityInfo("26787468defa42598581625e2164ffd8", "Consign", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.36: Sanctic 스킬
            // ========================================

            // Hammer of the Emperor - 황제의 망치 (자기 버프 - 근접 강화)
            { "d1c22cdb445543a08b6dbc9867503572", new AbilityInfo("d1c22cdb445543a08b6dbc9867503572", "HammerOfTheEmperor", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Shield of the Emperor - 황제의 방패 (자기 버프 - 방어)
            { "c096aedc367c4cfe8c8ed8507319ebab", new AbilityInfo("c096aedc367c4cfe8c8ed8507319ebab", "ShieldOfTheEmperor", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Sword of Faith - 신념의 검 (자기 버프 - 검 생성)
            { "20d5376f942f454daf37f2b81a81e7b1", new AbilityInfo("20d5376f942f454daf37f2b81a81e7b1", "SwordOfFaith", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Word of the Emperor - 황제의 말씀 (아군 버프)
            { "6285e5ba2a804e3d9df1dd5f7fa819f2", new AbilityInfo("6285e5ba2a804e3d9df1dd5f7fa819f2", "WordOfTheEmperor", AbilityTiming.PreCombatBuff, flags: AbilityFlags.AllyTarget) },

            // Inscribed Soul - 새겨진 영혼 (자기 버프 - 사이킥 강화)
            { "e85dbed4375f441ea3749c122666ea0e", new AbilityInfo("e85dbed4375f441ea3749c122666ea0e", "InscribedSoul", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Light of the Emperor - 황제 폐하의 빛 (자기 버프 - 정화)
            { "050815b9d2e947acb13b81072f9b7210", new AbilityInfo("050815b9d2e947acb13b81072f9b7210", "LightOfTheEmperor", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Emperor's Wrath - 황제의 분노 (적 공격)
            { "560aa7c658a441df8bb4b2f9f431064b", new AbilityInfo("560aa7c658a441df8bb4b2f9f431064b", "EmperorsWrath", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // Emperor's Wrath (Desperate) - 황제의 분노 (필사적인 수단)
            { "7039f3b35fc5487e9b6c64909062ea04", new AbilityInfo("7039f3b35fc5487e9b6c64909062ea04", "EmperorsWrath_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // Purge Soul - 영혼 정화 (적 공격)
            { "4b401c668e0d46a7a09dba9d5480cdaa", new AbilityInfo("4b401c668e0d46a7a09dba9d5480cdaa", "PurgeSoul", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // Sword of Faith Area Attack - 신념의 검 범위 공격
            { "33ca35c54b144fc984850b48e8946aae", new AbilityInfo("33ca35c54b144fc984850b48e8946aae", "SwordOfFaith_Area", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget | AbilityFlags.IsWeaponAttack) },

            // Sword of Faith Line Attack - 신념의 검 화염 줄기
            { "94010e117c664f7fbe431f0cf60ada4b", new AbilityInfo("94010e117c664f7fbe431f0cf60ada4b", "SwordOfFaith_Line", AbilityTiming.Normal, flags: AbilityFlags.PointTarget) },

            // Sword of Faith Wide Attack - 신앙의 검 화염 원뿔
            { "10e7e13789744f6096808eb93852057d", new AbilityInfo("10e7e13789744f6096808eb93852057d", "SwordOfFaith_Wide", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget | AbilityFlags.IsWeaponAttack) },

            // ========================================
            // ★ v3.0.36: Telepathy 스킬
            // ========================================

            // Vision of Death - 죽음의 환영 (적 공격)
            { "5a328c19cc214780b878be2183550910", new AbilityInfo("5a328c19cc214780b878be2183550910", "VisionOfDeath", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // Vision of Death (Desperate) - 죽음의 환영 (필사적인 수단)
            { "8986fa989c474068b160ab1af21d763c", new AbilityInfo("8986fa989c474068b160ab1af21d763c", "VisionOfDeath_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.EnemyTarget) },

            // Psychic Scream - 사이킥 비명 (범위 공격)
            { "fa305ba4e2aa48f394608d1ccfc6d385", new AbilityInfo("fa305ba4e2aa48f394608d1ccfc6d385", "PsychicScream", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.37: Soldier 스킬
            // ========================================

            // Controlled Shot - 제어 사격 (다음 공격 정확도 증가)
            { "c69d2b34dcb64d91bf12308930079e0c", new AbilityInfo("c69d2b34dcb64d91bf12308930079e0c", "ControlledShot", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Rapid Fire - 속사 (다음 공격 추가 발사)
            { "d42db343de7e4b08a3ea0d7bc36af41f", new AbilityInfo("d42db343de7e4b08a3ea0d7bc36af41f", "RapidFire", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Revel In Slaughter - 학살의 환희 (적 처치 시 활성화)
            { "530b6d2cefef42f180b90c7bdbb99f90", new AbilityInfo("530b6d2cefef42f180b90c7bdbb99f90", "RevelInSlaughter_Soldier", AbilityTiming.RighteousFury, flags: AbilityFlags.SelfTargetOnly) },

            // Soldier Desperate (Marksmanship) - 사격술 연마 (필사적인 수단)
            { "1ac5c22b5f0c47b380b7205696e8409e", new AbilityInfo("1ac5c22b5f0c47b380b7205696e8409e", "Marksmanship_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.SelfTargetOnly) },

            // Soldier Ultimate (Marksmanship) - 사격술 연마 (영웅적 행위)
            { "446277008fe14da194116dc74d804a13", new AbilityInfo("446277008fe14da194116dc74d804a13", "Marksmanship_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.SelfTargetOnly) },

            // Controlled Burst - 제어 점사 (무기 공격)
            { "60c599a47ab74425a6bfeb53828db0b0", new AbilityInfo("60c599a47ab74425a6bfeb53828db0b0", "ControlledBurst", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget | AbilityFlags.IsWeaponAttack) },

            // Soldier Dash - 질주 (이동 스킬)
            { "9b09708aa0c244a6bc4a8e46d69c5884", new AbilityInfo("9b09708aa0c244a6bc4a8e46d69c5884", "SoldierDash", AbilityTiming.GapCloser, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // ★ v3.0.37: Vanguard 스킬
            // ========================================

            // Fight Me (Forced Distraction) - 강제 교란 (아군 보호)
            { "dac2896f1b4249648300531819f4d31d", new AbilityInfo("dac2896f1b4249648300531819f4d31d", "FightMe", AbilityTiming.Taunt, flags: AbilityFlags.AllyTarget) },

            // Follow My Lead - 내 뒤를 따라와 (자기 버프)
            { "6d2666949acd442bac7a4ab914af0b1f", new AbilityInfo("6d2666949acd442bac7a4ab914af0b1f", "FollowMyLead", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Vanguard Desperate (Unyielding Guardian) - 불굴의 수호병 (필사적인 수단)
            { "7ae3192702484df78a4c60498c692972", new AbilityInfo("7ae3192702484df78a4c60498c692972", "UnyieldingGuardian_Desperate", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.SelfTargetOnly) },

            // Vanguard Ultimate (Unyielding Guardian) - 불굴의 수호병 (영웅적 행위)
            { "a356b5f227ac466798f1970b6b841a67", new AbilityInfo("a356b5f227ac466798f1970b6b841a67", "UnyieldingGuardian_Heroic", AbilityTiming.HeroicAct, flags: AbilityFlags.SingleUse | AbilityFlags.SelfTargetOnly) },

            // Wall of Stone (Rockcrete Wall) - 록크리트 벽 (방어 버프)
            { "1743ffd4b1ea4410846d3dae710e7479", new AbilityInfo("1743ffd4b1ea4410846d3dae710e7479", "WallOfStone", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Follow My Lead Move - 따라붙기! (아군 이동)
            { "a39d7daf9b884978a66ef00675b2766f", new AbilityInfo("a39d7daf9b884978a66ef00675b2766f", "FollowMyLead_Move", AbilityTiming.GapCloser, flags: AbilityFlags.AllyTarget) },

            // ========================================
            // ★ v3.0.37: Fighter 스킬
            // ========================================

            // Slice - 베기 (근접 공격)
            { "f6d693dbff0d44eb9590684241e63ef3", new AbilityInfo("f6d693dbff0d44eb9590684241e63ef3", "Slice", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget | AbilityFlags.IsWeaponAttack) },

            // Reckless Strike - 무모한 일격 (다음 공격 데미지 증가)
            { "ad2a77c5012c4ffd9a19204dcf794266", new AbilityInfo("ad2a77c5012c4ffd9a19204dcf794266", "RecklessStrike", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // ★ v3.0.37: Veteran (Arch-Militant) 추가 스킬
            // ========================================

            // ★ v3.0.95: Reckless Rush - 무모한 돌진 (MP 회복 버프)
            // Personal 타겟, 즉시 MP +(3 + AGI 보너스) 획득, Versatility 스택 +3
            // GapCloser가 아님! 런 앤 건처럼 PostFirstAction으로 처리
            // ★ v3.0.98: MP 회복량은 CombatAPI.GetAbilityMPRecovery()가 Blueprint에서 자동 감지
            { "801926b855d64391b465b6d75796a19a", new AbilityInfo("801926b855d64391b465b6d75796a19a", "RecklessRush", AbilityTiming.PostFirstAction, flags: AbilityFlags.SelfTargetOnly) },

            // Battle Fury - 전투 광란 (자기 버프)
            { "3f268aa2ef7e4e5d9ce59f3684720265", new AbilityInfo("3f268aa2ef7e4e5d9ce59f3684720265", "BattleFury", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Combat Respite - 전투 휴식 (자가 회복)
            { "97169b556af54603aead9ae5c2987886", new AbilityInfo("97169b556af54603aead9ae5c2987886", "CombatRespite", AbilityTiming.Healing, flags: AbilityFlags.SelfTargetOnly) },

            // Distracting Shots - 교란 사격 (적 디버프)
            { "ee90113ce57a49fa91e1e68b32038ecf", new AbilityInfo("ee90113ce57a49fa91e1e68b32038ecf", "DistractingShots", AbilityTiming.PreAttackBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Hit And Run - 히트 앤 런 (첫 행동 후)
            { "62a871b645a043c2ac382e3aacaa1bd1", new AbilityInfo("62a871b645a043c2ac382e3aacaa1bd1", "HitAndRun", AbilityTiming.PostFirstAction, flags: AbilityFlags.SelfTargetOnly) },

            // Veteran Overwatch - 감시 사격 (턴 종료)
            { "4e735a40e44d44d38b2429e3398420f4", new AbilityInfo("4e735a40e44d44d38b2429e3398420f4", "VeteranOverwatch", AbilityTiming.TurnEnding, flags: AbilityFlags.SelfTargetOnly) },

            // Ready Go (Trench Building) - 참호 구축 (방어 버프)
            { "c4c290fbbffe4c308b6c83de1f5b0bc8", new AbilityInfo("c4c290fbbffe4c308b6c83de1f5b0bc8", "TrenchBuilding", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // ========================================
            // ★ v3.0.38: Tech-Priest/Explorator 스킬
            // ========================================

            // Cognitive Optimisation - 인지 최적화 (자기 버프)
            { "b1cf4ee797fd4a468bf5ace36624a185", new AbilityInfo("b1cf4ee797fd4a468bf5ace36624a185", "CognitiveOptimisation", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Mechadendrite Push - 촉수 강타 (근접 공격)
            { "95c75fde7c314366b771bcd0b116feee", new AbilityInfo("95c75fde7c314366b771bcd0b116feee", "MechadendritePush", AbilityTiming.Normal, flags: AbilityFlags.EnemyTarget) },

            // Machine Repair - 수리 프로토콜 (아군 회복)
            { "79102af99dfc48598e6f481da707e815", new AbilityInfo("79102af99dfc48598e6f481da707e815", "MachineRepair", AbilityTiming.Healing, flags: AbilityFlags.AllyTarget) },

            // Mechadendrite Medicae - 메디카 기계 촉수 (아군/자기 회복)
            { "3a3b073676b24e7ead880363fef1bbaf", new AbilityInfo("3a3b073676b24e7ead880363fef1bbaf", "MechadendriteMedicae", AbilityTiming.Healing, flags: AbilityFlags.AllyTarget) },

            // Humility Protocol - 겸손 프로토콜 (디버프 해제)
            { "8d222d9e5e99413ba82137f943130d11", new AbilityInfo("8d222d9e5e99413ba82137f943130d11", "HumilityProtocol", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Machine Spirit Absolute - 절대적인 기계령 (자기 버프)
            { "dcf8b79fbea545df84696f07d5b8d354", new AbilityInfo("dcf8b79fbea545df84696f07d5b8d354", "MachineSpiritAbsolute", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Machine Spirit Communion - 기계령 교감 (적 디버프)
            { "95cdd5f42ef5480d9fadd9e0acee641b", new AbilityInfo("95cdd5f42ef5480d9fadd9e0acee641b", "MachineSpiritCommunion", AbilityTiming.Debuff, flags: AbilityFlags.EnemyTarget) },

            // ========================================
            // ★ v3.0.38: Servoskull 펫 명령 스킬 (Overseer)
            // ========================================

            // ★ v3.0.43: All Cornered (Comprehensive Analysis) - 자기 타겟 버프 (Debuff → PreCombatBuff)
            // 서보스컬에 적용되는 분석 버프, 적에게 사용하는 디버프가 아님
            { "bbb1398ebbbe41c6b6224e1057dcde46", new AbilityInfo("bbb1398ebbbe41c6b6224e1057dcde46", "AllCornered", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Expand (Extrapolation) - 외삽 (범위 확장)
            { "d68b6efac32b4db7afaf7de694eab819", new AbilityInfo("d68b6efac32b4db7afaf7de694eab819", "Expand", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Priority Signal - 우선 신호 (공격 버프)
            { "33aa1b047d084a9b8faf534767a3a534", new AbilityInfo("33aa1b047d084a9b8faf534767a3a534", "PrioritySignal", AbilityTiming.PreCombatBuff, flags: AbilityFlags.SelfTargetOnly) },

            // Vitality Signal (Medica Signal) - 메디카 신호 (범위 회복)
            { "62eeb81743734fc5b8fac71b34b14683", new AbilityInfo("62eeb81743734fc5b8fac71b34b14683", "VitalitySignal", AbilityTiming.Healing, flags: AbilityFlags.SelfTargetOnly) },

            // Redirect (Reposition) - 재배치 (펫 이동)
            { "5376c2d18af1499db985fbde6d5fe1ce", new AbilityInfo("5376c2d18af1499db985fbde6d5fe1ce", "Redirect", AbilityTiming.GapCloser, flags: AbilityFlags.PointTarget) },
        };

        #endregion

        #region Core Methods

        /// <summary>
        /// 능력의 GUID 추출
        /// </summary>
        public static string GetGuid(AbilityData ability)
        {
            try
            {
                return ability?.Blueprint?.AssetGuid?.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// GUID로 능력 정보 조회
        /// </summary>
        public static AbilityInfo GetInfo(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            return Database.TryGetValue(guid, out var info) ? info : null;
        }

        /// <summary>
        /// 능력 데이터로 정보 조회
        /// </summary>
        public static AbilityInfo GetInfo(AbilityData ability)
        {
            string guid = GetGuid(ability);
            return GetInfo(guid);
        }

        /// <summary>
        /// 능력의 타이밍 조회 (미등록 시 자동 감지)
        /// </summary>
        public static AbilityTiming GetTiming(AbilityData ability)
        {
            var info = GetInfo(ability);
            if (info != null) return info.Timing;

            // 미등록 스킬: 자동 감지
            return AutoDetectTiming(ability);
        }

        /// <summary>
        /// 스킬 속성 기반 자동 타이밍 감지
        /// ★ v3.0.20: 아군 버프 감지 로직 추가
        /// ★ v3.0.21: 위치 타겟 버프 감지 추가
        /// ★ v3.1.12: 게임 API 기반 자동 감지 강화
        ///   - EffectOnAlly/EffectOnEnemy 활용
        ///   - IsHeroicAct/IsMomentum 활용
        ///   - AbilityEffectRunAction 컴포넌트 분석
        /// </summary>
        private static AbilityTiming AutoDetectTiming(AbilityData ability)
        {
            try
            {
                var bp = ability?.Blueprint;
                if (bp == null) return AbilityTiming.Normal;

                // ═══════════════════════════════════════════════════════════════
                // 1단계: 게임 내장 속성 활용 (가장 정확)
                // ═══════════════════════════════════════════════════════════════

                // ★ v3.1.12: 영웅적 행동 (게임이 직접 판단)
                if (bp.IsHeroicAct)
                    return AbilityTiming.HeroicAct;

                // ★ v3.1.12: 필사적 조치 (게임이 직접 판단)
                if (bp.IsDesperateMeasure)
                    return AbilityTiming.DesperateMeasure;

                // ★ v3.1.12: 돌진 능력 (게임이 직접 판단)
                if (bp.IsCharge)
                    return AbilityTiming.GapCloser;

                // ★ v3.1.12: 이동 능력 (게임이 직접 판단)
                if (bp.IsMoveUnit)
                    return AbilityTiming.GapCloser;

                // ★ v3.1.12: 전략 능력 (게임이 직접 판단)
                if (bp.IsStratagem)
                    return AbilityTiming.Stratagem;

                // ═══════════════════════════════════════════════════════════════
                // 2단계: AbilityEffectRunAction 컴포넌트 분석
                // ═══════════════════════════════════════════════════════════════

                var runAction = bp.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions != null)
                {
                    var actions = runAction.Actions.Actions;

                    // ★ v3.1.12: 보너스 능력 사용 추가 → PostFirstAction (런 앤 건 등)
                    if (HasActionOfType<ContextActionAddBonusAbilityUsage>(actions))
                        return AbilityTiming.PostFirstAction;

                    // ★ v3.1.12: AP/MP 회복 → PostFirstAction (무모한 돌진 등)
                    if (HasActionOfType<WarhammerContextActionRestoreActionPoints>(actions))
                        return AbilityTiming.PostFirstAction;

                    // ★ v3.1.12: 힐링 액션 존재 → Healing
                    if (HasActionOfType<ContextActionHealTarget>(actions))
                        return AbilityTiming.Healing;
                }

                // ═══════════════════════════════════════════════════════════════
                // 3단계: EffectOnAlly/EffectOnEnemy 활용
                // ═══════════════════════════════════════════════════════════════

                var effectOnAlly = bp.EffectOnAlly;
                var effectOnEnemy = bp.EffectOnEnemy;
                bool canTargetSelf = bp.CanTargetSelf;
                bool canTargetEnemies = bp.CanTargetEnemies;
                bool canTargetFriends = bp.CanTargetFriends;
                bool canTargetPoint = bp.CanTargetPoint;
                bool hasWeapon = ability.Weapon != null;
                bool notOffensive = bp.NotOffensive;
                string range = bp.Range.ToString();
                string bpName = bp.name?.ToLower() ?? "";

                // ★ v3.1.12: 적에게 해롭고 아군에게 해로운 AoE → DangerousAoE
                if (effectOnEnemy == AbilityEffectOnUnit.Harmful &&
                    effectOnAlly == AbilityEffectOnUnit.Harmful && !hasWeapon)
                    return AbilityTiming.DangerousAoE;

                // ★ v3.1.12: 비공격 + 적 타겟 + 적에게 해로움 → Debuff
                if (notOffensive && canTargetEnemies && effectOnEnemy == AbilityEffectOnUnit.Harmful)
                    return AbilityTiming.Debuff;

                // ★ v3.1.12: 아군에게 이로움 + 적 타겟 불가 → 버프/힐
                if (effectOnAlly == AbilityEffectOnUnit.Helpful && !canTargetEnemies && !hasWeapon)
                {
                    // 힐링 키워드 체크
                    if (bpName.Contains("heal") || bpName.Contains("medikit") || bpName.Contains("mend") ||
                        bpName.Contains("restore") || bpName.Contains("revive"))
                        return AbilityTiming.Healing;

                    // 자기만 타겟 가능하면 PreAttackBuff
                    if (canTargetSelf && !canTargetFriends)
                        return AbilityTiming.PreAttackBuff;

                    // 아군 타겟 가능하면 PreCombatBuff
                    return AbilityTiming.PreCombatBuff;
                }

                // ═══════════════════════════════════════════════════════════════
                // 4단계: 기존 휴리스틱 (폴백)
                // ═══════════════════════════════════════════════════════════════

                // 구역 강화 스킬 (Stratagem)
                if (bpName.Contains("stratagem"))
                    return AbilityTiming.Stratagem;

                // 위치 타겟 버프 (전방/보조/후방 구역 등)
                if (canTargetPoint && !canTargetEnemies && !canTargetFriends && !hasWeapon)
                {
                    if (bpName.Contains("frontline") || bpName.Contains("backline") || bpName.Contains("rear") ||
                        bpName.Contains("keystone"))
                        return AbilityTiming.PositionalBuff;

                    if (range == "Unlimited")
                        return AbilityTiming.PositionalBuff;
                }

                // 위험한 AoE (적과 아군 모두 타겟 가능, 무기 공격 제외)
                if (canTargetEnemies && canTargetFriends && !canTargetSelf && !hasWeapon)
                    return AbilityTiming.DangerousAoE;

                // 아군 버프 감지
                if (canTargetFriends && !canTargetEnemies && !hasWeapon)
                {
                    if (bpName.Contains("heal") || bpName.Contains("medikit") || bpName.Contains("mend") ||
                        bpName.Contains("restore") || bpName.Contains("revive"))
                        return AbilityTiming.Healing;

                    if (bpName.Contains("overwatch") || bpName.Contains("guard") || bpName.Contains("defend"))
                        return AbilityTiming.TurnEnding;

                    return AbilityTiming.PreCombatBuff;
                }

                // Personal 자기 버프
                if (range == "Personal" && canTargetSelf && !canTargetEnemies && !hasWeapon)
                {
                    if (bpName.Contains("veil") || bpName.Contains("stance") ||
                        bpName.Contains("defend") || bpName.Contains("guard"))
                        return AbilityTiming.TurnEnding;

                    return AbilityTiming.PreAttackBuff;
                }

                // 자해 스킬 감지
                if (bpName.Contains("blood") || bpName.Contains("oath") ||
                    bpName.Contains("sacrifice") || bpName.Contains("wound"))
                {
                    if (canTargetSelf || range == "Personal")
                        return AbilityTiming.SelfDamage;
                }

                // 마무리 스킬 감지
                if (bpName.Contains("dispatch") || bpName.Contains("execute") ||
                    bpName.Contains("finish") || bpName.Contains("deathblow"))
                    return AbilityTiming.Finisher;

                // 재장전 감지
                if (bpName.Contains("reload"))
                    return AbilityTiming.Reload;

                // 힐링 감지
                if (bpName.Contains("heal") || bpName.Contains("medikit") || bpName.Contains("mend"))
                    return AbilityTiming.Healing;

                return AbilityTiming.Normal;
            }
            catch
            {
                return AbilityTiming.Normal;
            }
        }

        /// <summary>
        /// ★ v3.1.12: 액션 배열에서 특정 타입의 액션 존재 여부 확인
        /// </summary>
        private static bool HasActionOfType<T>(GameAction[] actions) where T : GameAction
        {
            if (actions == null) return false;
            return actions.Any(a => a is T);
        }

        #endregion

        #region Category Check Methods

        public static bool IsRunAndGun(AbilityData ability)
        {
            var info = GetInfo(ability);
            if (info == null) return false;
            return info.Timing == AbilityTiming.PostFirstAction && info.HPThreshold == 0f;
        }

        public static bool IsPostFirstAction(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.PostFirstAction;
        }

        public static bool IsReload(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.Reload;
        }

        public static bool IsTaunt(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.Taunt;
        }

        public static bool IsFinisher(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.Finisher;
        }

        public static bool IsHeroicAct(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.HeroicAct;
        }

        public static bool IsHealing(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.Healing;
        }

        public static bool IsSelfDamage(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.SelfDamage;
        }

        public static bool IsDangerousAoE(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.DangerousAoE;
        }

        public static bool IsDebuff(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.Debuff;
        }

        public static bool IsGapCloser(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.GapCloser;
        }

        public static bool IsTurnEnding(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.TurnEnding;
        }

        /// <summary>
        /// ★ v3.5.22: SpringAttack 능력인지 확인 (Acrobatic Artistry)
        /// CustomSpringAttackQueue 컴포넌트를 가진 능력
        /// - 갭클로저 사용 후 시작 위치로 복귀하는 능력
        /// </summary>
        public static bool IsSpringAttackAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // GUID로 직접 확인 (Acrobatic Artistry)
                string guid = ability?.Blueprint?.AssetGuid?.ToString() ?? "";
                if (guid == "1798e1237504457db15655280481d549")  // AcrobaticArtistry
                    return true;

                // 컴포넌트 기반 확인
                var component = ability.Blueprint?.GetComponent<Kingmaker.UnitLogic.Abilities.Components.CustomSpringAttackQueue>();
                return component != null;
            }
            catch { return false; }
        }

        public static bool IsRighteousFury(AbilityData ability)
        {
            if (GetTiming(ability) == AbilityTiming.RighteousFury)
                return true;

            // 이름 기반 폴백
            string bpName = ability?.Blueprint?.name?.ToLower() ?? "";
            return bpName.Contains("revelinslaughter") || bpName.Contains("righteousfury");
        }

        public static bool IsDOTIntensify(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.DOTIntensify;
        }

        public static bool IsChainEffect(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.ChainEffect;
        }

        /// <summary>
        /// ★ v3.0.28: 마킹 스킬 여부 (데미지 없이 적을 표시만 함)
        /// Hunt Down the Prey, Hot on the Trail 등
        /// </summary>
        public static bool IsMarker(AbilityData ability)
        {
            return GetTiming(ability) == AbilityTiming.Marker;
        }

        public static bool IsMedikit(AbilityData ability)
        {
            var info = GetInfo(ability);
            if (info != null && info.Timing == AbilityTiming.Healing &&
                (info.Flags & AbilityFlags.IsConsumable) != 0)
            {
                return true;
            }

            string name = ability?.Blueprint?.name?.ToLower() ?? "";
            return name.Contains("medikit");
        }

        public static float GetHPThreshold(AbilityData ability)
        {
            return GetInfo(ability)?.HPThreshold ?? 0f;
        }

        public static float GetTargetHPThreshold(AbilityData ability)
        {
            return GetInfo(ability)?.TargetHPThreshold ?? 30f;
        }

        public static bool IsSingleUse(AbilityData ability)
        {
            return GetInfo(ability)?.IsSingleUse ?? false;
        }

        /// <summary>
        /// ★ v3.0.44: AoE 능력 여부
        /// </summary>
        public static bool IsAoE(AbilityData ability)
        {
            var info = GetInfo(ability);
            if (info != null && (info.Flags & AbilityFlags.IsAoE) != 0)
                return true;

            // DangerousAoE 타이밍도 AoE
            if (GetTiming(ability) == AbilityTiming.DangerousAoE)
                return true;

            // Blueprint 체크
            try
            {
                var blueprint = ability?.Blueprint;
                if (blueprint == null) return false;

                // AoE 관련 컴포넌트 확인
                float radius = blueprint.AoERadius;
                if (radius > 0) return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// ★ v3.0.45: 사이킥 능력 여부 (게임 API 우선 체크)
        /// </summary>
        public static bool IsPsychic(AbilityData ability)
        {
            // 1. 데이터베이스 체크 (가장 정확)
            var info = GetInfo(ability);
            if (info != null && (info.Flags & AbilityFlags.IsPsychic) != 0)
                return true;

            // 2. ★ v3.0.45: Blueprint.AbilityParamsSource 체크 (게임 내장 판단)
            try
            {
                if (ability?.Blueprint?.AbilityParamsSource == WarhammerAbilityParamsSource.PsychicPower)
                    return true;
            }
            catch { }

            // 3. 이름 기반 폴백 (미등록 스킬용)
            try
            {
                string bpName = ability?.Blueprint?.name?.ToLower() ?? "";
                if (bpName.Contains("psychic") || bpName.Contains("psyker") ||
                    bpName.Contains("warp") || bpName.Contains("telekinetic") ||
                    bpName.Contains("telepathic") || bpName.Contains("biomancy") ||
                    bpName.Contains("pyromancy") || bpName.Contains("divination") ||
                    bpName.Contains("sanctic"))
                    return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// ★ v3.0.98: MP 회복 능력 여부 - CombatAPI에서 Blueprint 직접 검사
        /// GUID 하드코딩 대신 게임 데이터에서 직접 읽어옴
        /// </summary>
        public static bool IsMPRecovery(AbilityData ability)
        {
            return GameInterface.CombatAPI.GetAbilityMPRecovery(ability) > 0;
        }

        /// <summary>
        /// ★ v3.0.98: MP 회복량 - CombatAPI에서 Blueprint 직접 검사
        /// </summary>
        public static float GetExpectedMPRecovery(AbilityData ability)
        {
            return GameInterface.CombatAPI.GetAbilityMPRecovery(ability);
        }

        /// <summary>
        /// ★ v3.0.98: AP 회복 능력 여부
        /// </summary>
        public static bool IsAPRecovery(AbilityData ability)
        {
            return GameInterface.CombatAPI.GetAbilityAPRecovery(ability) > 0;
        }

        /// <summary>
        /// ★ v3.0.98: AP 회복량
        /// </summary>
        public static float GetExpectedAPRecovery(AbilityData ability)
        {
            return GameInterface.CombatAPI.GetAbilityAPRecovery(ability);
        }

        #endregion

        #region Registration

        public static void RegisterAbility(AbilityInfo info)
        {
            if (string.IsNullOrEmpty(info?.Guid)) return;
            Database[info.Guid] = info;
            Main.LogDebug($"[AbilityDB] Registered: {info.Name} ({info.Guid}) -> {info.Timing}");
        }

        public static int Count => Database.Count;

        #endregion
    }
}
