# Betrieb und Test: ADS-Schnittstelle SPS ↔ Epson-IPC

Stand: Vertrag v2 (`EpsonAdsContract.InterfaceVersion = 2`), Phase 3 + Hotfix
ResetError-Deadlock, Testbench mit IPC-Simulator.

## 1. Beteiligte Komponenten

| Komponente | Datei | Rolle |
|---|---|---|
| `FB_EpsonAdsControl` | `TwinCAT/POUs/FB_EpsonAdsControl.xml` | Transport-Layer auf der SPS: Postfach-State-Machine, Lock-Mechanik, Link-Supervision, Backchannels. Enthält **keine** Prozesslogik. |
| `FB_EpsonRobot` (Phase 4, später) | — | Instanziert `FB_EpsonAdsControl`, entscheidet über Ressourcen, Auftragsreihenfolge, Bedienerinteraktion. |
| `FB_EpsonRobotAdsTestbench` | `TwinCAT/POUs/FB_EpsonRobotAdsTestbench.xml` | Testumgebung: ruft den Transport-FB und **simuliert die IPC-Seite** (Heartbeat, Accept/Done, Fehlerinjektion). |
| `GVL_EpsonADS` | `TwinCAT/GVL/GVL_EpsonADS.xml` | gemeinsamer Datenaustausch: `stPlcToIpc` (SPS → IPC), `stIpcToPlc` (IPC → SPS). |
| `EpsonAdsService` | `IPC/EpsonAdsService.cs` | .NET `BackgroundService`, ADS-Verbindung zur SPS (Port 851), 100-ms-Zyklus. |
| `EpsonDummyCommandExecutor` | `IPC/EpsonDummyCommandExecutor.cs` | simulierter Roboter: Validierung + zeitgesteuerte Ablaufphasen statt echter EPSON-Steuerung. |

## 2. Inbetriebnahme

### 2.1 TwinCAT-Seite
1. Alle Dateien aus `TwinCAT/DUTs`, `TwinCAT/POUs`, `TwinCAT/GVL` in das
   PLC-Projekt importieren. **Achtung**: `ST_EpsonAdsControlProperties` und
   `ST_EpsonAdsControlHmi` im Projekt vorhandene Fassungen nur um die
   Phase-3-Felder ergänzen (Liste in `docs/Phase3_FB_EpsonAdsControl.md`).
2. Referenzen sicherstellen: `Tc2_Standard` (TON), Eventlogger
   (`FB_Base`-Framework), `TcHmiSymbol`-Attribute sind optional.
3. `FB_EpsonAdsControl` zyklisch aufrufen (in der Testbench über
   `fbEpsonADS()` in Abschnitt 2). Ein Aufruf pro Task-Zyklus, keine
   Mehrfach-Instanz gegen dieselbe GVL.
4. `stConfig` setzen (in der Testbench automatisch, Abschnitt 0):
   `nTimeoutHeartbeatMs := 1000`, `nTimeoutAckMs := 5000`,
   `nTimeoutCmdMs := 30000`.

### 2.2 IPC-Seite (.NET)
1. Projekt mit `TwinCAT.Ads` und `Microsoft.Extensions.Hosting` erstellen;
   `EpsonAdsService` als Hosted Service registrieren,
   `IEpsonCommandExecutor`-Implementierung einbinden (Test: `EpsonDummyCommandExecutor`).
2. Standard-Ziel: lokale SPS `AmsNetId.Local`, **AmsPort 851**. Für eine
   entfernte SPS `_plcAddress` anpassen.
3. Der Service schreibt `stIpcToPlc` und liest `stPlcToIpc` zyklisch
   (CycleTimeMs = 100, Änderungs-Notifications mit 10 ms).

## 3. Verwendung von `FB_EpsonAdsControl`

Grundsatz: **Der Baustein transportiert — er entscheidet nicht.** Alle
prozessbezogenen Entscheidungen (wo darf der Roboter hin, welcher Auftrag als
nächstes) trifft später `FB_EpsonRobot`.

### 3.1 `Run()` — Aufrufreihenfolge
`Run()` führt intern `EvaluateLink()` → `EvaluateCommand()` → `UpdateStatus()`
aus und muss zyklisch gerufen werden.

### 3.2 `Command(...) : BOOL` — Auftrag erteilen
Nur im State IDLE und bei lebendem IPC-Link möglich; bei gelatchtem
IPC-Fehler (`stIpcToPlc.Error.Active`) wird nur `ResetError` angenommen.
Rückgabe FALSE = Ablehnung (Meldung im Eventlogger), TRUE = Auftrag im Postfach.

