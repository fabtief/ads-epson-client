using System.Runtime.InteropServices;

namespace EpsonRobot.Service.Ads
{
    public enum E_EpsonCommand : ushort
    {
        None                     = 0,   // Kein Auftrag
        Initialize               = 10,  // Initialisieren und Parken (CInitEpsonSM)
        StartProgram             = 20,  // Controllerprogramm starten (StartEpsonProgSM)
        ContinueProgram          = 21,  // Controllerprogramm fortsetzen
        PauseProgram             = 22,  // Controllerprogramm pausieren
        StopProgram              = 23,  // Controllerprogramm geordnet stoppen
        MoveWafer                = 30,  // Wafertransport mit Werkzeugprüfung (CPutGetEpsonSM)
        GetTool                  = 40,  // Werkzeug aus internem/externem Lager holen (CGetToolEpsonSM)
        PutTool                  = 41,  // Aktuelles Werkzeug intern/extern ablegen (CPutToolEpsonSM)
        ChangeTool               = 42,  // Vollständiger Werkzeugwechsel (CChangeToolEpsonSM)
        Park                     = 50,  // Epson in bestätigte Parkposition fahren (CParkPositionEpsonSM)
        SetServiceMode           = 60,  // Service-Modus setzen (CServEpsonSM)
        CleanTool                = 70,  // Werkzeugreinigung (CCleanEpsonSM)
        PressplateInit           = 80,  // Pressplatte initialisieren (CPPINIT)
        PressplatePrepIn         = 81,  // Pressplatte für Waferempfang vorbereiten
        PressplatePrepOut        = 82,  // Pressplatte für Waferabgabe vorbereiten
        PressplateCloseIn        = 83,  // Pressplattenempfang abschliessen (ThinPlate beachten)
        PressplateCloseOut       = 84,  // Pressplattenabgabe abschliessen
        SetOutput                = 90,  // Einzelnen Epson-Ausgang im Servicebetrieb schalten (COUT)
        UserReaction             = 100, // Reserviert: Antwort läuft über Cmd.UserReaction*, nicht als Auftrag
        ResetError               = 110, // Fehler nur nach lokaler Controller-/Safety-Prüfung zurücksetzen
        SendSingleProtocolCommand = 120 // Genau einen typisierten Stationsbefehl aus Cmd.Protocol senden
    }

    public enum E_EpsonProtocolCommand : ushort
    {
        None        = 0,  // Kein Einzelbefehl
        CINIT       = 1,  // Stationsprotokoll initialisieren
        CPARK       = 2,  // Parkposition anfahren
        CMOVESERV   = 3,  // Serviceposition anfahren
        CSTOP       = 4,  // Geordneter Stopp (kein Safety-Not-Halt; Ergebnis = Versand)
        CTPUT       = 10, // EXT, optional NOPARK
        CTGET       = 11, // TOOL, EXT
        CMOVE       = 12, // EXT, POS, DMIN, THIN
        CPWR        = 20, // SET; bis Freigabe eines sicheren Power-Ablaufs abweisen
        CSERVICE    = 21, // SET
        COUT        = 22, // IDX, SET
        CCLEAN      = 23, // POS, CNT
        CPPINIT     = 30,
        CPPPREPIN   = 31,
        CPPPREPOUT  = 32,
        CPPCLOSEIN  = 33, // THIN
        CPPCLOSEOUT = 34
    }

    public enum E_EpsonProgress : ushort
    {
        Idle                  = 0,   // Kein Auftrag in Ausführung
        Validating            = 10,  // Auftrag wird validiert
        WaitingForResources   = 20,  // Warten auf Lock-Grants
        PreparingPress        = 30,  // Pressplatte/Umgebung vorbereiten
        WaitingForPressRecipe = 40,  // Warten auf RecipeStartAck/Result
        MovingToSource        = 50,  // Bewegung zur Quelle
        WaferOnRobot          = 60,  // Waferaufnahme bestätigt
        MovingToTarget        = 70,  // Bewegung zum Ziel
        Finishing             = 80,  // Nacharbeiten (Ablegen, Parken, Freigaben)
        Done                  = 90,  // Auftrag erfolgreich beendet
        Error                 = 100  // Auftrag fehlerhaft beendet
    }

    public enum E_EpsonUserReaction : ushort
    {
        None   = 0,
        OK     = 1,   // Zweierpotenzen: direkt als Bits in UserPrompt.OptionsMask
        No     = 2,
        Abort  = 4,
        Ignore = 8,
        Retry  = 16,
        Yes    = 32
    }

