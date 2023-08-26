using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protocol;
using UnityEngine;
using Ping = Protocol.Ping;

namespace Framework
{
    public enum ConnectStatus
    {
        Succeed,
        Failed,
        Closed
    }

    public class MessageInfo
    {
        public UInt16   MessageId;
        public IMessage Message;
    }

    public class NetworkConnector
    {
        private bool _isConnecting = false;
        private bool _isClosing    = false;

        private bool  _isUsePing    = true;
        private int   _pingInterval = 3;
        private int   _pongTimeOut  = 120;
        private float _lastPingTime = 0f;
        private float _lastPongTime = 0f;

        private Socket           _socket;
        private ByteArray        _readBuffer;
        private Queue<ByteArray> _sendQueue;

        private       List<MessageInfo> _messageList                 = new List<MessageInfo>();
        private       int               _messageCount                = 0;
        private const int               MAX_HANDLE_MESSAGE_PER_FRAME = 10;

        private Dictionary<ConnectStatus, Action<string>> _eventHandlers =
            new Dictionary<ConnectStatus, Action<string>>();

        private Dictionary<UInt16, Action<IMessage>>
            _messageHandlers = new Dictionary<UInt16, Action<IMessage>>();

        #region - Connect EventHandler -

        public void AddEventHandler(ConnectStatus status, Action<string> eventHandler)
        {
            if (_eventHandlers.ContainsKey(status))
            {
                _eventHandlers[status] += eventHandler;
            }
            else
            {
                _eventHandlers[status] = eventHandler;
            }
        }

        public void RemoveEventHandler(ConnectStatus status, Action<string> eventHandler)
        {
            if (!_eventHandlers.ContainsKey(status)) return;

            _eventHandlers[status] -= eventHandler;

            if (_eventHandlers[status] != null) return;

            _eventHandlers.Remove(status);
        }

        private void InvokeEventHandlers(ConnectStatus status, string message)
        {
            if (!_eventHandlers.ContainsKey(status)) return;

            _eventHandlers[status]?.Invoke(message);
        }

        #endregion

        #region - Message Handler -

        public void AddMessageHandler(Type type, Action<IMessage> handler)
        {
            UInt16 messageId = MessageUtils.MessageToId[type];
            
            if (_messageHandlers.ContainsKey(messageId))
            {
                _messageHandlers[messageId] += handler;
            }
            else
            {
                _messageHandlers[messageId] = handler;
            }
        }

        public void RemoveMessageHandler(Type type, Action<IMessage> handler)
        {
            UInt16 messageId = MessageUtils.MessageToId[type];
            
            if (_messageHandlers.ContainsKey(messageId))
            {
                _messageHandlers[messageId] -= handler;
            }
        }

        private void InvokeMessageHandlers(UInt16 messageId, IMessage message)
        {
            if (_messageHandlers.ContainsKey(messageId))
            {
                _messageHandlers[messageId]?.Invoke(message);
            }
        }

        #endregion

        #region - Connect -

        public void Connect(string ip, int port)
        {
            if (_socket != null && _socket.Connected)
            {
                EzLog.Log(LogType.Warning, "Connect Failed, already connected!", "NetworkManager");
                return;
            }

            if (_isConnecting)
            {
                EzLog.Log(LogType.Warning, "Connect Failed, now connecting...", "NetworkManager");
                return;
            }

            Init();

            _socket.NoDelay = true;
            _isConnecting   = true;

            EzLog.Log(LogType.Normal, "Start Connecting...", "NetworkManager");

            _socket.BeginConnect(ip, port, ConnectCallback, _socket);
        }

        private void ConnectCallback(IAsyncResult result)
        {
            try
            {
                var socket = (Socket)result.AsyncState;
                socket.EndConnect(result);

                EzLog.Log(LogType.Normal, "Connect Succeed!", "NetworkManager");

                InvokeEventHandlers(ConnectStatus.Succeed, "");
                _isConnecting = false;

                socket.BeginReceive(_readBuffer.Data, _readBuffer.WriteIndex, _readBuffer.Remain, 0, ReceiveCallback,
                    socket);
            }
            catch (SocketException ex)
            {
                EzLog.Log(LogType.Error, "Connect Failed!", "NetworkManager");
                Debug.LogException(ex);

                InvokeEventHandlers(ConnectStatus.Failed, ex.ToString());
                _isConnecting = false;
            }
        }

        #endregion

        #region - Init -

