﻿using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace EpicTransport {
    public class Client : Common {

        public bool Connected { get; private set; }
        public bool Error { get; private set; }

        private event Action<byte[], int> OnReceivedData;
        private event Action OnConnected;
        private event Action OnDisconnected;

        private TimeSpan ConnectionTimeout;

        private ProductUserId hostProductId = null;
        private TaskCompletionSource<Task> connectedComplete;
        private CancellationTokenSource cancelToken;

        private Client(EosTransport transport) : base(transport) {
            ConnectionTimeout = TimeSpan.FromSeconds(Math.Max(1, transport.Timeout));
        }

        public static Client CreateClient(EosTransport transport, string host) {
            Client c = new Client(transport);

            c.OnConnected += () => transport.OnClientConnected.Invoke();
            c.OnDisconnected += () => transport.OnClientDisconnected.Invoke();
            c.OnReceivedData += (data, channel) => transport.OnClientDataReceived.Invoke(new ArraySegment<byte>(data), channel);

            if (EOSSDKComponent.Initialized) {
                c.Connect(host);
            } else {
                Debug.LogError("EOS not initialized");
                c.OnConnectionFailed(null);
            }

            return c;
        }

        private async void Connect(string host) {
            cancelToken = new CancellationTokenSource();

            try {
                hostProductId = ProductUserId.FromString(host);
                connectedComplete = new TaskCompletionSource<Task>();

                OnConnected += SetConnectedComplete;

                SendInternal(hostProductId, InternalMessages.CONNECT);

                Task connectedCompleteTask = connectedComplete.Task;

                if (await Task.WhenAny(connectedCompleteTask, Task.Delay(ConnectionTimeout, cancelToken.Token)) != connectedCompleteTask) {
                    Debug.LogError($"Connection to {host} timed out.");
                    OnConnected -= SetConnectedComplete;
                    OnConnectionFailed(hostProductId);
                }

                OnConnected -= SetConnectedComplete;
            } catch (FormatException) {
                Debug.LogError($"Connection string was not in the right format. Did you enter a ProductId?");
                Error = true;
                OnConnectionFailed(hostProductId);
            } catch (Exception ex) {
                Debug.LogError(ex.Message);
                Error = true;
                OnConnectionFailed(hostProductId);
            } finally {
                if (Error) {
                    OnConnectionFailed(null);
                }
            }

        }

        public void Disconnect() {
            Debug.Log("Sending Disconnect message");
            SendInternal(hostProductId, InternalMessages.DISCONNECT);
            Dispose();
            cancelToken?.Cancel();

            WaitForClose(hostProductId);
        }

        private void SetConnectedComplete() => connectedComplete.SetResult(connectedComplete.Task);

        protected override void OnReceiveData(byte[] data, ProductUserId clientUserId, int channel) {
            if (clientUserId != hostProductId) {
                Debug.LogError("Received a message from an unknown");
                return;
            }

            OnReceivedData.Invoke(data, channel);
        }

        protected override void OnNewConnection(OnIncomingConnectionRequestInfo result) {
            if (hostProductId == result.RemoteUserId) {
                EOSSDKComponent.GetP2PInterface().AcceptConnection(
                    new AcceptConnectionOptions() {
                        LocalUserId = EOSSDKComponent.LocalUserProductId,
                        RemoteUserId = result.RemoteUserId,
                        SocketId = new SocketId() { SocketName = SOCKET_ID }
                    });
            } else {
                Debug.LogError("P2P Acceptance Request from unknown host ID.");
            }
        }

        protected override void OnReceiveInternalData(InternalMessages type, ProductUserId clientUserId) {
            switch (type) {
                case InternalMessages.ACCEPT_CONNECT:
                    Connected = true;
                    OnConnected.Invoke();
                    Debug.Log("Connection established.");
                    break;
                case InternalMessages.DISCONNECT:
                    Connected = false;
                    Debug.Log("Disconnected.");
                    OnDisconnected.Invoke();
                    break;
                default:
                    Debug.Log("Received unknown message type");
                    break;
            }
        }

        public void Send(byte[] data, int channelId) => Send(hostProductId, data, (byte) channelId);

        protected override void OnConnectionFailed(ProductUserId remoteId) => OnDisconnected.Invoke();
    }
}