    public enum E_EpsonPressResult : ushort
    {
        Pending  = 0, // Rezeptstart noch nicht bestätigt
        Started  = 1, // Rezept gestartet
        Rejected = 2, // Rezeptstart abgelehnt
        Error    = 3  // Rezeptstart fehlerhaft
    }

    public enum E_EpsonState : ushort
    {
        Offline      = 0, // Keine Verbindung zum Epson-Controller
        Connecting   = 1, // Verbindungsaufbau läuft
        Initializing = 2, // Initialisierung läuft
        Ready        = 3, // Bereit für zulässige Aufträge
        Busy         = 4, // Auftrag in Ausführung
        Paused       = 5, // Programm pausiert
        Service      = 6, // Service-Modus aktiv
        Error        = 7, // Fehlerzustand
        Stopping     = 8  // Geordneter Stopp läuft
    }

    public enum E_EpsonOutput : ushort
    {
        CylPressplate               = 0,   // Entspricht dem Legacy-EOutputIndex aus EpsonProt.cs
        ToolConnector               = 1,
        CylFillWaferTransfer        = 2,
        CylWaferTransfer            = 3,
        WaferAbblasen               = 4,
        BalligBlasenKammerBelueften = 5,
        ToolVac                     = 6,
        WaferVac                    = 7,
        Reserve0                    = 8,
        PlateVac                    = 9,
        Reserve1                    = 10,
        Reserve2                    = 11,
        Reserve3                    = 12,
        Ammoniac                    = 13,
        Unknown                     = 255  // Legacy eUnknown=-1, im UINT-Bereich abgebildet
    }

    // Legacy-Protokoll: Tool-ID 0 = kein Werkzeug montiert
    public enum E_EpsonTool : ushort
    {
        Free              = 0,   // Kein Werkzeug montiert
        Tool3SingleSide   = 3,
        Tool4SingleSide   = 4,
        Tool5DoubleSide   = 5,
        Tool6SingleSide   = 6,
        Tool8DoubleSide   = 8,
        Unknown           = 255  // Werkzeug nicht erkannt/nicht auswertbar
    }

    public enum E_EpsonSource : ushort
    {
        WaferTransfer     = 0, // Wafer-Übertragungsstation
        FillWaferCarousel = 1  // Karussell / Füllwafer-Station
    }

    [System.Flags]
    public enum E_EpsonLockMask : uint
    {
        None               = 0,
        WaferTransfer      = 1 << 0, // Bit 0: Wafer-Transferstation
        WaferPress         = 1 << 1, // Bit 1: WaferPress / Pressplattenziel
        ToolStation        = 1 << 2, // Bit 2: Tool-Station / Tool-Carousel / Tool-Übergabe
        GenmarkArea        = 1 << 3, // Bit 3: Genmark-Aktionsbereich
        StaeubliArea       = 1 << 4  // Bit 4: Stäubli-Aktionsbereich
    }

