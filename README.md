# ADS-Epson-Client

ADS-Schnittstelle TwinCAT (SPS-Master) <-> Epson-IPC zur Ansteuerung eines EPSON-Roboters.

- `TwinCAT/` — PLC-Seite: DUTs (Structs/Enums, pack_mode 1) und GVL
- `IPC/` — IPC-Seite: C#-Structspiegel für EpsonAdsService (TwinCAT.Ads)
- `docs/` — Schnittstellenverträge und Phase-Dokumentation

Vertrag: InterfaceVersion 2, Sequenz-Postfach, Lock-Grants, Backchannels (UserPrompt, Pressrezept).
