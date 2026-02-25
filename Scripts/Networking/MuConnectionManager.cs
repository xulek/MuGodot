using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial;

namespace MuGodot.Networking;

internal sealed class MuConnectionManager(SimpleModulusKeys encryptKeys, SimpleModulusKeys decryptKeys) : IAsyncDisposable
{
    private readonly SimpleModulusKeys _encryptKeys = encryptKeys;
    private readonly SimpleModulusKeys _decryptKeys = decryptKeys;

    private SocketConnection? _socketConnection;
    private IConnection? _connection;
    private CancellationTokenSource? _receiveCts;

    public IConnection Connection => _connection ?? throw new InvalidOperationException("Connection is not initialized.");

    public IConnection? CurrentConnection => _connection;

    public bool IsConnected => _connection?.Connected ?? false;

    public async Task<bool> ConnectAsync(string host, int port, bool useEncryption, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return false;
        }

        await CleanupCurrentConnectionAsync();

        SocketConnection? newSocketConnection = null;
        IConnection? newConnection = null;
        CancellationTokenSource? newCts = null;

        try
        {
            var ipAddress = (await Dns.GetHostAddressesAsync(host, cancellationToken))
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (ipAddress is null)
            {
                return false;
            }

            var endpoint = new IPEndPoint(ipAddress, port);
            newSocketConnection = await SocketConnection.ConnectAsync(endpoint, new PipeOptions());

            IDuplexPipe transportPipe = newSocketConnection;
            if (useEncryption)
            {
                var decryptor = new PipelinedSimpleModulusDecryptor(transportPipe.Input, _decryptKeys);
                var simpleModulusEncryptor = new PipelinedSimpleModulusEncryptor(transportPipe.Output, _encryptKeys);
                var xor32Encryptor = new PipelinedXor32Encryptor(simpleModulusEncryptor.Writer);
                newConnection = new Connection(transportPipe, decryptor, xor32Encryptor, NullLogger<Connection>.Instance);
            }
            else
            {
                newConnection = new Connection(transportPipe, null, null, NullLogger<Connection>.Instance);
            }

            newCts = new CancellationTokenSource();

            _socketConnection = newSocketConnection;
            _connection = newConnection;
            _receiveCts = newCts;

            return true;
        }
        catch
        {
            await CleanupTemporaryResourcesAsync(newSocketConnection, newConnection, newCts);
            return false;
        }
    }

    public void StartReceiving(CancellationToken externalCancellationToken = default)
    {
        if (_connection is null || _receiveCts is null || _receiveCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _ = CancellationTokenSource.CreateLinkedTokenSource(_receiveCts.Token, externalCancellationToken);
            _ = _connection.BeginReceiveAsync();
        }
        catch
        {
            // The caller handles reconnection/errors through disconnection events.
        }
    }

    public async Task DisconnectAsync()
    {
        var connectionToDisconnect = _connection;
        var ctsToCancel = _receiveCts;

        if (ctsToCancel is not null && !ctsToCancel.IsCancellationRequested)
        {
            try
            {
                ctsToCancel.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }
        }

        if (connectionToDisconnect is not null && connectionToDisconnect.Connected)
        {
            try
            {
                await connectionToDisconnect.DisconnectAsync();
            }
            catch
            {
                // Ignore disconnect race.
            }
        }

        await CleanupCurrentConnectionAsync();
    }

    private async Task CleanupCurrentConnectionAsync()
    {
        var connectionToClean = _connection;
        var socketToClean = _socketConnection;
        var ctsToClean = _receiveCts;

        _connection = null;
        _socketConnection = null;
        _receiveCts = null;

        if (ctsToClean is not null)
        {
            try
            {
                if (!ctsToClean.IsCancellationRequested)
                {
                    ctsToClean.Cancel();
                }
            }
            catch
            {
                // Ignore cancellation race.
            }

            try
            {
                ctsToClean.Dispose();
            }
            catch
            {
                // Ignore disposal race.
            }
        }

        if (connectionToClean is IAsyncDisposable asyncDisposableConnection)
        {
            try
            {
                await asyncDisposableConnection.DisposeAsync();
            }
            catch
            {
                // Ignore disposal race.
            }
        }
        else if (connectionToClean is IDisposable disposableConnection)
        {
            try
            {
                disposableConnection.Dispose();
            }
            catch
            {
                // Ignore disposal race.
            }
        }

        if (socketToClean is not null)
        {
            try
            {
                socketToClean.Dispose();
            }
            catch
            {
                // Ignore disposal race.
            }
        }
    }

    private static async Task CleanupTemporaryResourcesAsync(
        SocketConnection? socketConnection,
        IConnection? connection,
        CancellationTokenSource? cancellationTokenSource)
    {
        if (cancellationTokenSource is not null)
        {
            try
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                }
            }
            catch
            {
                // Ignore cancellation race.
            }

            try
            {
                cancellationTokenSource.Dispose();
            }
            catch
            {
                // Ignore disposal race.
            }
        }

        if (connection is IAsyncDisposable asyncDisposableConnection)
        {
            try
            {
                await asyncDisposableConnection.DisposeAsync();
            }
            catch
            {
                // Ignore disposal race.
            }
        }
        else if (connection is IDisposable disposableConnection)
        {
            try
            {
                disposableConnection.Dispose();
            }
            catch
            {
                // Ignore disposal race.
            }
        }

        if (socketConnection is not null)
        {
            try
            {
                socketConnection.Dispose();
            }
            catch
            {
                // Ignore disposal race.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
