# FTPFlow

FTPFlow is a Traditional Chinese WPF FTP file manager for Windows, built with .NET Framework 4.7.2 and C# 5.

See [使用說明.md](使用說明.md) and [API.md](API.md) for account management, the authenticated loopback control API, local DPAPI-protected settings, and build instructions.

`%LOCALAPPDATA%\FTPFlow\settings.xml` is user-specific and must never be committed. `original/` contains the legacy Python program and must never be committed because it includes private connection data. Compiled executables and local verification output stay out of Git as well.

