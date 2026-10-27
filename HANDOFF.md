# Git handoff

1. Build on Windows with .NET Framework 4.7.2 or later by running `Build.cmd`.
2. FTP profiles and the AI control API token stay under the current user's Local AppData and are encrypted with Windows DPAPI.
3. Do not commit `settings.xml`, `original/`, executables, logs, or verification screenshots.
4. The control API exposes only a loopback listener and requires its locally generated bearer token.
