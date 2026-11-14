namespace Atlas.Server.Features.Ai.Providers;

/// <summary>
/// The one catalog of call outcomes, shared by <c>AiUsage</c>, the circuit breaker (A3) and Administrace → AI → Přehled.
/// A code, never an exception name or upstream text. <see cref="AiErrorClassifier"/> maps failures to it.
/// </summary>
public static class AiOutcome
{
    public const string Ok = "ok";
    public const string Timeout = "timeout";
    public const string Connection = "connection";
    public const string Http5xx = "http_5xx";
    public const string Http429 = "http_429";
    /// <summary>401 or 403: the key is wrong or lacks the right.</summary>
    public const string Auth = "auth";
    /// <summary>404: the server does not know the model (or the URL is wrong).</summary>
    public const string ModelNotFound = "model_not_found";
    /// <summary>400 and other 4xx: Atlas sent something the server refuses. Atlas's error, not the server's state.</summary>
    public const string BadRequest = "bad_request";
    /// <summary>The answer is unusable: no JSON where a schema was asked, a vector of the wrong size, not finite values.</summary>
    public const string InvalidOutput = "invalid_output";
    /// <summary>The server answered with a redirect; Atlas never follows one, so the key cannot leave for another host.</summary>
    public const string RedirectRefused = "redirect_refused";
    /// <summary>The stored key cannot be unprotected (the key ring was lost); the admin enters it again.</summary>
    public const string KeyUnreadable = "key_unreadable";
    /// <summary>The caller gave up (request aborted, shutdown).</summary>
    public const string Canceled = "canceled";
    /// <summary>No free concurrency slot for an interactive call (<see cref="AiConcurrencyGate"/>). Rejected before any call:
    /// never recorded as usage and never reported to the availability sink.</summary>
    public const string Busy = "busy";

    /// <summary>Whether the outcome says the profile is unusable right now, so it counts towards the circuit breaker (A3,
    /// O14). Atlas's own bad requests, cancellations, unusable answers and a busy gate do not.</summary>
    public static bool IsAvailabilityFailure(string code)
        => code is Timeout or Connection or Http5xx or Http429 or Auth or ModelNotFound or RedirectRefused or KeyUnreadable;
}

/// <summary>A failed model call, reduced to its <see cref="AiOutcome"/> code. Carries no inner exception and no upstream
/// text, so whatever logs it (a handler, the request error middleware) cannot leak a prompt, answer or error body.</summary>
public class AiCallException : Exception
{
    public AiCallException(string code) : base("AI call failed: " + code + ".")
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>The model answered, but not with what was asked: no JSON object, a vector of the wrong dimension or with
/// non-finite values.</summary>
public sealed class AiInvalidOutputException : AiCallException
{
    public AiInvalidOutputException() : base(AiOutcome.InvalidOutput)
    {
    }
}

/// <summary>The profile cannot serve the request at all (Anthropic asked for embeddings, a profile kind used for the wrong
/// operation, no URL). Thrown before any network call; a programming or configuration error, not an outage.</summary>
public sealed class AiConfigurationException(string message) : InvalidOperationException(message);
