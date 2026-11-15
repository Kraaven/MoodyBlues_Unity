# MoodyBlues cleanup plan

Covers all three repositories:

| Short name | Path |
|---|---|
| **Unity** | `~/Alexandria/Projects/Unity/MoodyBlues_Unity` |
| **Backend** | `~/Alexandria/Projects/Web/MoodyBlues_Backend` |
| **Frontend** | `~/Alexandria/Projects/Web/MoodyBlues_FrontEnd` |

Ground rules, from the audit and the answers to its questions:

- **Only the current spec is supported.** Wire protocol v3, raw log `MBRAWv02` and replay format 2 are
  the only formats. Compatibility shims, legacy routes, upgrade paths and "older version" branches are
  deleted, not kept.
- **The backend database may be reset.** There is no data to preserve. Migrations are squashed into
  one, and `deploy.sh` gets a command that wipes the database and stored files.
- **Wire protocol v3 does not change.** No Unity-to-backend byte changes. The REST API and the replay
  file may change, but only in Backend and Frontend together, exactly as specified below.
- **Code is right, docs follow.** Documentation is rewritten last (Phase 4), against the code as it is
  after Phases 1-3.
- **No commits.** Changes are left in the working trees for review. Unity's existing uncommitted changes
  (`Packages/*`, `ProjectSettings/*`, `Assets/Settings/UniversalRenderPipelineGlobalSettings.asset`,
  `.vscode/`, `MoodyBlues_Unity.slnx`) are the user's and must not be touched.

---

## Phase 1: Unity

### 1.1 Bug: streamer drop-oldest during an in-flight send

`Assets/Scripts/Wire/MoodyBluesStreamer.cs`. When the ring buffer is full while the pump has a send on
the socket, `TryReserve` skips `FlushSync` and `TryDropOldestEvent` drops events at the read cursor,
which are the bytes of the in-flight chunk. When the send completes, `ReleaseChunk(chunk.Length)`
releases that length again, discarding never-sent events without counting them, and later writes can
reuse memory `SendAsync` may still be reading.

Fix: drop-oldest must never touch the in-flight chunk. Track the in-flight byte length and event
count, and drop only events *after* it (or, if only the in-flight chunk is buffered, refuse the new
event and count it as dropped). Keep `_eventSizes` and the accumulator in lockstep. Add an EditMode
test that reproduces the old double release (simulate a pending send via the internal state, fill
the buffer, then complete the send) if that is possible without a socket; otherwise factor the
bookkeeping into a small testable class and test that.

### 1.2 Dead code

- `Serializer`: `DeserializeTimeStamp`, `DeserializeInstantiateObject`, `DeserializeDeltaRotation`,
  `ReadEnvelope` (keep `DeserializeReparentParentObjectId` and `CurrentRanges` only if tests use them;
  they do).
- `MoodyBluesSessionExportUtility.cs`: the one-argument `PopulateObjectUserData` overload. See 1.3 for
  the rest of this file.
- `MoodyBluesStreamer.DroppedEventCount`, `MoodyBluesMaintenanceSystem.Streamer`,
  `MoodyBluesSessionManager.Config`.
- `MoodyBluesSessionManager`: the `useJobDiff` field and `RunDiffMainThread`. The Burst job is the only
  diff path. (`TransformEventDispatcher.Has*Changed` stay; the main-thread paths still use them.)
- Tracked junk: `ServerLog.txt`, `Assets/Prefabs.meta` (and the empty `Assets/Prefabs/` folder),
  `Assets/New Physics Material.physicMaterial(.meta)` and `Assets/InputSystem_Actions.inputactions(.meta)`,
  after confirming with `grep` over `Assets/` and `ProjectSettings/` that no asset references their
  GUIDs. Anything still referenced stays, and is reported.