        private void Init()
        {
            InitState();
            InitPingPong();
        }

        private void InitState()
        {
            _socket       = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _readBuffer   = new ByteArray();
            _sendQueue   = new Queue<ByteArray>();
            _messageList  = new List<MessageInfo>();
            _messageCount = 0;

            _isConnecting = false;
            _isClosing    = false;
        }

        private void InitPingPong()
        {
            _lastPingTime = Time.realtimeSinceStartup;
            _lastPongTime = Time.realtimeSinceStartup;

            AddMessageHandler(typeof(Pong), (_) =>
            {
                _lastPongTime = Time.realtimeSinceStartup;
            });
        }

        #endregion

        #region - Close -

        public void Close()
        {
            if (_socket == null || !_socket.Connected)
            {
                EzLog.Log(LogType.Warning, "Close Failed, socket is null or not connected", "NetworkManager");
                return;
            }

            if (_isConnecting)
            {
                EzLog.Log(LogType.Warning, "Close Failed, socket is connecting...", "NetworkManager");
                return;
            }

            // 要等到writeQueue中的資料都發送完才可以關閉
            if (_sendQueue.Count > 0)
            {
                _isClosing = true;
            }
            else
            {
                _socket.Close();
                EzLog.Log(LogType.Normal, "Close Succeed, socket is disconnected!", "NetworkManager");
                InvokeEventHandlers(ConnectStatus.Closed, "");
            }
        }

        #endregion

        #region - Send Message -

        /// <summary>
        /// 訊息格式:
        /// 0                2                    4                                n+4
        /// |  總長度 2 Byte  |  MessageId 2 Byte  |          資料本體 n Byte          |
        /// 總長度與Message皆以小端表示
        /// </summary>
        public void Send(IMessage message)
        {
            if (_socket == null || !_socket.Connected)
            {
                EzLog.Log(LogType.Warning, "Send Failed, socket is null or not connected", "NetworkManager");
                return;
            }

            if (_isConnecting)
            {
                EzLog.Log(LogType.Warning, "Send Failed, socket is connecting...", "NetworkManager");
                return;
            }

            if (_isClosing)
            {
                EzLog.Log(LogType.Warning, "Send Failed, socket is closing...", "NetworkManager");
                return;
            }

            var messageId = MessageUtils.MessageToId[message.GetType()];

            // 資料編碼
            byte[] bodyBytes = MessageUtils.Encode(message);

            // 拼接 (總長度 + MessageId + Body)
            byte[] sendBytes = new byte[2 + 2 + bodyBytes.Length];

            sendBytes[0] = (byte)(sendBytes.Length % 256);
            sendBytes[1] = (byte)(sendBytes.Length / 256);

            sendBytes[2] = (byte)(messageId % 256);
            sendBytes[3] = (byte)(messageId / 256);

            Array.Copy(bodyBytes, 0, sendBytes, 2 + 2, bodyBytes.Length);

            // 寫入MessageQueue
            ByteArray byteArray = new ByteArray(sendBytes);
            int       count     = 0; //writeQueue的长度
            lock (_sendQueue)
            {
                _sendQueue.Enqueue(byteArray);
                count = _sendQueue.Count;
            }

            // 發送
            if (count == 1)
            {
                EzLog.Log(LogType.Debug, $"Send: {message.GetType().Name}" );
                _socket.BeginSend(sendBytes, 0, sendBytes.Length, 0, SendCallback, _socket);
            }
        }

        private void SendCallback(IAsyncResult result)
        {
            Socket socket = (Socket)result.AsyncState;

            if (socket == null || !socket.Connected)
            {
                EzLog.Log(LogType.Warning, "Send Failed, socket is null or not connected", "NetworkManager");
                return;
            }

            int count = socket.EndSend(result);

            ByteArray byteArray;
            lock (_sendQueue)
            {
                byteArray = _sendQueue.First();
            }

            // byteArray完整發送
            byteArray.ReadIndex += count;
            if (byteArray.Length == 0)
            {
                lock (_sendQueue)
                {
                    _sendQueue.Dequeue();
                    if (_sendQueue.Count >= 1)
                    {
                        byteArray = _sendQueue.First();
                    }
                    else
                    {
                        byteArray = null;
                    }
                }
            }

            // 繼續發送
            if (byteArray != null)
            {
                socket.BeginSend(byteArray.Data, byteArray.ReadIndex, byteArray.Length, 0, SendCallback, socket);
            }
            else if (_isClosing)
            {
                socket.Close();
            }
        }

