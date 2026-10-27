# FTPFlow Local Control API v1

The API controls the currently selected FTP account and its active connection. It listens only on `http://127.0.0.1:<port>/api/v1` and requires `Authorization: Bearer <token>` on every request. The token is generated locally and encrypted with Windows DPAPI. Browser-origin requests are rejected.

## Endpoints

- `GET /status` — connection, selected profile, current path, and busy state.
- `GET /files` — current directory entries (`name`, `path`, `type`, `size`, `modified`).
- `POST /navigate` — body: `{"path":"/remote/path"}`.
- `POST /folders` — body: `{"name":"new-folder"}`.
- `POST /upload` — body: `{"localPath":"C:\\\\folder\\\\file.txt","remoteName":"file.txt","overwrite":false}`. `remoteName` and `overwrite` are optional; overwriting an existing regular file requires `overwrite:true`.
- `POST /download` — body: `{"name":"file.txt","destination":"C:\\\\downloads"}`. The name must refer to a file in the current remote directory.
- `POST /delete` — body: `{"name":"file.txt","confirm":true}`. One item in the current remote directory is deleted; `confirm:true` is required.

All JSON bodies are UTF-8 and limited to 1 MB. Operations are serialized with UI transfers. When FTP is disconnected, connection-dependent operations return HTTP 409. Responses use `{"ok":true,"result":...}` or `{"ok":false,"error":"..."}`.

## Example

```powershell
$headers = @{ Authorization = "Bearer <token copied from FTPFlow>" }
Invoke-RestMethod -Uri 'http://127.0.0.1:17821/api/v1/status' -Headers $headers
Invoke-RestMethod -Uri 'http://127.0.0.1:17821/api/v1/files' -Headers $headers
```
