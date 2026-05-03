# Argus Engine Direct File Overlay v2.6.1

This overlay is built for direct extraction into the repository root.

It contains the updated `ArgusEngine.*` source/config/test/doc files directly.
There is no generator or migration script required to create the renamed files.

Because zip extraction cannot delete files that are already tracked in Git, this
overlay also includes a root-level cleanup script:

```powershell
.\Delete-OldNightmareV2Files.ps1 -WhatIf
.\Delete-OldNightmareV2Files.ps1 -Force
```

Deployment version is bumped to `2.6.1` / `2.6.1.0`.
