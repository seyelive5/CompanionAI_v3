# Machine Spirit UX — Provider Presets + Ollama Auto-Setup

## Problem

Machine Spirit requires users to manually install/configure an LLM server. Too high a barrier for typical mod users.

## Solution

1. **Provider preset dropdown** — auto-fills URL/Model on selection
2. **Ollama one-click setup** — detects, starts, and pulls model automatically with progress display

## Provider Presets

| Provider | URL | Default Model | API Key | Cost |
|----------|-----|---------------|---------|------|
| Ollama (default) | `http://localhost:11434/v1` | `llama3` | none | Free (local GPU) |
| Groq | `https://api.groq.com/openai/v1` | `llama-3.3-70b-versatile` | required | Free tier |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` | required | Paid |
| Custom | user input | user input | user input | — |

## Ollama Auto-Setup Flow

```
[Auto Setup] button click:

Step 1: Check ollama installed (where ollama / which ollama)
  → Not installed: show "Install Ollama from ollama.com" message

Step 2: Check ollama serve running (HTTP GET localhost:11434)
  → Not running: start `ollama serve` in background → "Starting server..."

Step 3: Check model exists (ollama list | grep model)
  → Not found: run `ollama pull <model>` → parse stdout for progress
  → "Downloading llama3... 2.3GB/4.7GB (49%)"

Step 4: Complete → "Ready! Press F2 to start chatting"
```

## UI Changes

- Provider dropdown replaces raw URL field (URL shown only for Custom)
- Auto Setup button visible only when Provider = Ollama
- Status text below button shows setup progress (gold color)
- API Key field hidden when Provider = Ollama

## Files

- `MachineSpiritConfig.cs` — add `ApiProvider` enum, update defaults
- `MachineSpirit/OllamaSetup.cs` (new) — Process execution + progress parsing
- `UI/MainUI.cs` — preset dropdown + Auto Setup button + status display
- `Settings/ModSettings.cs` — localization keys
