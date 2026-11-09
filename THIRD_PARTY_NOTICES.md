# Dependencies

- CounterStrikeSharp.API 1.0.378, https://github.com/roflmuffin/CounterStrikeSharp, GPL-3.0 with an exception permitting plugins and software referencing its published .NET packages to use MIT: https://github.com/roflmuffin/CounterStrikeSharp/blob/v1.0.378/LICENSE . NadeLab's current authored code uses GPL-3.0-only; it does not rely on the MIT exception to impose additional restrictions. Referenced via NuGet; the API/native runtime is supplied by the training server and is not bundled in either release ZIP.
- Microsoft .NET 10 SDK/runtime, https://dotnet.microsoft.com/, project-local development dependency. Not bundled in the plugin ZIP.
- Metamod:Source 2.0.0-git1473 is a required server dependency, https://www.metamodsource.net/. Not bundled in either release ZIP.

The Windows launcher downloads the pinned Windows x64 MetaMod and CounterStrikeSharp-with-runtime archives from their official upstreams. Exact URLs and SHA256 hashes are recorded in `scripts/release/dependencies.lock.json` in the Windows package. The downloaded archives retain their upstream notices and runtime files. Updates are explicit; the launcher does not silently replace pinned dependencies with a latest build.

CS2 and its assets belong to Valve. No CS2 executable, game VPK, character model, map, or account configuration is distributed. Installation prepares a private training instance using the user's own installed game. The three distributed compiled panel resources are built from NadeLab's own UI sources.

Local vendor folders are development-only and excluded from the source repository. No Ray-Trace or CounterStrikeSharp implementation source is incorporated in NadeLab's authored source or release ZIP. Native tracing uses the public CounterStrikeSharp API.

Third-party dependencies retain their own licenses. The full GPL-3.0 license text in `LICENSE` is reproduced unmodified from the upstream `LICENSE.GPL3`; its Free Software Foundation notice is retained. See `docs/license-history.md` for the current project license and treatment of earlier MIT-distributed copies.