    public enum E_EpsonErrorCode : ushort
    {
        None                     = 0,
        InvalidCommand           = 1001,
        InvalidParameter         = 1002,
        MissingExternalState     = 1003,
        MissingLockGrant         = 1004,
        VersionMismatch          = 1005,
        ProtocolCommandNotAllowed= 1006,
        InvalidUserReaction      = 1007,
        EpsonProtocolError       = 2001,
        EpsonCommandTimeout      = 2002,
        EpsonCommandRejected     = 2003,
        EpsonCommandFailed       = 2004,
        RecipeStartRejected      = 2005,
        UserDecisionRequired     = 3001
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonProtocolCmd
    {
        public E_EpsonProtocolCommand Code;   // UINT in ST (typisierter Stationsbefehl)
        [MarshalAs(UnmanagedType.I1)] public bool Ext;                       // EXT
        public ushort Position;               // UINT in ST (POS: Slot bzw. Reinigungsposition)
        public int MinPressDistanceUm;        // DINT in ST (DMIN in µm, unverändert serialisieren)
        [MarshalAs(UnmanagedType.I1)] public bool ThinPlate;                 // THIN
        [MarshalAs(UnmanagedType.I1)] public bool NoPark;                    // NOPARK (CTPUT)
        [MarshalAs(UnmanagedType.I1)] public bool NoParkParameterPresent;    // TRUE: NOPARK übertragen
        public ushort ToolDiameterInch;       // UINT in ST (TOOL: 3,4,5,6,8 Zoll)
        [MarshalAs(UnmanagedType.I1)] public bool Set;                       // SET (CPWR/CSERVICE/COUT)
        public ushort CleanStepCount;         // UINT in ST (CNT)
        public ushort OutputIndex;            // UINT in ST (IDX, gegen E_EpsonOutput validieren)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonCmd
    {
        [MarshalAs(UnmanagedType.I1)] public bool Valid;                     // Bis zum terminalen Ergebnis gesetzt lassen
        public uint Sequence;                 // UDINT in ST (eindeutige, steigende Auftragsnummer)
        public E_EpsonCommand Code;           // UINT in ST
        public E_EpsonSource Source;          // UINT in ST (0=Transfer, 1=FillWafer)
        public ushort TargetSlot;             // UINT in ST (0..23)
        public E_EpsonTool RequiredTool;      // UINT in ST
        public int MinPressDistanceUm;        // DINT in ST (in µm)
        [MarshalAs(UnmanagedType.I1)] public bool ThinPlate;
        public ushort CleanPosition;          // UINT in ST
        public ushort CleanStepCount;         // UINT in ST
        public E_EpsonOutput OutputIndex;     // UINT in ST
        [MarshalAs(UnmanagedType.I1)] public bool OutputSet;
        [MarshalAs(UnmanagedType.I1)] public bool ServiceEnable;
        [MarshalAs(UnmanagedType.I1)] public bool ToolFromExternal;
        [MarshalAs(UnmanagedType.I1)] public bool OldToolToExternal;
        [MarshalAs(UnmanagedType.I1)] public bool UseExternalPutFallback;
        public ST_EpsonProtocolCmd Protocol;  // Parameter für SendSingleProtocolCommand
        public uint UserMessageId;            // UDINT in ST (Backchannel: bezieht sich auf UserPrompt.MessageId)
        public E_EpsonUserReaction UserReaction; // UINT in ST (Backchannel)
        public uint UserReactionSequence;     // UDINT in ST (Backchannel: bei jeder neuen Antwort erhöhen)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonTransferState
    {
        [MarshalAs(UnmanagedType.I1)] public bool Ready;
        [MarshalAs(UnmanagedType.I1)] public bool WaferExpected;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonPressState
    {
        [MarshalAs(UnmanagedType.I1)] public bool Ready;
        [MarshalAs(UnmanagedType.I1)] public bool PlatePresent;
        [MarshalAs(UnmanagedType.I1)] public bool ThinPlate;
        [MarshalAs(UnmanagedType.I1)] public bool TargetSlotFree;
        public uint RecipeStartAckSequence;   // UDINT in ST (quittierte Sequenz der Pressrezept-Anforderung)
        public E_EpsonPressResult RecipeStartResult; // UINT in ST (Ergebnis für die quittierte Sequenz)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonToolStationState
    {
        [MarshalAs(UnmanagedType.I1)] public bool Ready;
        [MarshalAs(UnmanagedType.I1)] public bool DoorLocked;
    }

    // Genmark/Stäubli im SPS-Snapshot
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonRobotState
    {
        [MarshalAs(UnmanagedType.I1)] public bool Ready;
        [MarshalAs(UnmanagedType.I1)] public bool Idle;
        [MarshalAs(UnmanagedType.I1)] public bool Parked;   // Bestätigte Parkposition (Genmark)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonExternalState
    {
        [MarshalAs(UnmanagedType.I1)] public bool Valid;
        public uint Sequence;                 // UDINT in ST (Snapshot-Version)
        [MarshalAs(UnmanagedType.I1)] public bool AutoMode;
        [MarshalAs(UnmanagedType.I1)] public bool SafetyPermissive;
        public ST_EpsonTransferState Transfer;
        public ST_EpsonPressState Press;
        public ST_EpsonToolStationState ToolStation;
        public ST_EpsonRobotState Genmark;
        public ST_EpsonRobotState Staeubli;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonLockGrant
    {
        public uint GrantedMask;              // DWORD in ST (Bitmaske erteilter Ressourcen, Bit 0..4)
        public uint GrantSequence;            // UDINT in ST (bei jeder Änderung der Grants erhöhen)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_PlcToEpsonIpc
    {
        public ushort InterfaceVersion;       // UINT in ST (SPS-Seite: 2)
        public ST_EpsonCmd Cmd;               // Auftragsdaten + Bedienerreaktions-Backchannel
        public ST_EpsonExternalState External;// Prozess-Snapshot + Pressrezept-Antwort
        public ST_EpsonLockGrant Lock;        // Ressourcenfreigaben
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonCmdAck
    {
        public uint AcceptedSequence;         // UDINT in ST (zuletzt angenommene Auftragssequenz)
        public uint DoneSequence;             // UDINT in ST (erfolgreich beendete Sequenz)
        public uint ErrorSequence;            // UDINT in ST (mit Fehler beendete Sequenz)
        [MarshalAs(UnmanagedType.I1)] public bool Busy;      // Lokale State-Machine führt Auftrag aus
        [MarshalAs(UnmanagedType.I1)] public bool Accepted;  // Letzter Auftrag angenommen
        [MarshalAs(UnmanagedType.I1)] public bool Done;      // Letzter Auftrag erfolgreich beendet
        public E_EpsonProgress Progress;      // UINT in ST (aktuelle, sequenzgebundene Ablaufphase)
        [MarshalAs(UnmanagedType.I1)] public bool WaferPicked;  // Waferaufnahme bestätigt
        [MarshalAs(UnmanagedType.I1)] public bool WaferPlaced;  // Waferablage bestätigt
    }

    // Epson-eigener Status (IPC -> SPS)
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonRobotStatus
    {
        public E_EpsonState State;            // UINT in ST (Epson-Gesamtzustand)
        [MarshalAs(UnmanagedType.I1)] public bool Initialized;
        [MarshalAs(UnmanagedType.I1)] public bool Ready;
        [MarshalAs(UnmanagedType.I1)] public bool Paused;
        [MarshalAs(UnmanagedType.I1)] public bool Running;
        public E_EpsonTool CurrentToolType;   // UINT in ST (erkanntes Werkzeug)
        public int CurrentToolChuckId;        // DINT in ST (erkannte Chuck-ID)
        [MarshalAs(UnmanagedType.I1)] public bool ParkPosition;
        [MarshalAs(UnmanagedType.I1)] public bool WaferPresent;
        [MarshalAs(UnmanagedType.I1)] public bool FillWaferPresent;
        [MarshalAs(UnmanagedType.I1)] public bool ToolChange34Active;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonPressStatus
    {
        public uint RecipeStartRequestSequence; // UDINT in ST (Rezeptstart-Anforderung des Move-Ablaufs)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonUserPrompt
    {
        [MarshalAs(UnmanagedType.I1)] public bool Active;
        public uint MessageId;                // UDINT in ST
        public uint OptionsMask;              // DWORD in ST (Bits aus E_EpsonUserReaction)
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Text;                   // STRING(255) in ST (255 Zeichen + '\0')
        public uint AcceptedReactionSequence; // UDINT in ST (zuletzt verarbeitete Cmd.UserReactionSequence)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonWarning
    {
        [MarshalAs(UnmanagedType.I1)] public bool Active;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Text;                   // STRING(255) in ST (255 Zeichen + '\0')
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonError
    {
        [MarshalAs(UnmanagedType.I1)] public bool Active;
        public ushort Code;                   // UINT in ST
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Text;                   // STRING(255) in ST (255 Zeichen + '\0')
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonLockRequest
    {
        public uint OwnerSequence;            // UDINT in ST (Auftragssequenz der Anforderungen)
        public uint RequestMask;              // DWORD in ST
        public uint HeldMask;                 // DWORD in ST (verwendete, bestätigte Ressourcen)
        public uint ReleaseMask;              // DWORD in ST (explizite Freigabe)
        [MarshalAs(UnmanagedType.I1)] public bool ForceReleaseRequest; // Recovery nur nach sicherem Stopp
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ST_EpsonIpcToPlc
    {
        public ushort InterfaceVersion;       // UINT in ST (vom IPC implementierte Version)
        public uint Heartbeat;                // UDINT in ST (zyklischer Watchdog-Zähler)
        [MarshalAs(UnmanagedType.I1)] public bool AdsConnected;           // ADS-Verbindung des IPC aktiv
        [MarshalAs(UnmanagedType.I1)] public bool StationChannelConnected;// Epson-Telegrammkanal verbunden
        [MarshalAs(UnmanagedType.I1)] public bool RemoteControlConnected; // Epson-Remote-Control-Kanal verbunden
        public ST_EpsonCmdAck Cmd;            // Auftragsphasen/-ergebnis
        public ST_EpsonRobotStatus Robot;     // Epson-eigener Status
        public ST_EpsonPressStatus Press;     // Pressrezept-Anforderung
        public ST_EpsonUserPrompt UserPrompt; // Bedienerprompt
        public ST_EpsonWarning Warning;       // Warnung
        public ST_EpsonError Error;           // Fehlerdiagnose
        public ST_EpsonLockRequest Lock;      // Ressourcenanforderungen/-besitz
    }

    public static class EpsonAdsContract
    {
        public const ushort InterfaceVersion = 2; // D2: inkompatible Strukturänderung -> Version 2
    }
}
