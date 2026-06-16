# Changelog  
All notable changes to this project will be documented in this file.  
  
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),  
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).  
  
## [2.1.0] - 2026-06-16
### Changed
- Replaced the bundled Bun runtime with the editor's own MonoBleedingEdge: the MCP server is now a
  small C# program compiled by Unity's `mcs` and run under Unity's `mono`. No third-party runtime is
  shipped.
- The server reconnects to the editor across domain reloads (including reloads triggered during test
  runs) by polling the internal API until the listener is rebuilt.
- The editor's internal API now binds a fresh ephemeral port each domain (published to
  `Library/UniSlop/unity-api-port.txt`) instead of a fixed `:5108`, and the server re-reads it before
  every call. Connections are accepted asynchronously and each request is handled on its own thread.
### Fixed
- Editor no longer wedges after a stop/start plus a compile while unfocused. A leaked old-domain
  listener could squat on the fixed `:5108`, so the new domain never rebound and every tool call hung
  (status polls starved the shared ThreadPool). Dynamic ports, async accept, and dedicated handler
  threads remove the leak and the starvation.
### Removed
- Bun binaries (`Editor/Bun`), the TypeScript server, and its `node_modules`.
  
## [2.0.0] - 2026-06-15
### Changed
- Detached MCP server from native C# to Bun to survive domain reloads.  
- Gave more descriptive names to the toolset: unity_compile, unity_list_tests, unity_run_tests
  
## [1.0.0] - 2026-06-14
### Added
- Initial release.  
  
[2.1.0] https://github.com/forcepusher/com.bananaparty.webutility/compare/2.0.0...2.1.0  
[2.0.0] https://github.com/forcepusher/com.bananaparty.webutility/compare/1.0.0...2.0.0  
