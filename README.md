# Chrome Extension Version API

Lightweight HTTP API that checks published Chrome extension versions from Chrome Web Store.

## Endpoint

```
GET /check-published-extension-version/{extensionId}
```

### Response (200 OK)
```json
{
  "version": "1.23.4",
  "cached": false,
  "checkedAt": "2026-03-16T12:00:00Z"
}
```

### Error responses
- **400** - Invalid extension ID format
- **404** - Version could not be extracted
- **503** - Chrome Web Store unreachable or rate limited (JSON body with `"source": "chrome-extension-version-api"` to distinguish from nginx 503)

## Configuration

| Env Variable | Default | Description |
|---|---|---|
| `CacheTtlMinutes` | `5` | How long to cache version checks |
| `ASPNETCORE_URLS` | `http://+:8080` | Listen address |

## Docker

```bash
docker build -t chrome-extension-version-api .
docker run -p 8080:8080 chrome-extension-version-api
```
