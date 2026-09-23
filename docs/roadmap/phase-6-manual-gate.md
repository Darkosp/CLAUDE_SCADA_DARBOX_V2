# Walking the Phase 6 gate by hand

Phase 6's test gate is in [the phase plan](phase-plan.md): on a clean machine,
`docker compose up` brings the whole on-premises stack to a working system. The tests and
the scripted checks in the step-4 PR cover the parts a script can see — the order the
containers start in, where the privileged password goes, that a second `up` changes
nothing, that `down` and `up` keep the data, that the OPC UA certificate outlives its
container. This is the other half: using it, in a browser, the way Phase 5.5's hand walk
found ten defects a green suite had passed.

Everything below was read out of the code and the Compose file rather than remembered.
Commands are for PowerShell, from the repository root. **You choose every password.**
Nothing here ships one, and none should be written into a file that is committed.

## 0. Before you start

- Docker Desktop running, and Git for Windows (its `bash` runs the image build).
- Port **8080** free, or pick another in step 2.
- This stack is **separate from the development database** started by the
  `docker-compose.yml` at the repository root. It has its own project name
  (`scada-darbox`), its own volumes, and does not publish the database port. Your
  development `scada` database is not touched.
- The Journal screen reads once, on opening and on **Refresh** (Phase 5.5). After a
  restart, press Refresh to see the new rows.

## 1. Build the images from a commit

```powershell
git checkout main
git pull
& "C:\Program Files\Git\bin\bash.exe" deploy/build-images.sh
```

It builds from `git archive` of `HEAD`, so uncommitted changes cannot reach an image; it
says so if there are any. The last line names the tag, the short commit hash:

```
Built scada-darbox/{migrator,gateway,modbus-sim,opcua-sim}:<tag> from <full hash>.
```

Keep `<tag>` for step 2.

## 2. Secrets

```powershell
Copy-Item deploy/.env.example deploy/.env
notepad deploy/.env
```

Fill in:

| Variable | What it is |
|---|---|
| `SCADA_IMAGE_TAG` | the `<tag>` from step 1 |
| `SCADA_DB_ADMIN_PASSWORD` | the privileged role `scada`; only the database and the migrator get it |
| `SCADA_APP_DB_PASSWORD` | the application role `scada_app` the Gateway connects as |
| `SCADA_INITIAL_ADMIN_USERNAME` / `_PASSWORD` | your first Admin; password at least 12 characters |
| `SCADA_HTTP_PORT` | 8080 unless it is taken |

No password may contain `;` — they are placed in connection strings. `deploy/.env` is
ignored by git.

**Check the refusal once, before filling it in completely:** with any one of the first
three left empty, step 3's command stops before starting anything and names the missing
variable (`required variable … is missing a value`). That is the whole point of `${VAR:?}`.

## 3. Up

```powershell
docker compose -f deploy/docker-compose.yml up -d
docker compose -f deploy/docker-compose.yml ps -a
```

**What must be true:**

- `migrator` is **Exited (0)**; `gateway`, `timescaledb`, `modbus-sim`, `opcua-sim` are
  running.
- The migrator finished before the Gateway started:

  ```powershell
  docker inspect -f '{{.State.FinishedAt}}' scada-darbox-migrator-1
  docker inspect -f '{{.State.StartedAt}}'  scada-darbox-gateway-1
  ```

- The Gateway holds no privileged credential. This prints nothing:

  ```powershell
  docker inspect -f '{{json .Config.Env}}' scada-darbox-gateway-1 | Select-String -SimpleMatch "Username=scada;"
  ```

- Neither log has a false `Error:` in it:

  ```powershell
  docker compose -f deploy/docker-compose.yml logs migrator gateway | Select-String "Error|libgssapi|not at the version"
  ```

  Expected: nothing. (A `warn:` about DataProtection keys is ASP.NET's default and
  harmless here: nothing in the Gateway uses DataProtection.)

## 4. Sign in, on one address

Open `http://localhost:8080` (or your `SCADA_HTTP_PORT`). The sign-in screen comes from the
Gateway itself: the API and the live push are on the same address, and no other origin is
allowed. Sign in with the initial Admin from step 2.

