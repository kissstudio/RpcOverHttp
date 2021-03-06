﻿using DynamicProxyImplementation;
using RpcOverHttp.Internal;
using RpcOverHttp.Serialization;
using RpcOverHttp.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TinyIoC;

namespace RpcOverHttp
{
    /// <summary>
    /// for server administration using built-in rpc service
    /// </summary>
    public interface IRpcServer
    {
        void Stop();
        Task ProcessWebsocketRequest(IRpcWebSocketContext ctx);
        void ProcessRequest(IRpcHttpContext ctx);
        void Register<T>() where T : class;
        void Register<T>(T instance) where T : class;
        void Register<T, TImplementation>() where T : class where TImplementation : class, T;
        void Register<T, TImplementation>(TImplementation instance) where T : class where TImplementation : class, T;
    }


    public class RpcServer : IocContainerWrapper, IRpcServer
    {
        private IEnumerable<Type> itfTypes;
        private IEnumerable<Type> implTypes;
        private IEnumerable<object> impls;
        RpcHttpListener listener;
        public IExceptionHandler ExceptionHandler { get; set; }
        public IAuthroizeHandler AuthroizeHandler { get; set; }
        public RpcServer() : base(new TinyIoCContainer())
        {
            iocContainer.Register<IWsDataSerializer, ProtoBufRpcDataSerializer>(new ProtoBufRpcDataSerializer(), "default");
            iocContainer.Register<IRpcDataSerializer, HttpMultipartSerializer>(new HttpMultipartSerializer(), "default");
            iocContainer.Register<IRpcHeadSerializer, JsonRpcHeadSerializer>(new JsonRpcHeadSerializer(), "default");
            iocContainer.Register<IRpcServer>(this, "default");
            iocContainer.Register<IRpcServiceAdministration>(new RpcServiceAdministration());
            iocContainer.Register<IExceptionHandler>((ExceptionHandler = new DefaultExceptionHandler()), "default");
            iocContainer.Register<IAuthroizeHandler>((AuthroizeHandler = new DefaultAuthroizeHandler()), "default");
        }

        /// <summary>
        /// webhost start
        /// </summary>
        internal void Start()
        {
            itfTypes = iocContainer.RegisteredTypes.Select(x => x.Type);
            impls = itfTypes.Select(x => iocContainer.Resolve(x));
            implTypes = impls.Select(x => x.GetType());
            new Thread(WsActiveDetection) { IsBackground = true, Name = "WsActiveDetection" }.Start();
        }

        /// <summary>
        /// self host start
        /// </summary>
        /// <param name="urlPrefix"></param>
        public void Start(string urlPrefix)
        {
            listener = new RpcHttpListener(this);
            var uri = new Uri(urlPrefix);
            if (uri.Scheme == "https")
            {
                EnsureCertBindingInstalled(uri.Port);
            }
            this.Start();
            listener.Start(urlPrefix);
            Console.WriteLine($"start {urlPrefix}");
        }

        public void ProcessRequest(IRpcHttpContext ctx)
        {
            if (ctx.IsWebSocketRequest)
            {
                ctx.AcceptWebSocket(ProcessWebsocketRequest);
            }
            else
            {

                this.ProcessRequestInternal(ctx);
            }
        }
        private void OnWebSocketTaskCanceled(object state)
        {

        }

        private void WsActiveDetection()
        {
            while (!stopRequested)
            {
                var keys = eventMessages.Keys.ToList();
                foreach (var item in keys)
                {
                    BlockingQueue<RpcEvent> messages;
                    if (eventMessages.TryGetValue(item, out messages))
                    {
                        messages.Enqueue(RpcEvent.Empty); // for detection
                    }
                }
                Thread.Sleep(5000);
            }
        }

