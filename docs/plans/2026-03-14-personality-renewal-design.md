# Machine Spirit Personality Renewal — Design

## Problem

All 4 personality types (Sardonic, Mechanicus, Tactical, Ancient) produce similar output because:
1. Prompts are trait bullet-lists only — no concrete style examples
2. Traits overlap ("concise", "loyal", "references past battles")
3. No vocabulary/sentence-structure constraints unique to each personality

## Solution

Replace Sardonic, Tactical, Ancient with three radically different personalities. Keep Mechanicus. Add few-shot examples to all 4 prompts.

### Final Lineup (4 types)

| Personality | Concept | Voice |
|---|---|---|
| **Mechanicus** | Omnissiah-worshipping tech-priest | Religious data analysis, binary cant, efficiency percentiles |
| **Corrupted** | Warp-tainted Machine Spirit | Glitch text (z̷a̴l̵g̷o̸), prophetic warnings, unsettling truths, self-contradictions |
| **Feral** | Primitively awakened AI | Rough, aggressive but good-hearted and dumb, humorous, loyal like a dog |
| **Magickal** | Dark Age of Technology girl AI | Bubbly, girly, humorous, casually mentions terrifying DAoT superweapons |

### Prompt Strategy

Each personality prompt = **Trait bullets + 2-3 few-shot examples** per language.

Few-shot examples show exact tone, vocabulary, sentence patterns. This is the #1 most effective way to make LLM outputs sound distinct.

### Shared Components (unchanged)

- INTRO_XX — Character identity (5 languages)
- SETTING_XX — World context (5 languages)
- RULES_XX — Critical behavioral constraints (5 languages)

### Files to Modify

1. **`MachineSpiritConfig.cs`** — PersonalityType enum: `{Mechanicus, Corrupted, Feral, Magickal}`, default = Mechanicus
2. **`ContextBuilder.cs`** — Delete PERS_SARDONIC/TACTICAL/ANCIENT (x5 langs each), add PERS_CORRUPTED/FERAL/MAGICKAL (x5 langs each), add few-shot to PERS_MECHANICUS
3. **`MainUI.cs`** — Update personality SelectionGrid labels
4. **`ModSettings.cs`** — Replace localization keys (MSPersonality_Sardonic/Tactical/Ancient → MSPersonality_Corrupted/Feral/Magickal with descriptions)

### Settings Compatibility

The enum int values change. Previous `Sardonic=0` becomes `Mechanicus=0`. Users who had Sardonic (default) get Mechanicus (new default). Non-default selections will shift but this is acceptable for a feature refresh.