        #endregion

        #region - Recieve Message -

        private void ReceiveCallback(IAsyncResult result)
        {
            try
            {
                Socket socket = (Socket)result.AsyncState;

                int count = socket.EndReceive(result);

                // 收到FIN訊號(count == 0)
                if (count == 0)
                {
                    Close();
                    return;
                }

                _readBuffer.WriteIndex += count;

                // 解析收到的Binary資料
                ParseReceivedData();

                // 繼續接收其他資料
                if (_readBuffer.Remain < 8)
                {
                    _readBuffer.ReuseCapacity();
                    _readBuffer.ReSize(_readBuffer.Length * 2);
                }

                socket.BeginReceive(_readBuffer.Data, _readBuffer.WriteIndex,
                    _readBuffer.Remain, 0, ReceiveCallback, socket);
            }
            catch (SocketException ex)
            {
                EzLog.Log(LogType.Warning, "Receive failed" + ex.ToString(), "NetworkManager");
            }
        }

        private void ParseReceivedData()
        {
            if (_readBuffer.Length <= 2) return;

            // 解析總長度
            int    readIndex = _readBuffer.ReadIndex;
            byte[] data      = _readBuffer.Data;
            UInt16 length    = (UInt16)((data[readIndex + 1] << 8) | data[readIndex]);

            // 資料不完整
            if (_readBuffer.Length < length) return;
            _readBuffer.ReadIndex += 2;

            // 解析MessageId
            readIndex = _readBuffer.ReadIndex;
            UInt16 messageId = (UInt16)((data[readIndex + 1] << 8) | data[readIndex]);
            _readBuffer.ReadIndex += 2;

            if (!MessageUtils.IdToMessage.ContainsKey(messageId))
            {
                EzLog.Log(LogType.Warning, "Parse Message Failed, unknown MessageId", "NetworkManager");
                return;
            }

            // 協議Body長度 = 總長度 - 表示總長度的UInt16(2 Byte) - MessageId(2 Byte) 
            int bodyLength = length - 2 - 2;

            // 解析協議Body
            Type     type    = MessageUtils.IdToMessage[messageId];
            IMessage message = MessageUtils.Decode(type, _readBuffer.Data, _readBuffer.ReadIndex, bodyLength);

            if (message == null)
            {
                EzLog.Log(LogType.Warning, "Parse Message Failed, unknown Message Type", "NetworkManager");
                return;
            }

            _readBuffer.ReadIndex += bodyLength;
            _readBuffer.CheckAndReuseCapacity();

            MessageInfo messageInfo = new MessageInfo();
            messageInfo.MessageId = messageId;
            messageInfo.Message   = message;
            
            EzLog.Log(LogType.Debug, $"Receive: {message.GetType().Name}" );

            // 加到MessageList，Update在主線程取用MessageList，這裡在子線程，可能會出現Race Condition
            lock (_messageList)
            {
                _messageList.Add(messageInfo);
                _messageCount++;
            }

            // 繼續解析
            if (_readBuffer.Length > 2)
            {
                ParseReceivedData();
            }
        }

        #endregion

        #region - Update: HandleMessages -

        public void UpdateLogic()
        {
            HandleMessages();
            UpdatePing();
        }

        private void HandleMessages()
        {
            if (_messageCount == 0) return;

            // 每幀處理 MAX_HANDLE_MESSAGE_PER_FRAME 個 Message
            for (int i = 0; i < MAX_HANDLE_MESSAGE_PER_FRAME; i++)
            {
                MessageInfo messageInfo = null;
                lock (_messageList)
                {
                    if (_messageList.Count > 0)
                    {
                        messageInfo = _messageList[0];
                        _messageList.RemoveAt(0);
                        _messageCount--;
                    }
                }

                if (messageInfo != null)
                {
                    InvokeMessageHandlers(messageInfo.MessageId, messageInfo.Message);
                }
                else
                {
                    break;
                }
            }
        }

        #endregion

        #region - Ping Pong -

        private void UpdatePing()
        {
            if (!_isUsePing) return;

            if (Time.realtimeSinceStartup - _lastPingTime > _pingInterval)
            {
                _lastPingTime = Time.realtimeSinceStartup;
                Send(new Ping());
            }

            if (Time.realtimeSinceStartup - _lastPongTime > _pongTimeOut)
            {
                Close();
            }
        }

        #endregion
    }
}