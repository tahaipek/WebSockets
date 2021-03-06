﻿using Microsoft.Extensions.Logging;
using NetCoreStack.WebSockets.Internal;
using System.Net.WebSockets;
using NetCoreStack.WebSockets.Interfaces;

namespace NetCoreStack.WebSockets
{
    public class WebSocketReceiverContext
    {
        public string ConnectionId { get; set; }
        public WebSocket WebSocket { get; set; }
        public InvocatorRegistry InvocatorRegistry { get; set; }
        public SocketsOptions Options { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
        public IStreamCompressor Compressor { get; set; }
    }
}