Parameter je Auftragsart:

| Auftrag | Relevante Parameter | Dummy-Validierung |
|---|---|---|
| `Initialize` | — | immer zulässig |
| `StartProgram` / `ContinueProgram` | — | `External.AutoMode` und `SafetyPermissive` müssen TRUE sein (1003) |
| `PauseProgram` / `StopProgram` / `Park` / `SetServiceMode` | `bServiceEnable` (bei Service) | immer zulässig |
| `MoveWafer` | `eSource`, `nTargetSlot` (0..23), `eRequiredTool` (≠ Free/Unknown), `nMinPressDistanceUm`, `bThinPlate` | Vorbedingungen Transfer/Press (1003), `bThinPlate` = `External.Press.ThinPlate` (1002) |
| `GetTool` / `PutTool` / `ChangeTool` | `bToolFromExternal`, `bOldToolToExternal`, `bUseExternalPutFallback` | Werkzeug-/Lagerprüfungen |
| `CleanTool` | `nCleanPosition`, `nCleanStepCount` (> 0, sonst 1002) | fordert Lock ToolStation |
| `PressplateInit/PrepIn/PrepOut/CloseIn/CloseOut` | `bThinPlate` (nur CloseIn: muss zu `External.Press.ThinPlate` passen) | fordert Lock WaferPress |
| `SetOutput` | `eOutputIndex` (kein Reserve/Unknown, sonst 1002), `bOutputSet` | immer zulässig |
| `SendSingleProtocolCommand` | `stProtocol` | Allowlist; `CPWR` → 1006 |
| `ResetError` | — | nur über `ResetError()`, nicht über `Command()` |

### 3.3 `ResetError() : BOOL` — Fehler quittieren
Nur im State ERROR, bei `bEnable`, lebendem IPC-Link und ohne hängende
Anforderung. Erzeugt den Auftrag `E_EpsonCommand.ResetError` (110), der
regulär durch Accept→Done läuft. Nach erfolgreichem Abschluss werden
`nErrorCode`/`sErrorText` zurückgesetzt; der IPC löscht `Error.Active`
(seine Fehler) selbst bei Done des Auftrags 110.

### 3.4 `UpdateExternalState(...)` — Prozessdaten liefern
Zyklisch vor/nach `Run()` rufen. Überträgt `bAutoMode`, `bSafetyPermissive`
und die fünf Station-Structs (`stTransfer`, `stPress`, `stToolStation`,
`stGenmark`, `stStaeubli`) als `External`-Snapshot in `stPlcToIpc`
(mit `External.Sequence`/`Valid`). Der IPC validiert Aufträge gegen diesen
Snapshot — veraltete oder invalide Snapshots führen zu 1003
(`MissingExternalState`).

### 3.5 `ManageLocks(...)` — Ressourcen-Mechanik
Zyklisch rufen. Die fünf `b*Allowed`-Eingänge bestimmen, welche Ressourcen
(Bits 0–4: WaferTransfer, WaferPress, ToolStation, GenmarkArea, StaeubliArea)
der IPC aktuell erhält; Änderungen erhöhen `Lock.GrantSequence`.

- **Keine Ressourcenpolitik im FB**: Der IPC beantragt (`RequestMask`),
  hält (`HeldMask`) und gibt zurück (`ReleaseMask` — Legacy-Semantik,
  z. B. `WaferOnRobot` gibt die Transferstation frei, während der Auftrag
  noch läuft). Die SPS entscheidet nur über `b*Allowed`.
- `bForceReleaseAllowed := TRUE` erlaubt dem IPC, alle Grants per
  `ForceReleaseRequest` zurückzugeben (Recovery nach sicherem Stopp,
  Legacy `ForceUnlock`). Nur gesetzt, wenn die übergeordnete Logik
  (später `FB_EpsonRobot`) den sicheren Zustand bestätigt.

### 3.6 `AnswerUserPrompt(eReaction) : BOOL` — Bedienerprompt beantworten
Backchannel, auch während eines laufenden Auftrags. Nur erfolgreich, wenn
`stIpcToPlc.UserPrompt.Active`, die `MessageId` zum aktiven Prompt passt und
`eReaction` als Bit in `OptionsMask` zugelassen ist (sonst Warnung 1007-seitig
IPC). Schreibt `Cmd.UserMessageId`/`UserReaction`/`UserReactionSequence`
(neue Sequenz). Die Reaktion `No/Abort/...` führt IPC-seitig zu
`EpsonCommandRejected` (2003).

