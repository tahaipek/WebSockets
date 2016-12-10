﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreStack.WebSockets.Interfaces;
using NetCoreStack.WebSockets.Internal;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetCoreStack.WebSockets
{
    public class ConnectionManager : IConnectionManager
    {
        private readonly InvocatorRegistry _invocatorRegistry;
        private readonly ServerSocketsOptions _options;
        private readonly IHandshakeStateTransport _initState;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IStreamCompressor _compressor;
        private readonly ConcurrentDictionary<string, WebSocketTransport> _connections;

        public ConnectionManager(IStreamCompressor compressor,
            InvocatorRegistry invocatorRegistry,
            IOptions<ServerSocketsOptions> options,
            IHandshakeStateTransport initState,
            ILoggerFactory loggerFactory)
        {
            _invocatorRegistry = invocatorRegistry;
            _options = options.Value;
            _initState = initState;
            _loggerFactory = loggerFactory;
            _compressor = compressor;
            _connections = new ConcurrentDictionary<string, WebSocketTransport>();
        }

        private async Task<byte[]> PrepareBytesAsync(byte[] input, JsonObject properties)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var props = JsonConvert.SerializeObject(properties);
            var propsBytes = Encoding.UTF8.GetBytes($"{props}{SocketsConstants.Splitter}");

            var bytesCount = input.Length;
            input = propsBytes.Concat(input).ToArray();

            return await _compressor.CompressAsync(input);         
        }

        private async Task SendAsync(WebSocketTransport transport, WebSocketMessageDescriptor descriptor)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (descriptor.Segments == null)
            {
                throw new ArgumentNullException(nameof(descriptor.Segments));
            }

            await transport.WebSocket.SendAsync(descriptor.Segments, 
                descriptor.MessageType, 
                descriptor.EndOfMessage, 
                CancellationToken.None);
        }

        private async Task SendBinaryAsync(WebSocketTransport transport, byte[] chunkedBytes, bool endOfMessage, CancellationToken token)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            var segments = new ArraySegment<byte>(chunkedBytes);

            await transport.WebSocket.SendAsync(segments,
                           WebSocketMessageType.Binary,
                           endOfMessage,
                           token);
        }

        public async Task ConnectAsync(WebSocket webSocket)
        {
            WebSocketTransport transport = new WebSocketTransport(webSocket);
            var connectionId = transport.ConnectionId;
            var context = new WebSocketMessageContext();
            context.Command = WebSocketCommands.Handshake;
            context.Value = connectionId;
            context.State = await _initState.GetStateAsync();
            _connections.TryAdd(connectionId, transport);

            await SendAsync(connectionId, context);

            var receiverContext = new WebSocketReceiverContext
            {
                Compressor = _compressor,
                ConnectionId = connectionId,
                InvocatorRegistry = _invocatorRegistry,
                LoggerFactory = _loggerFactory,
                Options = _options,
                WebSocket = webSocket
            };
            var receiver = new WebSocketReceiver(receiverContext, CloseConnection);
            await receiver.ReceiveAsync();
        }

        public async Task BroadcastAsync(WebSocketMessageContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Value == null)
            {
                throw new ArgumentNullException(nameof(context.Value));
            }

            if (!_connections.Any())
            {
                return;
            }

            var segments = context.ToSegment();
            var descriptor = new WebSocketMessageDescriptor
            {
                Segments = segments,
                EndOfMessage = true,
                MessageType = WebSocketMessageType.Text
            };

            foreach (var connection in _connections)
            {
                await SendAsync(connection.Value, descriptor);
            }
        }

        public async Task BroadcastBinaryAsync(byte[] inputs, JsonObject properties)
        {
            if (!_connections.Any())
            {
                return;
            }
            
            var bytes = await PrepareBytesAsync(inputs, properties);
            var buffer = new byte[SocketsConstants.ChunkSize];

            using (var ms = new MemoryStream(bytes))
            {
                using (var br = new BinaryReader(ms))
                {
                    byte[] chunkedBytes = null;
                    do
                    {
                        chunkedBytes = br.ReadBytes(SocketsConstants.ChunkSize);
                        var endOfMessage = false;

                        if (chunkedBytes.Length < SocketsConstants.ChunkSize)
                            endOfMessage = true;

                        foreach (var connection in _connections)
                        {
                            await SendBinaryAsync(connection.Value, chunkedBytes, endOfMessage, CancellationToken.None);
                        }

                        if (endOfMessage)
                            break;

                    } while (chunkedBytes.Length <= SocketsConstants.ChunkSize);
                }
            }                  
        }

        public async Task SendAsync(string connectionId, WebSocketMessageContext context)
        {
            if (!_connections.Any())
            {
                return;
            }

            WebSocketTransport transport = null;
            if (!_connections.TryGetValue(connectionId, out transport))
            {
                throw new ArgumentOutOfRangeException(nameof(transport));
            }

            var segments = context.ToSegment();
            var descriptor = new WebSocketMessageDescriptor
            {
                Segments = segments,
                EndOfMessage = true,
                MessageType = WebSocketMessageType.Text
            };

            await SendAsync(transport, descriptor);
        }

        public async Task SendBinaryAsync(string connectionId, byte[] input, JsonObject properties)
        {
            if (!_connections.Any())
            {
                return;
            }

            WebSocketTransport transport = null;
            if (!_connections.TryGetValue(connectionId, out transport))
            {
                throw new ArgumentOutOfRangeException(nameof(transport));
            }

            byte[] bytes = await PrepareBytesAsync(input, properties);

            var buffer = new byte[SocketsConstants.ChunkSize];
            using (var ms = new MemoryStream(bytes))
            {
                using (BinaryReader br = new BinaryReader(ms))
                {
                    byte[] chunkBytes = null;
                    do
                    {
                        chunkBytes = br.ReadBytes(SocketsConstants.ChunkSize);
                        var segments = new ArraySegment<byte>(chunkBytes);
                        var endOfMessage = false;

                        if (chunkBytes.Length < SocketsConstants.ChunkSize)
                            endOfMessage = true;

                        await transport.WebSocket.SendAsync(segments, 
                            WebSocketMessageType.Binary, 
                            endOfMessage, 
                            CancellationToken.None);

                        if (endOfMessage)
                            break;

                    } while (chunkBytes.Length <= SocketsConstants.ChunkSize);
                }
            }
        }

        public void CloseConnection(string connectionId)
        {
            WebSocketTransport transport = null;
            if (_connections.TryRemove(connectionId, out transport))
            {
                transport.Dispose();
            }
        }

        public void CloseConnection(WebSocketReceiverContext context)
        {
            CloseConnection(context.ConnectionId);
        }
    }
}