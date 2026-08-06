using System;
namespace RaceTrade
{
    /// <summary>
    /// Represents the result of a CBFTP transfer operation.
    /// Provides clear success/failure status instead of void returns.
    /// </summary>
    public class TransferResult
    {
        public int? JobId { get; set; }
        /// <summary>
        /// Whether the transfer was successfully initiated.
        /// </summary>
        public bool Success { get; set; }
        /// <summary>
        /// Error message if Success is false, null otherwise.
        /// </summary>
        public string ErrorMessage { get; set; }
        /// <summary>
        /// HTTP status code from CBFTP API.
        /// </summary>
        public int? StatusCode { get; set; }
        /// <summary>
        /// Raw response from CBFTP API.
        /// </summary>
        public string RawResponse { get; set; }
        /// <summary>
        /// The endpoint that was called.
        /// </summary>
        public string Endpoint { get; set; }
        /// <summary>
        /// When the transfer was initiated.
        /// </summary>
        public DateTime Timestamp { get; set; }
        /// <summary>
        /// Creates a successful transfer result.
        /// </summary>
        public static TransferResult Successful(string endpoint, string response, int? jobId = null) 
        {
            return new TransferResult
            {
                Success = true,
                Endpoint = endpoint,
                RawResponse = response,
                StatusCode = 200,
                Timestamp = DateTime.Now,
                JobId = jobId // ⭐ SET the JobId
            };
        }
        /// <summary>
        /// Creates a failed transfer result.
        /// </summary>
        public static TransferResult Failed(string endpoint, string errorMessage, int? statusCode = null, string response = null)
        {
            return new TransferResult
            {
                Success = false,
                Endpoint = endpoint,
                ErrorMessage = errorMessage,
                StatusCode = statusCode,
                RawResponse = response,
                Timestamp = DateTime.Now
            };
        }
        /// <summary>
        /// Creates a result from an exception.
        /// </summary>
        public static TransferResult FromException(string endpoint, Exception ex)
        {
            return new TransferResult
            {
                Success = false,
                Endpoint = endpoint,
                ErrorMessage = $"Exception: {ex.Message}",
                RawResponse = ex.StackTrace,
                Timestamp = DateTime.Now
            };
        }
    }
}