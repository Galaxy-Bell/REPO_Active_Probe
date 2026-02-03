# REPO_Active_Probe v0.4.0

## Keys
- F1: MARK line into log
- F2: Force invoke nearest ExtractionPoint.ButtonPress()
- F3: Force invoke nearest ExtractionPoint.OnClick() (closest to native click path)
- F4: Toggle trace mode (when ON, F3 opens a short trace window)

## What changed from 0.3.6
- Removed AudioListener compile dependency (no more UnityEngine.AudioModule.dll reference issues).
- More robust reflection invoke: supports methods that have parameters (build default args).
- Cache and rescan extraction points (lightweight), rescan after scene load.
- Trace patch is stricter and capped to reduce load impact.

## Logs
r2modman profile:
BepInEx\config\REPO_Active_Probe\logs\REPO_Active_Probe_*.log
