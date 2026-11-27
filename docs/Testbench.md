# Testbench: FB_EpsonRobotAdsTestbench (Vertrag v2)

Die vorherige Version war vollständig auskommentiert. Zwei Ursachen:
`fbEpsonADS.bError` existierte nie (korrekter Fehler-Indikator ist
`bAlarmCommandError`), und ohne IPC-Simulation konnte kein Auftrag je
quittiert werden (kein Heartbeat → `RequestCommand` lehnt ab, WAIT_ACCEPT
läuft in den Ack-Timeout).

## Anpassungen

- **IPC-Simulator** in der Testbench (Abschnitt 1): schreibt
  `GVL_EpsonADS.stIpcToPlc` — `InterfaceVersion := 2`, `AdsConnected`,
  `StationChannelConnected`, `RemoteControlConnected`, zyklischer
  `Heartbeat`, Accept-/Done-Handshake über `tSimDoneDelay`,
  ResetError-Quittierung löscht `Error.Active`.
- **Eingänge**: `bSimIpcOnline` (Verbindungstrennung testen → Fehler 8003/8004),
  `bSimIpcReject` (einmalige Validierungsablehnung → ErrorSequence-Pfad in
  WAIT_ACCEPT, danach Error-Latch D7), `bAutoAnswerPrompts`
  (Bedienerprompt → `AnswerUserPrompt(OK)`, Pressrezept-Anforderung →
  `AnswerRecipeStart(Started)`), `tSimDoneDelay`.
- **Rezept-Simulation**: bei laufendem MoveWafer setzt der Simulator 300 ms
  nach Accept `Press.RecipeStartRequestSequence` (Verhalten des echten
  Service) — erst dadurch wird `AnswerRecipeStart` in Umgebung A überhaupt
  erreichbar.
- **Konfiguration**: `stConfig.nTimeoutHeartbeatMs := 1000`,
  `nTimeoutAckMs := 5000`, `nTimeoutCmdMs := 30000` (D8).
- **ManageLocks** um `bForceReleaseAllowed := TRUE` ergänzt.
- **`Command`-Aufrufe** mit allen v2-Parametern (ThinPlate aus
  `stPress.ThinPlate` beim MoveWafer, übrige neutral).
- Fehlererkennung über `bAlarmCommandError` (Edge) statt `bError`.
- Auto-Sequenz unverändert: Init → StartProgram → MoveWafer → ChangeTool.

## ST_EpsonRobotAdsData

- `stGenmark`/`stStaeubli` initialisieren zusätzlich `Parked := TRUE`
  (neues Feld in `ST_EpsonRobotState`, wird u. a. für FillWafer-Moves geprüft).
- GUIDs unverändert; Rest identisch zur hochgeladenen Fassung.
