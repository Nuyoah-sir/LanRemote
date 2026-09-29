namespace LanRemote.Transport;

internal enum VideoAttachStatus
{
    Attached,
    Cancelled,
    Unavailable,
    Expired,
    AlreadyAttached,
    InvalidProof,
    InvalidInput,
    IdentityMismatch
}
