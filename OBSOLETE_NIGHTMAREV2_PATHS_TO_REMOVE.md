# Obsolete NightmareV2 Paths To Remove From Git

This direct overlay contains the replacement `ArgusEngine.*` files.

A standard zip extraction can add or overwrite files, but it cannot delete files
that are already tracked by Git. Run this root-level PowerShell script after
extracting the overlay:

```powershell
.\Delete-OldNightmareV2Files.ps1 -WhatIf
.\Delete-OldNightmareV2Files.ps1 -Force
```

The script deletes:

- `NightmareV2.sln`
- `NightmareV2.slnx`
- `src/NightmareV2.*`
- `src/tests/NightmareV2.*.Tests`

No database tables or database names are renamed by this change. The local
databases `nightmare_v2` and `nightmare_v2_files` are intentionally left
unchanged for backward compatibility.
