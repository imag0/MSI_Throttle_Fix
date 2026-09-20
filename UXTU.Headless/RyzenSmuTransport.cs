using System.Threading;
using Universal_x86_Tuning_Utility.Scripts.AMD_Backend;

namespace UXTU.Headless;

public sealed record MailboxDefinition(
    string Name,
    uint MessageAddress,
    uint ResponseAddress,
    uint ArgumentAddress)
{
    public static readonly MailboxDefinition Mp1 = new(
        "MP1",
        DragonRangeAm5CommandTable.Mp1MessageAddress,
        DragonRangeAm5CommandTable.Mp1ResponseAddress,
        DragonRangeAm5CommandTable.Mp1ArgumentAddress);

    public static readonly MailboxDefinition Rsmu = new(
        "RSMU",
        DragonRangeAm5CommandTable.RsmuMessageAddress,
        DragonRangeAm5CommandTable.RsmuResponseAddress,
        DragonRangeAm5CommandTable.RsmuArgumentAddress);
}

/// <summary>
/// Minimal synchronous extraction of UXTU's RyzenSMU mailbox flow. It deliberately
/// executes one complete command at a time and returns every transport/SMU failure.
/// </summary>
public sealed class RyzenSmuTransport : IDisposable
{
    private const string ReadFunction = "ioctl_read_smu_register";
    private const string WriteFunction = "ioctl_write_smu_register";
    private const string PciMutexName = "Global\\Access_PCI";
    private const int PollLimit = 8192;
    private const int MutexTimeoutMs = 1000;
    private const uint MaxArguments = 6;

    private readonly AMDPawnIo _pawnIo;
    private Mutex? _pciMutex;
    private bool _disposed;

    public RyzenSmuTransport(AMDPawnIo pawnIo)
    {
        _pawnIo = pawnIo ?? throw new ArgumentNullException(nameof(pawnIo));
    }

    public void Open()
    {
        ThrowIfDisposed();
        if (_pciMutex is not null)
            return;

        try
        {
            _pciMutex = new Mutex(false, PciMutexName);
        }
        catch (UnauthorizedAccessException)
        {
            _pciMutex = Mutex.OpenExisting(PciMutexName);
        }
    }

    public SmuSendResult Send(MailboxDefinition mailbox, uint message, uint argument)
    {
        ThrowIfDisposed();
        if (message == 0)
            return SmuSendResult.Failure("Invalid message", "Message ID must not be zero.");

        Open();
        if (_pciMutex is null || !WaitForMutex(_pciMutex, MutexTimeoutMs))
            return SmuSendResult.Failure("PCI mutex timeout", $"Could not acquire {PciMutexName}.");

        try
        {
            if (!WaitForResponse(mailbox.ResponseAddress, out _, out string? waitError))
                return SmuSendResult.Failure("Mailbox was not ready", waitError);

            if (!Write32(mailbox.ResponseAddress, 0, out string? clearError))
                return SmuSendResult.Failure("Failed to clear response register", clearError);

            for (uint index = 0; index < MaxArguments; index++)
            {
                uint value = index == 0 ? argument : 0;
                uint register = mailbox.ArgumentAddress + (index * 4);
                if (!Write32(register, value, out string? argumentError))
                    return SmuSendResult.Failure($"Failed to write argument {index}", argumentError);
            }

            if (!Write32(mailbox.MessageAddress, message, out string? messageError))
                return SmuSendResult.Failure("Failed to write message register", messageError);

            if (!WaitForResponse(mailbox.ResponseAddress, out uint response, out string? responseError))
                return SmuSendResult.Failure("SMU response timeout", responseError);

            if (response != 0x01)
                return SmuSendResult.Failure(
                    $"SMU rejected command with response 0x{response:X8}",
                    DescribeResponse(response),
                    response);

            // Match UXTU's flow by reading back all returned argument slots on success.
            for (uint index = 0; index < MaxArguments; index++)
            {
                uint register = mailbox.ArgumentAddress + (index * 4);
                if (!Read32(register, out _, out string? readbackError))
                    return SmuSendResult.Failure($"Failed to read argument {index}", readbackError, response);
            }

            return SmuSendResult.Success(response);
        }
        catch (Exception ex)
        {
            return SmuSendResult.Failure("Unhandled mailbox transport error", ex.ToString());
        }
        finally
        {
            try { _pciMutex.ReleaseMutex(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try { _pciMutex?.Dispose(); } catch { }
        _pciMutex = null;
        _disposed = true;
    }

    private bool WaitForResponse(uint responseRegister, out uint response, out string? error)
    {
        response = 0;
        error = null;
        for (int attempt = 0; attempt < PollLimit; attempt++)
        {
            if (!Read32(responseRegister, out response, out error))
                return false;
            if (response != 0)
                return true;
        }

        error = $"No nonzero response after {PollLimit} polls at 0x{responseRegister:X8}.";
        return false;
    }

    private bool Read32(uint register, out uint value, out string? error)
    {
        long[] input = [unchecked((long)register)];
        long[] output = new long[1];
        int hr = _pawnIo.ExecuteHr(ReadFunction, input, 1, output, 1, out uint returned);
        if (hr != 0 || returned < 1)
        {
            value = 0;
            error = $"{ReadFunction}(0x{register:X8}) failed: HRESULT=0x{hr:X8}, returned={returned}.";
            return false;
        }

        value = unchecked((uint)output[0]);
        error = null;
        return true;
    }

    private bool Write32(uint register, uint value, out string? error)
    {
        long[] input = [unchecked((long)register), unchecked((long)value)];
        int hr = _pawnIo.ExecuteHr(WriteFunction, input, 2, Array.Empty<long>(), 0, out _);
        if (hr != 0)
        {
            error = $"{WriteFunction}(0x{register:X8}, 0x{value:X8}) failed: HRESULT=0x{hr:X8}.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool WaitForMutex(Mutex mutex, int timeoutMs)
    {
        try { return mutex.WaitOne(timeoutMs, false); }
        catch (AbandonedMutexException) { return true; }
        catch { return false; }
    }

    private static string DescribeResponse(uint response) => response switch
    {
        0xFF => "SMU reported failure.",
        0xFE => "SMU reported unknown command.",
        0xFD => "SMU rejected a prerequisite.",
        0xFC => "SMU reported busy.",
        _ => "Unexpected SMU response."
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
