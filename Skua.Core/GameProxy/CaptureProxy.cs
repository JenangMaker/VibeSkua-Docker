using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Skua.Core.GameProxy;

public partial class CaptureProxy : ObservableRecipient, ICaptureProxy
{
    private CancellationTokenSource? _captureProxyCTS;

    /// <summary>
    /// The default port for the capture proxy to run on.
    /// </summary>
    public const int DefaultPort = 5588;

    public IPEndPoint? Destination { get; set; }
    public List<IInterceptor> Interceptors { get; } = new();

    private Thread? _thread;
    private TcpListener? _listener;
    private TcpClient? _forwarder;
    private TcpClient? _client;
    private int _listenPort = DefaultPort;

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    private bool _running;

    public void Start()
    {
        if (Destination == null)
            return;
        Running = true;
        _listenPort = Destination.Port;
        _thread = new(() =>
        {
            _captureProxyCTS = new();
            _listener = new TcpListener(IPAddress.Loopback, _listenPort);
            _Listen(_captureProxyCTS.Token);
            _captureProxyCTS.Dispose();
            _captureProxyCTS = null;
        })
        { Name = "Capture Proxy" };
        _thread.Start();
    }
    public void Stop()
    {
        _captureProxyCTS?.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_forwarder?.Connected ?? false)
        {
            _forwarder.Close();
            _forwarder.Dispose();
        }
        if (_client?.Connected ?? false)
        {
            _client.Close();
            _client.Dispose();
        }
        Running = false;
    }

    private void _Listen(CancellationToken token)
    {
        try
        {
            _listener?.Start();
        }
        catch
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            TcpClient? localClient = null;
            TcpClient? localForwarder = null;
            try
            {
                localClient = _listener?.AcceptTcpClient();
                if (localClient == null)
                    break;
                localClient.NoDelay = true;
                localForwarder = new TcpClient
                {
                    NoDelay = true
                };
                localForwarder.Connect(Destination!);

                _client = localClient;
                _forwarder = localForwarder;

                TcpClient client = localClient;
                TcpClient forwarder = localForwarder;

                Task.Factory.StartNew(() => _DataInterceptor(client, forwarder, true, token), token);
                Task.Factory.StartNew(() => _DataInterceptor(forwarder, client, false, token), token);
            }
            catch
            {
                localClient?.Close();
                localClient?.Dispose();
                localForwarder?.Close();
                localForwarder?.Dispose();
            }
        }

        _listener?.Stop();
    }
    private async Task _DataInterceptor(TcpClient target, TcpClient destination, bool outbound, CancellationToken token)
    {
        byte[] messageBuffer = new byte[4096];
        List<byte> cpacket = new();
        NetworkStream targetStream = target.GetStream();
        NetworkStream destStream = destination.GetStream();

        try
        {
            while (!token.IsCancellationRequested && target.Connected && destination.Connected)
            {
                int read = await targetStream.ReadAsync(messageBuffer, token).ConfigureAwait(false);

                if (read == 0)
                    break;

                for (int i = 0; i < read; i++)
                {
                    if (token.IsCancellationRequested)
                        break;

                    byte b = messageBuffer[i];
                    if (b > 0)
                    {
                        cpacket.Add(b);
                        continue;
                    }

                    if (cpacket.Count == 0)
                        continue;

                    byte[] data = cpacket.ToArray();
                    cpacket.Clear();

                    MessageInfo message = new(Encoding.UTF8.GetString(data, 0, data.Length));
                    // A snapshot under the lock the view model registers with (upstream cfcb4a2).
                    IInterceptor[] currentInterceptors;
                    lock (Interceptors)
                    {
                        currentInterceptors = Interceptors.Count == 0
                            ? Array.Empty<IInterceptor>()
                            : Interceptors.OrderBy(i => i.Priority).ToArray();
                    }

                    if (currentInterceptors.Length > 0)
                    {
                        foreach (IInterceptor interceptor in currentInterceptors)
                            interceptor.Intercept(message, outbound);
                    }

                    if (message.Send)
                    {
                        byte[] contentBytes = Encoding.UTF8.GetBytes(message.Content);
                        byte[] msg = new byte[contentBytes.Length + 1];
                        Buffer.BlockCopy(contentBytes, 0, msg, 0, contentBytes.Length);
                        await destStream.WriteAsync(msg, token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* Cancelled */
        }
        finally
        {
            targetStream?.Dispose();
            destStream?.Dispose();
            try { target.Close(); } catch { }
            try { destination.Close(); } catch { }
        }
    }

}