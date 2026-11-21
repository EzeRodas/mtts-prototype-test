namespace MoonTTS.Domain.Common;

using System;
using System.Diagnostics.CodeAnalysis;

/* Same as result but holds a value. Sepparated them for visual clarity */

public readonly struct Result<T>
{
    [MemberNotNullWhen(true, nameof(Value))]
    public bool IsSuccess { get; }

    [MemberNotNullWhen(false, nameof(Value))]
    public bool IsFailure => !IsSuccess;
    public T? Value { get; }
    public string? ErrorMessage { get; }
    public Exception? Exception { get; }

    /* Private constructor to enforce factory method usage */
    private Result(bool isSuccess,T? value, string? errorMessage, Exception? exception)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorMessage = errorMessage;
        Exception = exception;
    }
    
    /* Creates a successful result containing a typed value */
    public static Result<T> Success(T value) => 
        new(true,value,null,null);

    /* Creates a failed result with an error message and optional exception */
    public static Result<T> Failure(string error, Exception? ex = null) => 
        new(false,default,error,ex);

    /* Creates a failed result from an exception */
    public static Result<T> Failure(Exception ex) => 
        new(false,default,ex.Message,ex);
    
    /* Implicit operator for clean syntax */
    public static implicit operator Result<T>(T value) => Success(value);
}