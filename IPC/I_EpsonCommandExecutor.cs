namespace EpsonRobot.Service.Ads
{
    /// <summary>Cyclic execution status published by the executor.</summary>
    public struct EpsonExecutionStatus
    {
        public E_EpsonProgress Progress;
        public bool WaferPicked;
        public bool WaferPlaced;
        public uint RequestedReleaseMask;
    }

    /// <summary>Operator prompt raised by a running state machine.</summary>
    public sealed class EpsonUserPromptRequest : EventArgs
    {
        public uint MessageId { get; set; }
        public uint OptionsMask { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>Terminal result of a command execution.</summary>
    public struct EpsonExecutionResult
    {
        public bool Success;
        public E_EpsonErrorCode ErrorCode;
        public string ErrorText;
    }

    /// <summary>
    /// Command execution abstraction. Dummy implementation now,
    /// real services / state machines later.
    /// </summary>
    public interface IEpsonCommandExecutor
    {
        /// <summary>Validate and prepare a command. False = rejected with failure details.</summary>
        bool TryStart(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc, out EpsonExecutionResult failure, out uint requestMask);

        /// <summary>Cyclic execution step. Null while running, terminal result when finished.</summary>
        EpsonExecutionResult? Execute(in ST_EpsonCmd cmd, in ST_PlcToEpsonIpc plc);

        /// <summary>Stop the active execution (reconnect, withdrawn command, failure). Must be idempotent.</summary>
        void Abort(string reason);

        /// <summary>Current progress, wafer flags and requested lock releases.</summary>
        EpsonExecutionStatus Status { get; }

        /// <summary>Operator reaction for the active prompt (already validated by the service).</summary>
        void AnswerUserPrompt(uint messageId, E_EpsonUserReaction reaction);

        /// <summary>SPS answer to the active press recipe start request.</summary>
        void AnswerRecipeStart(uint sequence, E_EpsonPressResult result);

        /// <summary>Raised when a running state machine waits for an operator reaction.</summary>
        event EventHandler<EpsonUserPromptRequest>? UserPromptRequested;

        /// <summary>Raised when a running MoveWafer requests the press recipe start.</summary>
        event EventHandler? RecipeStartRequested;
    }
}