### 3.7 `AnswerRecipeStart(eResult) : BOOL` — Pressrezept-Anforderung quittieren
Backchannel während MoveWafer. Antwortet auf
`stIpcToPlc.Press.RecipeStartRequestSequence` mit
`Press.RecipeStartAckSequence` + `RecipeStartResult`. Alles außer
`E_EpsonPressResult.Started` führt IPC-seitig zu `RecipeStartRejected` (2005).
Jede Anfrage wird genau einmal quittiert (`nLastRecipeAckSequence`).

### 3.8 Properties (Leseschnittstelle)

| Property | Bedeutung |
|---|---|
| `eState` | Postfach-State: IDLE / SEND_COMMAND / WAIT_ACCEPT / WAIT_COMPLETION / ERROR |
| `bBusy`, `bDone` | Auftrag läuft / 1-Zyklus-Puls „erfolgreich beendet" |
| `bAlarmCommandError` | TRUE im State ERROR (Fehler-Bit für Aufrufer/HMI) |
| `nErrorCode`, `sErrorText` | gelatchter PLC-Fehler (8001 Ack-Timeout, 8002 Cmd-Timeout, 8003 Heartbeat, 8004 ADS, 9001 Version, sonst IPC-Code) |
| `nActiveSequence` / `nDoneSequence` / `nErrorSequence` | Sequenzbuchhaltung |
| `dwRequestMask` / `dwGrantedMask` / `dwHeldMask` / `dwReleaseMask` | Lock-Bitmap |
| `eProgress` | Ablaufphase des aktiven Auftrags (aus `Cmd.Progress`) |
| `bWaferPicked` / `bWaferPlaced` | Bestands-Feedback des IPC |
| `eRobotState`, `nCurrentToolType` | abgeleiteter Roboterstatus / aktuelles Werkzeug |
| `stUserPrompt` | aktiver Prompt (MessageId, OptionsMask, Text) |
| `bHeartbeatOk`, `bAdsConnected`, `bStationChannelConnected`, `bRemoteControlConnected` | Link-Zustand |
| Alarm-Properties (`bAlarmTimeoutAck`, `bAlarmTimeoutCmd`, `bAlarmHeartbeatLost`, `bAlarmVersionMismatch`) | Eventlogger-Kopplung |

### 3.9 HMI-Schnittstelle (`stHmi`)
Mirror aller Properties plus Bedienelemente:
- `bCmdReset` (Flanke) → `ResetError()` — nur bei `eControlSource = HMI`.
- `bCmdUserReaction` (Flanke) + `eCmdUserReaction` → `AnswerUserPrompt()`.

## 4. Ablaufmodell eines Auftrags

```
IDLE ──(RequestCommand)──► SEND_COMMAND ──► WAIT_ACCEPT ──► WAIT_COMPLETION ──► IDLE (bDone-Puls)
                                (Valid:=TRUE)   │ AcceptedSequence    │ DoneSequence
                                                │ oder ErrorSequence  │ oder ErrorSequence
                                                ▼                     ▼
                                              ERROR ◄──────────────────┘
                                                │ ResetError (Auftrag 110)
                                                ▼
                                              IDLE
```

- Sequenzen sind eindeutig (0 ist ungültig); Dedup im IDLE über
  `stProperties.stLastCmd`.
- Timeouts: Ack (`nTimeoutAckMs`, Fehler 8001) und Auftrag (`nTimeoutCmdMs`,
  8002). Heartbeat ≥ 1000 ms (8003), ADS-Verlust während Auftrags (8004).
- Im ERROR bleibt der Baustein latched; einziges Entrinnen ist
  `ResetError()` → Auftrag 110. Eine echte Vertragsabweichung (Version)
  wird vom IPC erneut abgelehnt (1005) und bleibt somit bestehen.

## 5. Testumgebung A: SPS-Testbench ohne IPC

Die Testbench simuliert die IPC-Seite in ST (Abschnitt 1): schreibt
`InterfaceVersion := 2`, Verbindungsflags, zyklischen Heartbeat, und quittiert
Aufträge: Accept im nächsten Zyklus, Done nach `tSimDoneDelay` (Standard 1 s).

Bedienelemente:

