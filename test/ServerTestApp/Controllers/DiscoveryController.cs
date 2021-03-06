﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NetCoreStack.WebSockets;
using ServerTestApp.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;

namespace ServerTestApp.Controllers
{
    [Route("api/[controller]")]
    public class DiscoveryController : Controller
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IConnectionManager _connectionManager;
        private readonly IDistributedCache _distrubutedCache;
        private readonly IMemoryCache _memoryCache;

        public DiscoveryController(IConnectionManager connectionManager, 
            IDistributedCache distrubutedCache,
            IMemoryCache memoryCache,
            ILoggerFactory loggerFactory)
        {
            _connectionManager = connectionManager;
            _distrubutedCache = distrubutedCache;
            _memoryCache = memoryCache;
            _loggerFactory = loggerFactory;
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Json(new { processorCount = Environment.ProcessorCount });
        }

        [HttpPost(nameof(SendAsync))]
        public async Task<IActionResult> SendAsync([FromBody]SimpleModel model)
        {
            var echo = $"Echo from server '{model.Key}' - {DateTime.Now}";
            var obj = new { message = echo };
            var webSocketContext = new WebSocketMessageContext { Command = WebSocketCommands.DataSend, Value = obj };
            await _connectionManager.BroadcastAsync(webSocketContext);
            return Ok();
        }

        [HttpPost(nameof(BroadcastBinaryAsync))]
        public async Task<IActionResult> BroadcastBinaryAsync([FromBody]SimpleModel model)
        {
            var bytes = _distrubutedCache.Get(model.Key);
            var routeValueDictionary = new RouteValueDictionary(new { Key = model.Key });
            if (bytes != null)
            {
                await _connectionManager.BroadcastBinaryAsync(bytes, routeValueDictionary);
            }
            return Ok();
        }

        [HttpPost(nameof(SendBinaryAsync))]
        public async Task<IActionResult> SendBinaryAsync([FromBody]Context model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (model.Keys == null || !model.Keys.Any())
            {
                return NotFound();
            }

            foreach (var key in model.Keys)
            {
                try
                {
                    var routeValueDictionary = new RouteValueDictionary(new { Key = key });
                    var bytes = _distrubutedCache.Get(key);
                    await _connectionManager.BroadcastBinaryAsync(bytes, routeValueDictionary);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            
            return Ok();
        }

        [HttpPost(nameof(SendCompressedBinaryAsync))]
        public async Task<IActionResult> SendCompressedBinaryAsync([FromBody]Context model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (model.Keys == null || !model.Keys.Any())
            {
                return NotFound();
            }

            foreach (var key in model.Keys)
            {
                try
                {
                    var routeValueDictionary = new RouteValueDictionary(new { Key = key });

                    // Get compressed bytes from redis
                    var compressedBytes = _distrubutedCache.Get(key);
                    await _connectionManager.BroadcastBinaryAsync(compressedBytes, routeValueDictionary);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            return Ok();
        }

        [HttpGet(nameof(GetConnections))]
        public IActionResult GetConnections()
        {
            var connections = _connectionManager.Connections
                .Select(x => new { ConnectionId = x.Value.ConnectionId, ConnectorName = x.Value.ConnectorName });

            return Json(connections);
        }
    }
}