- `Assets/Scripts/Testing/` (BallsSpawner, CubeMovement, SampleSceneController, `Prefabs/Ball.prefab`):
  the sample scene uses them, so they are not deleted. They move to `Assets/Samples/` with their own
  `MoodyBlues.Samples.asmdef` referencing `MoodyBlues`, so they no longer ship inside the runtime
  assembly. Move the `.meta` files with them so GUIDs (and the scene's references) survive.

### 1.3 Duplication

- **Session manager baselines.** One private helper (for example `SendAbsolute(MoodyBluesStreamer, id,
  current pose, previous baseline) -> TrackedState`) replaces the four copies of "build a SendTrue
  TransformData, enqueue, accumulate diagnostics, store the baseline" in `SendSpawnPoseIfDifferent`,
  `ResyncTransformIfChanged`, snapshot step 4 and `EmitReparent`'s baseline store. `ToTrackedState`
  moves to `TrackedState` (for example `TrackedState.From(in TransformEncodeResult)`) and is used
  everywhere a `new TrackedState { LastSentPosition = encoded.Position ... }` is written today.
  `SetBaseline` replaces `RebaselinePendingRegistration`. `FailCallsBeforeStart` and the replay loop
  in `InitializeRecorder` become one `RunCallsBeforeStart()`. `RegisterSubtree` keeps its block check;
  `RegisterTransform`'s own check stays (single registrations need it), but the warning text lives once.
- **HTTP.** Merge `SceneUploadClient` into `MoodyBluesHandshakeClient` (rename the class to
  `MoodyBluesHttpClient`, file `Handshake/MoodyBluesHttpClient.cs`): one `SendAsync` helper, one
  `UploadSceneAsync` that gzips and posts, one header constant set. Delete `SceneUploadClient.cs`.
- **Export metadata.** Delete `Runtime/MoodyBluesSessionExportUtility.cs`. Its node-extras logic moves
  into `SceneGltfExporter.ExportJob.AfterNodeExport`, which already computes `invertLook`; there is one
  `HasInvertedLook(Transform)` check.
- **Hashing.** `SceneContentHashEditor` uses `SceneHasher` for SHA-256 and lowercase hex (make
  `SceneHasher.HashBytes` and a `HashText`/`ToLowerHex` public as needed).
- **Runtime manager.** Delete `_liveInstanceIds`; `TryGetObjectId` asks the session manager.
  `_templateIdBySpawnId` stays only if it cannot be derived (spawns that registered only their root
  have no `_spawnSubtrees` entry); if it stays, say why in one comment.
- **Streamer shutdown.** Reduce the three sync/async pairs where it is safe. The sync path runs on
  Unity's main thread and cannot await continuations that need the main thread, so only unify if the
  async methods use `ConfigureAwait(false)` throughout and the sync wrapper blocks with a timeout.
  If that is not clearly safe, leave both and do not force it.

### 1.4 Comments

Fix every comment that cites a wrong `Spec.md` section or a wrong size: Log is 32 bytes (not 35),
lifecycle events are Section 6.2, the handshake/config/export are Section 13, the governing model is
Section 1. Remove the "see the benchmark notes in Spec.md" reference.

### 1.5 Verify

Unity Editor `~/Unity/Hub/Editor/6000.6.9f1` is installed. If the project is not open in another
Editor (no `Temp/UnityLockfile` held), run EditMode tests in batch mode:
`.../Editor/Unity -batchmode -nographics -projectPath <Unity repo> -runTests -testPlatform EditMode
-testResults <tmp>/results.xml -logFile <tmp>/unity.log`. Report the result, or why it could not run.

---

## Phase 2: Backend

### 2.1 Project replaces Developer

`Developer`, `DeveloperProvisioning`, `IdGenerator` and `Project.DeveloperId` are deleted. Everything
keys on `Project.Id` (Guid):

| Entity | Key / columns |
|---|---|
| `Scene` | key `(ProjectId, SceneId)`, FK to `Project` |
| `SceneRevision` | key `(ProjectId, SceneId, Hash)`, unique `(ProjectId, SceneId, Version)`, FK to `Scene` |
| `Session` | `ProjectId` with FK to `Project`; unique `(ProjectId, ClientSessionId)`; index `(ProjectId, SceneId)` |

- Storage: `{ScenesDir}/{projectId:N}/{sceneId}/{hash}/...` and `{SessionsDir}/{projectId:N}/{sessionId:N}/...`.
- `IngestProject` carries `ProjectId` (Guid) and `Name`.
- Quota names: `MaxConcurrentSessionsPerProject`, `MaxStoragePerProjectBytes` (env var names unchanged).
- `StorageQuota.UsedBytesAsync(db, projectId)`.
- Audit targets use `{projectId}/{sceneId}`.

**Dashboard routes** (replacing `/api/scenes/{developerId}/{sceneId}/...`):

| Route | |
|---|---|
| `GET/PATCH/DELETE /api/projects/{projectId:guid}/scenes/{sceneId}` | scene detail / rename / delete |
| `GET /api/projects/{projectId:guid}/scenes/{sceneId}/file?hash=` | GLB |
| `DELETE /api/projects/{projectId:guid}/scenes/{sceneId}/versions/{version:int}` | |
| `DELETE /api/projects/{projectId:guid}/scenes/{sceneId}/archive` | |
| `GET /api/projects/{projectId:guid}/scenes/{sceneId}/sessions?version=&awaitingScene=` | |
| `/api/sessions/{sessionId}` (+ `/replay`, `/recompile`, DELETE) | unchanged |

Ownership is one helper (project owned by the caller, else 404) shared by the scene and session
routes. `SessionEndpoints.ListAsync` stops inlining it.

**Response contract changes** (camelCase JSON):

- `ProjectSummaryResponse`, `ProjectDetailResponse`: `developerId` removed.
- `SceneDetailResponse`: `developerId` replaced by `projectId`.
- `SessionDetailResponse`: `developerId` replaced by `projectId`; `rawEventCount` removed;
  `rotationWidenings` added (see 2.3).
- `SceneResponse`: `revisionCount` removed.
- `SessionSummaryResponse` and `SessionDetailResponse`: `activity` added (see 2.4).
- `AuthResponse` and `MeResponse` become one `UserResponse { userId, email }`.

`Session.RawEventCount` is dropped from the entity (it only fed the removed field). `CompiledReplay`
may keep counting events for the compile log line.

### 2.2 Legacy and dead code

- `PasswordHasher.NeedsRehash` and the rehash in `LoginAsync`.
- Ingest key columns are required: `IngestKeyHash`, `IngestKeyEncrypted`, `IngestKeyPrefix`,
  `IngestKeyCreatedAtUtc` are non-null (every project gets a key when created). `ProjectDetailResponse.connectionString`
  is null only when the key cannot be decrypted (the JWT secret changed). Remove the "no key" and
  "key predates stored keys" branches.
- `docker-entrypoint.sh`: drop the take-ownership step for files from older images. Keep dropping
  to the `app` user only if the container starts as root; otherwise run directly.
- All five migrations are deleted and replaced by one `Initial` migration generated from the new
  model (`dotnet ef migrations add Initial`, via the SDK container, see 2.7).
- `NullWireEventSink` is deleted; a `WireEventSinkBase` with virtual no-op members replaces the
  boilerplate in `StreamServer.EndSessionDetector` (and `SessionReconstructor` derives from it only if
  that reads better; it implements every member anyway).
- `WireDecoder`'s `requireBeginSession` parameter (always true) and its field.
- `RawLogWriter.Append(kind, payload)` and `AppendConnectionClosed(reason)` overloads and
  `_lastOpenedConnection`; tests pass the connection explicitly.
- `StoragePaths.RawGlbFile` and any other constant left unused.
- Comments that say the stream token is in the URL (`StreamTokens`, `Session.StreamTokenHash`) or
  describe "older" clients/formats beyond what the version check needs.

### 2.3 Bug: RotationWidenings is dropped

Store `EndSession.RotationWidenings` like the other counters: `CompiledReplay.RotationWidenings`,
`Session.RotationWidenings`, `SessionDetailResponse.rotationWidenings`. The replay file header is not
changed for it.

### 2.4 Session activity, computed by the compiler

The frontend computed this from the replay in the browser. The compiler now computes it during
compilation and stores it on the session as JSON (`Session.ActivityJson`, `jsonb`, null until Ready).
Responses expose it as `activity` (object or null):

```json
{
  "bins": [0.0, 0.31, ...],         // 48 numbers, keyframe activity per equal time bucket, 0..1
  "errors": [0.12, 0.5],            // session fractions 0..1 of Error logs
  "warnings": [0.3],                // ... of Warning logs
  "spawns": [0.01, 0.02],           // ... of Spawn events
  "gaps": [[0.4, 0.45], [0.7, 0.706]] // [start, end] fractions with no data
}
```

Algorithm (port exactly from `MoodyBlues_FrontEnd/src/viewer/replay/glyphTimeline.ts`):
`total = DurationSeconds`; if `total <= 0` all lists empty and 48 zero bins. `fraction(t) =
clamp(t / total, 0, 1)`. Every key time of every position/rotation/scale track of every object goes to
bucket `min(47, floor(fraction(t) * 48))`; `bins[i] = sqrt(count[i] / max(1, maxCount))`. Errors and
warnings from log levels 3 and 2. Events sorted by time: Spawn adds a spawn; DataGap adds
`[f, min(1, f + 0.006)]`; Disconnected with `time < total - 0.05` opens a gap that the next Connected
closes. Round values to 4 decimals to keep the JSON small. Key times are the fitted keys
(`ChannelFitter.KeyTimes`), which is what the replay file contains. Port the frontend's glyph test
cases to an xUnit test. Bump `SessionCompiler.Version` to 3.

### 2.5 Duplication

- One helper for "does this scene version exist" (`SceneRevisions.ExistsAsync(db, projectId, sceneId,
  hash)` or similar), used by handshake, scene check, upload and the compile worker where it is a
  plain existence check (EF query composition inside larger LINQ queries may stay inline).
- Handshake and scene check share request validation (`sceneId`, `sceneHash`) and the upload-URL builder.
- `StreamTokens.Hash` and `IngestAccess.Hash` become one `Sha256Hex(string)` (in `Common/`).
- `MOODYBLUES_TRUST_FORWARDED_HEADERS` is read into `ServerConfig.TrustForwardedHeaders`.
- `ProjectEndpoints.GetAsync`: build scene responses with a fixed number of queries, not two per scene.
- `SceneWorkSignal`/`CompileWorkSignal` may stay as they are.

### 2.6 deploy.sh: wipe

Add `./deploy.sh wipe`: deletes **all data** (database and stored scene/session files) and starts a
fresh, empty stack.

- Prints what will be deleted and requires typing `wipe` to continue (or `--yes` as the second
  argument for scripts).
- Offers nothing automatic beyond that; it suggests running `./deploy.sh backup` first.
- `docker compose down -v` (removes the stack's named volumes: `moodyblues-db-data`,
  `moodyblues-scenes`, `moodyblues-sessions`; check the names in `docker-compose.yml`), then
  `docker compose up -d --build`. Migrations run at startup and create the empty schema.
- Add it to the usage text and to the `case` statement. The existing `reset` (update and restart)
  keeps its meaning; its comment says plainly that it keeps data.

### 2.7 Verify

`dotnet` is not installed; use podman with the SDK image:
`podman run --rm -v <Backend repo>:/src:Z -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`.
The migration is generated the same way (install `dotnet-ef` per `dotnet-tools.json` inside the
container: `dotnet tool restore && dotnet ef migrations add Initial --project src/MoodyBlues.Backend`).
`bin/` and `obj/` created by the container are git-ignored. Report failures as they are.

---

## Phase 3: Frontend

### 3.1 API contract

Follow 2.1 and 2.4 exactly:

- `src/api/types.ts`: drop `developerId` from projects; `SceneDetail.projectId`;
  `SessionDetail.projectId`, no `rawEventCount`, add `rotationWidenings`; no `revisionCount`;
  `AuthResponse`/`MeResponse` become `UserResponse`; add `SessionActivity` and
  `activity: SessionActivity | null` on both session types.
- `src/api/queries.ts`: scene paths become `/api/projects/{projectId}/scenes/{sceneId}`; hooks and
  query keys take `projectId`. `sceneFilePath(projectId, sceneId, hash)`. Every caller updated
  (`ScenePage`, `VersionsDialog`, `sceneSource`, studio, etc.).
- `DetailsWindow` shows rotation widenings next to the other encode counters.

### 3.2 Glyphs from the backend

- `ActivityGlyph` draws `session.activity` (renamed fields: `bins` instead of `activity`). A Ready
  session without `activity` (compiled by an older compiler: cannot happen after the reset, but the
  type allows null) shows the "unavailable" form. Delete the summary form.
- Delete `src/pages/scene/glyphs.ts` (IndexedDB glyph store, `useGlyphTimelines`,
  `useSessionIndexer`, the "Index sessions" UI), `src/viewer/replay/glyphTimeline.ts` and its test,
  and `saveGlyph` in `StudioPage`.

### 3.3 Dead code

- `src/stage/Trace.tsx`, the `'trace'` stage content, `traceQuality` in the store and DebugPanel, and
  any comment describing the Trace.
- Legacy routes and components in `App.tsx`: `LegacyInspectRedirect`, `LegacySceneRedirect`, the
  `/history`, `/settings`, `/archive`, `/inspect` and `/dashboard` routes. The catch-all stays.
- Unused exports: `formatRecorded`, `RecordingsEmpty`, `snapshotAttempted`, `Textarea`, `frameObject`.
  Exported types and values used only in their own file lose the `export` keyword.
- `package.json`: the `mock` and `shots` scripts; `playwright-core`,
  `@fontsource-variable/jetbrains-mono`, `@fontsource/instrument-serif`, and `autoprefixer` (and
  remove it from `postcss.config.js`; Tailwind 4 handles prefixes). Add `three-stdlib`, which is
  imported but not declared (use the version already in `package-lock.json`).
- Any UI branch for projects without a key or with a key that "predates stored keys": the
  `connectionString` is now null only when the server cannot decrypt the key, and the copy says to
  rotate it.

### 3.4 Duplication

- `testReplay.ts` imports `HEADER_SIZE` from `replayFile.ts`.
- One helper for the per-page idle stage view (`idleView(id)`), replacing the `NONE` constants.
- `src/player/` (binding, playback, logNav, unityPath and its test) moves into `src/studio/`, its only
  user.

### 3.5 Verify

`npm ci`, then `npx tsc -b`, `npx oxlint`, `npx vitest run`, `npm run build`. All must pass.

---

## Phase 4: Documentation

After Phases 1-3, every document is rewritten against the code, not patched:

- **Unity:** `Spec.md` (keep its structure and byte tables; fix section cross-references, constants,
  and anything the code no longer does), `DOCUMENTATION.md`.
- **Backend:** `README.md` (routes table, project-keyed storage layout, `wipe`, no legacy key
  states, project layout).
- **Frontend:** `README.md`, `DOCUMENTATION.md` (routes, stage content names, glyphs from the
  backend, no `DESIGN.md` or `dev/` references). Delete `REBUILD_PLAN.md`.
- Every cross-reference between the three repos' documents resolves.
- This `Plan.md` gets a short "Outcome" section listing what was done and anything left undone.

---

## Outcome

Nothing is committed. All changes are in the three working trees.

### Phase 1: Unity

Done as planned:

- **1.1 Streamer bug.** The outgoing buffer bookkeeping moved to `Wire/EventSendBuffer.cs`
  (ring buffer, per-event sizes, the in-flight send). Drop-oldest never touches the in-flight
  bytes; while a send is in flight a full buffer refuses the new event instead. New
  `Assets/Tests/EditMode/EventSendBufferTests.cs` covers the old double release, abandoned sends
  and oversized events.
- **1.2 Dead code and junk.** The listed `Serializer` methods, the `useJobDiff` field and
  `RunDiffMainThread` (the Burst job is the only diff path), the unused accessors,
  `ServerLog.txt` and `Assets/Prefabs.meta` are gone. `Assets/Scripts/Testing/` moved to
  `Assets/Samples/` with its own `MoodyBlues.Samples.asmdef`; `.meta` files moved with it.
- **1.3 Duplication.** `SceneUploadClient` merged into `MoodyBluesHttpClient` (renamed from
  `MoodyBluesHandshakeClient`); `MoodyBluesSessionExportUtility` deleted, node extras written in
  `SceneGltfExporter`; one hashing path via `SceneHasher`; the session manager baseline helpers
  consolidated; `_liveInstanceIds` removed.
- **1.4 Comments.** Spec section references and sizes fixed.

Done differently:

- The streamer's sync/async shutdown pairs were left as they are. Unifying them risked a
  main-thread deadlock (the sync path blocks the thread the async continuations need).
- `_templateIdBySpawnId` stays in `MoodyBluesRuntimeManager`: a spawn that registered only its
  root has no `_spawnSubtrees` entry, so the template id cannot be derived.
- `Assets/New Physics Material.physicMaterial` and `Assets/InputSystem_Actions.inputactions`
  stay, because assets reference their GUIDs.
- Refused events (full buffer, send in flight) count in `DroppedEvents` and trigger the absolute
  resync, same as dropped ones.
- `SampleScene.unity` still serializes the removed `useJobDiff` field. Harmless; Unity drops it
  on the next save of the scene.

Not verified (1.5): Unity was **not compiled or tested**, because the Editor had the project open.
For you: check the Editor console for compile errors, then run the EditMode tests (Window >
General > Test Runner), including the new `EventSendBufferTests`.

### Phase 2: Backend

Done as planned: `Developer` replaced by `Project` as the key everywhere (entities, storage paths
`{projectId:N}/...`, quotas, dashboard routes `/api/projects/{projectId}/scenes/{sceneId}/...`,
`UserResponse`); legacy key states, password rehash and the entrypoint chown removed; one `Initial`
migration; `RotationWidenings` stored and exposed; session `activity` computed by the compiler
(`SessionCompiler.Version` 3, `Compiler/SessionActivity.cs`) with xUnit tests; shared helpers
(`Common/Hashing.cs`, `Projects/ProjectAccess.cs`, `Ingest/IngestScenes.cs`,
`Scenes/SceneRevisions.cs`); `deploy.sh wipe`.

Done differently: `SessionReconstructor` still implements `IWireEventSink` directly rather than
deriving from a base class (it implements every member anyway).

Verified: `dotnet test` in the SDK container, 82 passed, 0 failed.

For you: existing databases and dumps from before the squash do not migrate onto `Initial`. Run
`./deploy.sh wipe` (after `backup` if anything matters) on any deployed stack, and restore only
dumps taken after this change.

### Phase 3: Frontend

Done as planned: API contract follows Phases 2.1 and 2.4 (project-keyed routes, `UserResponse`,
`activity`, `rotationWidenings`); `ActivityGlyph` draws the backend's `activity` and the client
glyph indexer, IndexedDB glyph store and its test are deleted; the Trace, legacy routes, unused
exports, the `mock` and `shots` scripts and unused dependencies are removed; `three-stdlib` is
declared; `src/player` moved into `src/studio`.

Done differently: the ingest key fields are non-null in the TypeScript types (the backend always
returns them); `connectionString` stays nullable.

Verified: `tsc -b` clean, oxlint 0 errors and 6 warnings (`react/only-export-components`), vitest
44 passed, `npm run build` ok.

For you: oxlint's 6 warnings are left as they are. Not run against a real backend or a browser.

### Phase 4: Documentation

Done: `Spec.md` (implementing files, constants table with private symbols marked, node extras
attribution, section 8.3 and 8.4 describe the send buffer and refuse-while-in-flight; byte tables
and sizes checked against `Serializer.cs`, unchanged), Unity `DOCUMENTATION.md`, Backend
`README.md` (routes, `activity`, storage layout, `wipe`, restore with an explicit chown, project
layout checked against the tree), Frontend `README.md` and `DOCUMENTATION.md` (routes from
`App.tsx`, stage content from `stage/store.ts`). `REBUILD_PLAN.md` removed with `git rm`.

Done differently: because the entrypoint no longer chowns, the backup restore recipe now runs
`chown -R 1654:1654 /app/data` after extracting (1654 is the `app` user in the .NET base image;
the README says how to check it).

Left for you: code comments that still cite `DESIGN.md` (a file that does not exist) in the
frontend, a wrong Spec section in a backend test helper, and two comments in Unity that name
`TryReserve`, which no longer exists. They are code, so they were not edited here.
