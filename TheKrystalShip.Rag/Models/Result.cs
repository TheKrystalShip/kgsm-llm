namespace TheKrystalShip.Rag.Models;

/// <summary>
/// Represents the result of an operation, with success/failure status and an optional error
/// message.
/// <para>
/// This is a deliberate, dependency-free copy of <c>TheKrystalShip.Llm.Models.Result</c>: the
/// RAG core must not reference the (JIT, reflection-serializing) Llm package so it can be
/// published Native AOT (plan §D10). Same idiom, no coupling.
/// </para>
/// </summary>
/// <typeparam name="T">The type of the result value.</typeparam>
public class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, T? value, string? error)
    {
        if (isSuccess && error != null)
            throw new InvalidOperationException("A successful result cannot have an error message.");

        if (!isSuccess && error == null)
            throw new InvalidOperationException("A failure result must have an error message.");

        if (isSuccess && value == null)
            throw new InvalidOperationException("A successful result must have a value.");

        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public static Result<T> Success(T value) => new(true, value, null);

    public static Result<T> Failure(string error) => new(false, default, error);

    public static implicit operator Result(Result<T> result) =>
        result.IsSuccess ? Result.Success() : Result.Failure(result.Error!);
}

/// <summary>
/// Represents the result of an operation that doesn't return a value.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, string? error)
    {
        if (isSuccess && error != null)
            throw new InvalidOperationException("A successful result cannot have an error message.");

        if (!isSuccess && error == null)
            throw new InvalidOperationException("A failure result must have an error message.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, null);

    public static Result Failure(string error) => new(false, error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(string error) => Result<T>.Failure(error);
}
