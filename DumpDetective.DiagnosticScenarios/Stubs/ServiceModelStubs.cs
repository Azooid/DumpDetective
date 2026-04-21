// Stub types placed in the System.ServiceModel namespace so that ClrMD reports
// them as "System.ServiceModel.*" — exactly what DumpDetective wcf-channels looks for.
// The field names mirror real WCF channel internals that the command reads via ClrMD.
namespace System.ServiceModel;

internal enum CommunicationState { Created, Opening, Opened, Closing, Closed, Faulted }

internal sealed class ClientChannel
{
    public CommunicationState state;
    public string?            remoteAddress;
    public string?            binding;
    public string?            faultReason;

    public ClientChannel(CommunicationState s, string addr, string bind, string? fault = null)
    {
        state         = s;
        remoteAddress = addr;
        binding       = bind;
        faultReason   = fault;
    }
}

internal sealed class DuplexClientChannel
{
    public CommunicationState state;
    public string?            remoteAddress;
    public string?            binding;
    public string?            faultReason;

    public DuplexClientChannel(CommunicationState s, string addr, string bind, string? fault = null)
    {
        state         = s;
        remoteAddress = addr;
        binding       = bind;
        faultReason   = fault;
    }
}