| Eingang | Wirkung |
|---|---|
| `bAutoSequence` | Auto-Ablauf Init → StartProgram → MoveWafer → ChangeTool |
| `bSimIpcOnline := FALSE` | Verbindungstrennung: Heartbeat stoppt, `AdsConnected` FALSE |
| `bSimIpcReject` | Einmalig: nächster Auftrag wird vom „IPC" abgelehnt (1001 + `ErrorSequence`) |
| `bAutoAnswerPrompts` | Prompt → `AnswerUserPrompt(OK)`, Pressrezept → `AnswerRecipeStart(Started)` |
| `tSimDoneDelay` | simulierte Auftragsdauer |
| `bReqCmd*` | manuelle Einzelaufträge (Init, Start/Continue/Pause/Stop, Move, ChangeTool) |
| `bReqCmdResetError` | Quittierung über Auftrag 110 |

Beobachten: `bDoneCount`/`nErrorCount`, `fbEpsonADS.eState`, `nErrorCode`,
`stIpcToPlc.Lock.*`, `stHmi.eProgress`.

## 6. Testumgebung B: End-to-End mit `EpsonAdsService` + Dummy

Der Dummy-Executor simuliert realistische Ablaufphasen mit festen Zeiten
(Dummy ersetzt den EPSON-Roboter vollständig):

| Auftrag | Ablauf (Progress → Dauer) |
|---|---|
| `MoveWafer` | PreparingPress 600 ms → **Pressrezept-Anforderung** → MovingToSource 800 ms → WaferOnRobot 400 ms (`WaferPicked`, Release WaferTransfer) → MovingToTarget 800 ms → Finishing 400 ms (`WaferPlaced`) |
| `ChangeTool` | Validating 300 ms → **UserPrompt** (OK/Yes) → Finishing 800 ms |
| alle übrigen | Validating 300 ms → Finishing 300 ms |

Validierungen des Dummys (Fehler → `ErrorSequence`, Auftrag endet im ERROR):
- `External.Valid = FALSE` → 1003 (auch während der Ausführung)
- Start/Continue ohne AutoMode/Safety → 1003
- MoveWafer: Slot > 23 → 1002; RequiredTool Free/Unknown → 1002;
  ThinPlate-Mismatch → 1002; Vorbedingungen Transfer/Press → 1003
- CleanTool ohne StepCount → 1002; PressplateCloseIn mit ThinPlate-Mismatch → 1002
- SetOutput auf Reserve/Unknown → 1002
- `SendSingleProtocolCommand` mit `CPWR` → 1006 (gesperrt)
- Prompt mit No/Abort → 2003; Rezept mit ≠ Started → 2005

Der Service überwacht zusätzlich: Snapshot-Stale (> 3000 ms ohne
`External.Sequence`-Wechsel), Versions-Kompatibilität (Freeze + 1005),
Heartbeat (100 ms) und Verbindungsflags.

## 7. Testfälle

Legende: Umgebung A = Testbench-Simulator, B = End-to-End mit C#-Dummy.

