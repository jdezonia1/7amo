namespace Raffaello.Core.Remote;

/// <summary>The server cannot be reached (network down, server stopped, timeout). The remote store switches to offline mode.</summary>
public sealed class ServerUnavailableException : Exception
{
    public ServerUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The server refused the request (permission, business rule, bad input). <see cref="Error"/> carries the stable code.</summary>
public class RemoteRejectedException : Exception
{
    public int Status { get; }
    public ErrorDto Error { get; }
    public RemoteRejectedException(int status, ErrorDto error) : base(error.Message.Length > 0 ? error.Message : $"The server refused the request ({status}).")
    {
        Status = status; Error = error;
    }
}

/// <summary>422 remaining_exceeded: another user claimed the remaining quantity first. Give a reason to post it OVER, or reduce it.</summary>
public sealed class RemainingExceededException : RemoteRejectedException
{
    public RemainingExceededException(ErrorDto error) : base(422, error) { }
}

/// <summary>403: the signed-in role may not do this (see <see cref="PermissionMatrix"/>).</summary>
public sealed class PermissionDeniedException : RemoteRejectedException
{
    public PermissionDeniedException(ErrorDto error) : base(403, error) { }
}

/// <summary>401: not signed in, or the session expired. Sign in again in Settings.</summary>
public sealed class RemoteAuthException : RemoteRejectedException
{
    public RemoteAuthException(ErrorDto error) : base(401, error) { }
}
