# CompanionAI v3

Warhammer 40,000: Rogue Trader - Companion AI Mod (Complete Rewrite)

## Overview

CompanionAI v3 is a complete AI replacement mod for companion characters in Warhammer 40K: Rogue Trader. It provides intelligent, role-based tactical decision making for your party members during turn-based combat.

## Features

- **Role-Based Strategies**: DPS, Tank, Support, Balanced - each with specialized behavior
- **Smart Positioning**: PathfindingService-based tile scoring for optimal combat positions
- **Range Preference**: Configure characters for ranged or melee combat
- **GapCloser Support**: Automatic use of charge/teleport abilities to close distance
- **Safe Retreat**: Ranged characters maintain safe distance from enemies
- **Ability Optimization**: Intelligent buff/debuff/attack sequencing

## Installation

1. Install [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)
2. Download the latest release ZIP from [Releases](https://github.com/seyelive5/CompanionAI_v3/releases)
3. Drag and drop the ZIP file into Unity Mod Manager
4. Enable the mod and start the game

## Configuration

In-game settings accessible via Unity Mod Manager:

| Setting | Options | Description |
|---------|---------|-------------|
| AI Enabled | On/Off | Enable/disable AI for each character |
| Role | DPS, Tank, Support, Balanced | Combat role strategy |
| Range Preference | Prefer Ranged, Prefer Melee, Auto | Weapon preference |

## Architecture

```
CompanionAI_v3/
├── Analysis/           # Situation analysis and scoring
│   ├── Situation.cs
│   ├── SituationAnalyzer.cs
│   ├── SequenceOptimizer.cs
│   └── UtilityScorer.cs
├── Core/               # Core AI logic
│   ├── TurnOrchestrator.cs
│   ├── TurnPlan.cs
│   ├── PlannedAction.cs
│   └── AbilityUsageTracker.cs
├── Data/               # Ability database
│   ├── AbilityDatabase.cs
│   └── SpecialAbilityHandler.cs
├── Execution/          # Action execution
│   └── ActionExecutor.cs
├── GameInterface/      # Game API integration
│   ├── MainAIPatch.cs
│   ├── CombatAPI.cs
│   └── MovementAPI.cs
├── Planning/           # Turn planning
│   ├── TurnPlanner.cs
│   ├── Plans/          # Role-specific plans
│   │   ├── DPSPlan.cs
│   │   ├── TankPlan.cs
│   │   ├── SupportPlan.cs
│   │   └── BalancedPlan.cs
│   └── Planners/       # Action planners
│       ├── AttackPlanner.cs
│       ├── BuffPlanner.cs
│       ├── HealPlanner.cs
│       └── MovementPlanner.cs
├── Settings/           # Mod settings
└── UI/                 # Mod UI
```

## AI Flow

```
Turn Start
    │
    ▼
┌─────────────────┐
│ SituationAnalyzer │  ← Analyze combat state
└────────┬────────┘
         │
         ▼
┌─────────────────┐
│   TurnPlanner   │  ← Select role-specific plan
└────────┬────────┘
         │
         ▼
┌─────────────────┐
│  Role Plan      │  ← Generate action sequence
│  (DPS/Tank/etc) │
└────────┬────────┘
         │
         ▼
┌─────────────────┐
│  ActionExecutor │  ← Execute planned actions
└────────┬────────┘
         │
         ▼
    Turn End
```

## Role Behaviors

### DPS
- Prioritizes damage output
- Uses Heroic Acts at high momentum
- Targets low HP enemies for kills
- Uses GapClosers aggressively

### Tank
- Defensive stance priority
- Taunts when multiple enemies nearby
- Maintains front line position
- Charges into enemy groups

### Support
- Heals wounded allies first
- Buffs Tank > DPS > Others
- Maintains safe distance
- Uses SequenceOptimizer for attack decisions

### Balanced
- Adapts to situation
- Heals allies when needed
- Can fill any role as needed

## Requirements

- Warhammer 40,000: Rogue Trader
- Unity Mod Manager 0.23.0+
- .NET Framework 4.8.1

## Changelog

### v3.0.74 (Initial Release)
- Complete rewrite from v2.2
- New Planning-based architecture
- MovementAPI with PathfindingService integration
- Melee character adjacent tile movement
- Ranged character safe positioning

## License

MIT License

## Credits

- Developed with [Claude Code](https://claude.com/claude-code)
