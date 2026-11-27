# Phase 3: FB_EpsonAdsControl — Erweiterung auf Vertrag v2

Grundstruktur (Requests / Command / Evaluate\* / Properties / States / Alarms) bleibt
unverändert — der Baustein transportiert weiterhin nur; die Prozesslogik entsteht
später in `FB_EpsonRobot` (Phase 4), welches `FB_EpsonAdsControl` instanziert.

Merksatz: **FB_EpsonRobot entscheidet — FB_EpsonAdsControl transportiert.**

## Geänderte Dateien

| Datei | Änderung |
|---|---|
| `TwinCAT/POUs/FB_EpsonAdsControl.xml` | Erweiterung auf Vertrag v2 (Details unten), POU-GUIDs unverändert |
| `TwinCAT/DUTs/ST_EpsonAdsControlProperties.xml` | rekonstruiert aus POU-Nutzung (siehe Merge-Hinweis) |
| `TwinCAT/DUTs/ST_EpsonAdsControlHmi.xml` | rekonstruiert aus POU-Nutzung (siehe Merge-Hinweis) |

## Änderungen im POU

### Vertragsversion
- `nEPSON_INTERFACE_VERSION := 2` (muss `EpsonAdsContract.InterfaceVersion` im IPC entsprechen).

### Postfach / RequestCommand / Command
- Neue VARs: `nLastReactionSequence`, `nLastRecipeAckSequence`, `nLastGrantedMask`,
  `bReqCommand` (ersetzt `bReqReset`).
- `Command`/`RequestCommand` um die v2-Parameter erweitert: `bThinPlate`,
  `nCleanPosition`, `nCleanStepCount`, `eOutputIndex`, `bOutputSet`,
  `bServiceEnable`, `stProtocol`.
- IDLE schreibt alle neuen `stPlcToIpc.Cmd`-Parameter — **außer** den
  Backchannel-Feldern `Cmd.UserMessageId` / `Cmd.UserReaction` /
  `Cmd.UserReactionSequence` (werden nur von `AnswerUserPrompt` gesetzt).
- `ResetError` ist jetzt ein normaler Auftrag (`stReqCmd.Code :=
  E_EpsonCommand.ResetError`) statt einer Sonderbehandlung — damit läuft er durch
  denselben Handshake (Accept → Done) und bleibt im Postfach sichtbar.
  Nur im State ERROR und bei freigegebener Verbindung zulässig.

### EvaluateCommand
- **WAIT_ACCEPT prüft jetzt `ErrorSequence` zuerst**, dann `AcceptedSequence`:
  Eine vom IPC abgelehnte Validierung (z. B. 1001/1002/1006/1007/2005) ist
  unmittelbar im State ERROR sichtbar, statt in WAIT_ACCEPT zu hängen
  (Korrektur des Legacy-Bruchs aus der Inkonsistenzanalyse).
- IDLE nutzt `stProperties.stLastCmd` für Dedup-Vergleich (Sequenz-Dedup).

### ManageLocks (nur Mechanik, keine Ressourcenpolitik)
- `ReleaseMask` aus dem IPC wird verarbeitet: `dwGrants AND NOT dwRelease`
  (Revokation, Legacy-Semantik `WaferOnRobot → Release`).
- `ForceReleaseRequest` wird nur bei `bForceReleaseAllowed := TRUE` ausgeführt
  (Legacy `ForceUnlock`); die Freigabe-Entscheidung trifft `FB_EpsonRobot`.
- Bei Maskenänderung wird `stPlcToIpc.Lock.GrantSequence` inkrementiert
  (Skip 0), Edge-Detection über `nLastGrantedMask`.

### Backchannels
- `AnswerUserPrompt(eReaction)`: prüft `UserPrompt.Active`, gleiche
  `MessageId` wie aktiver Prompt, Bit in `OptionsMask`; setzt
  `Cmd.UserMessageId`, `Cmd.UserReaction`, `Cmd.UserReactionSequence`
  (neue Sequenz). Reaktion während eines laufenden Auftrags möglich.
