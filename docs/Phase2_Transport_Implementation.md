# Phase 2 — EpsonAdsService: Transport/Vertrag (Stand: umgesetzt)

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
- Bei erfolgreichem Ende fordert die IPC alle gehaltenen Ressourcen zur Freigabe an; bei Fehler bleibt der Besitz bestehen (Freigabe erst nach SPS-Sicherheitsprüfung).
- Neu: `TryRequestForceRelease()` (public) — Recovery-Anforderung nur im Leerlauf; wird zurückgesetzt, sobald die SPS alle Grants entzogen hat.

### Fehlerverhalten (D7, §5)
- `Error.Active` wird **nicht** mehr automatisch bei `!Cmd.Valid` gelöscht.
- Einziger Reset: Auftrag `ResetError` (110); bei erfolgreichem Abschluss löscht die IPC den Latch. Fehlerreset löst keine Bewegung aus.
- Während `Error.Active` werden neue Aufträge (außer `ResetError`) abgewiesen (`ErrorSequence` gesetzt, nicht akzeptiert).
- Bei Fehler bleibt `HeldMask` bestehen; Aufräumen über `ForceReleaseRequest`/ResetError-Fluss.

### Versions- und Snapshot-Überwachung
- Versions-Mismatch friert `RunExecution` ein: keine neuen Aufträge, laufender Auftrag wird mit `VersionMismatch` beendet, Queue geleert.
- `External.Sequence`-Watchdog: 3 s ohne Sequenzfortschritt → vor Auftragsstart Ablehnung, während `Run` Fehler `MissingExternalState`.

### Backchannels (§3.1/3.2)
- Bedienerreaktion: `Cmd.UserMessageId/UserReaction/UserReactionSequence`; Prüfung gegen `UserPrompt.Active`, `MessageId` und `OptionsMask`; gültige Reaktion → `AcceptedReactionSequence` gesetzt, Prompt deaktiviert, `executor.AnswerUserPrompt(...)`. Ungültige Reaktion → `Warning.*`, Reaktionssequenz wird nicht bestätigt.
- Pressrezept: Executor-Event → `Press.RecipeStartRequestSequence` (eigener Zähler); SPS antwortet mit Ack-Sequenz + `RecipeStartResult != Pending` → `executor.AnswerRecipeStart(...)`; `Rejected`/`Error` beendet den Auftrag mit `RecipeStartRejected`.

### Statuspublikation
- `Cmd.Busy/Accepted/Done/Progress/WaferPicked/WaferPlaced` aus Ausführungsphase und `executor.Status`; `Progress` hält `Done`/`Error` bis zum nächsten Auftragsbeginn (sequenzgebunden, §2).
- `Robot.State` abgeleitet (Error/Busy/Ready) — Platzhalter bis Phase 4 (echter Status aus Epson-Protokoll).
- `AdsConnected` sowie `StationChannelConnected`/`RemoteControlConnected` (public Properties für die Kanaladapter aus Phase 4).
- Heartbeat unverändert (100 ms Takt).

### Executor-Interface (Phase-2-Vertrag für Phase 4)
```csharp
bool TryStart(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc, out EpsonExecutionResult failure, out uint requestMask);
EpsonExecutionResult? Execute(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc);
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
