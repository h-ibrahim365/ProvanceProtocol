using Provance.Core.Verification;

namespace Provance.Core.Exceptions
{
    /// <summary>
    /// Thrown by <see cref="Services.Interfaces.ILedgerStore.AppendAsync"/> when an entry with the same
    /// sequence or id already exists. The writer handles it by reloading the head; applications rarely see it.
    /// </summary>
    public sealed class LedgerConcurrencyException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public LedgerConcurrencyException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// The entry could not be written: the writer is stopped, or the store kept failing after all retries.
    /// The entry may or may not have been persisted when the cause is a store failure.
    /// </summary>
    public class LedgerUnavailableException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public LedgerUnavailableException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// The queue is full and <see cref="Options.QueueFullBehavior.FailFast"/> is configured.
    /// Map it to HTTP 503 or 429.
    /// </summary>
    public sealed class LedgerOverloadedException : LedgerUnavailableException
    {
        /// <summary>Creates the exception.</summary>
        public LedgerOverloadedException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// An operation succeeded on the primary store but not on the archive copy, for example a payload erased in the
    /// primary store that is still present in the archive. Retry it, or erase it in the archive by other means.
    /// </summary>
    public sealed class LedgerArchiveException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public LedgerArchiveException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Thrown by <see cref="Services.Interfaces.ILedgerService.VerifyAsync"/> when
    /// <see cref="LedgerVerificationOptions.ThrowOnFailure"/> is set and verification fails.
    /// </summary>
    public sealed class LedgerTamperedException : Exception
    {
        /// <summary>Creates the exception from a failed verification result.</summary>
        public LedgerTamperedException(LedgerVerificationResult result)
            : base(result.Message)
        {
            Result = result;
        }

        /// <summary>The verification result that caused the exception.</summary>
        public LedgerVerificationResult Result { get; }
    }
}
