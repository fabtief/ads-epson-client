using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace EpsonRobot.Service.Ads
{
    /// <summary>Dummy executor: simulates runtimes and backchannels so the PLC side can be tested.</summary>
    public sealed class EpsonDummyCommandExecutor : IEpsonCommandExecutor
    {
        private enum StepKind { Phase, Recipe, Prompt }

        private readonly struct Step
        {
            public readonly StepKind Kind;
            public readonly E_EpsonProgress Progress;
            public readonly int DurationMs;
            public readonly E_EpsonLockMask Release;

            private Step(StepKind kind, E_EpsonProgress progress, int durationMs, E_EpsonLockMask release)
            {
                Kind = kind;
                Progress = progress;
                DurationMs = durationMs;
                Release = release;
            }

            public static Step Phase(E_EpsonProgress progress, int durationMs,
                E_EpsonLockMask release = E_EpsonLockMask.None)
                => new Step(StepKind.Phase, progress, durationMs, release);

            public static Step Recipe() => new Step(StepKind.Recipe, E_EpsonProgress.WaitingForPressRecipe, 0, E_EpsonLockMask.None);
            public static Step Prompt() => new Step(StepKind.Prompt, E_EpsonProgress.Finishing, 0, E_EpsonLockMask.None);
        }

        private readonly ILogger _logger;
        private readonly Queue<Step> _steps = new Queue<Step>();

        private EpsonExecutionStatus _status;
        private bool _hasCurrentStep;
        private Step _currentStep;
        private long _stepStartTick;
        private bool _waitRecipe;
        private bool _waitPrompt;
        private uint _activePromptId;
        private (E_EpsonErrorCode Code, string Text)? _pendingFailure;

        public EpsonDummyCommandExecutor(ILogger logger)
        {
            _logger = logger;
            _status = new EpsonExecutionStatus { Progress = E_EpsonProgress.Idle };
        }

        public EpsonExecutionStatus Status => _status;

        public event EventHandler<EpsonUserPromptRequest>? UserPromptRequested;
        public event EventHandler? RecipeStartRequested;

        public bool TryStart(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc, out EpsonExecutionResult failure, out uint requestMask)
        {
            failure = new EpsonExecutionResult { ErrorCode = E_EpsonErrorCode.None, ErrorText = string.Empty };
            requestMask = 0;

            if (!Validate(cmd, plc, ref failure, out requestMask))
                return false;

            BuildSteps(cmd);
            return true;
        }

        public EpsonExecutionResult? Execute(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc)
        {
            if (_pendingFailure.HasValue)
            {
                (E_EpsonErrorCode code, string text) = _pendingFailure.Value;
                _pendingFailure = null;
                return Fail(code, text);
            }

            if (!plc.External.Valid)
                return Fail(E_EpsonErrorCode.MissingExternalState, "External state snapshot invalid during execution.");

            while (true)
            {
                if (_waitRecipe || _waitPrompt)
                    return null;

                if (!_hasCurrentStep)
                {
                    if (_steps.Count == 0)
                    {
                        _status.Progress = E_EpsonProgress.Done;
                        _logger.LogInformation("Dummy done: Seq={Sequence}, Code={Code}.", cmd.Sequence, cmd.Code);
                        return new EpsonExecutionResult { Success = true };
                    }

                    _currentStep = _steps.Dequeue();
                    _hasCurrentStep = true;
                    _stepStartTick = Environment.TickCount64;

                    if (_currentStep.Kind == StepKind.Recipe)
                    {
                        _status.Progress = E_EpsonProgress.WaitingForPressRecipe;
                        _logger.LogInformation("Dummy requests press recipe start: Seq={Sequence}.", cmd.Sequence);
                        RecipeStartRequested?.Invoke(this, EventArgs.Empty);
                        _waitRecipe = true;
                        return null;
                    }

                    if (_currentStep.Kind == StepKind.Prompt)
                    {
                        _status.Progress = E_EpsonProgress.Finishing;
                        _activePromptId++;
                        UserPromptRequested?.Invoke(this, new EpsonUserPromptRequest
                        {
                            MessageId = _activePromptId,
                            OptionsMask = (uint)(E_EpsonUserReaction.OK | E_EpsonUserReaction.Yes),
                            Text = "Dummy: Werkzeugwechsel bestaetigen?"
                        });
                        _waitPrompt = true;
                        return null;
                    }

                    _status.Progress = _currentStep.Progress;
                }

                if (Environment.TickCount64 - _stepStartTick >= _currentStep.DurationMs)
                {
                    if (_currentStep.Progress == E_EpsonProgress.WaferOnRobot)
                        _status.WaferPicked = true;
                    if (_currentStep.Progress == E_EpsonProgress.Finishing && cmd.Code == E_EpsonCommand.MoveWafer)
                        _status.WaferPlaced = true;
                    if (_currentStep.Release != E_EpsonLockMask.None)
                        _status.RequestedReleaseMask |= (uint)_currentStep.Release;

                    _hasCurrentStep = false;
                    continue;
                }

                return null;
            }
        }

        public void Abort(string reason)
        {
            if (_steps.Count == 0 && !_hasCurrentStep && !_waitRecipe && !_waitPrompt
                && _status.Progress == E_EpsonProgress.Idle)
                return;

            _steps.Clear();
            _hasCurrentStep = false;
            _waitRecipe = false;
            _waitPrompt = false;
            _pendingFailure = null;
            _status = new EpsonExecutionStatus { Progress = E_EpsonProgress.Idle };
            _logger.LogWarning("Dummy aborted: {Reason}.", reason);
        }

        public void AnswerUserPrompt(uint messageId, E_EpsonUserReaction reaction)
        {
            if (!_waitPrompt || messageId != _activePromptId)
                return;

            _waitPrompt = false;
            _hasCurrentStep = false;

            if (reaction is E_EpsonUserReaction.OK or E_EpsonUserReaction.Yes)
                return;

            _pendingFailure = (E_EpsonErrorCode.EpsonCommandRejected, $"Operator rejected prompt: {reaction}.");
        }

        public void AnswerRecipeStart(uint sequence, E_EpsonPressResult result)
        {
            if (!_waitRecipe)
                return;

            _waitRecipe = false;
            _hasCurrentStep = false;

            if (result != E_EpsonPressResult.Started)
                _pendingFailure = (E_EpsonErrorCode.RecipeStartRejected, $"Press recipe start result: {result}.");
            else
                _logger.LogInformation("Dummy press recipe started: Seq={Sequence}.", sequence);
        }

        private void BuildSteps(in ST_EpsonCmd cmd)
        {
            _steps.Clear();
            _hasCurrentStep = false;
            _waitRecipe = false;
            _waitPrompt = false;
            _pendingFailure = null;
            _status = new EpsonExecutionStatus { Progress = E_EpsonProgress.Validating };

            switch (cmd.Code)
            {
                case E_EpsonCommand.MoveWafer:
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.PreparingPress, 600));
                    _steps.Enqueue(Step.Recipe());
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.MovingToSource, 800));
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.WaferOnRobot, 400, E_EpsonLockMask.WaferTransfer));
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.MovingToTarget, 800));
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.Finishing, 400));
                    break;

                case E_EpsonCommand.ChangeTool:
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.Validating, 300));
                    _steps.Enqueue(Step.Prompt());
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.Finishing, 800));
                    break;

                default:
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.Validating, 300));
                    _steps.Enqueue(Step.Phase(E_EpsonProgress.Finishing, 300));
                    break;
            }
        }

        private static bool Validate(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc, ref EpsonExecutionResult failure, out uint requestMask)
        {
            requestMask = 0;

            if (!plc.External.Valid)
            {
                failure.ErrorCode = E_EpsonErrorCode.MissingExternalState;
                failure.ErrorText = "External state snapshot invalid.";
                return false;
            }

            switch (cmd.Code)
            {
                case E_EpsonCommand.Initialize:
                case E_EpsonCommand.ResetError:
                    return true;

                case E_EpsonCommand.StartProgram:
                case E_EpsonCommand.ContinueProgram:
                    if (!plc.External.AutoMode || !plc.External.SafetyPermissive)
                        return Reject(ref failure, E_EpsonErrorCode.MissingExternalState, "AutoMode or SafetyPermissive not set.");
                    return true;

                case E_EpsonCommand.PauseProgram:
                case E_EpsonCommand.StopProgram:
                case E_EpsonCommand.Park:
                case E_EpsonCommand.SetServiceMode:
                    return true;

                case E_EpsonCommand.MoveWafer:
                    return ValidateMoveWafer(cmd, plc, ref failure, out requestMask);

                case E_EpsonCommand.GetTool:
                case E_EpsonCommand.PutTool:
                case E_EpsonCommand.ChangeTool:
                    return ValidateToolCommand(cmd, plc, ref failure, out requestMask);

                case E_EpsonCommand.CleanTool:
                    if (cmd.CleanStepCount == 0)
                        return Reject(ref failure, E_EpsonErrorCode.InvalidParameter, "CleanStepCount must be > 0.");
                    requestMask = (uint)E_EpsonLockMask.ToolStation;
                    return true;

                case E_EpsonCommand.PressplateInit:
                case E_EpsonCommand.PressplatePrepIn:
                case E_EpsonCommand.PressplatePrepOut:
                case E_EpsonCommand.PressplateCloseOut:
                    requestMask = (uint)E_EpsonLockMask.WaferPress;
                    return true;

                case E_EpsonCommand.PressplateCloseIn:
                    if (cmd.ThinPlate != plc.External.Press.ThinPlate)
                        return Reject(ref failure, E_EpsonErrorCode.InvalidParameter,
                            "ThinPlate does not match external press state.");
                    requestMask = (uint)E_EpsonLockMask.WaferPress;
                    return true;

                case E_EpsonCommand.SetOutput:
                    if (!IsUsableOutput(cmd.OutputIndex))
                        return Reject(ref failure, E_EpsonErrorCode.InvalidParameter,
                            $"Output {(ushort)cmd.OutputIndex} is not usable (reserve/unknown).");
                    return true;

                case E_EpsonCommand.SendSingleProtocolCommand:
                    return ValidateProtocolCommand(cmd, plc, ref failure, out requestMask);

                default:
                    return Reject(ref failure, E_EpsonErrorCode.InvalidCommand,
                        $"Unknown command code {(ushort)cmd.Code}.");
            }
        }

        private static bool ValidateMoveWafer(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc, ref EpsonExecutionResult failure, out uint requestMask)
        {
            requestMask = 0;

            if (cmd.TargetSlot > 23)
                return Reject(ref failure, E_EpsonErrorCode.InvalidParameter, "TargetSlot out of range (0..23).");

            if (cmd.RequiredTool is E_EpsonTool.Free or E_EpsonTool.Unknown)
                return Reject(ref failure, E_EpsonErrorCode.InvalidParameter, "RequiredTool invalid.");

            if (cmd.ThinPlate != plc.External.Press.ThinPlate)
                return Reject(ref failure, E_EpsonErrorCode.InvalidParameter,
                    "ThinPlate does not match external press state.");

            switch (cmd.Source)
            {
                case E_EpsonSource.WaferTransfer:
                    if (!plc.External.Transfer.Ready || !plc.External.Transfer.WaferExpected ||
                        !plc.External.Press.Ready || !plc.External.Press.PlatePresent ||
                        !plc.External.Press.TargetSlotFree)
                        return Reject(ref failure, E_EpsonErrorCode.MissingExternalState,
                            "Transfer/Press preconditions not met.");
                    requestMask = (uint)(E_EpsonLockMask.WaferTransfer | E_EpsonLockMask.WaferPress);
                    return true;

                case E_EpsonSource.FillWaferCarousel:
                    if (!plc.External.ToolStation.Ready || !plc.External.ToolStation.DoorLocked ||
                        !plc.External.Genmark.Parked ||
                        !plc.External.Press.Ready || !plc.External.Press.PlatePresent ||
                        !plc.External.Press.TargetSlotFree)
                        return Reject(ref failure, E_EpsonErrorCode.MissingExternalState,
                            "FillWafer/Press preconditions not met (Genmark must be parked).");
                    requestMask = (uint)(E_EpsonLockMask.WaferPress |
                                         E_EpsonLockMask.ToolStation | E_EpsonLockMask.GenmarkArea);
                    return true;

                default:
                    return Reject(ref failure, E_EpsonErrorCode.InvalidParameter,
                        $"Unknown source {(ushort)cmd.Source}.");
            }
        }

        private static bool ValidateToolCommand(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc, ref EpsonExecutionResult failure, out uint requestMask)
        {
            requestMask = 0;

            if (!plc.External.ToolStation.Ready || !plc.External.ToolStation.DoorLocked)
                return Reject(ref failure, E_EpsonErrorCode.MissingExternalState,
                    "Tool station not ready or door not locked.");

            if (cmd.Code != E_EpsonCommand.PutTool &&
                cmd.RequiredTool is E_EpsonTool.Free or E_EpsonTool.Unknown)
                return Reject(ref failure, E_EpsonErrorCode.InvalidParameter, "RequiredTool invalid.");

            requestMask = (uint)(E_EpsonLockMask.ToolStation | E_EpsonLockMask.GenmarkArea);
            if (cmd.ToolFromExternal || cmd.OldToolToExternal)
                requestMask |= (uint)E_EpsonLockMask.StaeubliArea;
            return true;
        }

        // Allowlist per interface description 3.3/3.4. CESTA runs over the user
        // reaction channel, CPWR stays rejected until a safe power sequence exists.
        private static bool ValidateProtocolCommand(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc, ref EpsonExecutionResult failure, out uint requestMask)
        {
            requestMask = 0;

            switch (cmd.Protocol.Code)
            {
                case E_EpsonProtocolCommand.CINIT:
                case E_EpsonProtocolCommand.CPARK:
                case E_EpsonProtocolCommand.CMOVESERV:
                case E_EpsonProtocolCommand.CSTOP:
                    return true;

                case E_EpsonProtocolCommand.CTPUT:
                case E_EpsonProtocolCommand.CTGET:
                    if (!plc.External.ToolStation.Ready || !plc.External.ToolStation.DoorLocked)
                        return Reject(ref failure, E_EpsonErrorCode.MissingExternalState, "Tool station not ready.");
                    requestMask = (uint)E_EpsonLockMask.ToolStation;
                    return true;

                case E_EpsonProtocolCommand.CMOVE:
                    return ValidateMoveWafer(cmd, plc, ref failure, out requestMask);

                case E_EpsonProtocolCommand.CSERVICE:
                case E_EpsonProtocolCommand.CCLEAN:
                    requestMask = (uint)E_EpsonLockMask.ToolStation;
                    return true;

                case E_EpsonProtocolCommand.COUT:
                    if (!IsUsableOutput((E_EpsonOutput)cmd.Protocol.OutputIndex))
                        return Reject(ref failure, E_EpsonErrorCode.InvalidParameter,
                            $"Output {cmd.Protocol.OutputIndex} is not usable (reserve/unknown).");
                    return true;

                case E_EpsonProtocolCommand.CPPINIT:
                case E_EpsonProtocolCommand.CPPPREPIN:
                case E_EpsonProtocolCommand.CPPPREPOUT:
                case E_EpsonProtocolCommand.CPPCLOSEIN:
                case E_EpsonProtocolCommand.CPPCLOSEOUT:
                    requestMask = (uint)E_EpsonLockMask.WaferPress;
                    return true;

                case E_EpsonProtocolCommand.CPWR:
                    return Reject(ref failure, E_EpsonErrorCode.ProtocolCommandNotAllowed,
                        "CPWR is rejected until a safe power sequence is defined.");

                default:
                    return Reject(ref failure, E_EpsonErrorCode.InvalidParameter,
                        $"Protocol command {(ushort)cmd.Protocol.Code} is not on the allowlist.");
            }
        }

        private static bool IsUsableOutput(E_EpsonOutput output)
        {
            switch (output)
            {
                case E_EpsonOutput.Reserve0:
                case E_EpsonOutput.Reserve1:
                case E_EpsonOutput.Reserve2:
                case E_EpsonOutput.Reserve3:
                case E_EpsonOutput.Unknown:
                    return false;
                default:
                    return true;
            }
        }

        private static bool Reject(ref EpsonExecutionResult failure, E_EpsonErrorCode code, string text)
        {
            failure.ErrorCode = code;
            failure.ErrorText = text;
            return false;
        }

        private EpsonExecutionResult? Fail(E_EpsonErrorCode code, string text)
        {
            _status.Progress = E_EpsonProgress.Error;
            _logger.LogWarning("Dummy failed: {Code} ({Text}).", code, text);
            return new EpsonExecutionResult { Success = false, ErrorCode = code, ErrorText = text };
        }
    }
}
