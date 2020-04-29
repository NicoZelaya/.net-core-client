﻿using Splitio.Services.Exceptions;
using Splitio.Services.Common;
using Splitio.Services.Logger;
using Splitio.Services.Shared.Classes;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Splitio.Services.Shared.Interfaces;

namespace Splitio.Services.EventSource
{
    public class EventSourceClient : IEventSourceClient
    {
        private const string KeepAliveResponse = "\n";
        private const int ReadTimeout = 70;

        private readonly ISplitLogger _log;
        private readonly INotificationParser _notificationParser;
        private readonly IBackOff _backOff;
        private readonly IWrapperAdapter _wrapperAdapter;

        private readonly object _connectedLock = new object();
        private bool _connected;

        private ISplitioHttpClient _splitHttpClient;
        private CancellationTokenSource _cancellationTokenSource;
        private string _url;

        public EventSourceClient(int backOffBase,
            ISplitLogger log = null,
            INotificationParser notificationParser = null,
            IBackOff backOff = null,
            IWrapperAdapter wrapperAdapter = null)
        {
            _log = log ?? WrapperAdapter.GetLogger(typeof(EventSourceClient));
            _notificationParser = notificationParser ?? new NotificationParser();
            _backOff = backOff ?? new BackOff(backOffBase);
            _wrapperAdapter = wrapperAdapter ?? new WrapperAdapter();
        }

        public event EventHandler<EventReceivedEventArgs> EventReceived;
        public event EventHandler<FeedbackEventArgs> ConnectedEvent;
        public event EventHandler<FeedbackEventArgs> DisconnectEvent;

        #region Public Methods
        public void Connect(string url)
        {
            _url = url;
            Task.Factory.StartNew(() => ConnectAsync());
        }

        public bool IsConnected()
        {
            lock (_connectedLock)
            {
                return _connected;
            }
        }

        public void Disconnect()
        {
            if (_cancellationTokenSource.IsCancellationRequested) return;
            
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _splitHttpClient.Dispose();

            UpdateStatus(connected: false);

            if (_backOff.GetAttempt() == 0)
            {
                DispatchDisconnect();
            }

            _log.Info($"Disconnected from {_url}");
        }
        #endregion

        #region Private Methods
        private async Task ConnectAsync()
        {
            try
            {
                _wrapperAdapter.TaskDelay(Convert.ToInt32(_backOff.GetInterval()) * 1000).Wait();

                _splitHttpClient = new SplitioHttpClient();
                _cancellationTokenSource = new CancellationTokenSource();

                _log.Info($"Connecting to {_url}");

                using (var response = await _splitHttpClient.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, _cancellationTokenSource.Token))
                {
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        stream.ReadTimeout = ReadTimeout;
                        _log.Info($"Connected to {_url}");
                        UpdateStatus(connected: true);
                        _backOff.Reset();
                        DispatchConnected();
                        await ReadStreamAsync(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"Error connecting to {_url}: {ex.Message}");                

                Disconnect();
                ConnectAsync();
            }
        }

        private async Task ReadStreamAsync(Stream stream)
        {
            var encoder = new UTF8Encoding();

            _log.Debug($"Reading stream ....");

            while (!_cancellationTokenSource.IsCancellationRequested && IsConnected())
            {
                if (stream.CanRead)
                {
                    var buffer = new byte[2048];

                    int len = await stream.ReadAsync(buffer, 0, 2048, _cancellationTokenSource.Token);

                    if (len > 0 && IsConnected())
                    {
                        var notificationString = encoder.GetString(buffer, 0, len);
                        _log.Debug($"Read stream encoder buffer: {notificationString}");

                        if (notificationString != KeepAliveResponse)
                        {
                            var lines = notificationString.Split('\n');
                            foreach (var line in lines)
                            {

                                try
                                {
                                    var eventData = _notificationParser.Parse(line);

                                    DispatchEvent(eventData);
                                }
                                catch (NotificationErrorException ex)
                                {
                                    _log.Debug($"Notification error: {ex.Message}. Status Server: {ex.Notification.Error.StatusCode}.");
                                    Disconnect();
                                }
                                catch (Exception ex)
                                {
                                    _log.Debug($"Error during event parse: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }

            _log.Debug($"Stop read stream");
        }

        private void DispatchEvent(IncomingNotification incomingNotification)
        {
            _log.Debug($"DispatchEvent: {incomingNotification}");
            OnEvent(new EventReceivedEventArgs(incomingNotification));
        }

        private void DispatchDisconnect()
        {
            OnDisconnect(new FeedbackEventArgs(isConnected: false));
        }

        private void DispatchConnected()
        {
            OnConnected(new FeedbackEventArgs(isConnected: true));
        }

        private void OnEvent(EventReceivedEventArgs e)
        {
            EventReceived?.Invoke(this, e);
        }

        private void OnConnected(FeedbackEventArgs e)
        {
            ConnectedEvent?.Invoke(this, e);
        }

        private void OnDisconnect(FeedbackEventArgs e)
        {
            DisconnectEvent?.Invoke(this, e);
        }

        private void UpdateStatus(bool connected)
        {
            lock (_connectedLock)
            {
                _connected = connected;
            }
        }
        #endregion
    }
}
