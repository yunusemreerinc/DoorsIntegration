# WPF application icon

The active WPF project owns its icon assets. `doors-integration.ico` and its PNG master were copied byte-for-byte from the previous WinForms assets when that application was archived. The ICO remains unchanged and is compiled through `ApplicationIcon` in the WPF project.

`scripts/Convert-AppIcon.ps1` now uses these WPF paths by default. The WPF application has no icon or source dependency on `archive/WinForms`.