Then open `deploy/.env`, **clear both `SCADA_INITIAL_ADMIN_*` values**, and save. They are
ignored once a user exists; clearing them keeps the password out of the running
container's environment from the next re-creation on.

## 5. Live values

*Browse* → Site **Skopje** → **Pump House** → **Discharge Pressure**.

**What must be true:** the value moves continuously between about **3.4 and 5.0 bar** (a
sine around 4.2, period about 50 seconds), **Pump Running** toggles with it, and the trend
fills in. This is the Modbus simulator in its own container, reached by service name.

## 6. An alarm that stays up

With **Discharge Pressure** selected → **Alarm threshold** → **Add**. **High limit**
`3.0`, Low blank, save. The simulator never goes below 3.4, so the alarm stays **Active**.

In the banner, **Acknowledge** it. It stays listed, now acknowledged by your username.

## 7. Restart the Gateway container

```powershell
docker compose -f deploy/docker-compose.yml restart gateway
```

Wait for `http://localhost:8080` to answer, reload, sign in.

**What must be true:** the alarm is still listed, still **Acknowledged**, still naming
**you**. In **Journal** → **Refresh**, the restart reads as `EvaluationStopped` followed by
`EvaluationStarted` — a stop inside a container is a clean stop, and journals like one.

## 8. A device going away

```powershell
docker compose -f deploy/docker-compose.yml stop modbus-sim
```

**What must be true:** within a few seconds both Pump House tags read **Bad** — not zero,
not the last value (ADR-0003) — and the trend shows a gap, not a line drawn across it. The
alarm from step 6 does **not** clear: a Bad reading has no value to compare.

```powershell
docker compose -f deploy/docker-compose.yml start modbus-sim
```

The tags return to Good.

## 9. An OPC UA device, and its certificate outliving the container

*Browse* → Site **Skopje** → **Add device**:

- **Name** `OPC UA Pump`, **Driver** `opc-ua`
- **Add setting** `endpointUrl` = `opc.tcp://opcua-sim:4840/ScadaDarboxSimulator`
- **Add setting** `acceptUntrustedCertificates` = `true` (the simulator's certificate is
  self-signed; a real installation decides this deliberately)

Save, select the device → **Add tag**: **Name** `OPC Pressure`, kind **Numeric**,
**Source address** `ns=2;s=Pump1.Pressure`. It reads Good, around 4.2 bar, with the
simulator's own source timestamps.

Note the Gateway's certificate, re-create the container, and look again:

```powershell
docker compose -f deploy/docker-compose.yml exec gateway ls pki/own/certs
docker compose -f deploy/docker-compose.yml up -d --force-recreate gateway
docker compose -f deploy/docker-compose.yml exec gateway ls pki/own/certs
```

**What must be true:** the same file name — the thumbprint in brackets — both times, and
**OPC Pressure** reads Good again after the re-creation. A new certificate on every
upgrade is what the `gateway-pki` volume exists to prevent; any server that had trusted
the old one would refuse the new.

## 10. A second `up` changes nothing

```powershell
docker compose -f deploy/docker-compose.yml up -d
docker compose -f deploy/docker-compose.yml logs --since 2m migrator
```

**What must be true:** the migrator ran again and said `No new scripts need to be
executed`; the Gateway was **not** restarted (no new `EvaluationStarted` in the Journal
after Refresh); everything on screen is as it was.

## 11. Down, then up

```powershell
docker compose -f deploy/docker-compose.yml down
docker compose -f deploy/docker-compose.yml up -d
```

`down` without `-v` removes the containers and keeps the volumes. Reload, sign in.

**What must be true:**

- The **Discharge Pressure** trend still shows history from before the `down`, with the
  gap where the stack was down — not a line drawn across it.
- The Journal still has every earlier row, and the outage as `EvaluationStopped` /
  `EvaluationStarted`.
- The alarm from step 6 is still listed, in the state you left it.
- The OPC UA device, its tag and its certificate are unchanged (repeat the `ls` from
  step 9).

## 12. Afterwards

To stop the stack and keep everything: `docker compose -f deploy/docker-compose.yml down`.

**`down -v` deletes the database and the certificate volume** — every sample, the
journal, users. Only for throwing a stack away.

## What to report

Anything that looks wrong or merely confusing, even where the behaviour is technically
correct — including anything in this document that did not match what you saw.
