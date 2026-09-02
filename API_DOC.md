# Qwen Captcha Solver API — Agent Usage Guide

## Overview

This API solves the Qwen AWSC "slide to verify" captcha. It accepts an **AES-256-CBC encrypted** token payload, decrypts it to obtain the session token, solves the captcha, then encrypts and returns the result (`token` + `x5sec`). Progress is streamed via **Server-Sent Events (SSE)**.

**Base URL:** `https://qwencaptchasolver-production-a965.up.railway.app`

---

## Encryption

All token payloads are encrypted with **AES-256-CBC** (PKCS7 padding, Base64 encoding).

### Payload Structures

**Request payload** (what you encrypt before sending):
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "s5_token": "s5-secret-xyz789"
}
```

**Response payload** (what you receive and decrypt on success):
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "s5_token": "v1_abc123..."
}
```

### Encryption Details

| Parameter | Value |
|-----------|-------|
| Algorithm | AES-256-CBC |
| Padding   | PKCS7 |
| Encoding  | UTF-8 JSON → Base64 |
| Key       | `T8kZLR4OsUC92Bv7SkibEUSYHXdhQ+JAXseP5tHypQ0=` (Base64) |
| IV        | `+andwrOoiwPcpltvfAActg==` (Base64) |

---

## Endpoints

### `GET /health`

Health check.

**Response:**
```json
{"status": "ok"}
```

---

### `POST /solve`

Solves the captcha and streams progress via SSE. Returns the encrypted result payload on success.

#### Request Body

```json
{
  "encrypted_token": "<Base64 AES-encrypted payload>",
  "captcha_url": "https://..."  // optional
}
```

| Field             | Type   | Required | Description |
|-------------------|--------|----------|-------------|
| `encrypted_token` | string | **Yes**  | AES-encrypted JSON payload containing `{"token": "...", "s5_token": "..."}`. The `token` field is extracted and used for session authentication. |
| `captcha_url`     | string | No       | The punish URL that triggered the captcha. If omitted, the API will attempt to trigger a captcha automatically by sending a probe prompt. |

#### SSE Event Stream

The response is an SSE stream (`Content-Type: text/event-stream`). Events are delivered as:

```
event: <event_name>
data: <json_payload>

```

| Event             | Payload                  | Description |
|-------------------|--------------------------|-------------|
| `log`             | `{"message": "..."}`     | Progress/log messages while solving. |
| `error`           | `{"message": "..."}`     | Fatal error — the solve failed. |
| `encrypted_token` | `"<Base64 encrypted>"`   | AES-encrypted result payload (token + x5sec). Only on success. |
| `done`            | `{"status": "success"\|"failed"}` | Terminal event. Stream ends after this. |

#### Event Flow

```
log → log → ... → encrypted_token → done(success)
       OR
log → log → ... → error → done(failed)
```

---

## Usage Examples

### Example 1: Solve with a known captcha URL

```bash
curl -N -X POST "https://qwencaptchasolver-production-a965.up.railway.app/solve" \
  -H "Content-Type: application/json" \
  -d '{
    "encrypted_token": "BASE64_ENCRYPTED_PAYLOAD",
    "captcha_url": "https://..."
  }'
```

### Example 2: Let the API trigger the captcha automatically

```bash
curl -N -X POST "https://qwencaptchasolver-production-a965.up.railway.app/solve" \
  -H "Content-Type: application/json" \
  -d '{
    "encrypted_token": "BASE64_ENCRYPTED_PAYLOAD"
  }'
```

### Example 3: Python client