| # | Umgebung | Ziel | Schritte | Erwartung |
|---|---|---|---|---|
| 1 | A+B | Kaltstart ohne IPC | SPS starten, IPC still | State IDLE, `bHeartbeatOk` FALSE, **kein** Fehler 9001 |
| 2 | A | Normalablauf | `bAutoSequence := TRUE` | 4 Aufträge je Done-Puls, `nDoneCount = 4`, `nErrorCount = 0`, Sequence monoton steigend |
| 3 | A+B | Manuelle Wrapper-Befehle | `bReqCmdInit`, `bReqCmdStartProg`, … je einmal | je Done-Puls; zwischen zwei Aufträgen State kurz IDLE |
| 4 | A+B | Validation-Reject | `bSimIpcReject := TRUE`, dann Auftrag | WAIT_ACCEPT → ERROR mit `ErrorSequence`-Pfad, `nErrorCode = 1001` (B: Code der Dummy-Validierung), `Error.Active` gelatcht |
| 5 | A+B | Error-Latch + Reset | nach #4: `bReqCmdResetError` | ERROR → IDLE → Auftrag 110 → Done; `nErrorCode` 0, `Error.Active` FALSE, neue Aufträge wieder möglich |
| 6 | A | Stale Request | `bSimIpcReject`, dann `Command()` während ERROR versuchen | `Command` FALSE; `ResetError` funktioniert trotzdem (kein „request pending") |
| 7 | A+B | Heartbeat-Verlust ohne Auftrag | `bSimIpcOnline := FALSE` | nach `nTimeoutHeartbeatMs` `bHeartbeatOk` FALSE, kein ERROR |
| 8 | A+B | Heartbeat-Verlust während Auftrag | Auftrag starten, sofort `bSimIpcOnline := FALSE` | Fehler 8003 (ADS aus: 8004), ERROR latched; nach Wiederkehr Reset per #5 |
| 9 | A+B | Ack-Timeout | Auftrag starten und Simulator Accept verweigern (B: Service anhalten) | nach `nTimeoutAckMs` Fehler 8001, Alarm `bAlarmTimeoutAck` |
| 10 | B | Versions-Freeze | Service mit `InterfaceVersion = 1` kompilieren | 9001 auf SPS, 1005 am IPC, keine Ausführung; Mismatch nicht weg-resetbar |
| 11 | A+B | ThinPlate-Mismatch | `stPress.ThinPlate` am External ändern, ohne `bThinPlate` anzupassen (B), bzw. MoveWafer mit abweichendem Bit (A: via `bSimIpcReject` nicht abgedeckt — in B testen) | Ablehnung 1002 |
| 12 | B | Pressrezept-Handshake | MoveWafer; wenn `Press.RecipeStartRequestSequence` ≠ 0: über `fbEpsonADS.AnswerRecipeStart(eResult := Started)` antworten (fließt nach `stPlcToIpc.External.Press.RecipeStartAckSequence/Result`, dort wartet der Service) | Progress WaitingForPressRecipe → Started → Done; Release von WaferPress/WaferTransfer; ohne Antwort → Ack-Timeout 8002 |
| 13 | B | Prompt-Ablehnung | MoveWafer/ChangeTool, wenn `UserPrompt.Active`: `fbEpsonADS.AnswerUserPrompt(eReaction := E_EpsonUserReaction.No)` (fließt nach `Cmd.UserReaction*`) | Fehler 2003, ERROR latched, Locks bleiben bis Reset gehalten |
| 14 | B | Lock-Lebenszyklus | MoveWafer beobachten | RequestMask = Transfer+Press; nach WaferOnRobot Release WaferTransfer (Grant verschwindet trotz laufendem Auftrag); nach Done Release Rest |
| 15 | B | ForceRelease | nach #13 Reset verweigern (IPC offline), Service-Fehler erzeugen, `ForceReleaseRequest` setzen, `ManageLocks(bForceReleaseAllowed := TRUE)` | alle Grants 0; mit FALSE keine Änderung |
| 16 | B | Snapshot-Stale | External-Updates stoppen (`UpdateExternalState` nicht mehr rufen, `External.Valid` FALSE) | laufender Auftrag bricht mit 1003 ab |
| 17 | A+B | Doppelbefehl/Dedup | denselben `Command`-Aufruf zweimal hintereinander | nur ein Auftrag mit neuer Sequence |
| 18 | A+B | SetOutput-Reserve | `eOutputIndex := E_EpsonOutput.Reserve0` | Ablehnung 1002 |

## 8. Beobachtungsmittel

- **SPS**: Online-View auf `fbEpsonADS` (`stHmi`), Watch auf
  `GVL_EpsonADS.stPlcToIpc` / `stIpcToPlc`, Eventlogger (Alarme
  Timeout-Ack/Timeout-Cmd/Heartbeat/Version/CommandError).
- **IPC**: Logs von `EpsonAdsService` (Start, Commands mit Sequenz,
  Warnungen), `Status`-Flags `StationChannelConnected` /
  `RemoteControlConnected`.
- **Hilfreiche Watch-Ausdrücke**: `stIpcToPlc.Cmd.AcceptedSequence`,
  `DoneSequence`, `ErrorSequence`, `Lock.RequestMask`, `Lock.HeldMask`,
  `Lock.ReleaseMask`, `Lock.GrantSequence`, `UserPrompt.Active`,
  `Press.RecipeStartRequestSequence`.

## 9. Bekannte offene Punkte

- `TO_DWORD(eReaction)` in `AnswerUserPrompt` im XAE verifizieren (ggf.
  Bitvergleich per CASE).
- `E_EpsonAdsControlState`, `E_ControlSource`, `ST_EpsonAdsControlAlarms`,
  `ST_EpsonAdsControlConfig`, `FB_Base` sind Framework-Typen des Projekts und
  hier nicht enthalten.
- ST- und C#-Code wurden statisch geprüft (XML-Wellformedness, Feld-/Enum-
  Konsistenz, Layout-Selbsttest Phase 1), aber nicht kompiliert — erster
  Übersetzungs-/Testlauf im Projektumfeld erforderlich.