        public async Task ProcessWebsocketRequest(IRpcWebSocketContext ctx)
        {
            WebSocket webSocket = ctx.WebSocket;
            const int maxMessageSize = 1024;
            var receivedDataBuffer = new ArraySegment<Byte>(new Byte[maxMessageSize]);
            var cancellationToken = new CancellationToken();
            cancellationToken.Register(OnWebSocketTaskCanceled, ctx);
            var instanceIdStr = ctx.RequestUri.PathAndQuery.Substring(ctx.RequestUri.PathAndQuery.IndexOf('=') + 1);
            BlockingQueue<RpcEvent> messages;
            var instanceId = Guid.Parse(instanceIdStr);

            if (!eventMessages.TryGetValue(instanceId, out messages))
            {
                eventMessages[instanceId] = messages = new BlockingQueue<RpcEvent>(cancellationToken);
            }
            Console.WriteLine("ws client connected, rpc service instance id is {0}.", instanceId);
            IWsDataSerializer serializer;
            if (!iocContainer.TryResolve(out serializer))
            {
                serializer = iocContainer.Resolve<IWsDataSerializer>("default");
            }
            //检查WebSocket状态
            while (webSocket.State == WebSocketState.Open)
            {
                var msg = messages.Dequeue();
                if (msg == null)
                {
                    if (webSocket.State == WebSocketState.Open)
                    {
                        webSocket.Abort();
                    }
                    break;
                }
                //Zero handler id for live detect
                if (msg.handlerId == 0)
                {
                    if (webSocket.State != WebSocketState.Open)
                    {
                        break;
                    }
                    else
                    {
                        continue;
                    }
                }
                Console.WriteLine("message is dequeued at " + DateTime.Now);
                IRpcEventHandleResult result = null;
                if (msg.ReturnType == typeof(void))
                {
                    result = new RpcEventHandleResultVoid();
                }
                else
                {
                    var resultType = typeof(RpcEventHandleResultGeneral<>).MakeGenericType(msg.ReturnType);
                    result = Activator.CreateInstance(resultType) as IRpcEventHandleResult;
                }
                if (webSocket.State != WebSocketState.Open)
                {
                    result.Error = new RpcError(string.Format("ws connection broken, instance = {0}, state = {1}", instanceId, webSocket.State), null);
                    msg.SetResult(result);
                    break;
                }
                try
                {
                    using (var mssend = new MemoryStream())
                    {
                        //response = eventKey + args
                        var keyBytes = BitConverter.GetBytes(msg.handlerId);
                        mssend.Write(keyBytes, 0, keyBytes.Length);
                        this.FixupEventHandlerSender(msg);
                        serializer.Serialize(mssend, msg.ArgumentTypes, msg.Arguments, Enumerable.Range(0, msg.Arguments.Length).Select(x => x.ToString()).ToArray());
                        Console.WriteLine("rpc invoking client through websocket");
                        await webSocket.SendAsync(new ArraySegment<byte>(mssend.ToArray()), WebSocketMessageType.Binary, true, cancellationToken);
                    }
                    //读取数据 
                    WebSocketReceiveResult webSocketReceiveResult = await webSocket.ReceiveAsync(receivedDataBuffer, cancellationToken);
                    Console.WriteLine("rpc invoking client result received.");
                    if (webSocketReceiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, String.Empty, cancellationToken);
                    }
                    else
                    {
                        byte[] payloadData = receivedDataBuffer.Take(webSocketReceiveResult.Count).ToArray();
                        using (var msrecv = new MemoryStream(payloadData))
                        {
                            object[] values = serializer.Deserialize(msrecv, new Type[] { result.GetType() }, new string[] { "p0" });
                            msg.SetResult(values[0] as IRpcEventHandleResult);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    if (ex.InnerException != null)
                    {
                        result.Error = new RpcError(ex.InnerException.Message, ex.InnerException.StackTrace);
                    }
                    else
                    {
                        result.Error = new RpcError(ex.Message, ex.StackTrace);
                    }
                    msg.SetResult(result);
                }
            }
            Console.WriteLine(string.Format("disconnect ws connection on instance {0}", instanceId));
        }

        private void FixupEventHandlerSender(RpcEvent msg)
        {
            for (int i = 0; i < msg.Arguments.Length; i++)
            {
                var item = msg.Arguments[i];
                if (item != null && typeof(IRpcService).IsAssignableFrom(item.GetType()))
                {
                    msg.Arguments[i] = "<service_instance>";
                }
                else if (item != null && i == 0 && msg.ArgumentTypes[i] == typeof(object))
                {
                    if (item.GetType().GetInterfaces().Any())
                    {
                        msg.Arguments[i] = "<service_instance>";
                    }
                }
            }
        }

        public void Stop()
        {
            stopRequested = true;
            if (listener != null)
                listener.Stop();
        }

        private void EnsureCertBindingInstalled(int port)
        {
            var name = this.GetType().Assembly.GetName().Name;
            var cert = CertificateHandler.GetCertificates().FirstOrDefault(x => x.Name == name);
            if (cert == null)
                CertificateHandler.InstallServantCertificate(name);
            else
            {
                CertificateHandler.ExportPkFile(cert, name);
            }
            if (!CertificateHandler.IsCertificateBound(port))
            {
                CertificateHandler.AddCertificateBinding(name, port);
            }
        }

        internal void ProcessRequestInternal(object state)
        {

            var ctx = state as IRpcHttpContext;
            Stream outputStream = ctx.Response.OutputStream;
            bool acceptGzip = AcceptsGzip(ctx.Request);
            if (acceptGzip)
            {
                //ctx.Response.SendChunked = true;
                ctx.Response.AddHeader("Content-Encoding", "gzip");
                outputStream = new GZipStream(outputStream, CompressionMode.Compress, true);
            }
            try
            {
                ctx.Response.KeepAlive = ctx.Request.KeepAlive;
                ctx.Response.AddHeader("Server", "RpcServer-RpcOverHttp/1.0");
                if (ctx.Request.Url.AbsolutePath == "/metadata") //接口方法定义和方法实现之间的映射信息
                {
                    this.WriteMetadata(ctx, outputStream);
                }
                else if (ctx.Request.Url.AbsolutePath == "/")
                {
                    ProcessNormalRequest(ctx, outputStream);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.StatusDescription = "Not Found";
                }
                outputStream.Flush();
                outputStream.Close();
                ctx.Response.Flush();
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("{0} {1}", ex.Message, ex.StackTrace);
                ctx.Response.StatusCode = 500;
                ctx.Response.StatusDescription = "Internal Server Error";
                ctx.Response.Close();
            }
        }

        private static bool AcceptsGzip(IRpcHttpRequest request)
        {
            string encoding = request.Headers["Accept-Encoding"];
            if (string.IsNullOrEmpty(encoding))
            {
                return false;
            }

            return encoding.Contains("gzip");
        }


        ThunkImplementationFactory thunkImplFactory = new ThunkImplementationFactory();
        SafeDictionary<Guid, object> instances = new SafeDictionary<Guid, object>();
        internal SafeDictionary<Guid, BlockingQueue<RpcEvent>> eventMessages = new SafeDictionary<Guid, BlockingQueue<RpcEvent>>();
        private bool stopRequested;

        private void ProcessNormalRequest(IRpcHttpContext ctx, Stream outputStream)
        {
            RpcError error = new RpcError("rpc server error.", null);
            if (ctx.Request.ContentLength64 > ByteSize.FromMbs(10).TotalBytes)
            {
                error.Message = "request data is limited in 10Mb";
            }
            else
            {
                var requestMeta = ctx.Request.Headers["meta"];
                if (requestMeta != null)
                {
                    //get request metadata
                    IRpcHeadSerializer headSerializer;
                    if (!iocContainer.TryResolve(out headSerializer))
                    {
                        headSerializer = iocContainer.Resolve<IRpcHeadSerializer>("default");
                    }
                    RpcHead head = null;
                    bool deserialize_head_error_obtained = false;
                    bool deserialize_body_error_obtained = false;
                    try
                    {
                        head = headSerializer.Deserialize(requestMeta);
                    }
                    catch (Exception ex)
                    {
                        deserialize_head_error_obtained = true;
                        error.Message = "error on deserialize rpc head metadata. " + ex.Message;
                        error.StackTrace = ex.StackTrace;
                    }
                    if (head != null)
                    {
                        RpcHead.SetCurrent(head);
                        Type itfType = itfTypes.FirstOrDefault(x => x.Namespace == head.Namespace && x.Name == head.TypeName);
                        if (itfType != null)
                        {
                            var itfMethod = (MethodInfo)ReflectionHelper.ResolveMethod(itfType, head.MethodMDToken);
                            if (itfMethod != null)
                            {
                                var parmTypes = itfMethod.GetParameters().Select(x => x.ParameterType).ToArray();
                                var pramNames = itfMethod.GetParameters().Select(x => x.Name).ToArray();
                                bool error_generated = false;
                                IRpcService rpcService = null;

                                //deserialize arguments
                                IRpcDataSerializer serializer;
                                if (!iocContainer.TryResolve(out serializer))
                                {
                                    serializer = iocContainer.Resolve<IRpcDataSerializer>("default");
                                }
                                object[] args = null;
                                if (head.EventOp)
                                {
                                    FixupEventHandlerType(parmTypes);
                                }
                                try
                                {
                                    args = serializer.Deserialize(ctx.Request.InputStream, parmTypes, pramNames);

                                }
                                catch (Exception ex)
                                {
                                    deserialize_body_error_obtained = true;
                                    error.Message = "error on deserialize rpc request data. " + ex.Message;
                                    error.StackTrace = ex.StackTrace;
                                }
                                if (!deserialize_body_error_obtained)
                                {
                                    object impl = null;
                                    object returnVal = null;

                                    //find a instance
                                    if (!this.instances.TryGetValue(head.InstanceId, out impl))
                                    {
                                        //resolve the implimentation of the interface
                                        impl = iocContainer.Resolve(itfType);
                                    }
                                    var implInternal = impl;
                                    if (impl != null && !typeof(ThunkImplementation).IsAssignableFrom(impl.GetType()))
                                    {
                                        //thunk impl is for event remote handle, if no this thunk, server can not known which client handler will call
                                        impl = this.instances[head.InstanceId] = thunkImplFactory.GetProxy(itfType, impl, head.InstanceId, this);
                                    }

                                    //process a call for getting user infomation if user code support authroize
                                    rpcService = impl as IRpcService;
                                    var abstractRpcService = implInternal as RpcService;
                                    if (abstractRpcService != null)
                                    {
                                        abstractRpcService.exceptionHandler = this.ExceptionHandler;
                                        abstractRpcService.authorizeHandler = this.AuthroizeHandler;
                                    }
                                    RpcIdentity identity = rpcService != null ? rpcService.Authroize(head.Token) : AuthroizeHandler.Authroize(head.Token);
                                    var principal = new RpcPrincipal(identity);
                                    Thread.CurrentPrincipal = principal;
                                    if (abstractRpcService != null)
                                    {
                                        abstractRpcService.User = principal;
                                    }
                                    var rpcAdministration = implInternal as RpcServiceAdministration;
                                    if (rpcAdministration != null)
                                    {
                                        rpcAdministration.Server = this;
                                    }

                                    Type instanceType = impl.GetType();

                                    try
                                    {
                                        Console.WriteLine("[rpc call] {0}.{1}.{2}", head.Namespace, head.TypeName, head.MethodName);
                                        if (!RpcMethodHelper.IsAuthoirzied(itfType, itfMethod, instanceType))
                                        {
                                            error_generated = true;
                                            error = new RpcError("access denied.", null);
                                            ctx.Response.StatusCode = 401;
                                        }
                                        else
                                        {
                                            //execute the call
                                            if (head.EventOp)
                                            {
                                                //Debugger.Break();
                                                var op = head.GetEventOp();
                                                //create a method as the event proxy

                                                EventInfo e = TypeHelper.GetEventInfo(itfType, op.EventName);
                                                var hanlderName = itfType.Name + "_" + op.EventName;

                                                //thunkHandler is a proxy method will call DelegateHelper.
                                                var thunkHandler = instanceType.GetMethod(hanlderName);

                                                var clientHandlerId = (int)args[0]; /*event callback id of client*/

                                                //event register/unregister
                                                if (op.EventKind == RpcEventKind.Add)
                                                {
                                                    EventHub.AddEventHandler(e, impl, head.InstanceId, thunkHandler, clientHandlerId);
                                                }
                                                else
                                                {
                                                    EventHub.RemoveEventHandler(e, impl, head.InstanceId, thunkHandler, clientHandlerId);
                                                    BlockingQueue<RpcEvent> messages;
                                                    if (this.eventMessages.TryGetValue(head.InstanceId, out messages))
                                                    {
                                                        //null for close the ws connection
                                                        //Console.WriteLine("send null RpcEvent for close the ws connection on instance " + head.InstanceId);
                                                        messages.Enqueue(null);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                MethodInfo implMethod = RpcMethodHelper.FindImplMethod(itfType, itfMethod, instanceType);
                                                returnVal = RpcMethodHelper.Invoke(itfType, itfMethod, impl, implMethod, head.EventOp, head.Timeout, head.Token, args);
                                            }
                                            //dispose resouces like stream
                                            foreach (IDisposable item in args.OfType<IDisposable>())
                                            {
                                                try
                                                {
                                                    item.Dispose();
                                                }
                                                catch { }
                                            }
                                        }
                                    }
                                    catch (TargetInvocationException ex)
                                    {
                                        error_generated = true;
                                        if (rpcService != null)
                                        {
                                            try
                                            {
                                                error = rpcService.HandleException(head, ex.InnerException);
                                            }
                                            catch (Exception ex2)
                                            {
                                                //do not use exceptionHandler here, it may cause a deal loop
                                                //because the rpcService backend ex handler is exceptionHandler by default
                                                error = RpcError.FromException(ex2);
                                            }
                                        }
                                        else
                                        {
                                            error = this.ExceptionHandler.HandleException(head, ex.InnerException);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        error_generated = true;
                                        if (rpcService != null)
                                        {
                                            try
                                            {
                                                error = rpcService.HandleException(head, ex);
                                            }
                                            catch (Exception ex2)
                                            {
                                                //do not use exceptionHandler here, it may cause a deal loop
                                                //because the rpcService backend ex handler is exceptionHandler by default
                                                error = RpcError.FromException(ex2);
                                            }
                                        }
                                        else
                                        {
                                            error = this.ExceptionHandler.HandleException(head, ex);
                                        }
                                    }
                                    if (!error_generated)
                                    {
                                        try
                                        {
                                            if (itfMethod.ReturnType != typeof(void))
                                            {
                                                if (typeof(Task).IsAssignableFrom(itfMethod.ReturnType))
                                                {
                                                    if (itfMethod.ReturnType.IsGenericType)
                                                    {
                                                        serializer.Serialize(outputStream, new Type[] { itfMethod.ReturnType.GenericTypeArguments[0] }, new object[] { returnVal }, new string[] { "p0" });
                                                    }
                                                    else
                                                    {
                                                        //no need handle task without a result.
                                                    }
                                                }
                                                else
                                                {
                                                    serializer.Serialize(outputStream, new Type[] { itfMethod.ReturnType }, new object[] { returnVal }, new string[] { "p0" });
                                                }
                                                IDisposable value = returnVal as IDisposable;
                                                if (value != null)
                                                {
                                                    try { value.Dispose(); } catch { }
                                                }
                                                IDisposable inst = impl as IDisposable;
                                                if (inst != null)
                                                {
                                                    try { inst.Dispose(); } catch { }
                                                }
                                            }
                                            return;
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine("error on writting response, " + ex.Message);
                                            return; // connection is broken. nothing to do.
                                        }
                                    }
                                }
                            }
                            else
                            {
                                error.Message = "invalid rpc request metadata, unknown method under the interface.";
                            }
                        }
                        else
                        {
                            error.Message = "invalid rpc request metadata, unknown interface.";
                        }
                    }
                    else if (!deserialize_head_error_obtained)
                    {
                        error.Message = "invalid rpc request metadata, deserialize failed.";
                    }
                }
                else
                {
                    error.Message = "invalid rpc request metadata";
                }
            }
            Console.WriteLine(error.Message);
            Console.WriteLine(error.StackTrace);
            if (ctx.Response.StatusCode == 200)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.StatusDescription = "Internal Server Error";
            }
            ctx.WriteOutput(outputStream, error);
        }

        private void FixupEventHandlerType(Type[] parmTypes)
        {
            for (int i = 0; i < parmTypes.Length; i++)
            {
                parmTypes[i] = typeof(int);
            }
        }
        private void FixupEventHandler(object[] args)
        {

        }

        private void WriteMetadata(IRpcHttpContext ctx, Stream outputStream)
        {
            var md = ReflectionHelper.GetRpcServiceInfo(impls);
            ctx.WriteOutput(outputStream, md);
        }
    }
}