```python
import base64
import json

import requests
from Crypto.Cipher import AES
from Crypto.Util.Padding import pad, unpad

API_BASE = "https://qwencaptchasolver-production-a965.up.railway.app"
AES_KEY_B64 = "T8kZLR4OsUC92Bv7SkibEUSYHXdhQ+JAXseP5tHypQ0="
AES_IV_B64 = "+andwrOoiwPcpltvfAActg=="


def encrypt_payload(data: dict) -> str:
    key = base64.b64decode(AES_KEY_B64)
    iv = base64.b64decode(AES_IV_B64)
    plaintext = json.dumps(data, separators=(",", ":")).encode("utf-8")
    cipher = AES.new(key, AES.MODE_CBC, iv)
    ciphertext = cipher.encrypt(pad(plaintext, AES.block_size))
    return base64.b64encode(ciphertext).decode("utf-8")


def decrypt_payload(encrypted_b64: str) -> dict:
    key = base64.b64decode(AES_KEY_B64)
    iv = base64.b64decode(AES_IV_B64)
    ciphertext = base64.b64decode(encrypted_b64)
    cipher = AES.new(key, AES.MODE_CBC, iv)
    plaintext = unpad(cipher.decrypt(ciphertext), AES.block_size)
    return json.loads(plaintext.decode("utf-8"))


def solve_captcha(token: str, s5_token: str, captcha_url: str | None = None) -> dict | None:
    # 1. Build and encrypt the request payload
    request_payload = {"token": token, "s5_token": s5_token}
    encrypted_token = encrypt_payload(request_payload)

    # 2. Send POST request
    body = {"encrypted_token": encrypted_token}
    if captcha_url:
        body["captcha_url"] = captcha_url

    resp = requests.post(f"{API_BASE}/solve", json=body, stream=True, timeout=180)
    encrypted_result = None
    current_event = None

    for line in resp.iter_lines(decode_unicode=True):
        if not line:
            continue
        if line.startswith("event:"):
            current_event = line[6:].strip()
        elif line.startswith("data:"):
            data = json.loads(line[5:].strip())
            if current_event == "encrypted_token":
                encrypted_result = data
            elif current_event == "done":
                if data.get("status") == "success":
                    # 3. Decrypt the result
                    return decrypt_payload(encrypted_result)
                else:
                    return None
            elif current_event == "log":
                print(f"  [progress] {data.get('message')}")
            elif current_event == "error":
                print(f"  [error] {data.get('message')}")

    return None


# --- Usage ---
result = solve_captcha(
    token="eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    s5_token="s5-secret-xyz789",
    captcha_url=None,  # or a specific punish URL
)

if result:
    print(f"Token: {result['token']}")
    print(f"x5sec: {result['x5sec']}")
else:
    print("Captcha solving failed.")
```

---

## Concurrency

- The server accepts **at most 3 concurrent requests** (`limit_concurrency=3`).
- A `threading.Lock` serializes the actual Chrome/Selenium captcha solving, so only **one Chrome instance** is active at a time.
- Additional requests wait in queue until the lock is freed (180s timeout).
- If all 3 slots are busy, new TCP connections are queued at the server level.

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Invalid/undecryptable token | SSE `error` event → `done(failed)` |
| Decrypted payload missing `token` field | SSE `error` event → `done(failed)` |
| No tokens configured | SSE `error` event → `done(failed)` |
| Captcha URL not triggerable | SSE `error` after 2 probe attempts → `done(failed)` |
| Slider not found / solve failed | SSE `error` → `done(failed)` |
| Encryption of result failed | SSE `error` → `done(failed)` |
| Lock timeout (>180s waiting) | SSE `error` → `done(failed)` |
| Server overloaded (>3 connections) | Connection queued at TCP level |

---

## Interactive Docs

Scalar API documentation is available at:

```
GET /docs
```

---

## Local Development

```bash
pip install -r requirements.txt
python captcha_api.py --host 127.0.0.1 --port 8200
```

Requires Chrome/Chromium + chromedriver. The solver looks for bundled Chrome in:
- Windows: `chrome/chrome-win64/chrome.exe`
- Linux: `chrome/chrome-linux64/chrome`

Falls back to system Chrome if not found.
