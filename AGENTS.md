# rusty-bridge

Runs old games as live simulation sources for Rusty Engine products and hands authority over to Rusty channel by channel. It is one repository for every donor game. Dark Messiah of Might and Magic is the only target today, so don't build a generic multi-donor framework before a second donor needs one ([docs/design.md](docs/design.md), "Scope").

Den guidance: run `den-tool den get_agent_guidance --project-id _global` and read `_global/den-operating-contract` before task work. Den project: `rusty-bridge`. Tasks, status and decisions live there, not in Markdown files here.

Repository rules:
- Donor content, recordings and anything extracted from the game stay in the gitignored `local/`. Never commit them.
- Keep the Wire layout in step on both sides: `tap/darkmessiah/src/wire.h` and `wire/RustyBridge.Wire/WireLayout.cs`. Bump the version when it changes, and keep readers able to load older recordings.
- Tap hooks patch fixed addresses in one `server.dll`. Each one checks its bytes before patching and must stay off when they differ. Record how an address was found in `docs/wire.md`.
- What the importer can't read yet goes in the Den document `rusty-bridge/known-limitations`, not in Markdown here.
- Downstream Rusty Engine products are C#. Rust belongs to the engine; ask for missing engine capabilities as rusty-engine tasks.
- The game runs on den-win11; see the `windows-box` skill. Never RDP into it.
- Before review: `dotnet test wire/RustyBridge.Wire.slnx` and `dotnet test import/DarkMessiah.Import.slnx` (its donor test needs `local/donor/vpks`; see `docs/import.md`), `dotnet run --project mirror/tests/DarkMessiah.Mirror.Smoke` (its product run needs `local/import`; see `docs/mirror.md`), and a tap build through `tap/darkmessiah/build.ps1` on den-win11 when the tap changes.
