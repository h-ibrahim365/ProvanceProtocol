namespace Provance.Core.Exceptions
{
    /// <summary>
    /// Exception thrown when the cryptographic integrity check of the ledger fails,
    /// indicating that tampering or data corruption has been detected.
    /// </summary>
    public class LedgerTamperedException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerTamperedException"/> class 
        /// with a default error message.
        /// </summary>
        public LedgerTamperedException()
            : base("Ledger integrity check failed. The cryptographic chain has been tampered with or corrupted.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerTamperedException"/> class 
        /// with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public LedgerTamperedException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LedgerTamperedException"/> class 
        /// with a specified error message and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
        public LedgerTamperedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}