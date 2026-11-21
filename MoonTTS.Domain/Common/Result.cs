namespace MoonTTS.Domain.Common;

using System;

public readonly struct Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string? ErrorMessage { get; }
    public Exception? Exception { get; }
    
    /* Private constructor to enforce factory method usage */
    private Result(bool isSuccess, string? errorMessage, Exception? exception)
    {
        IsSuccess = isSuccess;
        ErrorMessage = errorMessage;
        Exception = exception;
    }
    
    /* Creates a successful result */
    public static Result Success() => new(true, null, null);

    /* Creates a failed result with an error message and optional exception */
    public static Result Failure(string error, Exception? ex = null) => 
        new(false, error, ex);

    /* Creates a failed result from an exception */
    public static Result Failure(Exception ex) => 
        new(false, ex.Message, ex);
}