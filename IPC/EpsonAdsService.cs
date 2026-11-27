using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace EpsonRobot.Service.Ads
{
    /// <summary>ADS link between TwinCAT PLC and the Epson IPC.</summary>
    public sealed class EpsonAdsService : BackgroundService
    {
        private const int AmsPort = 851;
        private const int CycleTimeMs = 100;
        private const int NotificationCycleTimeMs = 10;
        private const int ReconnectDelayMs = 5000;

        private const string PlcToIpcPath = "GVL_EpsonADS.stPlcToIpc";
        private const string IpcToPlcPath = "GVL_EpsonADS.stIpcToPlc";
        private const string CommandValidPath = PlcToIpcPath + ".Cmd.Valid";
        private const ushort IpcInterfaceVersion = EpsonAdsContract.InterfaceVersion;

        // Commands pending at connect time are not executed.
        private const bool ExecutePendingCommandOnStart = false;

        // Max wait for the PLC to grant all requested lock bits.
        private const int GrantTimeoutMs = 15000;

        // A snapshot is stale when External.Sequence does not advance.
        private const int SnapshotStaleTimeoutMs = 3000;

        // Valid lock bits (bit 0..4).
        private const uint AllLockBits = 0x1F;

        private static readonly int PlcToIpcSize = Marshal.SizeOf<ST_PlcToEpsonIpc>();
        private static readonly int IpcToPlcSize = Marshal.SizeOf<ST_EpsonIpcToPlc>();

        private readonly ILogger<EpsonAdsService> _logger;
        private readonly AdsClient _ads = new AdsClient();
        private readonly AmsAddress _plcAddress = new AmsAddress(AmsNetId.Local, AmsPort);
        private readonly ConcurrentQueue<ST_EpsonCmd> _commands = new ConcurrentQueue<ST_EpsonCmd>();
        private readonly IEpsonCommandExecutor _executor;

        private uint _hPlcToIpc;
        private uint _hIpcToPlc;
        private uint _commandNotificationHandle;
        private ST_EpsonIpcToPlc _ipcToPlc;

        private readonly SemaphoreSlim _commandSignal = new SemaphoreSlim(0, 1);

        private bool _firstCycle;
        private bool? _interfaceCompatible;
        private bool _sequenceKnown;
        private uint _lastSequence;
        private long _lastHeartbeatTick;

        private uint _lastSnapshotSequence;
        private long _lastSnapshotChangeTick;

        private uint _lastReactionSequence;
        private uint _recipeRequestSequence;
        private uint _lastRecipeAckSequence;

        private uint _heldMask;
        private uint _releaseMask;
        private bool _forceReleaseActive;

        private volatile object? _lastPlc;
        private volatile object? _lastIpc;

        /// <summary>Epson telegram channel state, published to the PLC.</summary>
        public bool StationChannelConnected { get; set; }

        /// <summary>Epson remote control channel state, published to the PLC.</summary>
        public bool RemoteControlConnected { get; set; }

        // Command execution phases. Grant handling is ADS contract logic and
        // therefore kept in this service; the work itself is done by the executor.
        private enum ExecPhase { Idle, WaitGrant, Run }

        private ExecPhase _execPhase = ExecPhase.Idle;
        private ST_EpsonCmd _activeCmd;
        private uint _requestMask;
        private long _grantStartTick;

        /// <summary>Raised when a new command is accepted from the PLC.</summary>
        public event EventHandler<ST_EpsonCmd>? CommandReceived;

        public EpsonAdsService(ILogger<EpsonAdsService> logger, IEpsonCommandExecutor? executor = null)
        {
            _logger = logger;
            _executor = executor ?? new EpsonDummyCommandExecutor(logger);
            _executor.UserPromptRequested += OnUserPromptRequested;
            _executor.RecipeStartRequested += OnRecipeStartRequested;
            _ipcToPlc = new ST_EpsonIpcToPlc
            {
                InterfaceVersion = IpcInterfaceVersion,
                Error = new ST_EpsonError { Text = string.Empty },
                Warning = new ST_EpsonWarning { Text = string.Empty },
                UserPrompt = new ST_EpsonUserPrompt { Text = string.Empty }
            };
        }

        /// <summary>Last PLC and IPC values as path/value dictionary.</summary>
        public IReadOnlyDictionary<string, string> Snapshot
        {
            get
            {
                var result = new Dictionary<string, string>();
                if (_lastPlc != null) Flatten(_lastPlc, "PlcToIpc", result);
                if (_lastIpc != null) Flatten(_lastIpc, "IpcToPlc", result);
                return result;
            }
        }

        public bool TryDequeueCommand(out ST_EpsonCmd command) => _commands.TryDequeue(out command);

        /// <summary>Recovery request: asks the SPS to revoke all grants after a safe stop.</summary>
        public bool TryRequestForceRelease()
        {
            if (_execPhase != ExecPhase.Idle)
            {
                _logger.LogWarning("Force release rejected: a command is active.");
                return false;
            }

            _forceReleaseActive = true;
            _ipcToPlc.Lock.ForceReleaseRequest = true;
            _logger.LogWarning("Force release requested.");
            return true;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            _logger.LogInformation("Starting Epson ADS service ({NetId}:{Port}).",
                _plcAddress.NetId, _plcAddress.Port);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(CycleTimeMs));
            Task<bool> timerTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            Task signalTask = _commandSignal.WaitAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_ads.IsConnected || _hPlcToIpc == 0)
                    {
                        ConnectAndPrepare();
                        _firstCycle = true;
                    }

                    if (!_firstCycle)
                    {
                        Task completed = await Task.WhenAny(signalTask, timerTask);
                        if (completed == signalTask)
                        {
                            await signalTask;
                            signalTask = _commandSignal.WaitAsync(stoppingToken);
                        }
                        else
                        {
                            if (!await timerTask) break;
                            timerTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                        }
                    }

                    Cycle();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ADS communication failed. Retry in {Seconds} s.", ReconnectDelayMs / 1000);
                    Disconnect();
                    try { await Task.Delay(ReconnectDelayMs, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }

            Disconnect();
        }

        private void ConnectAndPrepare()
        {
            if (!_ads.IsConnected)
                _ads.Connect(_plcAddress);

            try
            {
                ISymbolLoader loader = SymbolLoaderFactory.Create(
                    _ads, new SymbolLoaderSettings(SymbolsLoadMode.Flat));

                CheckLayout(loader, PlcToIpcPath, PlcToIpcSize);
                CheckLayout(loader, IpcToPlcPath, IpcToPlcSize);

                _hPlcToIpc = _ads.CreateVariableHandle(PlcToIpcPath);
                _hIpcToPlc = _ads.CreateVariableHandle(IpcToPlcPath);

                _ads.AdsNotificationEx += OnCmdNotificationReceived;
                _commandNotificationHandle = _ads.AddDeviceNotificationEx(
                    CommandValidPath,
                    new NotificationSettings(AdsTransMode.OnChange, NotificationCycleTimeMs, 0),
                    null,
                    typeof(bool));

                _ipcToPlc = new ST_EpsonIpcToPlc
                {
                    InterfaceVersion = IpcInterfaceVersion,
                    Error = new ST_EpsonError { Text = string.Empty },
                    Warning = new ST_EpsonWarning { Text = string.Empty },
                    UserPrompt = new ST_EpsonUserPrompt { Text = string.Empty }
                };
                _interfaceCompatible = null;
                _sequenceKnown = false;
                _heldMask = 0;
                _releaseMask = 0;
                _forceReleaseActive = false;
                _lastSnapshotSequence = 0;
                _lastReactionSequence = 0;
                _lastRecipeAckSequence = 0;
                _logger.LogInformation("ADS connected.");
            }
            catch
            {
                Disconnect();
                throw;
            }
        }

        private static void CheckLayout(ISymbolLoader loader, string path, int expectedSize)
        {
            if (!loader.Symbols.TryGetInstance(path, out ISymbol? symbol) || symbol == null)
                throw new InvalidOperationException($"ADS symbol not found: {path}");

            if (symbol.ByteSize != expectedSize)
                throw new InvalidOperationException(
                    $"Layout mismatch at {path}: PLC={symbol.ByteSize} B, C#={expectedSize} B " +
                    "(check PLC pack mode and C# StructLayout).");
        }

        private void Disconnect()
        {
            _ads.AdsNotificationEx -= OnCmdNotificationReceived;
            if (_commandNotificationHandle != 0)
                TryCleanup(() => _ads.DeleteDeviceNotification(_commandNotificationHandle));
            if (_hPlcToIpc != 0)
                TryCleanup(() => _ads.DeleteVariableHandle(_hPlcToIpc));
            if (_hIpcToPlc != 0)
                TryCleanup(() => _ads.DeleteVariableHandle(_hIpcToPlc));
            TryCleanup(() => _ads.Disconnect());

            _commandNotificationHandle = 0;
            _hPlcToIpc = 0;
            _hIpcToPlc = 0;
        }

        private void TryCleanup(Action action)
        {
            try { action(); }
            catch (Exception ex) { _logger.LogDebug(ex, "ADS cleanup step failed."); }
        }

        private void OnCmdNotificationReceived(object? sender, AdsNotificationExEventArgs e)
        {
            // Notification is only a wake-up signal. Read the complete PLC frame in Cycle().
            if (e.Value is not bool valid || !valid) return;
            try { _commandSignal.Release(); }
            catch (SemaphoreFullException) { /* A wake-up is already pending. */ }
            catch (ObjectDisposedException) { /* Service is shutting down. */ }
        }

        private void Cycle()
        {
            byte[] raw = (byte[])_ads.ReadAny(_hPlcToIpc, typeof(byte[]), new[] { PlcToIpcSize });
            ST_PlcToEpsonIpc plc = BytesToStruct<ST_PlcToEpsonIpc>(raw);

            UpdateCompatibility(plc.InterfaceVersion);

            if (_firstCycle && !ExecutePendingCommandOnStart && plc.Cmd.Valid)
                DropPendingCommand(plc.Cmd);
            else if (_interfaceCompatible == true)
                HandleCommand(plc.Cmd);

            HandleUserReaction(plc);
            HandleRecipeResponse(plc);
            UpdateLocks(plc);
            RunExecution(plc);
            UpdateSnapshotWatchdog(plc);
            PublishStatus();
            UpdateConnectionFlags();

            UpdateHeartbeat();
            _ads.WriteAny(_hIpcToPlc, StructToBytes(_ipcToPlc));

            _lastPlc = plc;
            _lastIpc = _ipcToPlc;
            _firstCycle = false;
        }

        private void UpdateCompatibility(ushort plcVersion)
        {
            bool compatible = plcVersion == IpcInterfaceVersion;
            if (_interfaceCompatible == compatible) return;

            _interfaceCompatible = compatible;
            if (compatible)
                _logger.LogInformation("Interface version {Version} compatible.", IpcInterfaceVersion);
            else
                _logger.LogError("Interface version mismatch: PLC={Plc}, IPC={Ipc}. Commands are ignored.",
                    plcVersion, IpcInterfaceVersion);
        }

        private void DropPendingCommand(ST_EpsonCmd command)
        {
            _sequenceKnown = true;
            _lastSequence = command.Sequence;

            // Explicit rejection prevents the PLC mailbox from remaining blocked forever.
            _ipcToPlc.Cmd.ErrorSequence = command.Sequence;
            _ipcToPlc.Error.Active = true;
            _ipcToPlc.Error.Code = (ushort)E_EpsonErrorCode.EpsonCommandRejected;
            _ipcToPlc.Error.Text = "Command pending at IPC startup was rejected.";

            _logger.LogWarning("Pending command rejected at connect: Seq={Sequence}, Code={Code}.",
                command.Sequence, command.Code);
        }

        private void HandleCommand(ST_EpsonCmd command)
        {
            if (!command.Valid)
            {
                _sequenceKnown = false;
                return; // Keep the last accepted sequence as acknowledgement history.
            }

            if (_sequenceKnown && command.Sequence == _lastSequence)
                return; // Already processed, stays acknowledged via AcceptedSequence.

            _sequenceKnown = true;
            _lastSequence = command.Sequence;

            // Error latch (D7): only ResetError may pass, everything else is
            // rejected until the PLC performed the safe reset condition.
            if (_ipcToPlc.Error.Active && command.Code != E_EpsonCommand.ResetError)
            {
                _ipcToPlc.Cmd.ErrorSequence = command.Sequence;
                _logger.LogWarning("Command rejected while error latched: Seq={Sequence}, Code={Code}.",
                    command.Sequence, command.Code);
                return;
            }

            _commands.Enqueue(command);
            _ipcToPlc.Cmd.AcceptedSequence = command.Sequence;
            _ipcToPlc.Cmd.Accepted = true;
            _ipcToPlc.Warning.Active = false;
            _ipcToPlc.Warning.Text = string.Empty;

            _logger.LogInformation("PLC command accepted: Seq={Sequence}, Code={Code}.",
                command.Sequence, command.Code);
            CommandReceived?.Invoke(this, command);
        }

        private void HandleUserReaction(ST_PlcToEpsonIpc plc)
        {
            if (plc.Cmd.UserReactionSequence == _lastReactionSequence || plc.Cmd.UserReactionSequence == 0)
                return;

            uint sequence = plc.Cmd.UserReactionSequence;
            _lastReactionSequence = sequence;

            if (!_ipcToPlc.UserPrompt.Active || plc.Cmd.UserMessageId != _ipcToPlc.UserPrompt.MessageId)
            {
                SetWarning($"User reaction ignored: no matching prompt (MsgId={plc.Cmd.UserMessageId}).");
                return;
            }

            if (plc.Cmd.UserReaction == E_EpsonUserReaction.None ||
                ((uint)plc.Cmd.UserReaction & _ipcToPlc.UserPrompt.OptionsMask) == 0)
            {
                SetWarning($"User reaction {plc.Cmd.UserReaction} not allowed by OptionsMask.");
                return;
            }

            _ipcToPlc.UserPrompt.AcceptedReactionSequence = sequence;
            _ipcToPlc.UserPrompt.Active = false;
            _executor.AnswerUserPrompt(plc.Cmd.UserMessageId, plc.Cmd.UserReaction);

            _logger.LogInformation("User reaction forwarded: Seq={Sequence}, MsgId={MessageId}, Reaction={Reaction}.",
                sequence, plc.Cmd.UserMessageId, plc.Cmd.UserReaction);
        }

        private void HandleRecipeResponse(ST_PlcToEpsonIpc plc)
        {
            uint request = _ipcToPlc.Press.RecipeStartRequestSequence;
            if (request == 0 || request == _lastRecipeAckSequence)
                return;
            if (plc.External.Press.RecipeStartAckSequence != request)
                return;
            if (plc.External.Press.RecipeStartResult == E_EpsonPressResult.Pending)
                return; // Acknowledged without terminal result: keep waiting.

            _lastRecipeAckSequence = request;
            _executor.AnswerRecipeStart(request, plc.External.Press.RecipeStartResult);
        }

        private void OnUserPromptRequested(object? sender, EpsonUserPromptRequest request)
        {
            _ipcToPlc.UserPrompt.Active = true;
            _ipcToPlc.UserPrompt.MessageId = request.MessageId;
            _ipcToPlc.UserPrompt.OptionsMask = request.OptionsMask;
            _ipcToPlc.UserPrompt.Text = request.Text;
            _ipcToPlc.Warning.Active = false;
            _ipcToPlc.Warning.Text = string.Empty;
            _logger.LogInformation("User prompt published: MsgId={MessageId}, Mask=0x{Mask:X}.",
                request.MessageId, request.OptionsMask);
        }

        private void OnRecipeStartRequested(object? sender, EventArgs e)
        {
            unchecked { _recipeRequestSequence++; }
            _lastRecipeAckSequence = 0;
            _ipcToPlc.Press.RecipeStartRequestSequence = _recipeRequestSequence;
            _logger.LogInformation("Press recipe start requested: Seq={Sequence}.", _recipeRequestSequence);
        }

        // ------------------------------------------------------------------
        // Lock lifecycle: request during command, HeldMask for actually
        // granted bits, ReleaseMask for explicit release requests,
        // ForceReleaseRequest for recovery after a safe stop.
        // ------------------------------------------------------------------

        private void UpdateLocks(ST_PlcToEpsonIpc plc)
        {
            if (_execPhase == ExecPhase.WaitGrant)
            {
                _heldMask |= plc.Lock.GrantedMask & _requestMask;
                if ((plc.Lock.GrantedMask & _requestMask) == _requestMask)
                    _execPhase = ExecPhase.Run;
                else if (Environment.TickCount64 - _grantStartTick > GrantTimeoutMs)
                    FailExecution(E_EpsonErrorCode.MissingLockGrant, "Lock grant timeout.");
            }

            // Release processing: a bit is done when the PLC revoked the grant.
            uint revoked = _releaseMask & ~plc.Lock.GrantedMask & AllLockBits;
            if (revoked != 0)
            {
                _heldMask &= ~revoked;
                _releaseMask &= ~revoked;
            }

            if (_forceReleaseActive && (plc.Lock.GrantedMask & AllLockBits) == 0)
            {
                _forceReleaseActive = false;
                _ipcToPlc.Lock.ForceReleaseRequest = false;
                _heldMask = 0;
                _releaseMask = 0;
                _logger.LogInformation("Force release completed: all grants revoked.");
            }

            _ipcToPlc.Lock.HeldMask = _heldMask;
            _ipcToPlc.Lock.ReleaseMask = _releaseMask;
        }

        private void RunExecution(ST_PlcToEpsonIpc plc)
        {
            if (_interfaceCompatible != true)
            {
                // Freeze: no new command, no further execution on a foreign contract.
                while (_commands.TryDequeue(out _)) { }
                if (_execPhase != ExecPhase.Idle)
                    FailExecution(E_EpsonErrorCode.VersionMismatch,
                        "Interface version mismatch during execution.");
                return;
            }

            switch (_execPhase)
            {
                case ExecPhase.Idle:
                    if (_commands.TryDequeue(out ST_EpsonCmd cmd))
                        StartExecution(cmd, plc);
                    return;

                case ExecPhase.WaitGrant:
                    return; // Grant handling is done in UpdateLocks.

                case ExecPhase.Run:
                    EpsonExecutionResult? result = _executor.Execute(_activeCmd, plc);
                    if (result.HasValue)
                    {
                        if (result.Value.Success)
                            CompleteExecution();
                        else
                            FailExecution(result.Value.ErrorCode, result.Value.ErrorText);
                    }
                    return;
            }
        }

        private void StartExecution(ST_EpsonCmd cmd, ST_PlcToEpsonIpc plc)
        {
            _activeCmd = cmd;
            _grantStartTick = Environment.TickCount64;
            _ipcToPlc.Cmd.Accepted = false;
            _ipcToPlc.Cmd.WaferPicked = false;
            _ipcToPlc.Cmd.WaferPlaced = false;
            _ipcToPlc.Cmd.Progress = E_EpsonProgress.Validating;

            if (IsSnapshotStale(plc))
            {
                FailExecution(E_EpsonErrorCode.MissingExternalState,
                    "External snapshot stale (no Sequence change).");
                return;
            }

            if (!_executor.TryStart(cmd, plc, out EpsonExecutionResult failure, out uint mask))
            {
                FailExecution(failure.ErrorCode, failure.ErrorText);
                return;
            }

            _requestMask = mask;
            _ipcToPlc.Lock.OwnerSequence = cmd.Sequence;
            _ipcToPlc.Lock.RequestMask = mask;

            _execPhase = mask == 0 ? ExecPhase.Run : ExecPhase.WaitGrant;
            _logger.LogInformation("Command execution started: Seq={Sequence}, Code={Code}, RequestMask=0x{Mask:X}.",
                cmd.Sequence, cmd.Code, mask);
        }

        private void CompleteExecution()
        {
            // All resources are requested for release after the safely confirmed end.
            _releaseMask |= _heldMask;
            _ipcToPlc.Lock.RequestMask = 0;
            _ipcToPlc.Cmd.DoneSequence = _activeCmd.Sequence;
            _ipcToPlc.Cmd.Progress = E_EpsonProgress.Done;

            if (_activeCmd.Code == E_EpsonCommand.ResetError)
                ClearError();

            _logger.LogInformation("Command done: Seq={Sequence}, Code={Code}.",
                _activeCmd.Sequence, _activeCmd.Code);
            ResetExecution();
        }

        private void FailExecution(E_EpsonErrorCode code, string text)
        {
            // Held locks stay held on failure: release happens only after the
            // SPS safety check (ResetError flow / ForceReleaseRequest).
            _ipcToPlc.Lock.RequestMask = 0;
            _ipcToPlc.Cmd.ErrorSequence = _activeCmd.Sequence;
            _ipcToPlc.Cmd.Progress = E_EpsonProgress.Error;
            _ipcToPlc.Error.Active = true;
            _ipcToPlc.Error.Code = (ushort)code;
            _ipcToPlc.Error.Text = text;
            _logger.LogWarning("Command failed: Seq={Sequence}, Code={Code} ({Text}).",
                _activeCmd.Sequence, code, text);
            ResetExecution();
        }

        private void ResetExecution()
        {
            _execPhase = ExecPhase.Idle;
            _requestMask = 0;
        }

        private void UpdateSnapshotWatchdog(ST_PlcToEpsonIpc plc)
        {
            if (plc.External.Sequence != _lastSnapshotSequence)
            {
                _lastSnapshotSequence = plc.External.Sequence;
                _lastSnapshotChangeTick = Environment.TickCount64;
                return;
            }

            if (_execPhase == ExecPhase.Run && plc.External.Valid &&
                Environment.TickCount64 - _lastSnapshotChangeTick > SnapshotStaleTimeoutMs)
            {
                FailExecution(E_EpsonErrorCode.MissingExternalState,
                    "External snapshot stale (no Sequence change during execution).");
            }
        }

        private bool IsSnapshotStale(ST_PlcToEpsonIpc plc)
        {
            return plc.External.Valid &&
                   Environment.TickCount64 - _lastSnapshotChangeTick > SnapshotStaleTimeoutMs;
        }

        private void PublishStatus()
        {
            bool busy = _execPhase != ExecPhase.Idle;
            _ipcToPlc.Cmd.Busy = busy;

            if (busy && _execPhase == ExecPhase.Run)
            {
                EpsonExecutionStatus status = _executor.Status;
                _ipcToPlc.Cmd.WaferPicked = status.WaferPicked;
                _ipcToPlc.Cmd.WaferPlaced = status.WaferPlaced;
                _ipcToPlc.Cmd.Progress = status.Progress == E_EpsonProgress.Idle
                    ? E_EpsonProgress.Validating
                    : status.Progress;
                _releaseMask |= status.RequestedReleaseMask & _heldMask;
            }

            _ipcToPlc.Robot.State = _ipcToPlc.Error.Active
                ? E_EpsonState.Error
                : busy ? E_EpsonState.Busy
                : E_EpsonState.Ready;
        }

        private void UpdateConnectionFlags()
        {
            _ipcToPlc.AdsConnected = _ads.IsConnected;
            _ipcToPlc.StationChannelConnected = StationChannelConnected;
            _ipcToPlc.RemoteControlConnected = RemoteControlConnected;
        }

        private void ClearError()
        {
            _ipcToPlc.Error.Active = false;
            _ipcToPlc.Error.Code = 0;
            _ipcToPlc.Error.Text = string.Empty;
        }

        private void SetWarning(string text)
        {
            _ipcToPlc.Warning.Active = true;
            _ipcToPlc.Warning.Text = text;
            _logger.LogWarning(text);
        }

        private void UpdateHeartbeat()
        {
            long now = Environment.TickCount64;
            if (now - _lastHeartbeatTick < CycleTimeMs) return;
            _lastHeartbeatTick = now;
            unchecked { _ipcToPlc.Heartbeat++; }
        }

        private static void Flatten(object value, string prefix, IDictionary<string, string> target)
        {
            foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object? fieldValue = field.GetValue(value);
                string name = prefix + "." + field.Name;
                bool nested = field.FieldType.IsValueType && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum;
                if (nested && fieldValue != null) Flatten(fieldValue, name, target);
                else target[name] = fieldValue?.ToString() ?? string.Empty;
            }
        }

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            if (bytes == null || bytes.Length != size)
                throw new InvalidOperationException($"Invalid length for {typeof(T).Name}: expected {size}, got {bytes?.Length ?? 0} B.");
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
        }

        private static byte[] StructToBytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, pointer, false);
                Marshal.Copy(pointer, bytes, 0, size);
            }
            finally { Marshal.FreeHGlobal(pointer); }
            return bytes;
        }

        public override void Dispose()
        {
            _executor.UserPromptRequested -= OnUserPromptRequested;
            _executor.RecipeStartRequested -= OnRecipeStartRequested;
            Disconnect();
            _ads.Dispose();
            base.Dispose();
        }
    }
}