- `AnswerRecipeStart(eResult)`: schreibt `Press.RecipeStartAckSequence` +
  `Press.RecipeStartResult`; verarbeitet jede ausstehende Anfrage genau einmal
  (`nLastRecipeAckSequence`).
- `UpdateExternalState` presst die IPC-Backchannel-Daten (`Robot`, `Press`,
  `UserPrompt`, `Warning`, `StationChannelConnected`, `RemoteControlConnected`,
  `AdsConnected`) in `stProperties`; das Press-Pufferfeld ist lokal
  (`stPressBuf`), damit der Backchannel nicht vom Aufrufer überschrieben wird.

### UpdateStatus / Properties / HMI
- Neue Methode `UpdateStatus` (PROTECTED, Folder States) füllt `stProperties`
  aus der GVL; `Run()` ruft sie auf.
- Neue Properties: `bAdsConnected`, `bWaferPicked`, `bWaferPlaced`,
  `dwHeldMask`, `dwReleaseMask`, `eProgress`, `eRobotState`,
  `nCurrentToolType`, `stUserPrompt`.
- `UpdateHmi` kopiert alle Properties-Felder nach `stHmi` inkl. Trigger-FF
  `fbTrigUserReaction` für `bCmdUserReaction` (Bedienerreaktion aus dem HMI
  zurück an den IPC).

### Nicht gelieferte Framework-Typen
`ST_EpsonAdsControlAlarms`, `ST_EpsonAdsControlConfig`, `E_ControlSource`,
`E_EpsonAdsControlState` und `FB_Base` sind Framework-Bestandteile des
PLC-Projekts und wurden **nicht** neu angelegt. Der POU ist unverändert kompatibel
zur bestehenden Struktur.

## Merge-Hinweis: Properties-/Hmi-DUTs

Die beiden DUTs wurden aus der POU-Nutzung rekonstruiert (Originale wurden nie
hochgeladen). Falls sie im Projekt existieren: **nur die Phase-3-Felder mergen**,
nicht ersetzen:
- `ST_EpsonAdsControlProperties`: neu sind `bWaferPicked`, `bWaferPlaced`,
  `bAdsConnected`, `bStationChannelConnected`, `bRemoteControlConnected`,
  `bWarning`, `sWarningText`, `nGrantSequence`, `dwRequestMask`,
  `dwGrantedMask`, `dwHeldMask`, `dwReleaseMask`, `eProgress`, `eRobotState`,
  `nCurrentToolType`, `stPrompt`, `stLastCmd`.
- `ST_EpsonAdsControlHmi`: dieselben Felder zusätzlich zu den Framework-Feldern
  (`sName`, `bEnable`, `bError`, `eControlSource`, `bCmdReset`,
  `bCmdUserReaction`, `eCmdUserReaction`).

## Konfiguration (D8)

Empfohlene Werte in `stConfig`:
- `nTimeoutHeartbeatMs >= 1000` (IPC-Heartbeat-Periode ist 100 ms; Überwachung
  braucht Reserven für IPC-Reconnect).
- `nTimeoutAckMs = 5000` (deckt IPC-Reconnect ab).
- `nTimeoutCmdMs` auftragsabhängig (Moves lang, Wrapper kurz) — setzt
  `FB_EpsonRobot`.

## Offene Verifikationspunkte (TwinCAT-Umfeld)

- `TO_DWORD(eReaction)` in `AnswerUserPrompt` (Enum→DWORD): im TwinCAT-Compiler
  verifizieren; falls abgelehnt, Bitvergleich per `CASE`/Vergleich mit den
  Enumkonstanten ersetzen.
- ST-Code kann in dieser Umgebung nicht kompiliert werden (TwinCAT fehlt);
  geprüft wurden XML-Wellformedness, GUID-Eindeutigkeit (104 eindeutige GUIDs)
  und die Feldkonsistenz POU ↔ DUT (Properties: 27/27, HMI: 33/33 Felder
  abgedeckt).
