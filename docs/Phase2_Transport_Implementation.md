# Phase 2 — EpsonAdsService: Transport/Vertrag (Stand: umgesetzt, inkl. Review-Umsetzung)

## Umfang

`EpsonAdsService` bleibt dumb Transport (Postfach, Handshake, Locks, Link); Prozesslogik bleibt beim Executor (Phase 4: echte SMs).

## Änderungen gegenüber dem Stand nach Phase 1

### Vertrag v2
- Structs `ST_PlcToEpsonIpc` / `ST_EpsonIpcToPlc` (Phase 1), `IpcInterfaceVersion = EpsonAdsContract.InterfaceVersion` (2), GVL-Pfade unverändert.
- Layoutprüfung (`CheckLayout`) schlägt bei Größenabweichung mit klarer Meldung fehl.

### Lock-Lebenszyklus (§3.2, D6)
- `RequestMask`/`OwnerSequence` pro aktivem Auftrag (wie bisher).
- Neu: `HeldMask` — tatsächlich gewährte Bits; wird in `WaitGrant` aus `GrantedMask` akkumuliert.
- Neu: `ReleaseMask` — IPC fordert Freigabe; ein Bit gilt als verarbeitet, sobald die SPS den Grant entfernt; danach wird `HeldMask`/`ReleaseMask` bereinigt.
- Neu: frühzeitige Freigabe durch Executor-Status (`RequestedReleaseMask`) — Dummy gibt die Transfer-Ressource nach `WaferOnRobot` frei (D6, Legacy-Verhalten).
- Neu: `TryRequestForceRelease()` (public) — Recovery-Anforderung nur im Leerlauf; wird zurückgesetzt, sobald die SPS alle Grants entzogen hat.
- **Review-Fix**: `Lock.RequestMask` wird zyklisch aus `_requestMask` publiziert; eine frühe Freigabe (Executor-Status) entfernt das Bit auch aus `RequestMask`, damit die SPS es nicht sofort wieder vergibt.
- **Review-Fix**: Grant-Verlust während `Run` (SPS entzieht ein Bit, z. B. `b*Allowed` wird FALSE) bricht den Auftrag mit `MissingLockGrant` ab; die betroffenen Bits werden aus Held-/Release-/RequestMask entfernt.
- Bei erfolgreichem Ende fordert die IPC alle gehaltenen Ressourcen zur Freigabe an; **bei Fehler bleiben Held- und RequestMask bestehen** (Freigabe erst nach SPS-Sicherheitsprüfung via ResetError/ForceRelease).

### Fehlerverhalten (D7, §5)
- `Error.Active` wird **nicht** mehr automatisch bei `!Cmd.Valid` gelöscht.
- Einziger Reset: Auftrag `ResetError` (110); bei erfolgreichem Abschluss löscht die IPC den Latch. Fehlerreset löst keine Bewegung aus.
- Während `Error.Active` werden neue Aufträge (außer `ResetError`) abgewiesen (`ErrorSequence` gesetzt, nicht akzeptiert).
- Bei Fehler bleibt `HeldMask` bestehen; Aufräumen über `ForceReleaseRequest`/ResetError-Fluss.

### Versions- und Snapshot-Überwachung
- Versions-Mismatch friert `RunExecution` ein: keine neuen Aufträge, laufender Auftrag wird mit `VersionMismatch` beendet, Queue geleert.
- `External.Sequence`-Watchdog: 3 s ohne Sequenzfortschritt → vor Auftragsstart Ablehnung, während `Run` Fehler `MissingExternalState`.

