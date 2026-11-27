# Phase 1 — Vertrag: TwinCAT-DUTs ↔ C#-Structs (Stand: abgeschlossen)

## 1. Entscheidungen (D1–D5 umgesetzt)

| Entscheidung | Festlegung |
|---|---|
| D1 | GVL-Pfadnamen bleiben `GVL_EpsonADS.stPlcToIpc` / `stIpcToPlc`; Typnamen `ST_PlcToEpsonIpc` / `ST_EpsonIpcToPlc` (entspricht GVL-Upload) |
| D2 | `InterfaceVersion = 2` auf beiden Seiten (inkompatible Strukturänderung, Breaking Change) |
| D3 | Alle Struct-DUTs mit `{attribute 'pack_mode' := '1'}`; Strings `STRING(255)` Ansi (C#: `ByValTStr, SizeConst=256`); Enum-DUTs ohne expliziten Basistyp (INT/UINT, je 2 Byte — layoutidentisch zum C# `ushort`) |
| D4 | `E_EpsonTool`: `Free = 0` (Legacy: Tool-ID 0 = kein Werkzeug); `Unknown = 255`; 3/4/5/6/8 wie Legacy |
| D5 | Neue Enums `E_EpsonProgress`, `E_EpsonUserReaction`, `E_EpsonPressResult`, `E_EpsonState`, `E_EpsonOutput`, `E_EpsonProtocolCommand` — Zahlenwerte unten |

## 2. Neue Enum-Werte (Phase-1-Festlegung)

- **E_EpsonProgress**: Idle=0, Validating=10, WaitingForResources=20, PreparingPress=30, WaitingForPressRecipe=40, MovingToSource=50, WaferOnRobot=60, MovingToTarget=70, Finishing=80, Done=90, Error=100
- **E_EpsonUserReaction** (Zweierpotenzen → direkt als `OptionsMask`-Bits): None=0, OK=1, No=2, Abort=4, Ignore=8, Retry=16, Yes=32
- **E_EpsonPressResult**: Pending=0, Started=1, Rejected=2, Error=3
- **E_EpsonState**: Offline=0, Connecting=1, Initializing=2, Ready=3, Busy=4, Paused=5, Service=6, Error=7, Stopping=8
- **E_EpsonOutput** (aus Legacy `EOutputIndex`, EpsonProt.cs): CylPressplate=0, ToolConnector=1, CylFillWaferTransfer=2, CylWaferTransfer=3, WaferAbblasen=4, BalligBlasenKammerBelueften=5, ToolVac=6, WaferVac=7, Reserve0=8, PlateVac=9, Reserve1=10, Reserve2=11, Reserve3=12, Ammoniac=13, Unknown=255 (Legacy eUnknown=-1)
- **E_EpsonProtocolCommand** (neu nummeriert, nicht aus C#-Legacy-Enum):
  - Parameterlos: CINIT=1, CPARK=2, CMOVESERV=3, CSTOP=4
  - Bewegung/Werkzeug: CTPUT=10, CTGET=11, CMOVE=12
  - Set-Befehle: CPWR=20 (gesperrt), CSERVICE=21, COUT=22, CCLEAN=23
  - Pressplatte: CPPINIT=30, CPPPREPIN=31, CPPPREPOUT=32, CPPCLOSEIN=33, CPPCLOSEOUT=34
- **E_EpsonCommand** vervollständigt: +GetTool=40, +PutTool=41, +Park=50, +SetServiceMode=60, +CleanTool=70, +PressplateInit=80, +PressplatePrepIn=81, +PressplatePrepOut=82, +PressplateCloseIn=83, +PressplateCloseOut=84, +SetOutput=90, +UserReaction=100 (reserviert), +ResetError=110, +SendSingleProtocolCommand=120

## 3. DUT-Diff gegenüber den hochgeladenen DUTs

**Unverändert (4):** `ST_EpsonTransferState`, `ST_EpsonToolStationState`, `ST_EpsonError`, `E_EpsonSource`

**Geändert (11):**

| DUT | Änderung |
|---|---|
| `ST_PlcToEpsonIpc` | `InterfaceVersion := 2` |
| `ST_EpsonIpcToPlc` | + `AdsConnected`, `StationChannelConnected`, `RemoteControlConnected`, `Robot`, `Press`, `UserPrompt`, `Warning` |
| `ST_EpsonCmd` | + `ThinPlate`, `CleanPosition`, `CleanStepCount`, `OutputIndex`, `OutputSet`, `ServiceEnable`, `Protocol`, `UserMessageId`, `UserReaction`, `UserReactionSequence` |
| `ST_EpsonCmdAck` | + `Busy`, `Accepted`, `Done`, `Progress`, `WaferPicked`, `WaferPlaced` |
| `ST_EpsonExternalState` | (typgleich, über geänderte Substructs) |
| `ST_EpsonPressState` | + `RecipeStartAckSequence`, `RecipeStartResult` |
| `ST_EpsonRobotState` | + `Parked` (Genmark; Stäubli schreibt FALSE) |
| `ST_EpsonLockGrant` | + `GrantSequence` |
| `ST_EpsonLockRequest` | + `HeldMask`, `ReleaseMask`, `ForceReleaseRequest` |
| `E_EpsonCommand` | vervollständigt auf 22 Werte (0–120) |
| `E_EpsonTool` | `Free=0` (statt 1), `Unknown=255` (statt 0) |

**Neu (11):** `ST_EpsonProtocolCmd`, `ST_EpsonWarning`, `ST_EpsonUserPrompt`, `ST_EpsonPressStatus`, `ST_EpsonRobotStatus`, `E_EpsonProtocolCommand`, `E_EpsonProgress`, `E_EpsonUserReaction`, `E_EpsonPressResult`, `E_EpsonState`, `E_EpsonOutput`

## 4. C#-Diff gegenüber `EpsonAdsServiceStructs.cs` (Alt)

- Umbenennungen (Breaking → Version 2): `ST_PlcToIpc`→`ST_PlcToEpsonIpc`, `ST_IpcToPlc`→`ST_EpsonIpcToPlc`, `ST_EpsonExternal`→`ST_EpsonExternalState`, `ST_EpsonCmdStatus`→`ST_EpsonCmdAck`
- Alle Struct-Felder identisch zu den ST-DUTs; `E_EpsonErrorCode` + `VersionMismatch=1005`, `ProtocolCommandNotAllowed=1006`, `InvalidUserReaction=1007`, `RecipeStartRejected=2005`
- Neu: `EpsonAdsContract.InterfaceVersion = 2`

## 5. Layout-Selbsttest (DoD)

Automatischer Abgleich (Parser über ST-DUTs und C#-Datei, pack_mode 1 / Pack=1):

```
ST_EpsonCmd 58=58 | ST_EpsonCmdAck 19=19 | ST_EpsonError 259=259 | ST_EpsonExternalState 27=27
ST_EpsonIpcToPlc 850=850 | ST_EpsonLockGrant 8=8 | ST_EpsonLockRequest 17=17 | ST_EpsonPressState 10=10
ST_EpsonPressStatus 4=4 | ST_EpsonProtocolCmd 19=19 | ST_EpsonRobotState 3=3 | ST_EpsonRobotStatus 16=16
ST_EpsonToolStationState 2=2 | ST_EpsonTransferState 2=2 | ST_EpsonUserPrompt 269=269
ST_EpsonWarning 257=257 | ST_PlcToEpsonIpc 95=95
→ Feldfolge und Offsets stimmen 1:1 überein.
```

Sizes: `ST_PlcToEpsonIpc` = 95 Bytes (Schreibsymbol IPC), `ST_EpsonIpcToPlc` = 850 Bytes. In Phase 2 kann die Notification auf der IPC→SPS-Seite auf die Teilblöcke aufgeteilt werden; die Transaktionsgrenze bleibt die ganze Struktur, da `Pack=1` linear gemarshallt wird.

## 6. Verantwortungstrennung FB_EpsonRobot ↔ FB_EpsonAdsControl

### FB_EpsonAdsControl — reine Transportschicht (Struktur bleibt: Requests / Command / Evaluate* / Properties / States / Alarms)

Besitzt kein Prozesswissen. Nur Postfach, Handshake, Link:

| Baustein | Inhalt |
|---|---|
| `EvaluateLink` | InterfaceVersion-Vergleich (bei Mismatch: RunExecution einfrieren), Heartbeat-Timeout (≥ 1 s, D8), `AdsConnected`/`StationChannelConnected`/`RemoteControlConnected` weiterreichen |
| `EvaluateCommand` | Postfach-SM: IDLE → SEND_COMMAND → WAIT_ACCEPT → WAIT_COMPLETION / ERROR; Sequenz-Dedup (`AcceptedSequence`/`DoneSequence`/`ErrorSequence`); ErrorSequence auch in WAIT_ACCEPT auswerten (D7: `Error.Active` wird nicht durch `!Cmd.Valid` gelöscht, nur via ResetError=110); Ack-Timeout 5 s |
| `RequestCommand` / `Command()` | Gates: `bEnable`, Heartbeat ok, IDLE, `eControlSource=PLC` |
| Lock-Mechanik | `ManageLocks` als reine Masken-/Sequenzverarbeitung: RequestMask/HeldMask-Spiegel, `GrantSequence`-Änderung erkennen, `ReleaseMask` verarbeiten (Grant löschen), `ForceReleaseRequest` quittieren — ohne Ressourcenpolitik |
| Backchannel-Mechanik | `AnswerUserPrompt(reaction)` → `Cmd.UserMessageId/UserReaction/UserReactionSequence++` nur wenn MessageId == `UserPrompt.MessageId` und Reaction ∈ `OptionsMask`; `AnswerRecipeStart(result)` → `Press.RecipeStartAckSequence/Result`; `ResetError()` → Auftrag 110 |
| `UpdateExternalState` | Snapshot veröffentlichen, `External.Sequence++` |
| `UpdateHmi` | Publish der Roh-Contract-Daten (keine Interpretation) |

### FB_EpsonRobot — Prozessschicht (instanziert FB_EpsonAdsControl)

Besitzt das Prozesswissen (Rolle des Legacy-`CEpsonRobot`-Schedulers):

| Baustein | Inhalt |
|---|---|
| Ressourcen-Arbitration | Legacy-Mutexlogik: `ManageLocks` wird hier mit prozessabgeleiteten Allowed-Inputs gerufen (Genmark-Lock nur FillWafer-Bewegungen/Toolwechsel, Stäubli-Lock nur bei internen 3/4-Zoll-Plätzen → Legacy `IsToolChangeStaeubliCollisionPossible`), Recovery via `ForceReleaseRequest` nach Sicherheitsprüfung (Legacy `ReleaseCollision`/`ForceUnlock`) |
| Auftragserzeugung | Semantische Befehle in Legacy-Reihenfolge; Tool-Strategie `ToolFromExternal`/`OldToolToExternal` aus `IsToolPlaceFree`/`IsToolInToolPlace` + `Robot.CurrentToolType` (deshalb Feedback-Feld in ST_EpsonRobotStatus) |
| Bedienerinteraktion | Antworten auf `UserPrompt` (ersetzt Legacy `CToolValidatingWindow`/`DoMsg`): HMI-Meldung aus `UserPrompt.Text/OptionsMask`, dann `AnswerUserPrompt` mit operatorbestätigter Reaktion; `NewStartRequired`-Flag bei Init-Tool-Problem |
| Pressrezept-Koordination | Reagiert auf `Press.RecipeStartRequestSequence`: startet WaferPress-Rezept (Legacy `WaferPress.OnCloseIn/OnStartRecipe`), parkt Genmark/Stäubli, antwortet `AnswerRecipeStart(Started/Rejected/Error)` |
| Bestandsführung | Aus `Cmd.Progress` (`WaferOnRobot` → Transfer-Lock freigeben per ReleaseMask, D6), `WaferPicked`/`WaferPlaced`, `Robot.WaferPresent/FillWaferPresent` Legacy-Bestand (Wafer-/Tool-Modell) synchron halten |
| Snapshot-Erzeugung | Füllt `External.*` aus den Stations-FBs (Transfer, Press, ToolStation, Genmark, Stäubli) und ruft `UpdateExternalState` |
| Zustands-/Alarmabbildung | Mappt `Robot.State`/`Error`/`Warning` auf Stationszustand/HMI; Alarmlatch bleibt im Transport-FB, Anlagenprüfung/Quittierung hier |

### Datenfluss (Merksatz)

**FB_EpsonRobot entscheidet — FB_EpsonAdsControl transportiert.** Der Transport-FB leitet nie aus Prozesswerten Entscheidungen ab; der Robot-FB schreibt nie direkt ADS-Symbole, sondern nur über den Transport-FB.

## 7. Offene Punkte (für Phase 2/4)

1. `E_EpsonProtocolCommand`-Nummern sind Neudefinition — IPC-allowlist (Phase 2) muss deckungsgleich sein; CPWR wird abgewiesen.
2. `E_EpsonUserReaction` als OptionsMask-Bits ist eine Festlegung, die in der Schnittstellenbeschreibung so noch nicht steht (dort nur Werteliste) — in der Doku ergänzen.
3. Legacy `EpsonRemoteControl.cs`, `StateMachine.cs`, `EpsonServSM.cs`, `ToolType.cs` fehlen weiterhin (Phase 4).
4. Enum-Basistyp in ST: ohne expliziten Basistyp (2 Byte, layoutidentisch zu C# `ushort`). Falls 100 % Doc-Konformität zu `UINT` gewünscht: `TYPE E_X : UINT (...)` — rein optional.
