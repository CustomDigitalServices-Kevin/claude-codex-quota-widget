# Privacy and local data

The repository contains source code and synthetic examples, not a developer's provider profile or usage history.

The widget reads quota observations and selected session metadata from the current user's local profiles. It stores local snapshots, appearance settings and diagnostic logs in `%LOCALAPPDATA%\QuotaWidget`. Provider authentication stays with the official provider tools. The widget does not copy shared `auth.json` files and has no project-operated telemetry server.

Optional direct Codex reads invoke the official local Codex executable using the widget's isolated profile. That executable can communicate with its provider. No API key is requested by this project.

The Claude status line writes only allowlisted quota and metadata fields to the current user's local state directory. It never persists arbitrary input fields. The optional transcript fallback reads bounded local JSONL segments to select metadata; it does not return message, prompt or tool bodies. Local titles, model choices and paths can still be private. Do not upload your state directory or snapshots to an issue or repository.

Session IDs are required internally to match records. They are hidden from the detail UI, along with paths and technical origins. Account identifiers used for aggregation are fingerprinted. Diagnostic logs use fixed event names and do not intentionally record source content.

No cross-user account is bundled. Sign in personally, keep backups local, and share only synthetic reproductions. Uninstall preserves data by default; review removal options before deleting a provider profile.

The Windows installer configures Claude only when its optional checkbox is selected. The native configuration helper stores integration ownership in `%LOCALAPPDATA%\QuotaWidget\claude-integration.json` and backs up existing Claude settings locally. Removal restores only its own status line when the current command still matches; all unrelated settings and later changes are retained. `--packaging-smoke` uses generated synthetic data in an explicitly chosen empty directory and skips normal user-profile discovery.