### Executorschutz und Lebenszyklus (Review-Umsetzung)
- **`Abort(reason)` im `IEpsonCommandExecutor`**: Bei jedem Fehler (`FailExecution`), bei Befehlsentzug (`Cmd.Valid = FALSE` während der Ausführung) und bei Reconnect ruft der Service `Abort()`; Implementierungen müssen idempotent sein.
- **Reconnect-Reset (`ResetExecutionState`)**: Bei `Disconnect()` und `(Re)Connect` werden Executor abgebrochen, Auftragsqueue geleert und Ausführungs-/Lock-/Backchannel-Felder zurückgesetzt — ein alter Auftrag kann nach einem Neustart der ADS-Verbindung nicht mehr „weiterlaufen“ und kein zweites terminales Ergebnis liefern.
- **Executor-Exceptions**: `TryStart`, `Execute`, `AnswerUserPrompt`, `AnswerRecipeStart` laufen in eigenem try/catch → `FailExecution(EpsonProtocolError)`; ein Executor-Fehler erzeugt keinen ADS-Reconnect mehr.
- **Ein-Auftrag-Modell**: `HandleCommand` lehnt ab, solange ein Auftrag aktiv oder in der Queue ist (`ErrorSequence` + 2003 „IPC busy“).
- **Dedup**: `_lastSequence` bleibt über `Valid = FALSE`-Phasen erhalten; eine bereits verarbeitete Sequenz wird nie zweimal ausgeführt.
- **Thread-Modell**: Executor-Events (Prompt, Rezept-Anforderung) und `TryRequestForceRelease` werden über `ConcurrentQueue`/volatile Flags an den Zyklusthread übergeben und in `ApplyExecutorEvents()` (Beginn des Cycles) angewendet — alle Schreibzugriffe auf `stIpcToPlc` laufen im Zyklusthread.
- **Sequenz-Seeding**: Nach jedem (Re)Connect werden `_lastReactionSequence` und `_recipeRequestSequence` aus `Environment.TickCount` gesetzt, damit neue Anforderungen nicht mit alten Ack-Werten der SPS kollidieren.
- **Konfiguration**: `EpsonAdsService(logger, executor?, plcNetId?, plcPort?)` — AMS-Adresse konfigurierbar (Standard: lokal, Port 851); `ExecutePendingCommandOnStart` ist eine Property statt `const`.

### Backchannels (§3.1/3.2)
- Bedienerreaktion: `Cmd.UserMessageId/UserReaction/UserReactionSequence`; Prüfung gegen `UserPrompt.Active`, `MessageId` und `OptionsMask`; gültige Reaktion → `AcceptedReactionSequence` gesetzt, Prompt deaktiviert, `executor.AnswerUserPrompt(...)`. Ungültige Reaktion → `Warning.*`, Reaktionssequenz wird nicht bestätigt.
- Pressrezept: Executor-Event → `Press.RecipeStartRequestSequence` (eigener Zähler); SPS antwortet mit Ack-Sequenz + `RecipeStartResult != Pending` → `executor.AnswerRecipeStart(...)`; `Rejected`/`Error` beendet den Auftrag mit `RecipeStartRejected`.

### Statuspublikation
- `Cmd.Busy/Accepted/Done/Progress/WaferPicked/WaferPlaced` aus Ausführungsphase und `executor.Status`; `Cmd.Done` wird bei Abschluss TRUE und beim nächsten Auftragsbeginn FALSE; `Cmd.Accepted` bleibt nach der Annahme TRUE (Historie). `Progress` hält `Done`/`Error` bis zum nächsten Auftragsbeginn (sequenzgebunden, §2).
- Bei Fail werden `UserPrompt.Active` und `Press.RecipeStartRequestSequence` geräumt (keine hängende Anforderung ohne Auftrag).
- `Robot.State` abgeleitet: **Offline** ohne ADS-Verbindung, sonst Error/Busy/Ready — Platzhalter bis Phase 4 (echter Status aus Epson-Protokoll).
- `AdsConnected` sowie `StationChannelConnected`/`RemoteControlConnected` (public Properties für die Kanaladapter aus Phase 4).
- Heartbeat unverändert (100 ms Takt).

### Executor-Interface (Phase-2-Vertrag für Phase 4)
```csharp
bool TryStart(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc, out EpsonExecutionResult failure, out uint requestMask);
EpsonExecutionResult? Execute(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc);
void Abort(string reason); // idempotent
EpsonExecutionStatus Status { get; }
void AnswerUserPrompt(uint messageId, E_EpsonUserReaction reaction);
void AnswerRecipeStart(uint sequence, E_EpsonPressResult result);
event EventHandler<EpsonUserPromptRequest>? UserPromptRequested;
event EventHandler? RecipeStartRequested;
```

### Dummy-Executor
- Simuliert Phasen (`E_EpsonProgress`), `WaferPicked`/`WaferPlaced`, Pressrezept-Handshake und einen Test-Prompt bei `ChangeTool`.
- Validierung erweitert: alle Aufträge 0–120, `ThinPlate`-Konsistenz, `Genmark.Parked` bei FillWafer-Quelle, Ausgangs-/Reserveprüfung, `SendSingleProtocolCommand`-Allowlist; `CPWR` wird mit `ProtocolCommandNotAllowed` abgewiesen; `CESTA` existiert bewusst nicht (läuft über Reaktionskanal).

## Verifikation
- Statische Prüfung: keine Legacy-Typnamen, Klammerbalance, Service↔Executor-Referenzen konsistent (kein .NET-SDK im Sandbox-Build verfügbar; Kompiliertest folgt im Projektumfeld).
- Strukturgrößen unverändert zu Phase 1 (95/850 Bytes) — `CheckLayout` zur Laufzeit zusätzlich absichernd.
