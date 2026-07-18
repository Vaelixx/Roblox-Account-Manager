## What's new in v1.3.3

### New
- Smoother UI everywhere: animated page transitions plus soft hover/press fades on cards, buttons and list items
- Buttons polished: subtle scale/opacity feedback and animated icons (hover wiggle, press shrink)
- Presence detection is much faster: shorter poll cadence and an instant refresh right after account actions

### Fixes
- Card glow toned down: smaller reach, no more bleeding deep into card content
- "Update available" state now shows reliably after background update checks

### Under the hood
- Dropped the entire WinForms framework from the bundle — tray icon and global hotkeys now run on native Win32 (Shell_NotifyIcon / RegisterHotKey via HwndSource)
- Single-file exe shrunk from 63.2 MB to 55.9 MB with zero feature loss
- Clean build: 0 warnings, 0 errors

**Full Changelog**: https://github.com/Vaelixx/Roblox-Account-Manager/compare/v1.3.2...v1.3.3
