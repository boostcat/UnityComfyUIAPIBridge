using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.API
{
    public sealed class SimpleWebSocket : IDisposable
    {
        private const int ReceiveBufferSize = 16 * 1024;
        private const int ConnectTimeoutSeconds = 5;

        private readonly Uri _uri;
        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private Task _worker;
        private int _transportClosed;
        private volatile bool _stopRequested;
        private volatile bool _open;
        private bool _disposed;

        private UInt16 _closeCode = 1006;
        private string _closeReason = "连接已关闭";

        public SimpleWebSocket(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            _uri = uri;
        }

        public bool IsOpen
        {
            get
            {
                return _open && !_stopRequested && _socket != null
                       && _socket.State == WebSocketState.Open;
            }
        }

        public bool IsAlive { get { return !_stopRequested && Volatile.Read(ref _transportClosed) == 0; } }

        public event Action<SimpleWebSocket> OnOpen;

        public event Action<SimpleWebSocket, string> OnMessage;

        public event Action<SimpleWebSocket, byte[]> OnBinary;

        public event Action<SimpleWebSocket, UInt16, string> OnClosed;

        public event Action<SimpleWebSocket, string> OnError;

        public void Open()
        {
            if (_disposed || _worker != null) return;
            _stopRequested = false;
            _cts = new CancellationTokenSource();
            _socket = new ClientWebSocket();
            try { _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30); }
            catch (Exception) {  }
            _worker = Task.Run(RunAsync);
        }

        public void Close()
        {
            if (_disposed || _stopRequested) return;
            _stopRequested = true;

            var socket = _socket;
            if (socket != null && socket.State == WebSocketState.Open)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                                                          "client close", timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception) {  }
                });
            }
            try { if (_cts != null) _cts.Cancel(); }
            catch (Exception) {  }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Close();
            _disposed = true;
            OnOpen = null;
            OnMessage = null;
            OnBinary = null;
            OnClosed = null;
            OnError = null;
        }

        private async Task RunAsync()
        {
            bool errorReported = false;
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectTimeoutSeconds)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeout.Token))
                {
                    await _socket.ConnectAsync(_uri, linked.Token).ConfigureAwait(false);
                }

                if (_stopRequested) return;
                _open = true;
                RaiseOpen();
                await ReceiveLoopAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!_stopRequested)
                {
                    errorReported = true;
                    RaiseError("连接超时或被取消");
                }
            }
            catch (Exception e)
            {
                _closeReason = e.Message;
                if (!_stopRequested)
                {
                    errorReported = true;
                    RaiseError(e.Message);
                }
            }
            finally
            {
                _open = false;
                DisposeTransport();
                if (!_stopRequested && !errorReported)
                    RaiseClosed(_closeCode, _closeReason);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[ReceiveBufferSize];
            var segment = new ArraySegment<byte>(buffer);
            MemoryStream message = null;
            WebSocketMessageType messageType = WebSocketMessageType.Binary;
            try
            {
                while (!_stopRequested)
                {
                    var result = await _socket.ReceiveAsync(segment, _cts.Token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _closeCode = result.CloseStatus.HasValue
                            ? (UInt16)result.CloseStatus.Value
                            : (UInt16)1005;
                        _closeReason = result.CloseStatusDescription ?? string.Empty;
                        try
                        {
                            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                                                              string.Empty, timeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception) { }
                        return;
                    }

                    if (message == null)
                    {
                        messageType = result.MessageType;
                        message = new MemoryStream();
                    }
                    if (result.Count > 0) message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    byte[] full = message.ToArray();
                    message.Dispose();
                    message = null;
                    if (messageType == WebSocketMessageType.Text)
                        RaiseMessage(Encoding.UTF8.GetString(full));
                    else if (messageType == WebSocketMessageType.Binary)
                        RaiseBinary(full);
                }
            }
            finally
            {
                if (message != null) message.Dispose();
            }
        }

        private void DisposeTransport()
        {
            if (Interlocked.Exchange(ref _transportClosed, 1) != 0) return;
            try { if (_socket != null) _socket.Dispose(); } catch (Exception) { }
            try { if (_cts != null) _cts.Dispose(); } catch (Exception) { }
        }

        private void RaiseOpen()
        {
            var h = OnOpen;
            if (h == null) return;
            try { h(this); } catch (Exception e) { LogCallbackError(e); }
        }

        private void RaiseMessage(string message)
        {
            var h = OnMessage;
            if (h == null) return;
            try { h(this, message); } catch (Exception e) { LogCallbackError(e); }
        }

        private void RaiseBinary(byte[] data)
        {
            var h = OnBinary;
            if (h == null) return;
            try { h(this, data); } catch (Exception e) { LogCallbackError(e); }
        }

        private void RaiseClosed(UInt16 code, string reason)
        {
            var h = OnClosed;
            if (h == null) return;
            try { h(this, code, reason); } catch (Exception e) { LogCallbackError(e); }
        }

        private void RaiseError(string error)
        {
            var h = OnError;
            if (h == null) return;
            try { h(this, error); } catch (Exception e) { LogCallbackError(e); }
        }

        private static void LogCallbackError(Exception e)
        {
            System.Diagnostics.Debug.WriteLine("[SimpleWebSocket] 事件回调异常：" + e);
        }
    }
}
