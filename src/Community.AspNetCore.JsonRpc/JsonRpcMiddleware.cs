﻿using System;
using System.Collections.Generic;
using System.Data.JsonRpc;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Community.AspNetCore.JsonRpc.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Community.AspNetCore.JsonRpc
{
    internal sealed class JsonRpcMiddleware
    {
        private static readonly MediaTypeHeaderValue _mediaType = new MediaTypeHeaderValue("application/json");

        private readonly IJsonRpcHandler _handler;
        private readonly ILogger _logger;
        private readonly JsonRpcSerializer _serializer;

        public JsonRpcMiddleware(RequestDelegate next, IJsonRpcHandler handler, ILoggerFactory loggerFactory)
        {
            _handler = handler;
            _serializer = CreateSerializer(handler.CreateScheme());
            _logger = loggerFactory?.CreateLogger<JsonRpcMiddleware>();
            _mediaType.CharSet = Encoding.UTF8.WebName;
        }

        public async Task Invoke(HttpContext context)
        {
            if (string.Compare(context.Request.Method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase) != 0)
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            }
            else if ((context.Request.ContentType == null) || !ValidateMediaType(context.Request.ContentType))
            {
                context.Response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
            }
            else if ((context.Request.Headers["Accept"] == default(StringValues)) || !ValidateMediaType(context.Request.Headers["Accept"]))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotAcceptable;
            }
            else if (context.Request.ContentLength == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.LengthRequired;
            }
            else
            {
                var requestString = default(string);

                using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                {
                    requestString = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                if (requestString.Length != context.Request.ContentLength.Value)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                }
                else
                {

                    var jsonRpcRequestData = default(JsonRpcData<JsonRpcRequest>);
                    var responseString = string.Empty;

                    try
                    {
                        jsonRpcRequestData = _serializer.DeserializeRequestData(requestString);
                    }
                    catch (JsonRpcException ex)
                    {
                        responseString = _serializer.SerializeResponse(new JsonRpcResponse(ConvertExceptionToError(ex), JsonRpcId.None));

                        _logger?.LogInformation(0, "JSON-RPC \"{0}\" [1] -> [1]", context.Request.PathBase);
                    }

                    if (jsonRpcRequestData != null)
                    {
                        if (jsonRpcRequestData.IsSingle)
                        {
                            var jsonRpcResponse = await InvokeHandler(jsonRpcRequestData.SingleItem).ConfigureAwait(false);

                            if (jsonRpcResponse != null)
                            {
                                responseString = _serializer.SerializeResponse(jsonRpcResponse);

                                _logger?.LogInformation(0, "JSON-RPC \"{0}\" [1] -> [1]", context.Request.PathBase);
                            }
                            else
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.NoContent;

                                _logger?.LogInformation(0, "JSON-RPC \"{0}\" [1] -> [0]", context.Request.PathBase);
                            }
                        }
                        else
                        {
                            var jsonRpcResponses = new List<JsonRpcResponse>(jsonRpcRequestData.BatchItems.Count);

                            for (var i = 0; i < jsonRpcRequestData.BatchItems.Count; i++)
                            {
                                var jsonRpcResponse = await InvokeHandler(jsonRpcRequestData.BatchItems[i]).ConfigureAwait(false);

                                if (jsonRpcResponse != null)
                                {
                                    jsonRpcResponses.Add(jsonRpcResponse);
                                }
                            }

                            responseString = _serializer.SerializeResponses(jsonRpcResponses);

                            _logger?.LogInformation(0, "JSON-RPC \"{0}\" [{1}] -> [{2}]", context.Request.PathBase, jsonRpcRequestData.BatchItems.Count, jsonRpcResponses.Count);
                        }
                    }

                    var responseBytes = Encoding.UTF8.GetBytes(responseString);

                    context.Response.ContentType = _mediaType.ToString();
                    context.Response.ContentLength = responseBytes.Length;

                    if ((HttpStatusCode)context.Response.StatusCode != HttpStatusCode.NoContent)
                    {
                        await context.Response.Body.WriteAsync(responseBytes, 0, responseBytes.Length).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task<JsonRpcResponse> InvokeHandler(JsonRpcItem<JsonRpcRequest> item)
        {
            if (item.IsValid)
            {
                var request = item.Message;

                if (request.IsSystem)
                {
                    return null;
                }

                var response = await _handler.Handle(request).ConfigureAwait(false);

                if (!request.IsNotification)
                {
                    if (response == null)
                    {
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Strings.GetString("handler.response.undefined"), request.Id));
                    }
                    if (request.Id != response.Id)
                    {
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Strings.GetString("handler.response.id.invalid_value"), request.Id));
                    }

                    return response;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return new JsonRpcResponse(ConvertExceptionToError(item.Exception), item.Exception.MessageId);
            }
        }

        private static JsonRpcSerializer CreateSerializer(JsonRpcSerializerScheme scheme)
        {
            var settings = new JsonRpcSerializerSettings
            {
                JsonSerializerBufferPool = new JsonRpcBufferPool()
            };

            return new JsonRpcSerializer(scheme, settings);
        }

        private static JsonRpcError ConvertExceptionToError(JsonRpcException exception)
        {
            var code = default(long);

            switch (exception.Type)
            {
                case JsonRpcExceptionType.Parsing:
                    {
                        code = -32700L;
                    }
                    break;
                case JsonRpcExceptionType.InvalidParams:
                    {
                        code = -32602L;
                    }
                    break;
                case JsonRpcExceptionType.InvalidMethod:
                    {
                        code = -32601L;
                    }
                    break;
                case JsonRpcExceptionType.InvalidMessage:
                    {
                        code = -32600L;
                    }
                    break;
                default:
                    {
                        code = -32603L;
                    }
                    break;
            }

            return new JsonRpcError(code, exception.Message);
        }

        private static bool ValidateMediaType(string value)
        {
            return MediaTypeHeaderValue.TryParse(value, out var result) && (string.Compare(result.MediaType, _mediaType.MediaType, StringComparison.OrdinalIgnoreCase) == 0);
        }
    }
}