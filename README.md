# TA-Registry

A [.NET 10](https://dotnet.microsoft.com/) registry for [Open Annihilation](https://github.com/open-annihilation/open-annihilation/). It speaks [OA Registry Specification v1](https://github.com/open-annihilation/open-annihilation/tree/main/docs/registries) in tokens mode: a signed catalogue, package files addressed by SHA-256, and the download API (keys, install IDs, human checks, results, and counts).

## Run

Install the .NET 10 SDK, then from this folder:

```powershell
dotnet run --project src/TaRegistry.Web
```

Development listens on `http://127.0.0.1:5080`.

- Admin: `http://127.0.0.1:5080/admin`
- Email: `admin@localhost`
- Password: `ChangeMe-Admin1!`
- Signing passphrase: `local-dev-passphrase` (development only)

These settings should be changed before you expose the process. In production set `Signing:Passphrase`, `Admin:Email`, and `Admin:Password` (environment variables `Signing__Passphrase`, `Admin__Email`, `Admin__Password`). The passphrase must stay the one that sealed `data/keys`, and it must be at least 12 bytes.

Packages, pictures, the SQLite database, and the sealed key live under `data/` next to `src/`. That folder is local runtime state.

## Publish a package

1. Sign in and upload a `.oamod`, `.oamap`, `.oalang`, or `.oaexp`. Check the file with `oa-tool check` first (oa-tool is shipped with OA). This registry reads the manifest and stores the zip; it does not repeat the engine's full install checks.
2. Open the package and fill anything the catalogue still needs (map packs need a `maps` array, mods need `version` and `revision`, expansions need `extends`).
3. Publish. That writes a new signed `catalogue.json`. The sequence only moves forward.

Save `http://127.0.0.1:5080/registry.yaml` and add it from the Open Annihilation registries folder.

To rotate a key, add a rotation key, publish while the old key still signs, wait until players accept the new key, then promote it and publish again.

## API

| Method | Path |
|---|---|
| GET | `/registry.yaml` |
| GET | `/v1/catalogue.json` |
| GET | `/v1/catalogue.json.sig` |
| GET | `/v1/p/{sha256}.{oamod\|oamap\|oalang\|oaexp}` |
| GET | `/v1/i/{sha256}.png` |
| POST | `/api/v1/downloads` |
| POST | `/api/v1/downloads/{id}/result` |
| GET | `/api/v1/challenges/{code}` |
| POST | `/api/v1/challenges/{code}/solve` |
| GET | `/api/v1/counts` |
| GET | `/verify?code=...` |

`install-id` defaults to `required`. After enough keys for one install id, the API returns `428` and the verify page asks the player to confirm they are a person. There is no captcha. A later hour cap returns `429`.

## Tests

```powershell
dotnet test
```
